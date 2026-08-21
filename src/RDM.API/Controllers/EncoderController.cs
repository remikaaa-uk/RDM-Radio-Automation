using Microsoft.AspNetCore.Mvc;
using RDM.API.Mappers;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.API.Controllers;

/// <summary>
/// Streaming profiles and their live sessions.
///
/// Split of authority, deliberately not uniform: <b>editing</b> a profile is Admin-only — it holds
/// server credentials and decides what the station puts on air — but <b>starting and stopping</b> a
/// stream is an operational act a presenter performs during a show, so any authenticated user may
/// do it. Same reasoning as ScheduledEventsController, applied to a resource that also has runtime
/// actions.
/// </summary>
[ApiController]
[Route("api/v1/encoder")]
public sealed class EncoderController : ControllerBase
{
    // Below 32 kbps nothing usable comes out; above 320 no cast server will thank you.
    private const int MinBitrate = 32;
    private const int MaxBitrate = 320;

    // Reconnect interval bounds. Below 2s would hammer a restarting server; above 30 min a stream
    // could sit off-air far longer than any real outage warrants.
    private const int MinReconnect = 2;
    private const int MaxReconnect = 1800;

    private readonly IEncoderProfileRepository _profiles;
    private readonly IAudioEngine              _audioEngine;
    private readonly ISecretProtector          _secrets;
    private readonly StudioContext             _studioContext;

    public EncoderController(
        IEncoderProfileRepository profiles,
        IAudioEngine              audioEngine,
        ISecretProtector          secrets,
        StudioContext             studioContext)
    {
        _profiles      = profiles;
        _audioEngine   = audioEngine;
        _secrets       = secrets;
        _studioContext = studioContext;
    }

    private UserRole CurrentRole => HttpContext.Items["Role"] as UserRole? ?? UserRole.Operator;

    private ActionResult Error(int status, string code, string message)
        => StatusCode(status, new ErrorResponseDto(code, message, HttpContext.TraceIdentifier));

    private ActionResult AdminOnly()
        => Error(403, "FORBIDDEN", "Only Administrators are allowed to manage streaming profiles.");

    // ── Profiles ─────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult<EncoderProfileEnvelopeDto>> GetAll()
    {
        var profiles = await _profiles.GetByStudioAsync(_studioContext.StudioId);
        return Ok(new EncoderProfileEnvelopeDto(profiles.Select(DtoMapper.ToDto).ToList()));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<EncoderProfileDto>> GetById(string id)
    {
        var profile = await _profiles.GetByIdAsync(id);
        if (profile is null)
            return Error(404, "PROFILE_NOT_FOUND", "Streaming profile with given ID does not exist.");

        return Ok(DtoMapper.ToDto(profile));
    }

    [HttpPost]
    public async Task<ActionResult<EncoderProfileCreatedDto>> Create([FromBody] EncoderProfileCreateDto dto)
    {
        if (CurrentRole != UserRole.Admin) return AdminOnly();
        if (dto is null) return Error(400, "BAD_REQUEST", "Request body is required.");

        if (Validate(dto.Name, dto.Format, dto.BitrateKbps, dto.ServerType, dto.Host, dto.Port,
                     dto.Mount, dto.Channels, dto.ReconnectDelaySeconds, dto.TitleMode, dto.TitleText,
                     out var format, out var serverType, out var titleMode, out var failure))
            return failure!;

        var profile = new EncoderProfile
        {
            ProfileId         = Guid.NewGuid().ToString(),
            StudioId          = _studioContext.StudioId,
            Name              = dto.Name.Trim(),
            Format            = format,
            BitrateKbps       = dto.BitrateKbps,
            SampleRateHz      = dto.SampleRateHz,
            Channels          = dto.Channels,
            ServerType        = serverType,
            Host              = dto.Host.Trim(),
            Port              = dto.Port,
            Mount             = dto.Mount,
            Username          = dto.Username,
            StreamId          = dto.StreamId,
            PasswordEncrypted = _secrets.Protect(dto.Password),
            UseSsl            = dto.UseSsl,
            UsePut            = dto.UsePut,
            StreamName        = dto.StreamName,
            Genre             = dto.Genre,
            StreamUrl         = dto.StreamUrl,
            Description       = dto.Description,
            IsPublic          = dto.IsPublic,
            Enabled           = dto.Enabled,
            Armed             = dto.Armed || dto.AutoStart,
            AutoStart         = dto.AutoStart,
            ReconnectDelaySeconds = dto.ReconnectDelaySeconds,
            TitleMode         = titleMode,
            TitleText         = dto.TitleText,
            CreatedAt         = DateTime.Now
        };

        await _profiles.CreateAsync(profile);
        return Ok(new EncoderProfileCreatedDto(profile.ProfileId));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<EncoderProfileDto>> Update(string id, [FromBody] EncoderProfileUpdateDto dto)
    {
        if (CurrentRole != UserRole.Admin) return AdminOnly();
        if (dto is null) return Error(400, "BAD_REQUEST", "Request body is required.");

        var existing = await _profiles.GetByIdAsync(id);
        if (existing is null)
            return Error(404, "PROFILE_NOT_FOUND", "Streaming profile with given ID does not exist.");

        if (Validate(dto.Name, dto.Format, dto.BitrateKbps, dto.ServerType, dto.Host, dto.Port,
                     dto.Mount, dto.Channels, dto.ReconnectDelaySeconds, dto.TitleMode, dto.TitleText,
                     out var format, out var serverType, out var titleMode, out var failure))
            return failure!;

        // A null password means "unchanged". Only an explicit empty string clears it, so a client
        // that simply does not resend the secret cannot silently destroy a working credential.
        var password = dto.Password is null
            ? existing.PasswordEncrypted
            : _secrets.Protect(dto.Password);

        var updated = new EncoderProfile
        {
            ProfileId         = existing.ProfileId,
            StudioId          = existing.StudioId,
            Name              = dto.Name.Trim(),
            Format            = format,
            BitrateKbps       = dto.BitrateKbps,
            SampleRateHz      = dto.SampleRateHz,
            Channels          = dto.Channels,
            ServerType        = serverType,
            Host              = dto.Host.Trim(),
            Port              = dto.Port,
            Mount             = dto.Mount,
            Username          = dto.Username,
            StreamId          = dto.StreamId,
            PasswordEncrypted = password,
            UseSsl            = dto.UseSsl,
            UsePut            = dto.UsePut,
            StreamName        = dto.StreamName,
            Genre             = dto.Genre,
            StreamUrl         = dto.StreamUrl,
            Description       = dto.Description,
            IsPublic          = dto.IsPublic,
            Enabled           = dto.Enabled,
            Armed             = dto.Armed || dto.AutoStart,
            AutoStart         = dto.AutoStart,
            ReconnectDelaySeconds = dto.ReconnectDelaySeconds,
            TitleMode         = titleMode,
            TitleText         = dto.TitleText,
            CreatedAt         = existing.CreatedAt,
            UpdatedAt         = DateTime.Now
        };

        await _profiles.UpdateAsync(updated);
        return Ok(DtoMapper.ToDto(updated));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id)
    {
        if (CurrentRole != UserRole.Admin) return AdminOnly();

        var existing = await _profiles.GetByIdAsync(id);
        if (existing is null)
            return Error(404, "PROFILE_NOT_FOUND", "Streaming profile with given ID does not exist.");

        // Stopped first: deleting the row while the session streams would leave an encoder on air
        // that no longer has a profile to stop it with.
        await _audioEngine.StopEncoderAsync(id);
        await _profiles.DeleteAsync(id);
        return NoContent();
    }

    // ── Sessions ─────────────────────────────────────────────────────────────

    [HttpPost("{id}/start")]
    public async Task<ActionResult<EncoderStatusDto>> Start(string id)
    {
        var profile = await _profiles.GetByIdAsync(id);
        if (profile is null)
            return Error(404, "PROFILE_NOT_FOUND", "Streaming profile with given ID does not exist.");

        if (!profile.Enabled)
            return Error(422, "PROFILE_DISABLED", $"Profile '{profile.Name}' is disabled.");

        if (!_audioEngine.IsEncoderFormatAvailable(profile.Format))
            return Error(422, "FORMAT_UNAVAILABLE",
                $"{profile.Format} encoding is not available in this installation (missing BASSenc add-on).");

        string? password;
        try
        {
            password = _secrets.Unprotect(profile.PasswordEncrypted);
        }
        catch (Exception)
        {
            // DPAPI blobs are machine-bound: a profile copied from another machine decrypts to
            // nothing. Saying so beats an authentication failure the operator cannot explain.
            return Error(422, "PASSWORD_UNREADABLE",
                "The stored password could not be decrypted on this machine. Re-enter it in the profile.");
        }

        await _audioEngine.StartEncoderAsync(profile, password);

        var status = _audioEngine.GetEncoderStatus(id);
        return status is null
            ? Error(500, "ENCODER_NO_STATUS", "The encoder was started but reported no status.")
            : Ok(DtoMapper.ToDto(status));
    }

    [HttpPost("{id}/stop")]
    public async Task<ActionResult<EncoderStatusDto>> Stop(string id)
    {
        await _audioEngine.StopEncoderAsync(id);

        var status = _audioEngine.GetEncoderStatus(id);
        return status is null
            ? Ok(new EncoderStatusDto(id, string.Empty, "STOPPED", null, null, 0, null, null))
            : Ok(DtoMapper.ToDto(status));
    }

    /// <summary>
    /// Starts every profile flagged ready (<c>auto_start</c>). This is what the bottom-bar button
    /// calls: one request, so the UI cannot get half-way through a loop and leave the station in a
    /// state nobody asked for. A profile that cannot start is skipped and reported in the result,
    /// never allowed to abort the ones after it.
    /// </summary>
    [HttpPost("start-armed")]
    public async Task<ActionResult<EncoderStatusEnvelopeDto>> StartArmed()
    {
        var profiles = await _profiles.GetArmedAsync(_studioContext.StudioId);

        foreach (var profile in profiles)
        {
            if (!_audioEngine.IsEncoderFormatAvailable(profile.Format)) continue;

            string? password;
            try   { password = _secrets.Unprotect(profile.PasswordEncrypted); }
            catch { continue; }   // machine-bound blob — reported through the status below

            try   { await _audioEngine.StartEncoderAsync(profile, password); }
            catch { /* the session records its own failure; the next profile still gets its turn */ }
        }

        return Ok(new EncoderStatusEnvelopeDto(
            _audioEngine.GetAllEncoderStatuses().Select(DtoMapper.ToDto).ToList()));
    }

    /// <summary>Stops every running session. The other half of the bottom-bar toggle.</summary>
    [HttpPost("stop-all")]
    public async Task<ActionResult<EncoderStatusEnvelopeDto>> StopAll()
    {
        foreach (var status in _audioEngine.GetAllEncoderStatuses().ToList())
            await _audioEngine.StopEncoderAsync(status.ProfileId);

        return Ok(new EncoderStatusEnvelopeDto(
            _audioEngine.GetAllEncoderStatuses().Select(DtoMapper.ToDto).ToList()));
    }

    [HttpGet("status")]
    public ActionResult<EncoderStatusEnvelopeDto> GetAllStatuses()
        => Ok(new EncoderStatusEnvelopeDto(
            _audioEngine.GetAllEncoderStatuses().Select(DtoMapper.ToDto).ToList()));

    [HttpGet("{id}/status")]
    public ActionResult<EncoderStatusDto> GetStatus(string id)
    {
        var status = _audioEngine.GetEncoderStatus(id);
        return status is null
            ? Error(404, "SESSION_NOT_FOUND", "That profile has no session — it was never started.")
            : Ok(DtoMapper.ToDto(status));
    }

    // ── Validation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the payload is <b>invalid</b>, with <paramref name="failure"/> set to the
    /// response to return. Shared by create and update so the two cannot drift apart — a profile
    /// that could not be created must not become reachable by editing another one into it.
    /// </summary>
    private bool Validate(
        string  name,
        string  formatText,
        int     bitrate,
        string  serverTypeText,
        string  host,
        int     port,
        string? mount,
        byte    channels,
        int     reconnectDelaySeconds,
        string  titleModeText,
        string? titleText,
        out EncoderFormat   format,
        out CastServerType  serverType,
        out StreamTitleMode titleMode,
        out ActionResult?   failure)
    {
        format     = default;
        serverType = default;
        titleMode  = StreamTitleMode.NowPlaying;
        failure    = null;

        if (string.IsNullOrWhiteSpace(name))
            failure = Error(400, "BAD_REQUEST", "Profile name is required.");

        else if (!Enum.TryParse(formatText, ignoreCase: true, out format))
            failure = Error(400, "BAD_REQUEST",
                $"Unknown format '{formatText}'. Supported: {string.Join(", ", Enum.GetNames<EncoderFormat>())}.");

        else if (!_audioEngine.IsEncoderFormatAvailable(format))
            failure = Error(422, "FORMAT_UNAVAILABLE",
                $"{format} encoding is not available in this installation (missing BASSenc add-on).");

        else if (bitrate is < MinBitrate or > MaxBitrate)
            failure = Error(400, "BAD_REQUEST", $"Bitrate must be between {MinBitrate} and {MaxBitrate} kbps.");

        else if (channels is not (1 or 2))
            failure = Error(400, "BAD_REQUEST", "Channels must be 1 (mono) or 2 (stereo).");

        else if (!TryParseServerType(serverTypeText, out serverType))
            failure = Error(400, "BAD_REQUEST",
                $"Unknown server type '{serverTypeText}'. Supported: SHOUTCAST, SHOUTCAST2, ICECAST.");

        else if (string.IsNullOrWhiteSpace(host))
            failure = Error(400, "BAD_REQUEST", "Host is required.");

        else if (port is < 1 or > 65535)
            failure = Error(400, "BAD_REQUEST", "Port must be between 1 and 65535.");

        else if (reconnectDelaySeconds is < MinReconnect or > MaxReconnect)
            failure = Error(400, "BAD_REQUEST",
                $"Reconnect delay must be between {MinReconnect} and {MaxReconnect} seconds.");

        // Icecast addresses a mount point; SHOUTcast has no concept of one. Requiring it only for
        // Icecast keeps a SHOUTcast profile from carrying a field the server will never read.
        else if (serverType == CastServerType.Icecast && string.IsNullOrWhiteSpace(mount))
            failure = Error(400, "BAD_REQUEST", "Icecast profiles require a mount point (e.g. /live).");

        else if (!TryParseTitleMode(titleModeText, out titleMode))
            failure = Error(400, "BAD_REQUEST",
                $"Unknown title mode '{titleModeText}'. Supported: NOW_PLAYING, STATIC, NONE.");

        // A static title with nothing in it would put an empty string on air where the operator
        // expected a station name — silently emitting nothing is worse than refusing to save.
        else if (titleMode == StreamTitleMode.Static && string.IsNullOrWhiteSpace(titleText))
            failure = Error(400, "BAD_REQUEST", "A static stream title needs some text.");

        return failure is not null;
    }

    /// <summary>
    /// Parsed explicitly rather than through Enum.TryParse: the wire name is SHOUTCAST2 while the
    /// member is ShoutcastV2, so name-based parsing silently rejects a perfectly valid request.
    /// The other values only ever worked by coincidence of spelling.
    /// </summary>
    private static bool TryParseServerType(string? text, out CastServerType type)
    {
        switch (text?.Trim().ToUpperInvariant())
        {
            case "SHOUTCAST":  type = CastServerType.Shoutcast;   return true;
            case "SHOUTCAST2": type = CastServerType.ShoutcastV2; return true;
            case "ICECAST":    type = CastServerType.Icecast;     return true;
            default:           type = CastServerType.Icecast;     return false;
        }
    }

    private static bool TryParseTitleMode(string? text, out StreamTitleMode mode)
    {
        mode = StreamTitleMode.NowPlaying;
        return text?.Trim().ToUpperInvariant() switch
        {
            null or "" or "NOW_PLAYING" => true,
            "STATIC" => (mode = StreamTitleMode.Static) == mode,
            "NONE"   => (mode = StreamTitleMode.None) == mode,
            _        => false
        };
    }
}
