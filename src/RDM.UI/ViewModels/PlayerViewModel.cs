using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Shared.DTOs;
using RDM.UI.Controls;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService        _api;
    private readonly CountdownService        _countdown;
    private readonly IAudioEngine            _audioEngine;
    private readonly IEventBus               _eventBus;
    private readonly ILogger<PlayerViewModel> _logger;
    private readonly Action<RDM.Core.Events.PlayerLoopChangedEvent> _loopHandler;
    private readonly DispatcherTimer         _clockTimer;
    private readonly DispatcherTimer         _vuTimer;

    private static readonly IBrush ConnectedBrush    = new SolidColorBrush(Color.Parse("#4CAF50"));
    private static readonly IBrush DisconnectedBrush = new SolidColorBrush(Color.Parse("#F44336"));

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty] private string _title  = "";
    [ObservableProperty] private string _artist = "";

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(TitleDisplay));

    public string TitleDisplay => string.IsNullOrEmpty(Title)
        ? Localizer.Instance?["player.no_playback"] ?? "(no playback)"
        : Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusText))]
    [NotifyPropertyChangedFor(nameof(ConnectionStatusBrush))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromPlayerCommand))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveFromPlayerCommand))]
    private bool _isExecutingCommand;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseIcon))]
    private bool _isPlaying;

    public string PlayPauseIcon => IsPlaying ? "⏸" : "▶";

    public string ConnectionStatusText  => IsConnected
        ? Localizer.Instance?["player.connected"]    ?? "● CONNECTED"
        : Localizer.Instance?["player.disconnected"] ?? "● DISCONNECTED";
    public IBrush ConnectionStatusBrush => IsConnected ? ConnectedBrush : DisconnectedBrush;

    // ── Real-time System Clock ───────────────────────────────────────────────

    [ObservableProperty] private string _currentTimeText = "";
    [ObservableProperty] private string _currentDayOfWeek = "";
    [ObservableProperty] private string _currentDateText = "";
    [ObservableProperty] private string _currentFullDateText = "";
    [ObservableProperty] private int    _currentSecond = 0;

    // ── Active Track Details ─────────────────────────────────────────────────

    private string? _currentAssetId;
    // Display-only VU meter dB correction for the current track (LoudnessTargetLufs - Asset.LoudnessLufs).
    // Does not affect playback gain — see PlaylistEngine.OnTrackStartedAsync.
    private double  _currentVuOffsetDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverImage))]
    private Bitmap? _coverImage;

    public bool HasCoverImage => CoverImage is not null;

    [ObservableProperty] private float[]? _waveformPeaks;
    [ObservableProperty] private uint     _durationMs = 1;
    [ObservableProperty] private uint?    _introCueMs;
    [ObservableProperty] private uint?    _outroCueMs;
    [ObservableProperty] private IReadOnlyList<CueMarker>? _markers;
    [ObservableProperty] private decimal? _bpm;
    [ObservableProperty] private string   _formatName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRegularTrack))]
    private bool _isLiveStream;

    [ObservableProperty] private string _streamNowPlaying = "";

    public bool IsRegularTrack => !IsLiveStream;

    [ObservableProperty] private bool _isLoopEnabled;
    [ObservableProperty] private bool _isStopNextEnabled;

    // True while applying loop state pushed FROM the playout engine, so the setter doesn't echo
    // it straight back as a new request.
    private bool _suppressLoopPush;

    partial void OnIsLoopEnabledChanged(bool value)
    {
        if (_suppressLoopPush) return;
        _ = _api.SetPlayerLoopAsync(value);
    }

    /// Applies the loop state the playout engine actually holds — from the initial NowPlaying
    /// snapshot or a PlayerLoopChangedEvent. Keeps the button from drifting away from reality
    /// (a request can be rejected, and the engine also owns the state across reconnects).
    public void ApplyLoop(bool loop)
    {
        _suppressLoopPush = true;
        IsLoopEnabled     = loop;
        _suppressLoopPush = false;
    }
    [ObservableProperty] private double _vuLeft;
    [ObservableProperty] private double _vuRight;

    public CountdownService Countdown => _countdown;

    public uint CurrentPositionMs => _countdown.PositionMs;

    public string RemainingTimeText
    {
        get
        {
            var referenceMs = OutroCueMs ?? DurationMs;
            var posMs = _countdown.PositionMs;
            if (referenceMs > posMs)
            {
                var remainingMs = referenceMs - posMs;
                var t = TimeSpan.FromMilliseconds(remainingMs);
                return $"{t.Minutes:D2}:{t.Seconds:D2}";
            }
            return "00:00";
        }
    }

    public IBrush RemainingTimeBrush => _countdown.IsRedPhase
        ? new SolidColorBrush(Color.Parse("#FF8C42")) // Orange/Red outro countdown
        : new SolidColorBrush(Color.Parse("#5DDC8A")); // Green normal countdown

    public string MetaText
    {
        get
        {
            var bpmText = Bpm.HasValue ? $"{Bpm.Value:F0} BPM" : Localizer.Instance?["player.no_bpm"] ?? "No BPM";
            var categoryText = !string.IsNullOrWhiteSpace(FormatName) ? FormatName : Localizer.Instance?["player.track_fallback"] ?? "TRACK";
            var targetLabel = Localizer.Instance?["player.target_label"] ?? "TARGET:";
            var totalTime = TimeSpan.FromMilliseconds(DurationMs);
            return $"{categoryText}  |  {bpmText}  |  {targetLabel} {totalTime.Minutes:D2}:{totalTime.Seconds:D2}";
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public PlayerViewModel(
        ApiClientService api,
        CountdownService countdown,
        IAudioEngine audioEngine,
        IEventBus eventBus,
        ILogger<PlayerViewModel> logger)
    {
        _api         = api;
        _countdown   = countdown;
        _audioEngine = audioEngine;
        _eventBus    = eventBus;
        _logger      = logger;

        // Fired from the playout engine (possibly off the UI thread) whenever loop changes,
        // whatever triggered it — this window, the API, or a MIDI/script action.
        _loopHandler = e => Dispatcher.UIThread.Post(() => ApplyLoop(e.Loop));

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _clockTimer.Tick += (_, _) => UpdateClock();
        UpdateClock();

        _vuTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _vuTimer.Tick += (_, _) => UpdateVuMeters();

        if (Localizer.Instance is not null)
            Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TitleDisplay));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(MetaText));
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate()
    {
        _api.TrackStarted           += OnTrackStarted;
        _api.TrackEnded             += OnTrackEnded;
        _api.PlaylistStopped        += OnPlaylistStopped;
        _api.WaveformReady          += OnWaveformReady;
        _api.ConnectionStateChanged += OnConnectionStateChanged;
        _api.StreamMetaChanged      += OnStreamMetaChanged;
        _countdown.PropertyChanged  += OnCountdownPropertyChanged;
        _eventBus.Subscribe(_loopHandler);
        _clockTimer.Start();
        _vuTimer.Start();
        _ = _api.StartWebSocketAsync();
        _ = LoadInitialStateAsync();
    }

    public void Dispose()
    {
        _api.TrackStarted           -= OnTrackStarted;
        _api.TrackEnded             -= OnTrackEnded;
        _api.PlaylistStopped        -= OnPlaylistStopped;
        _api.WaveformReady          -= OnWaveformReady;
        _api.ConnectionStateChanged -= OnConnectionStateChanged;
        _api.StreamMetaChanged      -= OnStreamMetaChanged;
        _countdown.PropertyChanged  -= OnCountdownPropertyChanged;
        _eventBus.Unsubscribe(_loopHandler);
        _clockTimer.Stop();
        _vuTimer.Stop();

        if (Localizer.Instance is not null)
            Localizer.Instance.PropertyChanged -= OnLanguageChanged;
    }

    private async Task LoadInitialStateAsync()
    {
        try
        {
            var dto = await _api.GetNowPlayingAsync();
            if (dto is not null)
                Dispatcher.UIThread.Post(() =>
                {
                    IsPlaying = dto.State == "PLAYING";
                    ApplyLoop(dto.LoopCurrent);
                });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LoadInitialStateAsync failed");
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnTrackEnded(TrackEndedPayload _)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            ClearPlayerDisplay();
        });
    }

    private void OnPlaylistStopped()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            ClearPlayerDisplay();
        });
    }

    private void OnTrackStarted(TrackStartedPayload p)
    {
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            IsPlaying       = true;
            _currentAssetId = p.AssetId;
            _currentVuOffsetDb = p.VuOffsetDb;
            Title  = p.Title  ?? Localizer.Instance?["common.no_title"] ?? "(no title)";
            Artist = p.Artist ?? "";

            // Reset current peaks and parameters
            WaveformPeaks    = null;
            CoverImage       = null;
            DurationMs       = p.DurationMs > 0 ? p.DurationMs : 1u;
            IntroCueMs       = null;
            OutroCueMs       = null;
            Markers          = null;
            Bpm              = null;
            FormatName       = "";
            IsLiveStream     = false;
            StreamNowPlaying = "";

            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(RemainingTimeText));

            if (!string.IsNullOrEmpty(p.AssetId))
            {
                try
                {
                    var detail = await _api.GetAssetDetailAsync(p.AssetId);
                    if (detail is not null)
                    {
                        var isStream = detail.AssetType == "InternetStream";
                        IsLiveStream = isStream;

                        if (isStream)
                        {
                            FormatName = "LIVE";
                            OnPropertyChanged(nameof(MetaText));
                            OnPropertyChanged(nameof(RemainingTimeText));
                            return;
                        }

                        Bpm        = detail.Bpm;
                        FormatName = detail.FormatId ?? "";
                        DurationMs = detail.DurationMs > 0 ? detail.DurationMs : 1u;

                        IntroCueMs = detail.CueMarkers?.Intro is double intro ? (uint)(intro * 1000) : null;
                        OutroCueMs = detail.CueMarkers?.Outro is double outro ? (uint)(outro * 1000) : null;
                        Markers    = BuildMarkers(detail.CueMarkers, detail.DurationMs);

                        var compressed = await _api.GetWaveformAsync(p.AssetId);
                        WaveformPeaks = WaveformDecoder.Decode(compressed);
                        if (WaveformPeaks is null)
                            _ = _api.RequestWaveformAsync(p.AssetId);

                        if (!string.IsNullOrEmpty(detail.ImagePath) && File.Exists(detail.ImagePath))
                        {
                            try
                            {
                                using var stream = File.OpenRead(detail.ImagePath);
                                CoverImage = new Bitmap(stream);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "Failed to load cover image bitmap from {Path}", detail.ImagePath);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load active track details in PlayerViewModel");
                }
            }

            OnPropertyChanged(nameof(MetaText));
            OnPropertyChanged(nameof(RemainingTimeText));
        });
    }

    private void OnWaveformReady(WaveformReadyPayload p)
    {
        if (p.AssetId != _currentAssetId) return;
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            try
            {
                var compressed = await _api.GetWaveformAsync(p.AssetId);
                WaveformPeaks = WaveformDecoder.Decode(compressed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load dynamically generated waveform in PlayerViewModel");
            }
        });
    }

    private void OnStreamMetaChanged(StreamMetaChangedPayload p)
    {
        if (p.AssetId != _currentAssetId) return;
        Dispatcher.UIThread.Post(() => StreamNowPlaying = p.StreamTitle);
    }

    private void OnConnectionStateChanged(bool connected)
    {
        Dispatcher.UIThread.InvokeAsync(() => IsConnected = connected);
    }

    private void OnCountdownPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CountdownService.PositionMs))
        {
            OnPropertyChanged(nameof(CurrentPositionMs));
            OnPropertyChanged(nameof(RemainingTimeText));

        }
        else if (e.PropertyName == nameof(CountdownService.IsRedPhase))
        {
            OnPropertyChanged(nameof(RemainingTimeBrush));
        }
    }

    private void UpdateClock()
    {
        var now     = DateTime.Now;
        var culture = GetDateCulture();
        CurrentTimeText     = now.ToString("HH:mm:ss");
        CurrentDayOfWeek    = now.ToString("dddd", culture).ToUpper(culture);
        CurrentDateText     = now.ToString("d MMMM yyyy", culture);
        CurrentFullDateText = now.ToString("dddd d MMMM yyyy", culture) is var s && s.Length > 0
            ? char.ToUpper(s[0]) + s[1..]
            : s;
        CurrentSecond = now.Second;
    }

    private static CultureInfo GetDateCulture()
    {
        var code = Localizer.Instance?.CurrentLanguage ?? "en";
        try { return CultureInfo.GetCultureInfo(code); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    private void UpdateVuMeters()
    {
        var (l, r) = _audioEngine.GetPlaylistLevel(_currentVuOffsetDb);
        VuLeft  = l;
        VuRight = r;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private bool CanExecutePlayerCommand() => IsConnected && !IsExecutingCommand;

    [RelayCommand(CanExecute = nameof(CanExecutePlayerCommand))]
    private async Task PlayPauseAsync(CancellationToken ct)
    {
        IsExecutingCommand = true;
        try
        {
            if (IsPlaying)
                await _api.PauseAsync(ct);
            else
                await _api.PlayAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "PlayPauseCommand failed"); }
        finally { IsExecutingCommand = false; }
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayerCommand))]
    private async Task StopAsync(CancellationToken ct)
    {
        IsExecutingCommand = true;
        try   { await _api.StopAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "StopCommand failed"); }
        finally { IsExecutingCommand = false; }
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayerCommand))]
    private async Task NextAsync(CancellationToken ct)
    {
        IsExecutingCommand = true;
        try   { await _api.NextAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "NextCommand failed"); }
        finally { IsExecutingCommand = false; }
    }

    [RelayCommand(CanExecute = nameof(CanExecutePlayerCommand))]
    private async Task RemoveFromPlayerAsync(CancellationToken ct)
    {
        IsExecutingCommand = true;
        try
        {
            var ok = await _api.RemoveCurrentItemAsync(ct);
            if (ok) ClearPlayerDisplay();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "RemoveFromPlayerCommand failed"); }
        finally { IsExecutingCommand = false; }
    }

    private void ClearPlayerDisplay()
    {
        Title            = "";
        Artist           = "";
        WaveformPeaks    = null;
        CoverImage       = null;
        DurationMs       = 1u;
        IntroCueMs       = null;
        OutroCueMs       = null;
        Markers          = null;
        Bpm              = null;
        FormatName       = "";
        IsLiveStream     = false;
        StreamNowPlaying = "";
        _currentAssetId  = null;
        _currentVuOffsetDb = 0.0;
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(RemainingTimeText));
    }

    [RelayCommand]
    private void Loop()
    {
        IsLoopEnabled = !IsLoopEnabled;
    }

    [RelayCommand]
    private void StopNext()
    {
        IsStopNextEnabled = !IsStopNextEnabled;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IReadOnlyList<CueMarker> BuildMarkers(CueMarkersDto? m, uint durationMs)
    {
        var list = new List<CueMarker>(16);

        if (m is not null)
            foreach (var e in CueMarkerPalette.Enumerate(m))
                list.Add(new CueMarker((uint)(e.Seconds * 1000), e.Color, e.Label));

        // Ensure the track always has visible start/end bookends.
        if (m?.Start is null) list.Insert(0, new CueMarker(0u,         Color.Parse("#5DDC8A"), "START"));
        if (m?.End   is null) list.Add(      new CueMarker(durationMs, Color.Parse("#FFFFFF"), "END"));

        return list;
    }
}
