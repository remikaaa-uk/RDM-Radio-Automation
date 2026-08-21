namespace RDM.Core.Models;

/// <summary>
/// Lifecycle of one streaming session. A user-initiated stop always wins: it cancels any pending
/// retry and goes straight to <see cref="Stopped"/>, never back through the reconnect path.
/// </summary>
public enum EncoderSessionState
{
    /// <summary>Not running. Either never started, or stopped by the user.</summary>
    Stopped,

    /// <summary>Encoder is being created (DLL checks, BASS_Encode_*_Start).</summary>
    Starting,

    /// <summary>Encoder is up, handshake with the cast server in progress.</summary>
    Connecting,

    /// <summary>On air.</summary>
    Streaming,

    /// <summary>Was streaming, the server or network dropped us. A retry is being scheduled.</summary>
    Disconnected,

    /// <summary>Waiting out the backoff delay before the next connection attempt.</summary>
    RetryWaiting,

    /// <summary>Unrecoverable — bad configuration, missing DLL, rejected credentials. No retry.</summary>
    FatalError
}

/// <summary>
/// Snapshot of a streaming session, safe to hand to the UI or API. Carries no secrets.
/// </summary>
public sealed record EncoderStatus(
    string ProfileId,
    string ProfileName,
    EncoderSessionState State,
    string? Error = null,
    DateTime? ConnectedAt = null,
    int RetryAttempt = 0,
    DateTime? NextRetryAt = null,
    /// <summary>Null when the server does not report it — display as "—", never as 0.</summary>
    int? ListenerCount = null);
