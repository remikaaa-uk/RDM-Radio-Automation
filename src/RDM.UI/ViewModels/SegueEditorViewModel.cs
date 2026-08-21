using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

// ── Undo snapshot ───────────────────────────────────────────────────────────────

/// Editable segue parameters of a single clip, captured for undo/redo.
public record ClipSnapshot(string ItemId, uint CrossfadeMs, int LeadInMs, uint TrimStartMs, uint TrimEndMs, IReadOnlyList<EnvelopePoint> Envelope);

/// Full editor state — every clip's segue parameters.
public record SegueSnapshot(IReadOnlyList<ClipSnapshot> Clips);

// ── Per-clip view model ─────────────────────────────────────────────────────────

/// One track on the multi-track timeline. <see cref="CrossfadeMs"/> is the overlap
/// with the *previous* clip; <see cref="AbsoluteStartMs"/> is computed from the chain.
public sealed partial class SegueClipViewModel : ObservableObject
{
    public string  ItemId     { get; init; } = "";
    public uint    Position   { get; init; }
    public string  Title      { get; init; } = "";
    public string  Artist     { get; init; } = "";

    // Mutable so a voice-track clip can be finalized in place after recording
    // (asset id / file path / duration assigned once the import completes).
    [ObservableProperty] private string? _assetId;
    [ObservableProperty] private string? _filePath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveEndMs))]
    [NotifyPropertyChangedFor(nameof(EffectiveDurationMs))]
    private uint _durationMs;

    [ObservableProperty] private float[]?       _peaks;
    /// Sample-accurate slice decoded on demand when zoomed in past the coarse peaks.
    [ObservableProperty] private WaveformWindow? _detail;
    [ObservableProperty] private CueMarkersDto? _markers;
    [ObservableProperty] private uint           _crossfadeMs;
    /// Free-positioning offset (signed ms) relative to the previous clip's end —
    /// the layout source of truth. Unlike <see cref="CrossfadeMs"/> it is not bounded
    /// by the previous clip's duration, so a clip can slide past a short neighbour.
    [ObservableProperty] private int            _leadInMs;
    [ObservableProperty] private uint           _trimStartMs;
    [ObservableProperty] private uint           _trimEndMs;
    [ObservableProperty] private double         _absoluteStartMs;
    [ObservableProperty] private ObservableCollection<EnvelopePoint> _volumeEnvelope = new();

    // ── Voice-track recording ───────────────────────────────────────────────────
    /// True while this clip is being recorded live (microphone capture in progress).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveEndMs))]
    [NotifyPropertyChangedFor(nameof(EffectiveDurationMs))]
    private bool _isRecording;

    /// Live length of the in-progress recording; drives the clip's growth on the timeline.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveEndMs))]
    [NotifyPropertyChangedFor(nameof(EffectiveDurationMs))]
    private uint _recordingDurationMs;

    /// Peak levels (0..1) sampled once per recording tick, spread evenly across the
    /// recorded duration. Drawn as a live waveform so the speaker can see where speech
    /// started and how far to drag the clip on the timeline. Mutated and read on the UI
    /// thread only (DispatcherTimer tick + Render), so no synchronization is required.
    public List<float> RecordingPeaks { get; } = new();

    /// True for clips inserted by the VT recorder (not yet persisted to the playlist).
    public bool IsNew { get; set; }

    public string Label =>
        string.IsNullOrEmpty(Artist) ? Title : $"{Artist}  —  {Title}";

    /// End of the audible region within the source (trim end, or full duration).
    /// While recording, the live recorded length is the source of truth.
    public uint EffectiveEndMs =>
        IsRecording ? RecordingDurationMs
        : TrimEndMs > 0 ? TrimEndMs : DurationMs;

    /// Audible length after trimming.
    public uint EffectiveDurationMs =>
        EffectiveEndMs > TrimStartMs ? EffectiveEndMs - TrimStartMs : 0;
}

// ── Editor view model ───────────────────────────────────────────────────────────

public sealed partial class SegueEditorViewModel : ObservableObject, IHasUnsavedChanges
{
    private readonly ApiClientService              _api;
    private readonly IAudioEngine                  _audioEngine;
    private readonly IConfiguration                _config;
    private readonly IImportPipeline               _importPipeline;
    private readonly ILogger<SegueEditorViewModel> _logger;
    private readonly UndoRedoStack<SegueSnapshot>  _undoStack = new();
    private readonly DispatcherTimer               _previewTimer;
    private DateTime                               _lastTick;

    /// Which clip index is currently loaded on each PFL preview lane (-1 = idle).
    /// Lane = clipIndex % 2, so adjacent (overlapping) clips never share a lane.
    private readonly int[]                         _laneClipIndex = { -1, -1 };

    /// Per-clip cancellation for in-flight detail-waveform decodes, so a newer zoom
    /// request supersedes an older one instead of racing it.
    private readonly Dictionary<int, CancellationTokenSource> _detailCts = new();

    /// All tracks shown in the editor, ordered by playlist position. Source of truth.
    public ObservableCollection<SegueClipViewModel> Clips { get; } = new();

    /// Index of the outgoing clip in the transition currently driven by the sliders.
    private int _activePairIndex;

    // ── Active transition pair (legacy 2-track projection) ──────────────────────

    private SegueClipViewModel? OutgoingClip =>
        _activePairIndex >= 0 && _activePairIndex < Clips.Count ? Clips[_activePairIndex] : null;

    private SegueClipViewModel? IncomingClip =>
        _activePairIndex + 1 < Clips.Count ? Clips[_activePairIndex + 1] : null;

    public string         Track1Artist     => OutgoingClip?.Artist     ?? "";
    public string         Track1Title      => OutgoingClip?.Title      ?? "";
    public string         Track1Label      => OutgoingClip?.Label      ?? "";
    public float[]?       Track1Peaks      => OutgoingClip?.Peaks;
    public uint           Track1DurationMs => OutgoingClip?.DurationMs ?? 1;
    public CueMarkersDto? Track1Markers    => OutgoingClip?.Markers;

    public string         Track2Artist     => IncomingClip?.Artist     ?? "";
    public string         Track2Title      => IncomingClip?.Title      ?? "";
    public string         Track2Label      => IncomingClip?.Label      ?? "";
    public float[]?       Track2Peaks      => IncomingClip?.Peaks;
    public uint           Track2DurationMs => IncomingClip?.DurationMs ?? 1;
    public CueMarkersDto? Track2Markers    => IncomingClip?.Markers;

    /// Crossfade (overlap) of the active incoming clip with its predecessor.
    public uint CrossfadeMs
    {
        get => IncomingClip?.CrossfadeMs ?? 0;
        set
        {
            var clip = IncomingClip;
            if (clip is null || clip.CrossfadeMs == value) return;
            _undoStack.Push(Capture());
            clip.CrossfadeMs = value;
            // The slider sets the musical overlap; re-anchor the free offset to it so the
            // clip's position matches the chosen crossfade (drag handles free placement).
            clip.LeadInMs = (int)value;
            RecomputeLayout();
            HasUnsavedChanges = true;
            NotifyUndoRedo();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CrossfadeText));
        }
    }

    /// Trim-end of the active outgoing clip.
    public uint TrimEndMs
    {
        get => OutgoingClip?.TrimEndMs ?? 0;
        set
        {
            var clip = OutgoingClip;
            if (clip is null || clip.TrimEndMs == value) return;
            _undoStack.Push(Capture());
            clip.TrimEndMs = value;
            RecomputeLayout();
            HasUnsavedChanges = true;
            NotifyUndoRedo();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrimEndText));
        }
    }

    public string CrossfadeText => $"{CrossfadeMs} ms";
    public string TrimEndText   => TrimEndMs > 0 ? $"{TrimEndMs} ms" : "bez trymowania";

    // ── Timeline focus ──────────────────────────────────────────────────────────

    [ObservableProperty] private double _timelineFocusMs = double.NaN;

    // ── PFL preview / playhead ────────────────────────────────────────────────────

    /// Absolute mix position of the playhead, in milliseconds.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPositionText))]
    private double _previewPositionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewPlayPauseIcon))]
    private bool _isPreviewPlaying;

    /// Total length of the assembled mix (end of the last clip).
    public double PreviewTotalMs =>
        Clips.Count == 0 ? 0 : Clips.Max(c => c.AbsoluteStartMs + c.EffectiveDurationMs);

    public string PreviewPositionText => FormatClock(PreviewPositionMs);
    public string PreviewTotalText    => FormatClock(PreviewTotalMs);
    public string PreviewPlayPauseIcon => IsPreviewPlaying ? "⏸" : "▶";

    private static string FormatClock(double ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        var now     = DateTime.UtcNow;
        var elapsed = (now - _lastTick).TotalMilliseconds;
        _lastTick   = now;

        // Prefer the real BASS sample clock (no drift); fall back to the wall-clock estimate only
        // when nothing is audible (a gap between clips, or no audio device).
        var hwPos = ReadPreviewPositionFromBass();
        PreviewPositionMs = hwPos ?? PreviewPositionMs + elapsed;

        if (PreviewPositionMs >= PreviewTotalMs)
        {
            PreviewPositionMs = PreviewTotalMs;
            StopPreview();
            return;
        }

        UpdatePreviewAudio();
    }

    /// Maps the real playback position of an active preview lane back to absolute mix time,
    /// so the playhead tracks the BASS clock exactly. Returns null when no lane is audible.
    private double? ReadPreviewPositionFromBass()
    {
        if (!_audioEngine.IsInitialized) return null;

        for (int lane = 0; lane < _laneClipIndex.Length; lane++)
        {
            int clipIdx = _laneClipIndex[lane];
            if (clipIdx < 0 || clipIdx >= Clips.Count) continue;

            int filePosMs = _audioEngine.GetPreviewPositionMs(lane);
            if (filePosMs < 0) continue;

            var clip = Clips[clipIdx];
            return clip.AbsoluteStartMs + (filePosMs - clip.TrimStartMs);
        }
        return null;
    }

    [RelayCommand]
    private void PlayPausePreview()
    {
        if (IsPreviewPlaying) { PausePreview(); return; }

        // Restart from the active transition once the playhead has run off the end.
        bool rewound = PreviewPositionMs >= PreviewTotalMs - 1;
        if (rewound)
        {
            PreviewPositionMs = ActiveTransitionMs();
            ResetPreviewLanes(); // position jumped — reload lanes from here
        }

        _lastTick        = DateTime.UtcNow;
        IsPreviewPlaying = true;

        // Resume the lanes we already hold (seamless, BASS position preserved); only (re)load
        // when there is nothing paused to resume.
        bool resumed = false;
        if (_audioEngine.IsInitialized)
        {
            for (int lane = 0; lane < _laneClipIndex.Length; lane++)
            {
                if (_laneClipIndex[lane] < 0) continue;
                try { _ = _audioEngine.PreviewResumeAsync(lane); resumed = true; }
                catch (Exception ex) { _logger.LogDebug(ex, "PreviewResume failed"); }
            }
        }

        _previewTimer.Start();
        if (!resumed) ResetPreviewLanes();
        UpdatePreviewAudio();
    }

    /// Pauses the playhead and the PFL lanes in place (streams kept) so Play resumes seamlessly.
    private void PausePreview()
    {
        _previewTimer.Stop();
        IsPreviewPlaying = false;
        if (!_audioEngine.IsInitialized) return;

        for (int lane = 0; lane < _laneClipIndex.Length; lane++)
        {
            if (_laneClipIndex[lane] < 0) continue;
            try { _ = _audioEngine.PreviewPauseAsync(lane); }
            catch (Exception ex) { _logger.LogDebug(ex, "PreviewPause failed"); }
        }
    }

    [RelayCommand]
    private void SkipToStart()
    {
        var wasPlaying = IsPreviewPlaying;
        StopPreview();
        PreviewPositionMs = 0;
        if (wasPlaying) PlayPausePreview();
    }

    [RelayCommand]
    private void SkipToEnd()
    {
        StopPreview();
        PreviewPositionMs = PreviewTotalMs;
    }

    /// Moves the playhead to an absolute mix position (e.g. from a timeline click).
    public void SeekPreview(double absMs)
    {
        PreviewPositionMs = Math.Clamp(absMs, 0, PreviewTotalMs);
        if (IsPreviewPlaying)
        {
            _lastTick = DateTime.UtcNow;
            // Reposition the lanes to the new spot. Lanes still on the same clip are seeked in
            // place (no glitchy free/recreate); lanes whose clip changed are reloaded.
            UpdatePreviewAudio(seeking: true);
        }
    }

    /// Stops the playhead clock and any PFL audio. Safe to call repeatedly.
    public void StopPreview()
    {
        _previewTimer.Stop();
        IsPreviewPlaying = false;
        ResetPreviewLanes();
    }

    /// Default playhead anchor — the active transition point.
    private double ActiveTransitionMs() =>
        OutgoingClip is { } c ? c.AbsoluteStartMs + c.EffectiveEndMs - c.TrimStartMs : 0;

    // ── Dual-lane preview scheduler ───────────────────────────────────────────────

    /// Frees both preview lanes and marks them idle. Safe without an audio device.
    private void ResetPreviewLanes()
    {
        _laneClipIndex[0] = -1;
        _laneClipIndex[1] = -1;
        if (_audioEngine.IsInitialized)
        {
            try { _ = _audioEngine.PreviewStopAllAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "PreviewStopAll failed"); }
        }
    }

    /// Drives the (up to two) audible clips at the current playhead position:
    /// starts/stops lane streams as clips enter/leave and applies the crossfade
    /// gain envelope so overlaps are heard as a real mix. Visual-only without audio.
    private void UpdatePreviewAudio(bool seeking = false)
    {
        if (!_audioEngine.IsInitialized) return;

        var pos = PreviewPositionMs;
        var wantClip = new[] { -1, -1 };
        var wantGain = new[] { 0f, 0f };

        for (int i = 0; i < Clips.Count; i++)
        {
            var c     = Clips[i];
            var start = c.AbsoluteStartMs;
            var end   = c.AbsoluteStartMs + c.EffectiveDurationMs;
            if (pos < start || pos >= end) continue;

            var lane = i % 2;          // adjacent clips differ in parity → distinct lanes
            wantClip[lane] = i;
            wantGain[lane] = ClipGainAt(i, pos);
        }

        for (int lane = 0; lane < _laneClipIndex.Length; lane++)
        {
            var want = wantClip[lane];
            var clip = want >= 0 ? Clips[want] : null;
            var playable = clip is { AssetId: not null } && !string.IsNullOrEmpty(clip.FilePath);

            if (!playable)
            {
                if (_laneClipIndex[lane] != -1)
                {
                    _ = _audioEngine.PreviewStopAsync(lane);
                    _laneClipIndex[lane] = -1;
                }
                continue;
            }

            if (_laneClipIndex[lane] != want)
            {
                var offsetMs = (int)(clip!.TrimStartMs + (pos - clip.AbsoluteStartMs));
                _ = _audioEngine.PreviewPlayAsync(lane, clip.FilePath!, offsetMs, wantGain[lane]);
                _laneClipIndex[lane] = want;
            }
            else
            {
                if (seeking)
                {
                    // Jump the still-loaded lane to the new mix position (absolute file seek).
                    var offsetMs = (int)(clip!.TrimStartMs + (pos - clip.AbsoluteStartMs));
                    _ = _audioEngine.PreviewSeekAsync(lane, offsetMs);
                }
                _ = _audioEngine.PreviewSetGainAsync(lane, wantGain[lane]);
            }
        }
    }

    /// Linear crossfade gain [0..1] for clip <paramref name="i"/> at mix position
    /// <paramref name="pos"/>: fades in over its overlap with the previous clip and
    /// out over the next clip's overlap.
    private float ClipGainAt(int i, double pos)
    {
        var c     = Clips[i];
        var start = c.AbsoluteStartMs;
        var end   = c.AbsoluteStartMs + c.EffectiveDurationMs;
        float g   = 1f;

        if (i > 0)
        {
            double cf = c.CrossfadeMs;
            if (cf > 0 && pos < start + cf)
                g = Math.Min(g, (float)((pos - start) / cf));
        }
        if (i + 1 < Clips.Count)
        {
            double cf = Clips[i + 1].CrossfadeMs;
            if (cf > 0 && pos > end - cf)
                g = Math.Min(g, (float)((end - pos) / cf));
        }
        var env = c.VolumeEnvelope;
        if (env.Count > 0)
        {
            var fileTimeSec = (c.TrimStartMs + (pos - start)) / 1000.0;
            g *= InterpolateEnvelopeGain(env, fileTimeSec);
        }
        return Math.Clamp(g, 0f, 1f);
    }

    private static float InterpolateEnvelopeGain(ObservableCollection<EnvelopePoint> env, double timeSec)
    {
        if (env.Count == 0) return 1f;
        if (timeSec <= env[0].TimeS) return (float)env[0].Volume;
        for (int i = 1; i < env.Count; i++)
        {
            if (timeSec <= env[i].TimeS)
            {
                double t = (timeSec - env[i - 1].TimeS) / (env[i].TimeS - env[i - 1].TimeS);
                return (float)(env[i - 1].Volume + t * (env[i].Volume - env[i - 1].Volume));
            }
        }
        return (float)env[env.Count - 1].Volume;
    }

    // ── Voice-track recording ─────────────────────────────────────────────────────

    private readonly DispatcherTimer _vtTimer;
    private DateTime                 _vtStarted;
    private int                      _vtClipIndex = -1;
    private string?                  _vtWavPath;
    private string                   _vtTitle = "";

    /// True when the mic button is armed and the user is choosing an insertion lane.
    [ObservableProperty] private bool _isVtMode;

    /// True while a voice track is actively being recorded.
    [ObservableProperty] private bool _isVtRecording;

    [ObservableProperty] private string _vtElapsedText = "00:00";
    [ObservableProperty] private float  _vtPeakLevel;

    /// Arms / disarms VT insertion mode (the timeline then expects a lane click).
    [RelayCommand]
    private void ToggleVtMode()
    {
        if (IsVtRecording) return;
        IsVtMode = !IsVtMode;
        if (IsVtMode) StopPreview();   // avoid PFL fighting the mic
    }

    /// Inserts a live recording clip after <paramref name="afterIndex"/> and starts capture.
    /// Called by the timeline when the user clicks a lane while in VT mode.
    public async Task StartVtRecordingAsync(int afterIndex)
    {
        if (IsVtRecording) return;

        var folder = _config["voicetrack:path"] ?? "VoiceTracks";
        Directory.CreateDirectory(folder);
        _vtWavPath = Path.Combine(folder, $"VT_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        _vtTitle   = $"VT {DateTime.Now:dd.MM HH:mm}";

        try
        {
            await _audioEngine.StartVoiceTrackRecordingAsync(_vtWavPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VT recording start failed");
            StatusMessage = string.Format(Localizer.Instance?["sg.msg.rec_start_error"] ?? "Recording start error: {0}", ex.Message);
            IsVtMode = false;
            return;
        }

        // Insert the placeholder clip right after the chosen lane. Title/Artist are the
        // final names (hidden behind the "● REC" overlay until recording stops).
        var insertAt = Math.Clamp(afterIndex + 1, 0, Clips.Count);
        var clip = new SegueClipViewModel
        {
            ItemId              = Guid.NewGuid().ToString(),
            Title               = _vtTitle,
            Artist              = "Voice Track",
            DurationMs          = 0,
            IsRecording         = true,
            RecordingDurationMs = 0,
            LeadInMs            = 0,
            CrossfadeMs         = 0,
            IsNew               = true,
        };
        clip.RecordingPeaks.Clear();
        Clips.Insert(insertAt, clip);
        _vtClipIndex = insertAt;

        _vtStarted     = DateTime.UtcNow;
        IsVtMode       = false;
        IsVtRecording  = true;
        VtElapsedText  = "00:00";
        VtPeakLevel    = 0f;
        StatusMessage  = null;

        RecomputeLayout();
        RaiseProjections();
        OnPropertyChanged(nameof(PreviewTotalMs));
        OnPropertyChanged(nameof(PreviewTotalText));

        _vtTimer.Start();
    }

    private void OnVtTick(object? sender, EventArgs e)
    {
        if (!IsVtRecording || _vtClipIndex < 0 || _vtClipIndex >= Clips.Count) return;

        var elapsed = DateTime.UtcNow - _vtStarted;
        var level   = _audioEngine.GetVoiceTrackRecordingLevel();

        var clip = Clips[_vtClipIndex];
        clip.RecordingPeaks.Add(level);
        clip.RecordingDurationMs = (uint)elapsed.TotalMilliseconds;
        VtElapsedText = elapsed.ToString(@"mm\:ss");
        VtPeakLevel   = level;

        RecomputeLayout();
        OnPropertyChanged(nameof(PreviewTotalMs));
        OnPropertyChanged(nameof(PreviewTotalText));
    }

    /// Stops the live capture and freezes the clip length. Import + asset assignment
    /// happen in a later phase (the clip currently has no AssetId yet).
    [RelayCommand]
    private async Task StopVtRecordingAsync()
    {
        if (!IsVtRecording) return;

        _vtTimer.Stop();

        uint durationMs;
        try { durationMs = await _audioEngine.StopVoiceTrackRecordingAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VT recording stop failed");
            durationMs = _vtClipIndex >= 0 && _vtClipIndex < Clips.Count
                ? Clips[_vtClipIndex].RecordingDurationMs : 0;
        }

        IsVtRecording = false;
        VtPeakLevel   = 0f;

        SegueClipViewModel? vtClip = null;
        if (_vtClipIndex >= 0 && _vtClipIndex < Clips.Count)
        {
            vtClip                   = Clips[_vtClipIndex];
            vtClip.IsRecording       = false;
            vtClip.DurationMs        = durationMs;
            vtClip.FilePath          = _vtWavPath;
            vtClip.RecordingDurationMs = durationMs;

            // Freeze the live samples as the clip's coarse peaks so the waveform stays
            // visible after IsRecording flips off (the normal render path reads Peaks).
            // The on-demand detail decode from the WAV refines it when zoomed in.
            if (vtClip.RecordingPeaks.Count > 0)
                vtClip.Peaks = vtClip.RecordingPeaks.ToArray();
        }

        HasUnsavedChanges = true;
        RecomputeLayout();
        RaiseProjections();
        OnPropertyChanged(nameof(PreviewTotalMs));
        OnPropertyChanged(nameof(PreviewTotalText));

        // Import the WAV into the asset library so the clip gets a real AssetId.
        if (vtClip is not null && _vtWavPath is not null)
        {
            StatusMessage = "Importowanie nagrania…";
            try
            {
                var result = await _importPipeline.ImportVoiceTrackAsync(_vtWavPath, _vtTitle);
                if (result is ImportResult.Success s)
                {
                    vtClip.AssetId = s.Asset.AssetId;
                    StatusMessage  = Localizer.Instance?["sg.msg.vt_recorded"] ?? "VT recorded — ready to save.";
                }
                else if (result is ImportResult.Duplicate d)
                {
                    vtClip.AssetId = d.AssetId;
                    StatusMessage  = Localizer.Instance?["sg.msg.vt_duplicate"] ?? "VT: an identical file already exists in the library.";
                }
                else if (result is ImportResult.Failed f)
                {
                    StatusMessage = string.Format(Localizer.Instance?["sg.msg.vt_import_error"] ?? "VT import error: {0}", f.Reason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VT import failed");
                StatusMessage = string.Format(Localizer.Instance?["sg.msg.vt_import_error"] ?? "VT import error: {0}", ex.Message);
            }
        }
    }

    // ── UI state ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool    _isBusy;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool    _hasUnsavedChanges;

    bool IHasUnsavedChanges.HasUnsavedChanges => HasUnsavedChanges;
    public bool CanUndo => _undoStack.CanUndo;
    public bool CanRedo => _undoStack.CanRedo;

    public SegueEditorViewModel(
        ApiClientService api,
        IAudioEngine audioEngine,
        IConfiguration config,
        IImportPipeline importPipeline,
        ILogger<SegueEditorViewModel> logger)
    {
        _api            = api;
        _audioEngine    = audioEngine;
        _config         = config;
        _importPipeline = importPipeline;
        _logger         = logger;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _previewTimer.Tick += OnPreviewTick;

        _vtTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _vtTimer.Tick += OnVtTick;
    }

    // ── Initialization ──────────────────────────────────────────────────────────

    public async Task InitializeAsync(IReadOnlyList<PlaylistItemDto> items)
    {
        StopPreview();
        TimelineFocusMs = double.NaN;
        IsBusy          = true;
        Clips.Clear();
        _activePairIndex = 0;
        try
        {
            foreach (var item in items)
                Clips.Add(await LoadClipAsync(item));

            // For clips without a stored CrossfadeMs, derive it from the previous
            // clip's StartNext marker (the "correct" editorial transition point).
            // This is a one-time init: subsequent drags adjust CrossfadeMs directly.
            for (int i = 1; i < Clips.Count; i++)
            {
                if (items[i].CrossfadeMs is not null) continue;

                var prev = Clips[i - 1];
                if (prev.Markers?.StartNext is double sn)
                {
                    var snMs      = sn * 1000;
                    var trimStart = (double)prev.TrimStartMs;
                    var effEnd    = (double)prev.EffectiveEndMs;
                    if (snMs > trimStart && snMs <= effEnd)
                    {
                        var cf = (long)prev.EffectiveDurationMs - (long)(snMs - trimStart);
                        Clips[i].CrossfadeMs = (uint)Math.Max(0, cf);
                    }
                }
            }

            RecomputeLayout();

            if (OutgoingClip is { } first)
                TimelineFocusMs = first.AbsoluteStartMs + first.EffectiveEndMs;

            PreviewPositionMs = ActiveTransitionMs();
            OnPropertyChanged(nameof(PreviewTotalMs));
            OnPropertyChanged(nameof(PreviewTotalText));

            _undoStack.Clear();
            HasUnsavedChanges = false;
            RaiseProjections();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SegueEditor init failed"); }
        finally { IsBusy = false; }
    }

    private async Task<SegueClipViewModel> LoadClipAsync(PlaylistItemDto item)
    {
        string? filePath = null;
        CueMarkersDto? markers = item.CueMarkers;
        float[]? peaks = null;

        if (item.AssetId is not null)
        {
            var detail = await _api.GetAssetDetailAsync(item.AssetId);
            if (detail?.CueMarkers is { } cm)
                markers = cm;
            filePath = detail?.RdmFilePath;

            var compressed = await _api.GetWaveformAsync(item.AssetId);
            peaks = WaveformDecoder.Decode(compressed);
            if (peaks is null)
                _ = _api.RequestWaveformAsync(item.AssetId);
        }

        var envelope = new ObservableCollection<EnvelopePoint>();
        if (!string.IsNullOrWhiteSpace(item.VolumeEnvelope))
        {
            try
            {
                var pts = JsonSerializer.Deserialize<List<EnvelopePoint>>(item.VolumeEnvelope,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (pts is not null)
                    foreach (var p in pts) envelope.Add(p);
            }
            catch { /* ignore malformed envelope */ }
        }

        return new SegueClipViewModel
        {
            ItemId         = item.ItemId,
            AssetId        = item.AssetId,
            FilePath       = filePath,
            Position       = item.Position,
            Title          = item.Title  ?? item.DummyLabel ?? "",
            Artist         = item.Artist ?? "",
            DurationMs     = item.DurationMs ?? item.DummyDurationMs ?? 0,
            CrossfadeMs    = item.CrossfadeMs ?? 2000,
            LeadInMs       = item.LeadInMs ?? (int)(item.CrossfadeMs ?? 2000),
            TrimStartMs    = item.TrimStartMs ?? 0,
            TrimEndMs      = item.TrimEndMs   ?? 0,
            Markers        = markers,
            Peaks          = peaks,
            VolumeEnvelope = envelope,
        };
    }

    // ── Clip drag (called from control events) ──────────────────────────────────

    /// Pushes an undo snapshot at the start of a clip drag gesture.
    public void BeginClipDrag(int clipIndex)
    {
        if (clipIndex <= 0 || clipIndex >= Clips.Count) return;
        _undoStack.Push(Capture());
        NotifyUndoRedo();
    }

    /// Shifts clip [clipIndex] and all subsequent clips by deltaMs (positive = later start).
    /// Adjusts the dragged clip's free offset (LeadInMs); RecomputeLayout propagates the
    /// shift to all following clips. Unlike crossfade, LeadInMs is not capped by the
    /// previous clip's duration — only by the timeline origin — so a clip can be dragged
    /// freely past a short neighbour (e.g. a sweeper) onto an earlier track.
    public void ShiftClipFrom(int clipIndex, double deltaMs)
    {
        if (clipIndex <= 0 || clipIndex >= Clips.Count) return;

        var clip = Clips[clipIndex];
        var prev = Clips[clipIndex - 1];

        // start = prev.start + prev.dur - lead ≥ 0  ⇒  lead ≤ prev.start + prev.dur
        var maxLead = prev.AbsoluteStartMs + prev.EffectiveDurationMs;
        var newLead = Math.Min(clip.LeadInMs - deltaMs, maxLead);
        clip.LeadInMs = (int)Math.Round(newLead);

        // Keep the engine/save crossfade value the meaningful overlap with the predecessor.
        clip.CrossfadeMs = (uint)Math.Clamp(newLead, 0, prev.EffectiveDurationMs);

        RecomputeLayout();
        HasUnsavedChanges = true;
        RaiseProjections();
    }

    /// Recomputes each clip's absolute start by chaining effective durations and the
    /// per-clip free offset (LeadInMs). Start is clamped to ≥ 0 so a clip dragged hard
    /// left never goes before the timeline origin.
    private void RecomputeLayout()
    {
        for (int i = 0; i < Clips.Count; i++)
        {
            if (i == 0) { Clips[i].AbsoluteStartMs = 0; continue; }

            var raw = Clips[i - 1].AbsoluteStartMs
                      + Clips[i - 1].EffectiveDurationMs
                      - Clips[i].LeadInMs;
            Clips[i].AbsoluteStartMs = Math.Max(0, raw);
        }
    }

    // ── Fade-out handle (from timeline drag) ──────────────────────────────────────

    /// Sets clip [clipIndex]'s FadeOut marker from a new absolute fade-start position.
    /// The fade start is clamped to stay within the trimmed region and before the fade end.
    public void SetFadeOut(int clipIndex, double absMs)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return;
        var clip = Clips[clipIndex];
        if (clip.Markers is not { } m) return;

        double feSrcMs =
            m.FadeEnd is double fe ? fe * 1000 :
            m.End     is double en ? en * 1000 :
            clip.EffectiveEndMs;
        feSrcMs = Math.Min(feSrcMs, clip.EffectiveEndMs);

        var srcMs = clip.TrimStartMs + (absMs - clip.AbsoluteStartMs);
        srcMs = Math.Clamp(srcMs, clip.TrimStartMs, Math.Max(clip.TrimStartMs, feSrcMs - 1));

        clip.Markers = m with { FadeOut = srcMs / 1000.0 };   // record copy → redraws
        HasUnsavedChanges = true;
        RaiseProjections();
    }

    /// Persists clip [clipIndex]'s edited cue markers (the fade-out) to its asset.
    /// Fade-out is asset-level data, so it is saved immediately on drag end rather
    /// than waiting for the playlist save.
    public async Task CommitFadeOutAsync(int clipIndex)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return;
        var clip = Clips[clipIndex];
        if (clip.AssetId is not { } assetId || clip.Markers is not { } markers) return;

        try
        {
            var d = await _api.GetAssetDetailAsync(assetId);
            if (d is null) return;

            var req = new UpdateAssetRequestDto(
                Title: d.Title, Artist: d.Artist, Album: d.Album, FormatId: d.FormatId,
                SubcategoryId: d.SubcategoryId,
                Bpm: d.Bpm, Year: d.Year, Rating: d.Rating, Mood: d.Mood, Gender: d.Gender,
                Language: d.Language, Genre: d.Genre, Comments: d.Comments, Status: d.Status,
                StartDate: d.StartDate, EndDate: d.EndDate, PlayLimit: d.PlayLimit,
                CueMarkers: markers);

            await _api.UpdateAssetAsync(assetId, req);
            StatusMessage = Localizer.Instance?["sg.msg.fade_saved"] ?? "Fade-out saved.";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fade-out persist failed");
            StatusMessage = string.Format(Localizer.Instance?["sg.msg.fade_save_error"] ?? "Fade save error: {0}", ex.Message);
        }
    }

    // ── Cue marker editing ────────────────────────────────────────────────────────

    public void MoveCueMarker(int clipIdx, string key, double timeSec)
    {
        if (clipIdx < 0 || clipIdx >= Clips.Count) return;
        var clip = Clips[clipIdx];
        var m    = clip.Markers ?? new CueMarkersDto(null,null,null,null,null,null,null,null,null,null,null,null,null,null,null);
        clip.Markers      = SetMarker(m, key, timeSec);
        HasUnsavedChanges = true;
        RaiseProjections();
    }

    public void RemoveCueMarker(int clipIdx, string key)
    {
        if (clipIdx < 0 || clipIdx >= Clips.Count) return;
        var clip = Clips[clipIdx];
        if (clip.Markers is not { } m) return;
        clip.Markers      = SetMarker(m, key, null);
        HasUnsavedChanges = true;
        RaiseProjections();
    }

    public async Task AddCueMarkerAsync(int clipIdx, string key, double timeSec)
    {
        MoveCueMarker(clipIdx, key, timeSec);
        await CommitCueMarkerAsync(clipIdx);
    }

    public async Task CommitCueMarkerAsync(int clipIdx)
    {
        if (clipIdx < 0 || clipIdx >= Clips.Count) return;
        var clip = Clips[clipIdx];
        if (clip.AssetId is not { } assetId || clip.Markers is not { } markers) return;

        try
        {
            var d = await _api.GetAssetDetailAsync(assetId);
            if (d is null) return;

            var req = new UpdateAssetRequestDto(
                Title: d.Title, Artist: d.Artist, Album: d.Album, FormatId: d.FormatId,
                SubcategoryId: d.SubcategoryId,
                Bpm: d.Bpm, Year: d.Year, Rating: d.Rating, Mood: d.Mood, Gender: d.Gender,
                Language: d.Language, Genre: d.Genre, Comments: d.Comments, Status: d.Status,
                StartDate: d.StartDate, EndDate: d.EndDate, PlayLimit: d.PlayLimit,
                CueMarkers: markers);

            await _api.UpdateAssetAsync(assetId, req);
            StatusMessage = Localizer.Instance?["sg.msg.marker_saved"] ?? "Marker saved.";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cue marker persist failed");
            StatusMessage = string.Format(Localizer.Instance?["sg.msg.marker_save_error"] ?? "Marker save error: {0}", ex.Message);
        }
    }

    private static CueMarkersDto SetMarker(CueMarkersDto m, string key, double? v) => key switch
    {
        "Start"     => m with { Start     = v },
        "Intro"     => m with { Intro     = v },
        "Ramp2"     => m with { Ramp2     = v },
        "Ramp3"     => m with { Ramp3     = v },
        "Outro"     => m with { Outro     = v },
        "StartNext" => m with { StartNext = v },
        "FadeOut"   => m with { FadeOut   = v },
        "FadeEnd"   => m with { FadeEnd   = v },
        "End"       => m with { End       = v },
        "HookIn"    => m with { HookIn    = v },
        "HookFade"  => m with { HookFade  = v },
        "HookOut"   => m with { HookOut   = v },
        "LoopIn"    => m with { LoopIn    = v },
        "LoopOut"   => m with { LoopOut   = v },
        "Anchor"    => m with { Anchor    = v },
        _           => m,
    };

    // ── Save ────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            int insertOffset = 0;
            var patchTasks   = new List<Task<bool>>();

            for (int i = 0; i < Clips.Count; i++)
            {
                var clip = Clips[i];

                if (clip.IsNew)
                {
                    // New VT clip: add to the playlist at the correct server position.
                    // If import failed (AssetId is null), skip — the clip has no backing asset.
                    if (clip.AssetId is null) continue;
                    var basePos = i > 0 ? (int)Clips[i - 1].Position : 0;
                    await _api.AddPlaylistItemAsync(
                        new AddPlaylistItemRequestDto(clip.AssetId, null, basePos + 1 + insertOffset, "ASSET"));
                    insertOffset++;
                    continue;
                }

                var patch = new PatchPlaylistItemDto(
                    CrossfadeMs:    i > 0 ? clip.CrossfadeMs : null,
                    LeadInMs:       i > 0 ? clip.LeadInMs    : null,
                    TrimStartMs:    clip.TrimStartMs > 0 ? clip.TrimStartMs : null,
                    TrimEndMs:      clip.TrimEndMs   > 0 ? clip.TrimEndMs   : null,
                    SegueType:      null,
                    AutoLinkNext:   null,
                    VolumeEnvelope: clip.VolumeEnvelope.Count > 0
                                    ? JsonSerializer.Serialize(clip.VolumeEnvelope.ToList())
                                    : null);
                patchTasks.Add(_api.PatchPlaylistItemAsync(clip.ItemId, patch));
            }

            await Task.WhenAll(patchTasks);

            StatusMessage     = Localizer.Instance?["sg.msg.segues_saved"] ?? "Segues saved.";
            HasUnsavedChanges = false;
        }
        catch (Exception ex) { StatusMessage = string.Format(Localizer.Instance?["sg.msg.error"] ?? "Error: {0}", ex.Message); }
        finally { IsBusy = false; }
    }

    // ── Undo / Redo ───────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        Restore(_undoStack.Undo(Capture()));
        NotifyUndoRedo();
        HasUnsavedChanges = true;
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        Restore(_undoStack.Redo(Capture()));
        NotifyUndoRedo();
        HasUnsavedChanges = true;
    }

    private SegueSnapshot Capture() => new(
        Clips.Select(c => new ClipSnapshot(c.ItemId, c.CrossfadeMs, c.LeadInMs, c.TrimStartMs, c.TrimEndMs, c.VolumeEnvelope.ToList()))
             .ToList());

    private void Restore(SegueSnapshot snapshot)
    {
        foreach (var cs in snapshot.Clips)
        {
            var clip = Clips.FirstOrDefault(c => c.ItemId == cs.ItemId);
            if (clip is null) continue;
            clip.CrossfadeMs    = cs.CrossfadeMs;
            clip.LeadInMs       = cs.LeadInMs;
            clip.TrimStartMs    = cs.TrimStartMs;
            clip.TrimEndMs      = cs.TrimEndMs;
            clip.VolumeEnvelope = new ObservableCollection<EnvelopePoint>(cs.Envelope);
        }
        RecomputeLayout();
        RaiseProjections();
    }

    // ── Marker jump commands ──────────────────────────────────────────────────────

    [RelayCommand] private void JumpToT1Start()     => JumpAbs(OutgoingClip, OutgoingClip?.Markers?.Start);
    [RelayCommand] private void JumpToT1Intro()     => JumpAbs(OutgoingClip, OutgoingClip?.Markers?.Intro);
    [RelayCommand] private void JumpToT1Outro()     => JumpAbs(OutgoingClip, OutgoingClip?.Markers?.Outro);
    [RelayCommand] private void JumpToT1FadeOut()   => JumpAbs(OutgoingClip, OutgoingClip?.Markers?.FadeOut);
    [RelayCommand] private void JumpToT1StartNext() => JumpAbs(OutgoingClip, OutgoingClip?.Markers?.StartNext);

    [RelayCommand] private void JumpToT2Start()     => JumpAbs(IncomingClip, IncomingClip?.Markers?.Start);
    [RelayCommand] private void JumpToT2Intro()     => JumpAbs(IncomingClip, IncomingClip?.Markers?.Intro);
    [RelayCommand] private void JumpToT2Ramp2()     => JumpAbs(IncomingClip, IncomingClip?.Markers?.Ramp2);

    [RelayCommand]
    private void JumpToTransition()
    {
        if (OutgoingClip is { } clip)
            TimelineFocusMs = clip.AbsoluteStartMs + clip.EffectiveEndMs;
    }

    /// Moves the timeline cursor to a marker (in seconds) relative to a clip's start.
    private void JumpAbs(SegueClipViewModel? clip, double? markerSecs)
    {
        if (clip is null || markerSecs is not double secs) return;
        TimelineFocusMs = clip.AbsoluteStartMs + secs * 1000;
    }

    // ── On-demand detail waveform (timeline zoom) ───────────────────────────────

    /// Decodes a sample-accurate slice for <paramref name="clipIndex"/> over the
    /// source window [<paramref name="startMs"/>, <paramref name="endMs"/>] and stores
    /// it on the clip so the timeline can render a continuous line when zoomed in.
    /// Cancels any earlier in-flight request for the same clip.
    public async Task RequestDetailWaveformAsync(int clipIndex, double startMs, double endMs, int columns)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return;
        var clip = Clips[clipIndex];
        if (string.IsNullOrEmpty(clip.FilePath)) return;

        CancellationTokenSource cts;
        if (_detailCts.TryGetValue(clipIndex, out var prev))
        {
            prev.Cancel();
            prev.Dispose();
        }
        cts = new CancellationTokenSource();
        _detailCts[clipIndex] = cts;

        try
        {
            var win = await _audioEngine.ReadWaveformWindowAsync(
                clip.FilePath!, startMs, endMs, columns, cts.Token);
            if (win is not null && !cts.IsCancellationRequested)
                clip.Detail = win;
        }
        catch (OperationCanceledException) { /* superseded by a newer request */ }
        catch (Exception ex) { _logger.LogDebug(ex, "Detail waveform decode failed"); }
        finally
        {
            if (_detailCts.TryGetValue(clipIndex, out var cur) && ReferenceEquals(cur, cts))
                _detailCts.Remove(clipIndex);
            cts.Dispose();
        }
    }

    // ── Envelope editing (called from SegueEditorWindow event handlers) ───────────

    public void BeginEnvelopeNodeDrag(int clipIndex)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return;
        _undoStack.Push(Capture());
        NotifyUndoRedo();
    }

    public void MoveEnvelopeNode(int clipIndex, int nodeIndex, double timeSec, double volume)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return;
        var clip = Clips[clipIndex];
        if (nodeIndex < 0 || nodeIndex >= clip.VolumeEnvelope.Count) return;
        var list = clip.VolumeEnvelope.ToList();
        list[nodeIndex] = new EnvelopePoint(timeSec, Math.Clamp(volume, 0.0, 1.0));
        list.Sort((a, b) => a.TimeS.CompareTo(b.TimeS));
        clip.VolumeEnvelope = new ObservableCollection<EnvelopePoint>(list);
    }

    public void CommitEnvelopeNodeDrag()
    {
        HasUnsavedChanges = true;
    }

    public void AddEnvelopeNode(int clipIndex, double timeSec, double volume)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return;
        _undoStack.Push(Capture());
        var clip = Clips[clipIndex];
        var list = clip.VolumeEnvelope.ToList();
        list.Add(new EnvelopePoint(timeSec, Math.Clamp(volume, 0.0, 1.0)));
        list.Sort((a, b) => a.TimeS.CompareTo(b.TimeS));
        clip.VolumeEnvelope = new ObservableCollection<EnvelopePoint>(list);
        HasUnsavedChanges = true;
        NotifyUndoRedo();
    }

    public void RemoveEnvelopeNode(int clipIndex, int nodeIndex)
    {
        if (clipIndex < 0 || clipIndex >= Clips.Count) return;
        var clip = Clips[clipIndex];
        if (nodeIndex < 0 || nodeIndex >= clip.VolumeEnvelope.Count) return;
        _undoStack.Push(Capture());
        var list = clip.VolumeEnvelope.ToList();
        list.RemoveAt(nodeIndex);
        clip.VolumeEnvelope = new ObservableCollection<EnvelopePoint>(list);
        HasUnsavedChanges = true;
        NotifyUndoRedo();
    }

    // ── Data loading ──────────────────────────────────────────────────────────────

    private void NotifyUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    /// Raises change notifications for the legacy 2-track projection of the active pair.
    private void RaiseProjections()
    {
        OnPropertyChanged(nameof(Track1Artist));
        OnPropertyChanged(nameof(Track1Title));
        OnPropertyChanged(nameof(Track1Label));
        OnPropertyChanged(nameof(Track1Peaks));
        OnPropertyChanged(nameof(Track1DurationMs));
        OnPropertyChanged(nameof(Track1Markers));
        OnPropertyChanged(nameof(Track2Artist));
        OnPropertyChanged(nameof(Track2Title));
        OnPropertyChanged(nameof(Track2Label));
        OnPropertyChanged(nameof(Track2Peaks));
        OnPropertyChanged(nameof(Track2DurationMs));
        OnPropertyChanged(nameof(Track2Markers));
        OnPropertyChanged(nameof(CrossfadeMs));
        OnPropertyChanged(nameof(CrossfadeText));
        OnPropertyChanged(nameof(TrimEndMs));
        OnPropertyChanged(nameof(TrimEndText));
        OnPropertyChanged(nameof(PreviewTotalMs));
        OnPropertyChanged(nameof(PreviewTotalText));
    }
}
