namespace RDM.Shared.DTOs;

/// <summary>
/// A streaming profile as returned to a client. Carries <b>no password</b> — not even a masked
/// one — and no indication of its length. <see cref="HasPassword"/> answers the only question the
/// UI actually needs: whether to show "set" or "not set" next to the field.
/// </summary>
public record EncoderProfileDto(
    string  ProfileId,
    string  Name,
    string  Format,
    int     BitrateKbps,
    int?    SampleRateHz,
    byte    Channels,
    string  ServerType,
    string  Host,
    int     Port,
    string? Mount,
    string? Username,
    string? StreamId,
    bool    HasPassword,
    bool    UseSsl,
    bool    UsePut,
    string? StreamName,
    string? Genre,
    string? StreamUrl,
    string? Description,
    bool    IsPublic,
    bool    Enabled,
    bool    Armed,
    bool    AutoStart,
    int     ReconnectDelaySeconds,
    string  TitleMode,
    string? TitleText);

/// <summary>
/// Create/replace payload. The password travels in plaintext over the request body and is encrypted
/// server-side before it ever reaches storage — the client must never see the encrypted blob, and
/// the server must never keep the plaintext.
/// </summary>
public record EncoderProfileCreateDto(
    string  Name,
    string  Format,
    int     BitrateKbps,
    string  ServerType,
    string  Host,
    int     Port,
    string? Mount         = null,
    string? Username      = null,
    string? StreamId      = null,
    string? Password      = null,
    int?    SampleRateHz  = null,
    byte    Channels      = 2,
    bool    UseSsl        = false,
    bool    UsePut        = false,
    string? StreamName    = null,
    string? Genre         = null,
    string? StreamUrl     = null,
    string? Description   = null,
    bool    IsPublic      = false,
    bool    Enabled       = true,
    bool    Armed         = false,
    bool    AutoStart     = false,
    int     ReconnectDelaySeconds = 10,
    string  TitleMode     = "NOW_PLAYING",
    string? TitleText     = null);

/// <summary>
/// Update payload. <see cref="Password"/> null means "leave the stored password alone" — the only
/// way to send "no password" is an empty string, so a client that omits the field cannot wipe a
/// working credential by accident.
/// </summary>
public record EncoderProfileUpdateDto(
    string  Name,
    string  Format,
    int     BitrateKbps,
    string  ServerType,
    string  Host,
    int     Port,
    string? Mount         = null,
    string? Username      = null,
    string? StreamId      = null,
    string? Password      = null,
    int?    SampleRateHz  = null,
    byte    Channels      = 2,
    bool    UseSsl        = false,
    bool    UsePut        = false,
    string? StreamName    = null,
    string? Genre         = null,
    string? StreamUrl     = null,
    string? Description   = null,
    bool    IsPublic      = false,
    bool    Enabled       = true,
    bool    Armed         = false,
    bool    AutoStart     = false,
    int     ReconnectDelaySeconds = 10,
    string  TitleMode     = "NOW_PLAYING",
    string? TitleText     = null);

/// <summary>Live state of one session. Mirrors RDM.Core.Models.EncoderStatus.</summary>
public record EncoderStatusDto(
    string    ProfileId,
    string    ProfileName,
    string    State,
    string?   Error,
    DateTime? ConnectedAt,
    int       RetryAttempt,
    DateTime? NextRetryAt,
    int?      ListenerCount);

public record EncoderProfileEnvelopeDto(IReadOnlyList<EncoderProfileDto> Profiles);

public record EncoderStatusEnvelopeDto(IReadOnlyList<EncoderStatusDto> Statuses);

public record EncoderProfileCreatedDto(string ProfileId);
