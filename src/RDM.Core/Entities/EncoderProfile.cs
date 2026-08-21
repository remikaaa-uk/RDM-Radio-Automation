using RDM.Shared.Enums;

namespace RDM.Core.Entities;

/// <summary>
/// One streaming target: what to encode and where to cast it. A collection entity (like Cartwall) —
/// several profiles can run at once, each its own encoder tapping the same program bus.
/// The server password is never held in plaintext; <see cref="PasswordEncrypted"/> is a DPAPI blob.
/// </summary>
public sealed class EncoderProfile
{
    public string ProfileId { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    public EncoderFormat Format { get; init; }
    public int BitrateKbps { get; init; }

    /// <summary>Null = follow the program bus rate (no resampling).</summary>
    public int? SampleRateHz { get; init; }

    /// <summary>1 = mono, 2 = stereo.</summary>
    public byte Channels { get; init; } = 2;

    public CastServerType ServerType { get; init; }
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }

    /// <summary>Icecast only; SHOUTcast has no mount point.</summary>
    public string? Mount { get; init; }

    /// <summary>
    /// Source account name. Icecast and SHOUTcast 2 accept one, sent as "username:password";
    /// SHOUTcast 1 has no concept of it. Null falls back to the server's own default.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// SHOUTcast 2 only: which stream within the server to feed, sent as "address:port,streamid".
    /// A v2 profile cannot connect without it.
    /// </summary>
    public string? StreamId { get; init; }

    /// <summary>DPAPI-protected server password. Never populated from user input directly.</summary>
    public byte[]? PasswordEncrypted { get; init; }

    public bool UseSsl { get; init; }

    /// <summary>
    /// Icecast only: connect with the HTTP PUT method (2.4 and later) instead of SOURCE (older).
    /// </summary>
    public bool UsePut { get; init; }

    // Directory metadata advertised to the cast server.
    public string? StreamName { get; init; }
    public string? Genre { get; init; }
    public string? StreamUrl { get; init; }
    public string? Description { get; init; }
    public bool IsPublic { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// The bottom-bar button starts this profile. Independent of <see cref="AutoStart"/>, which
    /// additionally starts it when the application launches — an auto-starting profile is always
    /// armed, but an armed one need not auto-start.
    /// </summary>
    public bool Armed { get; init; }

    public bool AutoStart { get; init; }

    /// <summary>
    /// Seconds to wait between reconnect attempts after the cast connection drops. A fixed interval,
    /// repeated indefinitely until the server accepts the source again — a nightly server restart
    /// must bring the stream back on its own, without an operator. Clamped to 2–1800 at the edges.
    /// </summary>
    public int ReconnectDelaySeconds { get; init; } = 10;

    /// <summary>What to push to the cast server as the stream title.</summary>
    public StreamTitleMode TitleMode { get; init; } = StreamTitleMode.NowPlaying;

    /// <summary>The fixed title used when <see cref="TitleMode"/> is Static.</summary>
    public string? TitleText { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
