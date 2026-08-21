using System;
using System.Collections.Generic;
using System.Text.Json;
using RDM.API.Services;
using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;

namespace RDM.API.Mappers;

public static class DtoMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static AssetDto ToDto(Asset asset)
    {
        return new AssetDto(
            AssetId:         asset.AssetId,
            AssetType:       asset.AssetType.ToString(),
            FormatName:      asset.FormatName,
            SubcategoryName: asset.SubcategoryName,
            Title:           asset.Title,
            Artist:        asset.Artist,
            Album:         asset.Album,
            DurationMs:    asset.DurationMs,
            Bpm:           asset.Bpm,
            Year:          asset.Year,
            Rating:        asset.Rating,
            Status:        MapStatus(asset.Status),
            LoudnessLufs:  asset.LoudnessLufs,
            Mood:          asset.Mood,
            Gender:        asset.Gender,
            Language:      asset.Language,
            Genre:         asset.Genre,
            PlayCount:     asset.PlayCount,
            CreatedAt:     asset.CreatedAt,
            CueMarkers:    BuildCueMarkers(asset)
        );
    }

    public static AssetDetailDto ToDetailDto(Asset asset)
    {
        return new AssetDetailDto(
            AssetId:        asset.AssetId,
            AssetType:      asset.AssetType.ToString(),
            FormatId:       asset.FormatId,
            SubcategoryId:  asset.SubcategoryId,
            Title:          asset.Title,
            Artist:         asset.Artist,
            Album:          asset.Album,
            DurationMs:     asset.DurationMs,
            RdmFilePath:    asset.RdmFilePath,
            StreamUrl:      asset.StreamUrl,
            ImagePath:      asset.ImagePath,
            Bpm:            asset.Bpm,
            Year:           asset.Year,
            Rating:         asset.Rating,
            Mood:           asset.Mood,
            Gender:         asset.Gender,
            Language:       asset.Language,
            Genre:          asset.Genre,
            Comments:       asset.Comments,
            IsDamaged:      asset.IsDamaged,
            IsVariableDuration: asset.IsVariableDuration,
            Status:         MapStatus(asset.Status),
            StartDate:      asset.StartDate,
            EndDate:        asset.EndDate,
            PlayLimit:      asset.PlayLimit,
            PlayCount:      asset.PlayCount,
            LastPlayedAt:   asset.LastPlayedAt,
            LoudnessLufs:   asset.LoudnessLufs,
            LoudnessPeak:   asset.LoudnessPeak,
            CueMarkers:     BuildCueMarkers(asset)
        );
    }

    public static CueMarkersDto? BuildCueMarkers(Asset asset)
    {
        if (asset.CueStart    is null && asset.CueIntro    is null &&
            asset.CueRamp2    is null && asset.CueRamp3    is null &&
            asset.CueOutro    is null && asset.CueStartNext is null &&
            asset.CueFadeOut  is null && asset.CueFadeEnd  is null &&
            asset.CueEnd      is null && asset.CueHookIn   is null &&
            asset.CueHookFade is null && asset.CueHookOut  is null &&
            asset.CueLoopIn   is null && asset.CueLoopOut  is null &&
            asset.CueAnchor   is null)
            return null;

        return new CueMarkersDto(
            Start:     asset.CueStart,
            Intro:     asset.CueIntro,
            Ramp2:     asset.CueRamp2,
            Ramp3:     asset.CueRamp3,
            Outro:     asset.CueOutro,
            StartNext: asset.CueStartNext,
            FadeOut:   asset.CueFadeOut,
            FadeEnd:   asset.CueFadeEnd,
            End:       asset.CueEnd,
            HookIn:    asset.CueHookIn,
            HookFade:  asset.CueHookFade,
            HookOut:   asset.CueHookOut,
            LoopIn:    asset.CueLoopIn,
            LoopOut:   asset.CueLoopOut,
            Anchor:    asset.CueAnchor
        );
    }

    public static ScheduledEventDto ToDto(ScheduledEvent ev)
    {
        var daysList = string.IsNullOrWhiteSpace(ev.Days)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : ev.Days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var hoursList = string.IsNullOrWhiteSpace(ev.Hours)
            ? (IReadOnlyList<int>)Array.Empty<int>()
            : ev.Hours.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(h => int.TryParse(h, out var parsed) ? parsed : -1)
                      .Where(h => h >= 0)
                      .ToList();

        IReadOnlyList<ScheduledEventActionDto> actionsList = Array.Empty<ScheduledEventActionDto>();
        if (!string.IsNullOrWhiteSpace(ev.Actions))
        {
            try
            {
                actionsList = JsonSerializer.Deserialize<List<ScheduledEventActionDto>>(ev.Actions, JsonOptions)
                    ?? new List<ScheduledEventActionDto>();
            }
            catch
            {
                // Fallback in case of malformed database values
            }
        }

        return new ScheduledEventDto(
            EventId: ev.EventId,
            Name: ev.Name,
            EventType: ev.EventType.ToString(),
            Category: ev.Category,
            Enabled: ev.Enabled,
            EventHour: ev.EventHour?.ToString(@"hh\:mm\:ss"),
            Days: daysList,
            Hours: hoursList,
            SmartTiming: ev.SmartTiming,
            Actions: actionsList,
            LastFiredAt: ev.LastFiredAt,
            SkipNext: ev.SkipNext,
            OnlyOnDate: ev.OnlyOnDate?.ToString("yyyy-MM-dd")
        );
    }

    public static NowPlayingDto ToDto(NowPlayingInfo info, Asset? nextAsset)
    {
        CurrentTrackDto? currentTrack = null;
        if (info.CurrentAsset is not null)
        {
            var outroMs = info.OutroCueMs ?? info.CurrentAsset.DurationMs;
            var remainingMs = outroMs > info.PositionMs ? outroMs - info.PositionMs : 0;

            currentTrack = new CurrentTrackDto(
                AssetId:    info.CurrentAsset.AssetId,
                Title:      info.CurrentAsset.Title,
                Artist:     info.CurrentAsset.Artist,
                DurationMs: info.CurrentAsset.DurationMs,
                PositionMs: info.PositionMs,
                RemainingMs: remainingMs,
                StartedAt:  info.TrackStartedAt,
                CueMarkers: BuildCueMarkers(info.CurrentAsset)
            );
        }

        NextTrackDto? nextTrack = null;
        if (nextAsset is not null)
        {
            DateTime? scheduledAt = null;
            if (info.TrackStartedAt.HasValue && info.CurrentAsset is not null)
            {
                var outroMs = info.OutroCueMs ?? info.CurrentAsset.DurationMs;
                scheduledAt = info.TrackStartedAt.Value.AddMilliseconds(outroMs);
            }

            nextTrack = new NextTrackDto(
                AssetId: nextAsset.AssetId,
                Title: nextAsset.Title,
                Artist: nextAsset.Artist,
                DurationMs: nextAsset.DurationMs,
                ScheduledAt: scheduledAt
            );
        }

        return new NowPlayingDto(
            NowPlaying: currentTrack,
            NextTrack: nextTrack,
            PlaylistMode: MapMode(info.Mode),
            State: MapState(info.State),
            LoopCurrent: info.LoopCurrent
        );
    }

    public static NextTrackDto ToNextTrackDto(Asset nextAsset, DateTime? scheduledAt)
    {
        return new NextTrackDto(
            AssetId: nextAsset.AssetId,
            Title: nextAsset.Title,
            Artist: nextAsset.Artist,
            DurationMs: nextAsset.DurationMs,
            ScheduledAt: scheduledAt
        );
    }

    public static PlayoutLogDto ToDto(PlayoutLog log)
    {
        return new PlayoutLogDto(
            AssetId: log.AssetId,
            Title: log.TempTitle,
            Artist: log.TempArtist,
            StartedAt: log.StartedAt,
            EndedAt: log.EndedAt,
            SourceType: log.SourceType.ToString().ToUpperInvariant()
        );
    }

    private static string MapStatus(AssetStatus status) => status switch
    {
        AssetStatus.PendingReview => "PENDING_REVIEW",
        AssetStatus.Active        => "ACTIVE",
        AssetStatus.Disabled      => "DISABLED",
        _                         => status.ToString().ToUpperInvariant()
    };

    private static string MapMode(PlaylistMode mode) => mode switch
    {
        PlaylistMode.Auto => "AUTO",
        PlaylistMode.LiveAssist => "LIVE_ASSIST",
        PlaylistMode.Manual => "MANUAL",
        _ => mode.ToString().ToUpperInvariant()
    };

    private static string MapState(SessionState state) => state switch
    {
        SessionState.Idle => "IDLE",
        SessionState.Playing => "PLAYING",
        SessionState.Paused => "PAUSED",
        _ => state.ToString().ToUpperInvariant()
    };

    public static ImportStatusDto ToDto(ImportJobStatus status)
    {
        return new ImportStatusDto(
            ImportId: status.ImportId,
            Status: status.Status,
            AssetId: status.AssetId,
            Title: status.Title,
            Artist: status.Artist,
            CompletedAt: status.CompletedAt
        );
    }

    /// <summary>
    /// Maps a streaming profile for the wire. The password is not merely omitted — the DTO has no
    /// field able to carry it, so no future edit here can leak one by adding a line.
    /// </summary>
    public static EncoderProfileDto ToDto(EncoderProfile p) => new(
        ProfileId:    p.ProfileId,
        Name:         p.Name,
        Format:       p.Format.ToString().ToUpperInvariant(),
        BitrateKbps:  p.BitrateKbps,
        SampleRateHz: p.SampleRateHz,
        Channels:     p.Channels,
        ServerType:   p.ServerType switch
                      {
                          CastServerType.Shoutcast   => "SHOUTCAST",
                          CastServerType.ShoutcastV2 => "SHOUTCAST2",
                          _                          => "ICECAST"
                      },
        Host:         p.Host,
        Port:         p.Port,
        Mount:        p.Mount,
        Username:     p.Username,
        StreamId:     p.StreamId,
        HasPassword:  p.PasswordEncrypted is { Length: > 0 },
        UseSsl:       p.UseSsl,
        UsePut:       p.UsePut,
        StreamName:   p.StreamName,
        Genre:        p.Genre,
        StreamUrl:    p.StreamUrl,
        Description:  p.Description,
        IsPublic:     p.IsPublic,
        Enabled:      p.Enabled,
        Armed:        p.Armed,
        AutoStart:    p.AutoStart,
        ReconnectDelaySeconds: p.ReconnectDelaySeconds,
        TitleMode:    p.TitleMode switch
                      {
                          StreamTitleMode.NowPlaying => "NOW_PLAYING",
                          StreamTitleMode.Static     => "STATIC",
                          _                          => "NONE"
                      },
        TitleText:    p.TitleText);

    public static EncoderStatusDto ToDto(EncoderStatus s) => new(
        ProfileId:     s.ProfileId,
        ProfileName:   s.ProfileName,
        State:         s.State.ToString().ToUpperInvariant(),
        Error:         s.Error,
        ConnectedAt:   s.ConnectedAt,
        RetryAttempt:  s.RetryAttempt,
        NextRetryAt:   s.NextRetryAt,
        ListenerCount: s.ListenerCount);

    public static RecordingStatusDto ToDto(RecordingStatus s) => new(
        State:        s.State.ToString().ToUpperInvariant(),
        FilePath:     s.FilePath,
        StartedAt:    s.StartedAt,
        Error:        s.Error,
        BytesWritten: s.BytesWritten);
}
