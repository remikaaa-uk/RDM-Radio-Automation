using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.UI.Controls;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

// ── Per-channel VM ────────────────────────────────────────────────────────────

public sealed partial class AuxChannelViewModel : ObservableObject
{
    private static readonly string[] Accents = { "#1BA9C9", "#46B450", "#F07A1A", "#9B30D0" };

    private readonly ApiClientService          _api;
    private readonly Func<Task<string?>>       _pickFileAsync;
    private readonly ILogger                   _logger;

    public int    Index       { get; }
    public string DisplayName { get; }
    public string Number      { get; }
    public IBrush AccentBrush { get; }

    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private bool   _isLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelPercent))]
    [NotifyPropertyChangedFor(nameof(LevelBarHeight))]
    private float _levelDb = -60f;

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isPlaying;

    [ObservableProperty] private uint _durationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    [NotifyPropertyChangedFor(nameof(RemainingText))]
    private uint _positionMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartCueText))]
    [NotifyPropertyChangedFor(nameof(CueMarkers))]
    private uint _startCueMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndCueText))]
    [NotifyPropertyChangedFor(nameof(CueMarkers))]
    private uint _endCueMs;

    [ObservableProperty] private float[]? _waveformPeaks;
    [ObservableProperty] private bool      _isLooping;
    [ObservableProperty] private bool      _isPfl;
    [ObservableProperty] private bool      _isOn = true;
    [ObservableProperty] private double    _volume = 80; // slider 0..100

    // -60 dB → 0%, 0 dB → 100%
    public double LevelPercent   => Math.Max(0, (LevelDb + 60.0) / 60.0 * 100.0);
    public double LevelBarHeight => Math.Clamp(LevelPercent / 100.0 * 64.0, 0, 64);

    public string ElapsedText   => FormatTenths(PositionMs);
    public string RemainingText => FormatTenths(DurationMs > PositionMs ? DurationMs - PositionMs : 0);
    public string StartCueText  => FormatTenths(StartCueMs);
    public string EndCueText     => FormatTenths(EndCueMs);

    // START marker = green, END marker = white (overrides the default INTRO/OUTRO colours)
    public IReadOnlyList<CueMarker> CueMarkers =>
    [
        new CueMarker(StartCueMs, Color.Parse("#46B450"), "START"),
        new CueMarker(EndCueMs,   Color.Parse("#FFFFFF"), "END"),
    ];

    public AuxChannelViewModel(int index, ApiClientService api, Func<Task<string?>> pickFileAsync, ILogger logger)
    {
        Index          = index;
        DisplayName    = $"AUX {index + 1}";
        Number         = (index + 1).ToString();
        AccentBrush    = new SolidColorBrush(Color.Parse(Accents[index % Accents.Length]));
        _api           = api;
        _pickFileAsync = pickFileAsync;
        _logger        = logger;
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var path = await _pickFileAsync();
            if (string.IsNullOrWhiteSpace(path)) return;

            var result = await _api.LoadAuxAsync(Index, path);
            if (result is null)
            {
                _logger.LogWarning("LoadAux {Index} returned null for '{Path}'", Index, path);
                return;
            }

            FileName    = Path.GetFileName(path);
            DurationMs  = result.DurationMs;
            StartCueMs  = result.StartMs;
            EndCueMs    = result.EndMs;
            PositionMs  = 0;
            IsPlaying   = false;
            WaveformPeaks = await LoadPeaksAsync(result.WaveformPath);
            IsLoaded    = true;

            // Sync engine routing + volume with the current UI state
            await _api.SetAuxRouteAsync(Index, IsOn, IsPfl);
            await _api.SetAuxVolumeAsync(Index, (float)(Volume / 100.0));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LoadAux {Index} failed", Index); }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (!IsLoaded) return;
        try { await _api.PlayAuxAsync(Index); IsPlaying = true; }
        catch (Exception ex) { _logger.LogWarning(ex, "PlayAux {Index} failed", Index); }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        try { await _api.StopAuxAsync(Index); IsPlaying = false; PositionMs = 0; }
        catch (Exception ex) { _logger.LogWarning(ex, "StopAux {Index} failed", Index); }
    }

    [RelayCommand]
    private async Task EjectAsync()
    {
        try
        {
            await _api.EjectAuxAsync(Index);
            IsLoaded = false; IsPlaying = false;
            FileName = ""; WaveformPeaks = null;
            PositionMs = 0; DurationMs = 0; StartCueMs = 0; EndCueMs = 0;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EjectAux {Index} failed", Index); }
    }

    [RelayCommand]
    private async Task ToggleLoopAsync()
    {
        IsLooping = !IsLooping;
        try { await _api.SetAuxLoopAsync(Index, IsLooping); }
        catch (Exception ex) { _logger.LogWarning(ex, "SetAuxLoop {Index} failed", Index); }
    }

    [RelayCommand]
    private async Task TogglePflAsync()
    {
        IsPfl = !IsPfl;
        if (IsPfl) IsOn = false;   // PFL and ON are mutually exclusive
        try { await _api.SetAuxRouteAsync(Index, IsOn, IsPfl); }
        catch (Exception ex) { _logger.LogWarning(ex, "SetAuxRoute(PFL) {Index} failed", Index); }
    }

    [RelayCommand]
    private async Task ToggleOnAsync()
    {
        IsOn = !IsOn;
        if (IsOn) IsPfl = false;   // ON and PFL are mutually exclusive
        try { await _api.SetAuxRouteAsync(Index, IsOn, IsPfl); }
        catch (Exception ex) { _logger.LogWarning(ex, "SetAuxRoute(ON) {Index} failed", Index); }
    }

    // True while applying a volume pushed FROM the engine, so the setter doesn't echo it back.
    private bool _suppressVolumePush;

    partial void OnVolumeChanged(double value)
    {
        if (_suppressVolumePush) return;
        if (!IsLoaded) return;
        _ = SetVolumeAsync((float)(value / 100.0));
    }

    private async Task SetVolumeAsync(float gain)
    {
        try { await _api.SetAuxVolumeAsync(Index, gain); }
        catch (Exception ex) { _logger.LogWarning(ex, "SetAuxVolume {Index} failed", Index); }
    }

    // ── Event-driven updates (from the engine, via the parent VM) ───────────────

    public void UpdatePosition(uint positionMs, uint durationMs)
    {
        if (durationMs > 0) DurationMs = durationMs;
        PositionMs = positionMs;
        IsPlaying  = true;
    }

    public void OnDeactivated()
    {
        IsPlaying = false;
        PositionMs = 0;
    }

    public void UpdateLevel(float levelDb) => LevelDb = levelDb;
    public void SetActive(bool active)     => IsActive = active;

    // Fills the tile when a deck is (re)loaded by ANY trigger (in-app, API, MIDI/script, hardware).
    public async void ApplyLoaded(string filePath, uint durationMs, uint startMs, uint endMs, string waveformPath)
    {
        FileName   = Path.GetFileName(filePath);
        DurationMs = durationMs;
        StartCueMs = startMs;
        EndCueMs   = endMs;
        PositionMs = 0;
        IsPlaying  = false;
        IsLoaded   = true;
        try { WaveformPeaks = await LoadPeaksAsync(waveformPath); }
        catch (Exception ex) { _logger.LogWarning(ex, "ApplyLoaded {Index}: waveform load failed", Index); }
    }

    public void ApplyLoop(bool loop)          => IsLooping = loop;
    public void ApplyRoute(bool on, bool pfl) { IsOn = on; IsPfl = pfl; }

    public void ApplyVolume(float gain)
    {
        _suppressVolumePush = true;
        Volume = Math.Clamp(gain * 100.0, 0, 100);
        _suppressVolumePush = false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<float[]?> LoadPeaksAsync(string waveformPath)
    {
        if (string.IsNullOrEmpty(waveformPath) || !File.Exists(waveformPath)) return null;
        var bytes = await File.ReadAllBytesAsync(waveformPath);
        var peaks = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, peaks, 0, peaks.Length * sizeof(float));
        return peaks;
    }

    private static string FormatTenths(uint ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }
}

// ── Top-level VM ──────────────────────────────────────────────────────────────

public sealed class AuxPlayersViewModel : IDisposable
{
    private readonly ApiClientService             _api;
    private readonly IEventBus                    _eventBus;
    private readonly ILogger<AuxPlayersViewModel> _logger;

    private readonly Action<AuxLevelChangedEvent>    _levelHandler;
    private readonly Action<AuxActivatedEvent>       _activatedHandler;
    private readonly Action<AuxDeactivatedEvent>     _deactivatedHandler;
    private readonly Action<AuxPositionChangedEvent> _positionHandler;
    private readonly Action<AuxLoadedEvent>          _loadedHandler;
    private readonly Action<AuxLoopChangedEvent>     _loopHandler;
    private readonly Action<AuxVolumeChangedEvent>   _volumeHandler;
    private readonly Action<AuxRouteChangedEvent>    _routeHandler;

    public ObservableCollection<AuxChannelViewModel> Channels { get; } = new();

    /// Set by MainWindow; opens the system file picker and returns the chosen audio path.
    public Func<Task<string?>>? PickAudioFileAsync { get; set; }

    public AuxPlayersViewModel(
        ApiClientService             api,
        IEventBus                    eventBus,
        ILogger<AuxPlayersViewModel> logger)
    {
        _api      = api;
        _eventBus = eventBus;
        _logger   = logger;

        for (int i = 0; i < 4; i++)
        {
            var idx = i;
            Channels.Add(new AuxChannelViewModel(
                idx, _api,
                () => PickAudioFileAsync is null ? Task.FromResult<string?>(null) : PickAudioFileAsync(),
                _logger));
        }

        _levelHandler       = OnLevelChanged;
        _activatedHandler   = OnAuxActivated;
        _deactivatedHandler = OnAuxDeactivated;
        _positionHandler    = OnAuxPositionChanged;
        _loadedHandler      = OnAuxLoaded;
        _loopHandler        = OnAuxLoopChanged;
        _volumeHandler      = OnAuxVolumeChanged;
        _routeHandler       = OnAuxRouteChanged;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate()
    {
        _eventBus.Subscribe(_levelHandler);
        _eventBus.Subscribe(_activatedHandler);
        _eventBus.Subscribe(_deactivatedHandler);
        _eventBus.Subscribe(_positionHandler);
        _eventBus.Subscribe(_loadedHandler);
        _eventBus.Subscribe(_loopHandler);
        _eventBus.Subscribe(_volumeHandler);
        _eventBus.Subscribe(_routeHandler);
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe(_levelHandler);
        _eventBus.Unsubscribe(_activatedHandler);
        _eventBus.Unsubscribe(_deactivatedHandler);
        _eventBus.Unsubscribe(_positionHandler);
        _eventBus.Unsubscribe(_loadedHandler);
        _eventBus.Unsubscribe(_loopHandler);
        _eventBus.Unsubscribe(_volumeHandler);
        _eventBus.Unsubscribe(_routeHandler);
    }

    // ── Event handlers (may be called from the audio/monitor thread) ───────────

    private void OnLevelChanged(AuxLevelChangedEvent e)
    {
        if (e.AuxIndex < 0 || e.AuxIndex >= Channels.Count) return;
        Dispatcher.UIThread.Post(() => Channels[e.AuxIndex].UpdateLevel(e.LevelDb));
    }

    private void OnAuxActivated(AuxActivatedEvent e)
    {
        if (e.AuxIndex < 0 || e.AuxIndex >= Channels.Count) return;
        Dispatcher.UIThread.Post(() => Channels[e.AuxIndex].SetActive(true));
    }

    private void OnAuxDeactivated(AuxDeactivatedEvent e)
    {
        if (e.AuxIndex < 0 || e.AuxIndex >= Channels.Count) return;
        Dispatcher.UIThread.Post(() =>
        {
            Channels[e.AuxIndex].SetActive(false);
            Channels[e.AuxIndex].OnDeactivated();
        });
    }

    private void OnAuxPositionChanged(AuxPositionChangedEvent e)
    {
        if (e.AuxIndex < 0 || e.AuxIndex >= Channels.Count) return;
        Dispatcher.UIThread.Post(() => Channels[e.AuxIndex].UpdatePosition(e.PositionMs, e.DurationMs));
    }

    private void OnAuxLoaded(AuxLoadedEvent e)
    {
        if (e.AuxIndex < 0 || e.AuxIndex >= Channels.Count) return;
        Dispatcher.UIThread.Post(() =>
            Channels[e.AuxIndex].ApplyLoaded(e.FilePath, e.DurationMs, e.StartMs, e.EndMs, e.WaveformPath));
    }

    private void OnAuxLoopChanged(AuxLoopChangedEvent e)
    {
        if (e.AuxIndex < 0 || e.AuxIndex >= Channels.Count) return;
        Dispatcher.UIThread.Post(() => Channels[e.AuxIndex].ApplyLoop(e.Loop));
    }

    private void OnAuxVolumeChanged(AuxVolumeChangedEvent e)
    {
        if (e.AuxIndex < 0 || e.AuxIndex >= Channels.Count) return;
        Dispatcher.UIThread.Post(() => Channels[e.AuxIndex].ApplyVolume(e.Gain));
    }

    private void OnAuxRouteChanged(AuxRouteChangedEvent e)
    {
        if (e.AuxIndex < 0 || e.AuxIndex >= Channels.Count) return;
        Dispatcher.UIThread.Post(() => Channels[e.AuxIndex].ApplyRoute(e.On, e.Pfl));
    }
}
