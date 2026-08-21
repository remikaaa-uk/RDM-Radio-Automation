using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Shared.Enums;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Enc;

namespace RDM.Audio.Engine;

/// <summary>
/// One streaming target: an encoder tapped onto the program bus and cast to a SHOUTcast/Icecast
/// server, plus the reconnect policy for that one session.
///
/// Lives outside BassAudioEngine on purpose — the engine owns the mixer and hands it over, but the
/// session state machine, backoff timer and encoder handle belong together and nowhere else.
///
/// The tap is non-consuming: BASS_Encode_* installs a DSP on the mixer, exactly like the VU meter
/// already does. It observes the data flowing to the sound card and never pulls it, so adding or
/// removing a session cannot starve the audio output.
/// </summary>
internal sealed class EncoderSession : IDisposable
{
    /// Reconnect interval bounds. The profile carries the delay; these keep a bad value from either
    /// busy-looping the socket or stranding the stream off-air for longer than any real outage.
    private const int MinReconnectSeconds = 2;
    private const int MaxReconnectSeconds = 1800;

    private readonly EncoderProfile _profile;
    private readonly string? _password;
    private readonly int _mixerHandle;
    private readonly ILogger _log;
    private readonly Action<EncoderStatus> _publish;
    private readonly object _gate = new();

    /// Held in a field for as long as the session lives: the native side keeps calling this, and a
    /// delegate with no managed reference would be collected and crash the process.
    private readonly ENCODENOTIFYPROC _notifyProc;

    private int _encoderHandle;
    private EncoderSessionState _state = EncoderSessionState.Stopped;
    private string? _error;
    private DateTime? _connectedAt;
    private int _retryAttempt;
    private DateTime? _nextRetryAt;
    private Timer? _retryTimer;
    private bool _stopRequested;
    private bool _disposed;

    /// <summary>
    /// True once this session has reached the streaming state at least once. It draws the line
    /// between a first connection — where a rejected mount or bad credentials is a real config
    /// fault worth stopping on — and a reconnect, where the same BASS error means the server is
    /// momentarily down (a restart in progress) and the right thing is to keep trying.
    /// </summary>
    private bool _hasStreamed;

    public EncoderSession(
        EncoderProfile profile,
        string? password,
        int mixerHandle,
        ILogger log,
        Action<EncoderStatus> publish)
    {
        _profile     = profile;
        _password    = password;
        _mixerHandle = mixerHandle;
        _log         = log;
        _publish     = publish;
        _notifyProc  = OnNotify;
    }

    public string ProfileId => _profile.ProfileId;

    public EncoderStatus Status
    {
        get
        {
            lock (_gate) return Snapshot();
        }
    }

    /// <summary>Starts the encoder and initiates the cast connection. Never throws.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _state is EncoderSessionState.Streaming or EncoderSessionState.Connecting)
                return;

            _stopRequested = false;
            _retryAttempt  = 0;
            _hasStreamed   = false;
            StartAttemptLocked();
        }
    }

    /// <summary>
    /// Stops the session. A user stop always wins over a pending reconnect: the retry timer is
    /// cancelled first, so a backoff that was about to fire cannot resurrect the session.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopRequested = true;
            CancelRetryLocked();
            FreeEncoderLocked();
            TransitionLocked(EncoderSessionState.Stopped, error: null);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed      = true;
            _stopRequested = true;
            CancelRetryLocked();
            FreeEncoderLocked();
        }
    }

    /// <summary>
    /// Offers the current now-playing line. The session applies its own profile's rule, because
    /// the profile is what it holds: NowPlaying forwards the line, Static ignores it in favour of
    /// the operator's fixed text, None sends nothing at all.
    /// </summary>
    public void ApplyNowPlaying(string nowPlayingTitle) => SendTitle(_profile.TitleMode switch
    {
        StreamTitleMode.NowPlaying => nowPlayingTitle,
        StreamTitleMode.Static     => _profile.TitleText,
        _                          => null
    });

    /// <summary>
    /// Sends the title, if there is one to send. Only meaningful while streaming — before the
    /// handshake there is no connection to carry it and BASSenc would just fail.
    ///
    /// Sent as UTF-8: it is the only encoding that can carry the Polish alphabet, and it is what
    /// Icecast with Ogg and SHOUTcast 2 expect. The BASSenc documentation names ISO-8859-1 for
    /// SHOUTcast 1 and non-Ogg Icecast, but Latin-1 cannot represent ł/ą/ę/ś/ż at all — mojibake
    /// on an old server is the lesser evil against titles that are wrong by construction.
    ///
    /// An empty title is never sent: it would blank the server's display, which is worse than
    /// leaving the previous line standing.
    /// </summary>
    private void SendTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;

        lock (_gate)
        {
            if (_disposed || _encoderHandle == 0 || _state != EncoderSessionState.Streaming) return;

            if (!BassEnc.BASS_Encode_CastSetTitle(_encoderHandle, title, null))
                _log.LogDebug("Encoder '{Name}': title not accepted by the server ({Err})",
                    _profile.Name, Bass.BASS_ErrorGetCode());
        }
    }

    // ── Connection attempt ───────────────────────────────────────────────────

    private void StartAttemptLocked()
    {
        TransitionLocked(EncoderSessionState.Starting, error: null);

        if (BassEncoderFormats.RequiredLibrary(_profile.Format) is not { } lib)
        {
            FailFatalLocked($"Format {_profile.Format} is not supported by this build.");
            return;
        }

        if (!BassLibInitializer.HasLibrary("bassenc.dll"))
        {
            FailFatalLocked("bassenc.dll is missing — streaming is unavailable in this installation.");
            return;
        }

        if (!BassLibInitializer.HasLibrary(lib))
        {
            FailFatalLocked($"{lib} is missing — {_profile.Format} streaming is unavailable.");
            return;
        }

        // Fatal, not transient: a profile asking a format for a conversion it cannot do will ask
        // for the same thing on every retry. Better to say so once than to reconnect forever.
        if (BassEncoderFormats.Unsupported(SpecLocked(), _mixerHandle) is { } reason)
        {
            FailFatalLocked(reason);
            return;
        }

        int handle;
        try
        {
            handle = StartEncoderLocked();
        }
        catch (Exception ex)   // a missing/incompatible native DLL throws at the call site
        {
            FailFatalLocked($"Encoder could not be created: {ex.Message}");
            return;
        }

        if (handle == 0)
        {
            FailFatalLocked($"Encoder could not be created (BASS error {Bass.BASS_ErrorGetCode()}).");
            return;
        }

        _encoderHandle = handle;
        BassEnc.BASS_Encode_SetNotify(handle, _notifyProc, IntPtr.Zero);

        TransitionLocked(EncoderSessionState.Connecting, error: null);

        if (!CastInitLocked(handle))
        {
            var err = Bass.BASS_ErrorGetCode();
            FreeEncoderLocked();

            // FILEOPEN/ILLPARAM read as a bad mount or wrong credentials — but only on the *first*
            // connection. Once a session has streamed, those same codes mean the server is briefly
            // unreachable (a restart in progress): the credentials already worked once, so the fault
            // is transient and the session must keep trying. Classifying a reconnect failure as
            // fatal is exactly what stranded the stream off-air when the cast server was rebooted
            // and had not finished coming back within one retry interval.
            if (!_hasStreamed && err is BASSError.BASS_ERROR_FILEOPEN or BASSError.BASS_ERROR_ILLPARAM)
                FailFatalLocked($"Cast server rejected the connection (BASS error {err}).");
            else
                FailTransientLocked($"Could not reach the cast server (BASS error {err}).");
            return;
        }

        _connectedAt  = DateTime.Now;
        _retryAttempt = 0;
        _nextRetryAt  = null;
        _hasStreamed  = true;
        TransitionLocked(EncoderSessionState.Streaming, error: null);

        _log.LogInformation("Encoder '{Name}': streaming to {Host}:{Port}{Mount}",
            _profile.Name, _profile.Host, _profile.Port, _profile.Mount);

        // A fixed title has to go out now rather than at the next track change: a station name
        // that only appears after the first song would be missing for the whole first track.
        if (_profile.TitleMode == StreamTitleMode.Static)
            SendTitle(_profile.TitleText);
    }

    private BassEncoderFormats.Spec SpecLocked() => new(
        _profile.Format, _profile.BitrateKbps, _profile.SampleRateHz, _profile.Channels);

    /// StartToCast (not StartToFile): the encoded bytes go to the cast connection set up by
    /// BASS_Encode_CastInit, not to disk.
    private int StartEncoderLocked() =>
        BassEncoderFormats.StartToCast(SpecLocked(), _mixerHandle);

    private bool CastInitLocked(int handle)
    {
        var flags = BASSEncodeCast.BASS_ENCODE_CAST_DEFAULT;
        if (_profile.IsPublic) flags |= BASSEncodeCast.BASS_ENCODE_CAST_PUBLIC;
        if (_profile.UseSsl)   flags |= BASSEncodeCast.BASS_ENCODE_CAST_SSL;

        // Icecast 2.4 and later accept the HTTP PUT method; older builds want SOURCE, which is
        // what BASSenc uses unless told otherwise. Meaningless for SHOUTcast, so not sent there.
        if (_profile.UsePut && _profile.ServerType == CastServerType.Icecast)
            flags |= BASSEncodeCast.BASS_ENCODE_CAST_PUT;

        return BassEnc.BASS_Encode_CastInit(
            handle,
            ServerString(),
            Credentials(),
            BassEncoderFormats.ContentType(_profile.Format),
            _profile.StreamName,
            _profile.StreamUrl,
            _profile.Genre,
            _profile.Description,
            null,
            _profile.BitrateKbps,
            flags);
    }

    /// <summary>
    /// How BASSenc addresses each server, taken verbatim from its documentation:
    /// <c>address:port</c> for SHOUTcast 1, <c>address:port,streamid</c> for SHOUTcast 2, and
    /// <c>address:port/mount</c> for Icecast. Getting this wrong does not degrade the connection,
    /// it prevents it — a v2 server given a v1 string simply refuses the source.
    /// </summary>
    private string ServerString() => _profile.ServerType switch
    {
        CastServerType.Icecast     => $"{_profile.Host}:{_profile.Port}{NormalisedMount()}",
        CastServerType.ShoutcastV2 => $"{_profile.Host}:{_profile.Port},{StreamIdOrDefault()}",
        _                          => $"{_profile.Host}:{_profile.Port}"
    };

    /// <summary>SHOUTcast 2 numbers its streams from 1; a server with one stream uses stream 1.</summary>
    private string StreamIdOrDefault() =>
        string.IsNullOrWhiteSpace(_profile.StreamId) ? "1" : _profile.StreamId!.Trim();

    /// <summary>
    /// Icecast and SHOUTcast 2 take "username:password"; SHOUTcast 1 has no username at all and
    /// would read the whole string as the password, so it is never sent one.
    /// </summary>
    private string Credentials()
    {
        var password = _password ?? string.Empty;

        if (_profile.ServerType == CastServerType.Shoutcast) return password;
        if (string.IsNullOrWhiteSpace(_profile.Username))    return password;

        return $"{_profile.Username!.Trim()}:{password}";
    }

    private string NormalisedMount()
    {
        var mount = _profile.Mount;
        if (string.IsNullOrWhiteSpace(mount)) return "/stream";
        return mount.StartsWith('/') ? mount : "/" + mount;
    }

    // ── Failure handling and reconnect ───────────────────────────────────────

    private void FailFatalLocked(string message)
    {
        FreeEncoderLocked();
        _log.LogError("Encoder '{Name}': {Message}", _profile.Name, message);
        TransitionLocked(EncoderSessionState.FatalError, message);
    }

    private void FailTransientLocked(string message)
    {
        _log.LogWarning("Encoder '{Name}': {Message}", _profile.Name, message);
        TransitionLocked(EncoderSessionState.Disconnected, message);
        ScheduleRetryLocked();
    }

    /// <summary>
    /// The operator's chosen interval, clamped so a stray value in the database cannot turn the
    /// reconnect into a tight loop or an hours-long silence.
    /// </summary>
    private int ReconnectDelaySeconds =>
        Math.Clamp(_profile.ReconnectDelaySeconds, MinReconnectSeconds, MaxReconnectSeconds);

    private void ScheduleRetryLocked()
    {
        if (_stopRequested || _disposed) return;

        // A fixed interval, repeated for as long as it takes: a server that restarts overnight must
        // bring the stream back on its own. There is no attempt cap — only a user stop, or a genuine
        // config fault on the very first connection, ends the session.
        var delay = TimeSpan.FromSeconds(ReconnectDelaySeconds);
        _retryAttempt++;
        _nextRetryAt = DateTime.Now + delay;

        CancelRetryLocked();
        _retryTimer = new Timer(_ => OnRetryTick(), null, delay, Timeout.InfiniteTimeSpan);

        _log.LogInformation("Encoder '{Name}': reconnect attempt {Attempt} in {Delay}s",
            _profile.Name, _retryAttempt, (int)delay.TotalSeconds);

        TransitionLocked(EncoderSessionState.RetryWaiting, _error);
    }

    private void OnRetryTick()
    {
        lock (_gate)
        {
            // The stop may have landed between the timer firing and this lock being taken.
            if (_stopRequested || _disposed) return;
            StartAttemptLocked();
        }
    }

    private void CancelRetryLocked()
    {
        _retryTimer?.Dispose();
        _retryTimer  = null;
        _nextRetryAt = null;
    }

    /// <summary>
    /// Native callback from BASSenc. Only two of the five notifications mean the session is gone;
    /// treating the others as disconnects would drop a healthy connection and reconnect for nothing.
    /// </summary>
    private void OnNotify(int handle, BASSEncodeNotify status, IntPtr user)
    {
        lock (_gate)
        {
            if (_stopRequested || _disposed) return;

            switch (status)
            {
                // The cast connection or the encoder process is gone — this needs a reconnect.
                case BASSEncodeNotify.BASS_ENCODE_NOTIFY_CAST:
                case BASSEncodeNotify.BASS_ENCODE_NOTIFY_ENCODER:
                    FreeEncoderLocked();
                    _connectedAt = null;
                    FailTransientLocked($"Connection lost ({status}).");
                    break;

                // Data was dropped but the connection is still up. Worth knowing about — repeated
                // occurrences mean the uplink cannot keep up with the bitrate — but not a reconnect.
                case BASSEncodeNotify.BASS_ENCODE_NOTIFY_CAST_TIMEOUT:
                case BASSEncodeNotify.BASS_ENCODE_NOTIFY_QUEUE_FULL:
                    _log.LogWarning(
                        "Encoder '{Name}': {Status} — audio data dropped, connection still up",
                        _profile.Name, status);
                    break;

                // We freed it ourselves; Stop/Dispose already set the state.
                case BASSEncodeNotify.BASS_ENCODE_NOTIFY_FREE:
                    break;
            }
        }
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private void FreeEncoderLocked()
    {
        if (_encoderHandle == 0) return;
        BassEnc.BASS_Encode_Stop(_encoderHandle);
        _encoderHandle = 0;
    }

    private void TransitionLocked(EncoderSessionState state, string? error)
    {
        _state = state;
        _error = error;
        if (state is EncoderSessionState.Stopped or EncoderSessionState.FatalError)
            _connectedAt = null;

        _publish(Snapshot());
    }

    private EncoderStatus Snapshot() => new(
        _profile.ProfileId,
        _profile.Name,
        _state,
        _error,
        _connectedAt,
        _retryAttempt,
        _nextRetryAt);
}
