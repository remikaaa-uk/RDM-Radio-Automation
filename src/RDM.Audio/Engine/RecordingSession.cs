using Microsoft.Extensions.Logging;
using RDM.Core.Models;
using RDM.Core.Services;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Enc;

namespace RDM.Audio.Engine;

/// <summary>
/// Records the program bus to a file. The same non-consuming tap as <see cref="EncoderSession"/> —
/// BASS_Encode_* installs a DSP on the mixer, observes what flows to the sound card and never pulls
/// it — so recording cannot starve the output and works in all four output modes.
///
/// Separate from EncoderSession rather than a flag on it: a disk target has no handshake, no
/// credentials and no reconnect. Folding it in would mean a state machine where half the states are
/// unreachable. What the two genuinely share — format knowledge — lives in
/// <see cref="BassEncoderFormats"/>.
///
/// Silence is recorded as silence: the tap follows the mixer, not the playlist, so a gap between
/// tracks lands in the file at its real length. That is what a show archive is supposed to do.
/// </summary>
internal sealed class RecordingSession : IDisposable
{
    private readonly RecordingRequest _request;
    private readonly int _mixerHandle;
    private readonly ILogger _log;
    private readonly Action<RecordingStatus> _publish;
    private readonly object _gate = new();

    /// Held in a field for as long as the session lives: the native side keeps calling this, and a
    /// delegate with no managed reference would be collected and crash the process.
    private readonly ENCODENOTIFYPROC _notifyProc;

    private int _encoderHandle;
    private RecordingState _state = RecordingState.Stopped;
    private string? _filePath;
    private DateTime? _startedAt;
    private string? _error;
    private bool _stopped;
    private bool _disposed;

    public RecordingSession(
        RecordingRequest request,
        int mixerHandle,
        ILogger log,
        Action<RecordingStatus> publish)
    {
        _request     = request;
        _mixerHandle = mixerHandle;
        _log         = log;
        _publish     = publish;
        _notifyProc  = OnNotify;
    }

    public RecordingStatus Status
    {
        get { lock (_gate) return Snapshot(); }
    }

    public bool IsActive
    {
        get { lock (_gate) return _state is RecordingState.Recording or RecordingState.Starting; }
    }

    /// <summary>
    /// Creates the file and starts writing. Never throws — every fault ends as
    /// <see cref="RecordingState.Error"/> with a message the operator can act on.
    /// </summary>
    public RecordingStatus Start()
    {
        lock (_gate)
        {
            if (_disposed || _state is RecordingState.Recording or RecordingState.Starting)
                return Snapshot();

            Transition(RecordingState.Starting, error: null);

            if (BassEncoderFormats.RequiredLibrary(_request.Format) is not { } lib)
                return Fail($"Format {_request.Format} is not supported by this build.");

            if (!BassLibInitializer.HasLibrary("bassenc.dll"))
                return Fail("bassenc.dll is missing — recording is unavailable in this installation.");

            if (!BassLibInitializer.HasLibrary(lib))
                return Fail($"{lib} is missing — {_request.Format} recording is unavailable.");

            var spec = new BassEncoderFormats.Spec(
                _request.Format, _request.BitrateKbps, _request.SampleRateHz, _request.Channels);

            // Refused rather than silently ignored: a file named for a format the operator asked
            // for, holding something else, is worse than a recording that does not start.
            if (BassEncoderFormats.Unsupported(spec, _mixerHandle) is { } reason)
                return Fail(reason);

            string path;
            try
            {
                // Created before the encoder starts: BASS reports a missing directory as a generic
                // file error, which tells the operator nothing about what to fix.
                Directory.CreateDirectory(_request.Directory);
                path = RecordingFileNamer.Build(
                    _request.Directory, _request.Format, _request.NamePrefix,
                    DateTime.Now, File.Exists);
            }
            catch (Exception ex)   // unwritable path, invalid characters, missing drive
            {
                return Fail($"Recording folder is not usable: {ex.Message}");
            }

            int handle;
            try
            {
                handle = BassEncoderFormats.StartToFile(spec, _mixerHandle, path);
            }
            catch (Exception ex)   // a missing/incompatible native DLL throws at the call site
            {
                return Fail($"Encoder could not be created: {ex.Message}");
            }

            if (handle == 0)
                return Fail($"Recording could not be started (BASS error {Bass.BASS_ErrorGetCode()}).");

            _encoderHandle = handle;
            _filePath      = path;
            _startedAt     = DateTime.Now;
            BassEnc.BASS_Encode_SetNotify(handle, _notifyProc, IntPtr.Zero);

            Transition(RecordingState.Recording, error: null);
            _log.LogInformation("Recording started: {Path}", path);
            return Snapshot();
        }
    }

    /// <summary>Finalises the file. Safe to call when not recording.</summary>
    public RecordingStatus Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            bool wasRecording = _encoderHandle != 0;
            FreeEncoderLocked();

            // An Error state is left standing: a stop must not erase the reason a recording died,
            // which is the only thing the operator has to go on.
            if (_state != RecordingState.Error)
                Transition(RecordingState.Stopped, error: null);

            if (wasRecording)
                _log.LogInformation("Recording stopped: {Path}", _filePath);

            return Snapshot();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stopped  = true;
            FreeEncoderLocked();
        }
    }

    /// <summary>
    /// Native callback from BASSenc. Only ENCODER means the recording is over — the encoder died,
    /// which on a file target means the write failed (a full disk being the usual cause).
    /// </summary>
    private void OnNotify(int handle, BASSEncodeNotify status, IntPtr user)
    {
        lock (_gate)
        {
            if (_stopped || _disposed) return;

            switch (status)
            {
                case BASSEncodeNotify.BASS_ENCODE_NOTIFY_ENCODER:
                    FreeEncoderLocked();
                    Fail("Recording stopped — the encoder failed while writing (disk full?).");
                    break;

                // The queue overflowed: audio was dropped, but the file is still being written.
                // Worth a warning — the recording now has a gap — and nothing more.
                case BASSEncodeNotify.BASS_ENCODE_NOTIFY_QUEUE_FULL:
                    _log.LogWarning(
                        "Recording: encode queue full — audio dropped from {Path}", _filePath);
                    break;

                // We freed it ourselves; Stop/Dispose already set the state.
                case BASSEncodeNotify.BASS_ENCODE_NOTIFY_FREE:
                    break;
            }
        }
    }

    private RecordingStatus Fail(string message)
    {
        FreeEncoderLocked();
        _log.LogError("Recording: {Message}", message);
        Transition(RecordingState.Error, message);
        return Snapshot();
    }

    private void FreeEncoderLocked()
    {
        if (_encoderHandle == 0) return;
        BassEnc.BASS_Encode_Stop(_encoderHandle);
        _encoderHandle = 0;
    }

    private void Transition(RecordingState state, string? error)
    {
        _state = state;
        _error = error;
        _publish(Snapshot());
    }

    private RecordingStatus Snapshot() => new(
        _state,
        _filePath,
        _state is RecordingState.Recording ? _startedAt : null,
        _error,
        BytesWrittenLocked());

    /// <summary>
    /// Encoded bytes handed to the file so far. Read from BASSenc rather than the file length —
    /// the last block may still be buffered, and a length of 0 on a healthy recording would read
    /// as a fault in the UI.
    /// </summary>
    private long BytesWrittenLocked() =>
        _encoderHandle == 0
            ? 0
            : BassEnc.BASS_Encode_GetCount(_encoderHandle, BASSEncodeCount.BASS_ENCODE_COUNT_OUT);
}
