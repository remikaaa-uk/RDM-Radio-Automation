namespace RDM.Core.Models;

/// <summary>
/// Lifecycle of the file recorder. Deliberately shorter than <see cref="EncoderSessionState"/>:
/// a disk target has no handshake and no reconnect, so a failure is simply a failure.
/// </summary>
public enum RecordingState
{
    /// <summary>Not recording. Either never started, or stopped by the operator.</summary>
    Stopped,

    /// <summary>Encoder is being created (DLL checks, directory, BASS_Encode_*_StartFile).</summary>
    Starting,

    /// <summary>Writing to disk.</summary>
    Recording,

    /// <summary>Stopped by a fault — missing DLL, unwritable path, encoder died mid-recording.</summary>
    Error
}

/// <summary>
/// Snapshot of the file recorder, safe to hand to the UI or API.
/// <paramref name="FilePath"/> survives the transition to <see cref="RecordingState.Stopped"/> on
/// purpose: after a stop the operator still needs to be told what was just written and where.
/// </summary>
public sealed record RecordingStatus(
    RecordingState State,
    string? FilePath = null,
    DateTime? StartedAt = null,
    string? Error = null,
    /// <summary>Encoded bytes written so far. 0 when idle; grows while recording.</summary>
    long BytesWritten = 0);
