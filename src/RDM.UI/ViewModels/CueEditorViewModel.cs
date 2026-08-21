using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Services;
using RDM.Shared.DTOs;
using RDM.UI.Controls;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed partial class CueEditorViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService            _api;
    private readonly ILogger<CueEditorViewModel> _logger;
    private readonly DispatcherTimer             _positionTimer;
    private readonly DispatcherTimer             _autoSaveTimer;

    private string   _assetId = "";
    private DateTime _pflStartTime;
    private double   _pflStartPositionSec;
    private double?  _previewStopAtSec;
    private bool     _disposed;
    private Func<CueMarkersDto, Task<bool>>? _saveDelegate;

    // ── Marker storage ────────────────────────────────────────────────────────

    private readonly double?[] _markers = new double?[15];

    private static readonly string[] MarkerNames =
    [
        "Start", "Intro", "Ramp2", "Ramp3", "Outro",
        "StartNext", "FadeOut", "FadeEnd", "End",
        "HookIn", "HookFade", "HookOut",
        "LoopIn", "LoopOut", "Anchor"
    ];

    private static readonly Color[] MarkerColors =
    [
        Color.Parse("#00FF00"), Color.Parse("#4AB5FF"), Color.Parse("#7BC8FF"),
        Color.Parse("#AAD5FF"), Color.Parse("#FF8C42"), Color.Parse("#FF5555"),
        Color.Parse("#FF2222"), Color.Parse("#880000"), Color.Parse("#FFFFFF"),
        Color.Parse("#FFFF00"), Color.Parse("#FFAA00"), Color.Parse("#FF6600"),
        Color.Parse("#00FFFF"), Color.Parse("#00AAAA"), Color.Parse("#FF00FF"),
    ];

    private static readonly string[] MarkerHexColors =
    [
        "#00FF00", "#4AB5FF", "#7BC8FF", "#AAD5FF", "#FF8C42",
        "#FF5555", "#FF2222", "#880000", "#FFFFFF",
        "#FFFF00", "#FFAA00", "#FF6600",
        "#00FFFF", "#00AAAA", "#FF00FF"
    ];

    // ── PFL Player state ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    private bool _isPlaying;

    public string PlayPauseIcon => IsPlaying ? "⏸" : "▶";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionFormatted))]
    [NotifyPropertyChangedFor(nameof(PositionMs))]
    private double _positionSec;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationFormatted))]
    [NotifyPropertyChangedFor(nameof(DurationMs))]
    private double _durationSec = 1.0;

    public string PositionFormatted => FormatSec(PositionSec);
    public string DurationFormatted => FormatSec(DurationSec);
    public uint   PositionMs        => (uint)(PositionSec * 1000);
    public uint   DurationMs        => (uint)(DurationSec * 1000);

    // Zone coloring helpers used by WaveformControl
    public uint? IntroCueMs => _markers[1].HasValue ? (uint?)(_markers[1]!.Value * 1000) : null;
    public uint? OutroCueMs => _markers[4].HasValue ? (uint?)(_markers[4]!.Value * 1000) : null;

    // ── Waveform ──────────────────────────────────────────────────────────────

    [ObservableProperty] private float[]? _waveformPeaks;
    [ObservableProperty] private IReadOnlyList<CueMarker> _waveformMarkers = [];

    // ── Auto-calculate settings ───────────────────────────────────────────────

    public double AutoFadeOutDurationS { get; set; } = 10.0;

    // Silence Remover thresholds (from audio settings). Auto-calculate only runs
    // when SilenceRemoverEnabled is true; there are no hardcoded threshold values.
    public bool   SilenceRemoverEnabled   { get; set; }
    public double SilenceStartThresholdDb { get; set; }
    public double SilenceMixThresholdDb   { get; set; }
    public double SilenceEndThresholdDb   { get; set; }

    // ── Save state ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool    _isDirty;
    [ObservableProperty] private string? _statusMessage;

    // ── Marker rows (table) ───────────────────────────────────────────────────

    private readonly List<CueMarkerRowViewModel> _markerRows;
    public IReadOnlyList<CueMarkerRowViewModel> MarkerRows => _markerRows;

    [ObservableProperty] private CueMarkerRowViewModel? _selectedRow;

    // ── Constructor ───────────────────────────────────────────────────────────

    public CueEditorViewModel(ApiClientService api, ILogger<CueEditorViewModel> logger)
    {
        _api    = api;
        _logger = logger;

        _markerRows = Enumerable.Range(0, 15).Select(i =>
            new CueMarkerRowViewModel(
                name:               MarkerNames[i],
                hexColor:           MarkerHexColors[i],
                getValue:           () => _markers[i],
                setAction:          () => SetMarkerAt(i),
                jumpAction:         () => JumpToMarkerAsync(i),
                previewAction:      () => PreviewMarkerAsync(i),
                clearAction:        () => ClearMarkerAt(i),
                nudgeBackAction:    () => NudgeMarkerAt(i, -0.010),
                nudgeForwardAction: () => NudgeMarkerAt(i, +0.010)
            )).ToList();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _positionTimer.Tick += OnPositionTimerTick;

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _autoSaveTimer.Tick += OnAutoSaveTick;
    }

    // ── Initialization ────────────────────────────────────────────────────────

    public void SetSaveDelegate(Func<CueMarkersDto, Task<bool>> saveDelegate)
        => _saveDelegate = saveDelegate;

    public void Initialize(AssetDetailDto detail)
    {
        _assetId    = detail.AssetId;
        DurationSec = detail.DurationMs > 0 ? detail.DurationMs / 1000.0 : 1.0;

        // Position is derived from the wall clock (see OnPositionTimerTick), so the baseline has to be
        // reset together with it — otherwise the cursor snaps back to the previous asset's position.
        _pflStartPositionSec = 0;
        _pflStartTime        = DateTime.Now;
        _previewStopAtSec    = null;
        PositionSec          = 0;

        var m = detail.CueMarkers;
        _markers[0]  = m?.Start;
        _markers[1]  = m?.Intro;
        _markers[2]  = m?.Ramp2;
        _markers[3]  = m?.Ramp3;
        _markers[4]  = m?.Outro;
        _markers[5]  = m?.StartNext;
        _markers[6]  = m?.FadeOut;
        _markers[7]  = m?.FadeEnd;
        _markers[8]  = m?.End;
        _markers[9]  = m?.HookIn;
        _markers[10] = m?.HookFade;
        _markers[11] = m?.HookOut;
        _markers[12] = m?.LoopIn;
        _markers[13] = m?.LoopOut;
        _markers[14] = m?.Anchor;

        foreach (var row in _markerRows) row.Refresh();
        RefreshWaveformMarkers();
        IsDirty       = false;
        StatusMessage = null;
    }

    public void SetWaveformPeaks(float[]? peaks) => WaveformPeaks = peaks;

    /// <summary>
    /// Leaves the editor in a clean state before another asset is loaded (◀/▶ navigation): flushes a
    /// pending marker edit for the asset being left, stops PFL playback and blanks the waveform.
    /// Must be called while the owner still reports the OLD asset id — the save delegate builds its
    /// request from it.
    /// </summary>
    public async Task ResetForAssetSwitchAsync()
    {
        _autoSaveTimer.Stop();

        // The 500 ms auto-save may not have fired yet; without this flush the edit is lost silently.
        if (IsDirty && !await SaveMarkersAsync())
            _logger.LogWarning("Cue markers for {AssetId} were not saved before switching assets", _assetId);

        await StopPflInternalAsync();

        _pflStartPositionSec = 0;
        _pflStartTime        = DateTime.Now;
        PositionSec          = 0;

        Array.Clear(_markers);
        foreach (var row in _markerRows) row.Refresh();
        RefreshWaveformMarkers();          // also clears the Intro/Outro zone shading
        WaveformPeaks = null;
        IsDirty       = false;
        StatusMessage = null;
    }

    public CueMarkersDto GetCurrentMarkers() => new(
        Start:     _markers[0],
        Intro:     _markers[1],
        Ramp2:     _markers[2],
        Ramp3:     _markers[3],
        Outro:     _markers[4],
        StartNext: _markers[5],
        FadeOut:   _markers[6],
        FadeEnd:   _markers[7],
        End:       _markers[8],
        HookIn:    _markers[9],
        HookFade:  _markers[10],
        HookOut:   _markers[11],
        LoopIn:    _markers[12],
        LoopOut:   _markers[13],
        Anchor:    _markers[14]
    );

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        if (IsPlaying)
        {
            await StopPflInternalAsync();
            return;
        }
        if (string.IsNullOrEmpty(_assetId)) return;

        var targetSec = PositionSec;
        var started   = await _api.StartPflAsync(_assetId);
        if (!started) return;

        _pflStartPositionSec = 0;
        _pflStartTime        = DateTime.Now;
        IsPlaying            = true;
        _positionTimer.Start();

        if (targetSec > 0.1)
        {
            await Task.Delay(120);
            var offsetMs = (int)(targetSec * 1000);
            await _api.SeekPflAsync(offsetMs);
            _pflStartPositionSec = targetSec;
            _pflStartTime        = DateTime.Now;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await StopPflInternalAsync();
        PositionSec = 0;
    }

    [RelayCommand]
    private async Task SeekStartAsync()
    {
        if (IsPlaying)
        {
            var offsetMs = -(int)(PositionSec * 1000);
            if (offsetMs < 0) await _api.SeekPflAsync(offsetMs);
            _pflStartPositionSec = 0;
            _pflStartTime        = DateTime.Now;
        }
        PositionSec = 0;
    }

    [RelayCommand]
    private async Task JumpBackAsync()
    {
        var newPos = Math.Max(0, PositionSec - 0.100);
        if (IsPlaying) await _api.SeekPflAsync(-100);
        _pflStartPositionSec = newPos;
        _pflStartTime        = DateTime.Now;
        PositionSec          = newPos;
    }

    [RelayCommand]
    private async Task JumpForwardAsync()
    {
        var newPos = Math.Min(DurationSec, PositionSec + 0.100);
        if (IsPlaying) await _api.SeekPflAsync(100);
        _pflStartPositionSec = newPos;
        _pflStartTime        = DateTime.Now;
        PositionSec          = newPos;
    }

    [RelayCommand]
    private void AutoCalculate()
    {
        if (!SilenceRemoverEnabled)
        {
            StatusMessage = Localizer.Instance?["te.msg.silence_remover_auto_cue"]
                            ?? "Enable Silence Remover in Settings to use Auto Cue.";
            return;
        }

        var peaks = WaveformPeaks;
        if (peaks is null || peaks.Length == 0 || DurationSec <= 1) return;

        // AUTO cue detection: thresholds come from the Silence Remover audio settings.
        // FadeOut is kept as the existing convenience (End − configured duration).
        var result = AutoCueCalculator.Calculate(
            peaks, DurationSec,
            SilenceStartThresholdDb, SilenceMixThresholdDb, SilenceEndThresholdDb);
        if (result is null) return;
        var cues = result.Value;

        _markers[0] = cues.StartSec;                                   // Start
        _markers[5] = cues.NextStartSec;                               // StartNext (NEXT START)
        _markers[6] = Math.Max(0, cues.EndSec - AutoFadeOutDurationS); // FadeOut
        _markers[8] = cues.EndSec;                                     // End

        foreach (var row in _markerRows) row.Refresh();
        RefreshWaveformMarkers();
        MarkDirty();
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveMarkersAsync();

    /// <summary>Writes the current markers through the save delegate; returns whether they landed.</summary>
    private async Task<bool> SaveMarkersAsync()
    {
        if (_saveDelegate is null || string.IsNullOrEmpty(_assetId)) return false;
        try
        {
            var ok = await _saveDelegate(GetCurrentMarkers());
            StatusMessage = ok
                ? Localizer.Instance?["te.msg.saved"]      ?? "Saved."
                : Localizer.Instance?["te.msg.save_error"] ?? "Save error.";
            if (ok) IsDirty = false;
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CueEditor save failed for {AssetId}", _assetId);
            StatusMessage = Localizer.Instance?["te.msg.save_error"] ?? "Save error.";
            return false;
        }
    }

    // ── Public entry points called from code-behind ───────────────────────────

    public async Task OnWaveformClickedAsync(double normalized)
    {
        var targetSec = Math.Clamp(normalized * DurationSec, 0, DurationSec);
        if (IsPlaying)
        {
            var currentSec = _pflStartPositionSec + (DateTime.Now - _pflStartTime).TotalSeconds;
            var offsetMs   = (int)((targetSec - currentSec) * 1000);
            await _api.SeekPflAsync(offsetMs);
            _pflStartPositionSec = targetSec;
            _pflStartTime        = DateTime.Now;
        }
        PositionSec = targetSec;
    }

    public void HandleKey(Key key, KeyModifiers modifiers)
    {
        var none  = modifiers == KeyModifiers.None;
        var shift = modifiers == KeyModifiers.Shift;
        var ctrl  = modifiers == KeyModifiers.Control;

        switch (key)
        {
            case Key.Space  when none:  _ = PlayPauseCommand.ExecuteAsync(null); break;
            case Key.S      when none:  SelectedRow?.SetCommand.Execute(null);   break;
            case Key.Delete when none:  SelectedRow?.ClearCommand.Execute(null); break;
            case Key.Left   when none:  SelectPrevMarker();           break;
            case Key.Right  when none:  SelectNextMarker();           break;
            case Key.Left   when shift: AdjustSelectedMarker(-0.1);   break;
            case Key.Right  when shift: AdjustSelectedMarker(0.1);    break;
            case Key.Left   when ctrl:  AdjustSelectedMarker(-1.0);   break;
            case Key.Right  when ctrl:  AdjustSelectedMarker(1.0);    break;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _positionTimer.Stop();
        _autoSaveTimer.Stop();
        if (IsPlaying)
            _ = _api.StopPflAsync();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void SetMarkerAt(int index)
    {
        _markers[index] = PositionSec;
        _markerRows[index].Refresh();
        RefreshWaveformMarkers();
        MarkDirty();
    }

    private async Task JumpToMarkerAsync(int index)
    {
        if (!_markers[index].HasValue) return;
        await SeekToPositionAsync(_markers[index]!.Value);
    }

    private async Task PreviewMarkerAsync(int index)
    {
        if (!_markers[index].HasValue) return;
        var markerSec     = _markers[index]!.Value;
        _previewStopAtSec = Math.Min(DurationSec, markerSec + 2.0);
        await SeekToPositionAsync(Math.Max(0, markerSec - 2.0));
    }

    private void ClearMarkerAt(int index)
    {
        _markers[index] = null;
        _markerRows[index].Refresh();
        RefreshWaveformMarkers();
        MarkDirty();
    }

    private async Task SeekToPositionAsync(double targetSec)
    {
        if (!IsPlaying)
        {
            if (string.IsNullOrEmpty(_assetId)) return;
            var started = await _api.StartPflAsync(_assetId);
            if (!started) return;
            _pflStartPositionSec = 0;
            _pflStartTime        = DateTime.Now;
            IsPlaying            = true;
            _positionTimer.Start();
            await Task.Delay(120);
        }

        var currentSec = _pflStartPositionSec + (DateTime.Now - _pflStartTime).TotalSeconds;
        var offsetMs   = (int)((targetSec - currentSec) * 1000);
        if (Math.Abs(offsetMs) > 50)
            await _api.SeekPflAsync(offsetMs);

        _pflStartPositionSec = targetSec;
        _pflStartTime        = DateTime.Now;
        PositionSec          = targetSec;
    }

    private void SelectPrevMarker()
    {
        if (SelectedRow is null) { SelectedRow = _markerRows[^1]; return; }
        var idx = _markerRows.IndexOf(SelectedRow);
        if (idx > 0) SelectedRow = _markerRows[idx - 1];
    }

    private void SelectNextMarker()
    {
        if (SelectedRow is null) { SelectedRow = _markerRows[0]; return; }
        var idx = _markerRows.IndexOf(SelectedRow);
        if (idx < _markerRows.Count - 1) SelectedRow = _markerRows[idx + 1];
    }

    private void AdjustSelectedMarker(double deltaSec)
    {
        if (SelectedRow is null) return;
        var idx = _markerRows.IndexOf(SelectedRow);
        if (idx < 0 || !_markers[idx].HasValue) return;
        NudgeMarkerAt(idx, deltaSec);
    }

    private void NudgeMarkerAt(int index, double deltaSec)
    {
        if (!_markers[index].HasValue) return;
        _markers[index] = Math.Clamp(_markers[index]!.Value + deltaSec, 0, DurationSec);
        _markerRows[index].Refresh();
        RefreshWaveformMarkers();
        MarkDirty();
    }

    private void RefreshWaveformMarkers()
    {
        var list = new List<CueMarker>(15);
        for (int i = 0; i < 15; i++)
        {
            if (_markers[i].HasValue)
                list.Add(new CueMarker((uint)(_markers[i]!.Value * 1000), MarkerColors[i], MarkerNames[i]));
        }
        WaveformMarkers = list;
        OnPropertyChanged(nameof(IntroCueMs));
        OnPropertyChanged(nameof(OutroCueMs));
    }

    private void MarkDirty()
    {
        IsDirty = true;
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private async Task StopPflInternalAsync()
    {
        _positionTimer.Stop();
        _previewStopAtSec = null;
        if (!IsPlaying) return;
        IsPlaying = false;
        try { await _api.StopPflAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "StopPfl failed"); }
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (!IsPlaying) return;
        var elapsed = (DateTime.Now - _pflStartTime).TotalSeconds;
        var newPos  = Math.Min(_pflStartPositionSec + elapsed, DurationSec);
        PositionSec = newPos;

        if (_previewStopAtSec.HasValue && newPos >= _previewStopAtSec.Value)
        {
            _previewStopAtSec = null;
            _ = StopPflInternalAsync();
            return;
        }

        if (newPos >= DurationSec)
        {
            IsPlaying = false;
            _positionTimer.Stop();
        }
    }

    private async void OnAutoSaveTick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        await SaveAsync();
    }

    private static string FormatSec(double sec)
    {
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalMinutes >= 60
            ? t.ToString(@"h\:mm\:ss\.f")
            : t.ToString(@"mm\:ss\.f");
    }
}
