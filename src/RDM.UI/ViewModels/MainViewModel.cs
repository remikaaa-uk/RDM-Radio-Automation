using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Services;
using RDM.Infrastructure;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;


namespace RDM.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService                _api;
    private readonly PlaybackSessionSnapshotService  _snapshotService;
    private readonly IBackupService                  _backupService;
    private readonly AudioSettings                   _audioSettings;
    private readonly IAudioEngine                    _audioEngine;
    private readonly HostAccessor                    _hostAccessor;
    private readonly INavigationService              _navigation;
    private readonly IEventBus                       _eventBus;
    private readonly SettingsConfigService           _configService;
    private readonly MicDspChainStore                _micDspChainStore;
    private readonly ILogger<MainViewModel>          _logger;

    /// <summary>Exposed for the user badge/menu in the top bar — username, role, ⚙ visibility.</summary>
    public UserSessionContext Session { get; }

    // ── Next scheduled-event bar ──────────────────────────────────────────────
    private const string NextEventNormalColor = "#8FB8FF";
    private const string NextEventBlinkColor  = "#FF3B30";
    private static readonly TimeSpan NextEventBlinkThreshold = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _nextEventTimer;
    private readonly DispatcherTimer _nextEventBlinkTimer;
    private string?  _nextEventId;
    private string   _nextEventName = "";
    private DateTime? _nextEventFiresAt;
    private bool     _nextEventSkip;
    private int      _nextEventPollCounter;
    private bool     _nextEventBlinkOn;

    private static string NoEventsText => Localizer.Instance?["main.no_events"] ?? "No scheduled events";

    [ObservableProperty] private string _nextEventText = NoEventsText;
    [ObservableProperty] private bool   _hasNextEvent;
    [ObservableProperty] private string _nextEventForeground = NextEventNormalColor;

    // ── Bottom-bar output indicators (streaming + recording) ──────────────────

    // Red means "something is leaving the building" — the ON AIR / REC convention, and the same
    // colour for both modules so the bar reads consistently. Idle is the bar's own blue (the same
    // one the next-event text uses), which reads as "ready" rather than as "disabled".
    private const string IndicatorIdleColor      = "#8FB8FF";
    private const string IndicatorLiveColor      = "#FF4444";
    private const string IndicatorErrorColor     = "#FF6B6B";
    private const string IndicatorRecordingColor = "#FF4444";

    private readonly Action<EncoderStatusChangedEvent>   _encoderStatusHandler;
    private readonly Action<RecordingStatusChangedEvent> _recordingStatusHandler;

    /// Last known state per profile. Held here rather than re-queried: the events already carry
    /// everything, and the bar must stay right even while no streaming window is open.
    private readonly Dictionary<string, EncoderSessionState> _encoderStates = new();

    private int _knownProfileCount;
    private int _armedProfileCount;
    private RecordingState _recordingState = RecordingState.Stopped;

    [ObservableProperty] private string _streamingIndicatorColor = IndicatorIdleColor;
    [ObservableProperty] private string _streamingIndicatorTooltip = "";
    [ObservableProperty] private string _recordingIndicatorColor = IndicatorIdleColor;
    [ObservableProperty] private string _recordingIndicatorTooltip = "";

    /// Whether each module is switched on in Settings. Drives the button's very existence in the
    /// bottom bar, not merely its enabled state.
    [ObservableProperty] private bool _isStreamingModuleEnabled;
    [ObservableProperty] private bool _isRecordingModuleEnabled;

    // ── Properties ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isShuttingDown;

    [ObservableProperty]
    private int _selectedTabIndex;

    public RelayCommand<string> SelectTabCommand { get; }

    public void SelectTab(int index) => SelectedTabIndex = index;

    public PlayerViewModel    PlayerViewModel    { get; }
    public PlaylistViewModel  PlaylistViewModel  { get; }
    public LibraryViewModel   LibraryViewModel   { get; }
    public HistoryViewModel   HistoryViewModel   { get; }
    public CartwallViewModel  CartwallViewModel  { get; }
    public AuxPlayersViewModel AuxPlayersViewModel { get; }
    public SweeperSubcategoryViewModel SweeperSubcategory { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel(
        PlayerViewModel                 playerViewModel,
        PlaylistViewModel               playlistViewModel,
        LibraryViewModel                libraryViewModel,
        HistoryViewModel                historyViewModel,
        CartwallViewModel               cartwallViewModel,
        AuxPlayersViewModel             auxPlayersViewModel,
        SweeperSubcategoryViewModel     sweeperSubcategory,
        ApiClientService                api,
        PlaybackSessionSnapshotService  snapshotService,
        IBackupService                  backupService,
        AudioSettings                   audioSettings,
        IAudioEngine                    audioEngine,
        HostAccessor                    hostAccessor,
        IActionRegistry                 actionRegistry,
        INavigationService              navigation,
        IEventBus                       eventBus,
        SettingsConfigService           configService,
        MicDspChainStore                micDspChainStore,
        UserSessionContext              session,
        ILogger<MainViewModel>          logger)
    {
        PlayerViewModel     = playerViewModel;
        PlaylistViewModel   = playlistViewModel;
        LibraryViewModel    = libraryViewModel;
        HistoryViewModel    = historyViewModel;
        CartwallViewModel   = cartwallViewModel;
        AuxPlayersViewModel = auxPlayersViewModel;
        SweeperSubcategory  = sweeperSubcategory;
        _api             = api;
        _snapshotService = snapshotService;
        _backupService   = backupService;
        _audioSettings   = audioSettings;
        _audioEngine     = audioEngine;
        _hostAccessor    = hostAccessor;
        _navigation      = navigation;
        _eventBus        = eventBus;
        _configService   = configService;
        _micDspChainStore = micDspChainStore;
        _logger          = logger;
        Session          = session;

        RegisterHardwareActions(actionRegistry);

        // Bottom-bar output indicators. Event-driven: a stream that drops and reconnects on its
        // own must change the icon without anything polling for it.
        _encoderStatusHandler   = OnEncoderStatusForBar;
        _recordingStatusHandler = OnRecordingStatusForBar;
        _eventBus.Subscribe(_encoderStatusHandler);
        _eventBus.Subscribe(_recordingStatusHandler);
        _ = RefreshOutputModulesAsync();

        SelectTabCommand = new RelayCommand<string>(
            s => SelectTab(int.TryParse(s, out var i) ? i : 0));

        // Next scheduled-event bar: 1 s UI tick updates the countdown; every 10th tick
        // re-polls the API. Schedule changes (create/edit/delete) refresh immediately.
        _api.ScheduleChanged += OnScheduleChangedForBar;
        _nextEventTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _nextEventTimer.Tick += OnNextEventTick;
        _nextEventTimer.Start();

        // Separate, faster timer drives the red blink in the last 30s before firing —
        // decoupled from the 1 s countdown tick so the blink rate isn't tied to it.
        _nextEventBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _nextEventBlinkTimer.Tick += OnNextEventBlinkTick;
        _nextEventBlinkTimer.Start();

        _ = RefreshNextEventAsync();
        _ = SweeperSubcategory.ReloadAsync();
    }

    // ── Next scheduled-event bar ──────────────────────────────────────────────

    private void OnScheduleChangedForBar()
        => Dispatcher.UIThread.Post(() => _ = RefreshNextEventAsync());

    private void OnNextEventTick(object? sender, EventArgs e)
    {
        if (++_nextEventPollCounter >= 10)
        {
            _nextEventPollCounter = 0;
            _ = RefreshNextEventAsync();
        }
        else
        {
            UpdateNextEventText();
        }
    }

    private async Task RefreshNextEventAsync()
    {
        try
        {
            var next = await _api.GetNextEventAsync();
            if (next is null)
            {
                _nextEventId      = null;
                _nextEventFiresAt = null;
                HasNextEvent      = false;
                NextEventText     = NoEventsText;
                return;
            }

            _nextEventId      = next.EventId;
            _nextEventName    = next.Name;
            _nextEventFiresAt = next.FiresAt;
            _nextEventSkip    = next.SkipNext;
            HasNextEvent      = true;
            UpdateNextEventText();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Next-event refresh failed");
        }
    }

    private void UpdateNextEventText()
    {
        if (_nextEventFiresAt is null)
        {
            NextEventText       = NoEventsText;
            NextEventForeground = NextEventNormalColor;
            return;
        }

        var remaining = _nextEventFiresAt.Value - DateTime.Now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        string when = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
            : $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";

        NextEventText = _nextEventSkip
            ? string.Format(Localizer.Instance?["main.event_skipped"] ?? "⏭ {0} — SKIPPED", _nextEventName)
            : string.Format(Localizer.Instance?["main.event_in"] ?? "{0} — in {1}", _nextEventName, when);

        if (_nextEventSkip || remaining > NextEventBlinkThreshold || remaining <= TimeSpan.Zero)
        {
            _nextEventBlinkOn    = false;
            NextEventForeground  = NextEventNormalColor;
        }
    }

    private void OnNextEventBlinkTick(object? sender, EventArgs e)
    {
        bool imminent = false;
        if (!_nextEventSkip && _nextEventFiresAt.HasValue)
        {
            var remaining = _nextEventFiresAt.Value - DateTime.Now;
            imminent = remaining > TimeSpan.Zero && remaining <= NextEventBlinkThreshold;
        }

        if (!imminent)
        {
            _nextEventBlinkOn   = false;
            NextEventForeground = NextEventNormalColor;
            return;
        }

        _nextEventBlinkOn   = !_nextEventBlinkOn;
        NextEventForeground = _nextEventBlinkOn ? NextEventBlinkColor : NextEventNormalColor;
    }

    [RelayCommand]
    private async Task SkipNextEvent()
    {
        if (_nextEventId is null) return;
        try
        {
            await _api.PatchEventAsync(_nextEventId,
                new ScheduledEventPatchDto(Enabled: null, SkipNext: !_nextEventSkip));
            await RefreshNextEventAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skip next event failed");
        }
    }

    // ── Hardware action delegates ─────────────────────────────────────────────

    private void RegisterHardwareActions(IActionRegistry reg)
    {
        // ── Fader start (D&R) ─────────────────────────────────────────────────
        reg.RegisterAction(ActionId.MixerFaderStart, async payload =>
        {
            var isUp = payload is not NormalizedAnalogPayload analog || analog.Value > 0f;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (isUp)
                {
                    if (PlayerViewModel.PlayPauseCommand.CanExecute(null))
                        await PlayerViewModel.PlayPauseCommand.ExecuteAsync(null);
                }
                else
                {
                    if (PlayerViewModel.StopCommand.CanExecute(null))
                        await PlayerViewModel.StopCommand.ExecuteAsync(null);
                }
            });
        });

        // ── Player ────────────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.PlayerPlayStopToggle, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlayerViewModel.PlayPauseCommand.CanExecute(null))
                    await PlayerViewModel.PlayPauseCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlayerPlay, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!PlayerViewModel.IsPlaying && PlayerViewModel.PlayPauseCommand.CanExecute(null))
                    await PlayerViewModel.PlayPauseCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlayerPause, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlayerViewModel.IsPlaying && PlayerViewModel.PlayPauseCommand.CanExecute(null))
                    await PlayerViewModel.PlayPauseCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlayerStop, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlayerViewModel.StopCommand.CanExecute(null))
                    await PlayerViewModel.StopCommand.ExecuteAsync(null);
            }));

        // StopFade — brak osobnego API fade; odpada na StopCommand
        reg.RegisterAction(ActionId.PlayerStopFade, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlayerViewModel.StopCommand.CanExecute(null))
                    await PlayerViewModel.StopCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlayerNext, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlayerViewModel.NextCommand.CanExecute(null))
                    await PlayerViewModel.NextCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlayerLoopToggle, async _ =>
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (PlayerViewModel.LoopCommand.CanExecute(null))
                    PlayerViewModel.LoopCommand.Execute(null);
            }));

        reg.RegisterAction(ActionId.PlayerRemoveFromPlayer, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlayerViewModel.RemoveFromPlayerCommand.CanExecute(null))
                    await PlayerViewModel.RemoveFromPlayerCommand.ExecuteAsync(null);
            }));

        // ── Playlist ──────────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.PlaylistModeAuto, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlaylistViewModel.SetModeAutoCommand.CanExecute(null))
                    await PlaylistViewModel.SetModeAutoCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlaylistModeManual, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlaylistViewModel.SetModeManualCommand.CanExecute(null))
                    await PlaylistViewModel.SetModeManualCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlaylistModeLiveAssist, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlaylistViewModel.SetModeAssistCommand.CanExecute(null))
                    await PlaylistViewModel.SetModeAssistCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlaylistModeCycle, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var next = PlaylistViewModel.Mode switch
                {
                    "AUTO"        => PlaylistViewModel.SetModeAssistCommand,
                    "LIVE_ASSIST" => PlaylistViewModel.SetModeManualCommand,
                    _             => PlaylistViewModel.SetModeAutoCommand,
                };
                if (next.CanExecute(null))
                    await next.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlaylistRemoveSelected, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlaylistViewModel.RemoveSelectedCommand.CanExecute(null))
                    await PlaylistViewModel.RemoveSelectedCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.PlaylistClear, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlaylistViewModel.ClearPlaylistCommand.CanExecute(null))
                    await PlaylistViewModel.ClearPlaylistCommand.ExecuteAsync(null);
            }));

        // ── Aux players ──────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.AuxPlay1,     async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[0].PlayCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxPlay2,     async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[1].PlayCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxPlay3,     async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[2].PlayCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxPlay4,     async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[3].PlayCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxStop1,     async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[0].StopCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxStop2,     async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[1].StopCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxStop3,     async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[2].StopCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxStop4,     async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[3].StopCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxToggleOn1,   async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[0].ToggleOnCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxToggleOn2,   async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[1].ToggleOnCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxToggleOn3,   async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[2].ToggleOnCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxToggleOn4,   async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[3].ToggleOnCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxToggleLoop1, async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[0].ToggleLoopCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxToggleLoop2, async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[1].ToggleLoopCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxToggleLoop3, async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[2].ToggleLoopCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxToggleLoop4, async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[3].ToggleLoopCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxTogglePfl1,  async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[0].TogglePflCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxTogglePfl2,  async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[1].TogglePflCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxTogglePfl3,  async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[2].TogglePflCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxTogglePfl4,  async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[3].TogglePflCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxEject1,      async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[0].EjectCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxEject2,      async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[1].EjectCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxEject3,      async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[2].EjectCommand.ExecuteAsync(null)));
        reg.RegisterAction(ActionId.AuxEject4,      async _ => await Dispatcher.UIThread.InvokeAsync(async () => await AuxPlayersViewModel.Channels[3].EjectCommand.ExecuteAsync(null)));

        // ── Mic ───────────────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.MicToggle, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlaylistViewModel.ToggleMicCommand.CanExecute(null))
                    await PlaylistViewModel.ToggleMicCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.MicOn, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!PlaylistViewModel.IsMicActive && PlaylistViewModel.ToggleMicCommand.CanExecute(null))
                    await PlaylistViewModel.ToggleMicCommand.ExecuteAsync(null);
            }));

        reg.RegisterAction(ActionId.MicOff, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlaylistViewModel.IsMicActive && PlaylistViewModel.ToggleMicCommand.CanExecute(null))
                    await PlaylistViewModel.ToggleMicCommand.ExecuteAsync(null);
            }));

        // ── Emergency panic — omija kolejkę (bypass w ActionRouter) ──────────
        reg.RegisterAction(ActionId.AutomationEmergencyPanic, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (PlayerViewModel.StopCommand.CanExecute(null))
                    await PlayerViewModel.StopCommand.ExecuteAsync(null);
                if (PlaylistViewModel.IsMicActive && PlaylistViewModel.ToggleMicCommand.CanExecute(null))
                    await PlaylistViewModel.ToggleMicCommand.ExecuteAsync(null);
            }));

        // ── Cartwall — sloty z parametrem ────────────────────────────────────
        reg.RegisterAction(ActionId.CartwallPlaySlot, async payload =>
        {
            var param = (payload as ParameterizedPayload)?.Parameter;
            if (int.TryParse(param, out var slot))
                await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(slot - 1));
        });

        reg.RegisterAction(ActionId.CartwallStopSlot, async payload =>
        {
            _logger.LogWarning("CartwallStopSlot: brak publicznego API stop-per-slot, parametr={P}",
                (payload as ParameterizedPayload)?.Parameter);
            await Task.CompletedTask;
        });

        reg.RegisterAction(ActionId.CartwallStopAll, async _ =>
        {
            _logger.LogWarning("CartwallStopAll: brak publicznego API stop-all w CartwallViewModel");
            await Task.CompletedTask;
        });

        reg.RegisterAction(ActionId.CartwallToggleLoop, async _ =>
        {
            _logger.LogWarning("CartwallToggleLoop: brak publicznego API toggle-loop");
            await Task.CompletedTask;
        });

        reg.RegisterAction(ActionId.CartwallSetBank, async payload =>
        {
            var param = (payload as ParameterizedPayload)?.Parameter;
            if (int.TryParse(param, out var bank))
                await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.SelectTab(bank - 1));
        });

        // ── Playlist ──────────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.PlaylistSkipNextEvent, async _ =>
        {
            _logger.LogWarning("PlaylistSkipNextEvent: brak implementacji w API");
            await Task.CompletedTask;
        });

        // ── Mic talkback ──────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.MicTalkback, async _ =>
        {
            _logger.LogWarning("MicTalkback: brak dedykowanego API talkback");
            await Task.CompletedTask;
        });

        // ── PFL (prefader listen) ──────────────────────────────────────────────
        reg.RegisterAction(ActionId.PflStart, async _ =>
        {
            _logger.LogWarning("PflStart: brak implementacji PFL w bieżącej wersji");
            await Task.CompletedTask;
        });
        reg.RegisterAction(ActionId.PflStop, async _ =>
        {
            _logger.LogWarning("PflStop: brak implementacji PFL");
            await Task.CompletedTask;
        });
        reg.RegisterAction(ActionId.PflSeek, async _ =>
        {
            _logger.LogWarning("PflSeek: brak implementacji PFL");
            await Task.CompletedTask;
        });

        // ── Player volume (brak SetVolume w API) ─────────────────────────────
        reg.RegisterAction(ActionId.PlayerSetVolume, async _ =>
        {
            _logger.LogWarning("PlayerSetVolume: brak API SetVolume w bieżącej wersji");
            await Task.CompletedTask;
        });

        // ── Recorder ─────────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.RecorderStart, async _ =>
        {
            _logger.LogWarning("RecorderStart: brak implementacji nagrywania w bieżącej wersji");
            await Task.CompletedTask;
        });
        reg.RegisterAction(ActionId.RecorderStop, async _ =>
        {
            _logger.LogWarning("RecorderStop: brak implementacji nagrywania");
            await Task.CompletedTask;
        });

        // ── Visual trigger timer ───────────────────────────────────────────────
        reg.RegisterAction(ActionId.VisualTriggerTimer, async _ =>
        {
            _logger.LogWarning("VisualTriggerTimer: brak implementacji");
            await Task.CompletedTask;
        });

        // ── Edytor / Save / Undo / Redo ───────────────────────────────────────
        reg.RegisterAction(ActionId.WindowTrackEditor, async _ =>
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var assetId = LibraryViewModel.SelectedItem?.AssetId;
                if (assetId is null)
                {
                    _logger.LogDebug("WindowTrackEditor: brak zaznaczonego nagrania w bibliotece");
                    return;
                }
                await _navigation.ShowModalAsync<Views.TrackEditorWindow>(assetId);
            }));
        reg.RegisterAction(ActionId.Save, async _ =>
        {
            _logger.LogWarning("Save: brak globalnego Save w bieżącym kontekście");
            await Task.CompletedTask;
        });
        reg.RegisterAction(ActionId.Undo, async _ =>
        {
            _logger.LogWarning("Undo: brak globalnego Undo");
            await Task.CompletedTask;
        });
        reg.RegisterAction(ActionId.Redo, async _ =>
        {
            _logger.LogWarning("Redo: brak globalnego Redo");
            await Task.CompletedTask;
        });

        // ── Cartwall tabs ─────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.CartwallTab1, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.SelectTab(0)));
        reg.RegisterAction(ActionId.CartwallTab2, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.SelectTab(1)));
        reg.RegisterAction(ActionId.CartwallTab3, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.SelectTab(2)));
        reg.RegisterAction(ActionId.CartwallTab4, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.SelectTab(3)));
        reg.RegisterAction(ActionId.CartwallTab5, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.SelectTab(4)));
        reg.RegisterAction(ActionId.CartwallTab6, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.SelectTab(5)));
        reg.RegisterAction(ActionId.CartwallTab7, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.SelectTab(6)));

        reg.RegisterAction(ActionId.CartwallToggleMode, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.ToggleMode()));

        // ── Cartwall slots ────────────────────────────────────────────────────
        reg.RegisterAction(ActionId.CartwallTriggerSlot1,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(0)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot2,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(1)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot3,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(2)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot4,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(3)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot5,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(4)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot6,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(5)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot7,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(6)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot8,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(7)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot9,  async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(8)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot10, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(9)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot11, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(10)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot12, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(11)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot13, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(12)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot14, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(13)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot15, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(14)));
        reg.RegisterAction(ActionId.CartwallTriggerSlot16, async _ => await Dispatcher.UIThread.InvokeAsync(() => CartwallViewModel.TriggerSlot(15)));

        // ── Window navigation ─────────────────────────────────────────────────
        reg.RegisterAction(ActionId.WindowTracksManager,   async _ => await Dispatcher.UIThread.InvokeAsync(() => _navigation.OpenOrFocusWindow<TracksManagerWindow>()));
        reg.RegisterAction(ActionId.WindowPlaylistBuilder, async _ => await Dispatcher.UIThread.InvokeAsync(() => _navigation.OpenOrFocusWindow<PlaylistBuilderWindow>()));
        reg.RegisterAction(ActionId.WindowScheduledEvents, async _ => await Dispatcher.UIThread.InvokeAsync(() => _navigation.OpenOrFocusWindow<ScheduledEventsWindow>()));
        // Hiding the ⌨ button in MainWindow.axaml only hides the entry point — a hardware trigger
        // mapped to this action would otherwise still open the window for an Operator, so the same
        // role check has to be repeated here.
        reg.RegisterAction(ActionId.WindowHardwareManager, async _ => await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!Session.CanAccessSettings)
            {
                _logger.LogWarning("WindowHardwareManager trigger ignored: user {Username} lacks Admin role", Session.CurrentUser?.Username);
                return;
            }
            _navigation.OpenOrFocusWindow<HardwareManagerWindow>();
        }));
    }

    // ── Shutdown sequence ─────────────────────────────────────────────────────

    public async Task ShutdownSequenceAsync(CancellationToken ct)
    {
        IsShuttingDown = true;

        // ── Step 0: Close secondary windows, stop WebSocket ──────────────────
        await _navigation.CloseAllAsync();
        _api.StopWebSocket();
        PlayerViewModel.Dispose();

        // ── Step 1: Fade out ──────────────────────────────────────────────────
        await StepAsync("fade-out",
            () => _api.StopAsync(ct), ct);

        // ── Step 2: Snapshot PlaybackSession ──────────────────────────────────
        await StepAsync("snapshot",
            () => _snapshotService.TriggerSnapshotAsync(ct), ct);

        // ── Step 3: Backup (conditional) ─────────────────────────────────────
        if (_audioSettings.BackupOnClose)
        {
            await StepAsync("backup",
                () => _backupService.BackupNowAsync(ct), ct);
        }

        // ── Step 3.5: Mic DSP chain ───────────────────────────────────────────
        // Must run before BASS_Free: reading a plugin's settings needs the plugin still loaded.
        await StepAsync("mic-dsp-save",
            () => _micDspChainStore.SaveAsync(), ct);

        // ── Step 4: BASS_Free ─────────────────────────────────────────────────
        await StepAsync("bass-free",
            () => _audioEngine.ShutdownAsync(ct), ct);

        // ── Step 5: DB connections ────────────────────────────────────────────
        // No-op — Dapper opens/closes per method call.

        // ── Step 6: Stop host ─────────────────────────────────────────────────
        await StepAsync("host-stop",
            () => _hostAccessor.Host?.StopAsync(CancellationToken.None) ?? Task.CompletedTask,
            CancellationToken.None);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _nextEventTimer.Stop();
        _nextEventBlinkTimer.Stop();
        _api.ScheduleChanged -= OnScheduleChangedForBar;
        _eventBus.Unsubscribe(_encoderStatusHandler);
        _eventBus.Unsubscribe(_recordingStatusHandler);
        PlaylistViewModel.Dispose();
        LibraryViewModel.Dispose();
        HistoryViewModel.Dispose();
        CartwallViewModel.Dispose();
        AuxPlayersViewModel.Dispose();
    }

    // ── Bottom-bar output indicators ──────────────────────────────────────────

    /// <summary>
    /// One-off sync at startup. Everything after this arrives as events; this exists only because
    /// a session may already be running when the window is built (auto-start, or a restarted UI).
    /// </summary>
    /// <summary>
    /// Reads the module switches from rdm.config.json. Called at startup and again whenever the
    /// Settings window closes, so turning a module on or off shows up in the bar immediately
    /// rather than after a restart.
    /// </summary>
    public async Task RefreshOutputModulesAsync()
    {
        try
        {
            var cfg = await _configService.LoadAsync();
            IsStreamingModuleEnabled = cfg["streaming"]?["enabled"]?.GetValue<bool>() ?? false;
            IsRecordingModuleEnabled = cfg["recording"]?["enabled"]?.GetValue<bool>() ?? false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the streaming/recording module switches");
        }

        await RefreshOutputIndicatorsAsync();
    }

    private async Task RefreshOutputIndicatorsAsync()
    {
        try
        {
            var profiles = await _api.GetEncoderProfilesAsync();
            _knownProfileCount = profiles?.Profiles.Count ?? 0;
            _armedProfileCount = profiles?.Profiles.Count(p => p.AutoStart) ?? 0;

            var statuses = await _api.GetEncoderStatusesAsync();
            if (statuses is not null)
                foreach (var s in statuses.Statuses)
                    if (Enum.TryParse<EncoderSessionState>(s.State, ignoreCase: true, out var state))
                        _encoderStates[s.ProfileId] = state;

            var recording = await _api.GetRecordingStatusAsync();
            if (recording is not null
                && Enum.TryParse<RecordingState>(recording.State, ignoreCase: true, out var recState))
                ApplyRecordingIndicator(recState);
            else
                ApplyRecordingIndicator(RecordingState.Stopped);

            ApplyStreamingIndicator();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the initial streaming/recording state");
        }
    }

    private void OnEncoderStatusForBar(EncoderStatusChangedEvent e)
        => Dispatcher.UIThread.Post(() =>
        {
            _encoderStates[e.Status.ProfileId] = e.Status.State;
            if (_encoderStates.Count > _knownProfileCount) _knownProfileCount = _encoderStates.Count;
            ApplyStreamingIndicator();
        });

    private void OnRecordingStatusForBar(RecordingStatusChangedEvent e)
        => Dispatcher.UIThread.Post(() => ApplyRecordingIndicator(e.Status.State));

    private void ApplyStreamingIndicator()
    {
        var live = _encoderStates.Values.Count(s => s == EncoderSessionState.Streaming);
        var bad  = _encoderStates.Values.Count(
            s => s is EncoderSessionState.FatalError or EncoderSessionState.Disconnected);

        // A live stream outranks a failed one: something is on air, and that is the fact the
        // operator needs first. The failure is still named in the tooltip.
        if (live > 0)
        {
            StreamingIndicatorColor   = IndicatorLiveColor;
            StreamingIndicatorTooltip = bad > 0
                ? string.Format(Localizer.Instance?["streaming.bar.live_with_errors"]
                                ?? "On air: {0} — {1} with problems", live, bad)
                : string.Format(Localizer.Instance?["streaming.bar.live"] ?? "On air: {0}", live);
        }
        else if (bad > 0)
        {
            StreamingIndicatorColor   = IndicatorErrorColor;
            StreamingIndicatorTooltip = string.Format(
                Localizer.Instance?["streaming.bar.error"] ?? "Streaming problem: {0}", bad);
        }
        else
        {
            StreamingIndicatorColor = IndicatorIdleColor;

            // Idle tooltip says what the click will actually do. "Not streaming" alone leaves the
            // operator guessing whether pressing this will put anything on air.
            StreamingIndicatorTooltip =
                _knownProfileCount == 0
                    ? Localizer.Instance?["streaming.bar.no_profiles"] ?? "No streaming profiles"
                : _armedProfileCount == 0
                    ? Localizer.Instance?["streaming.bar.none_armed"] ?? "No profile marked ready"
                : string.Format(
                    Localizer.Instance?["streaming.bar.ready"] ?? "Start streaming ({0} ready)",
                    _armedProfileCount);
        }
    }

    // ── Bottom-bar actions ────────────────────────────────────────────────────

    /// <summary>
    /// One button, both directions: nothing on air → start every profile marked ready; anything on
    /// air → stop the lot. The colour tells the operator which half of the toggle they are about
    /// to press, so a single control cannot be ambiguous.
    /// </summary>
    [RelayCommand]
    private async Task ToggleStreamingAsync()
    {
        var anyLive = _encoderStates.Values.Any(
            s => s is EncoderSessionState.Streaming or EncoderSessionState.Starting
                   or EncoderSessionState.Connecting or EncoderSessionState.RetryWaiting);

        var result = anyLive
            ? await _api.StopAllEncodersAsync()
            : await _api.StartArmedEncodersAsync();

        if (!result.Ok)
        {
            _logger.LogWarning("Streaming toggle failed: {Error}", result.ErrorMessage);
            return;
        }

        // The statuses come back with the response, so the bar is right immediately rather than
        // waiting for the events to land.
        if (result.Value is not null)
        {
            foreach (var s in result.Value.Statuses)
                if (Enum.TryParse<EncoderSessionState>(s.State, ignoreCase: true, out var state))
                    _encoderStates[s.ProfileId] = state;
            ApplyStreamingIndicator();
        }
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (_recordingState == RecordingState.Recording)
        {
            var stopped = await _api.StopRecordingAsync();
            if (!stopped.Ok)
                _logger.LogWarning("Stopping the recording failed: {Error}", stopped.ErrorMessage);
            return;
        }

        // Read the target fresh: Settings may have been edited since the window opened, and a
        // recording written to last week's folder would be a quiet kind of wrong.
        string directory, format, prefix;
        int bitrate;
        try
        {
            var cfg = await _configService.LoadAsync();
            var rec = cfg["recording"];
            directory = rec?["output_directory"]?.GetValue<string>() ?? "";
            format    = rec?["format"]?.GetValue<string>() ?? "MP3";
            bitrate   = rec?["bitrate_kbps"]?.GetValue<int>() ?? 192;
            prefix    = rec?["name_prefix"]?.GetValue<string>() ?? "rec";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the recording target");
            return;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            _logger.LogWarning("Recording not started — no folder is configured in Settings.");
            RecordingIndicatorTooltip =
                Localizer.Instance?["recording.bar.no_folder"] ?? "No recording folder set";
            return;
        }

        var started = await _api.StartRecordingAsync(new RecordingStartRequestDto(
            directory, format, bitrate, NamePrefix: string.IsNullOrWhiteSpace(prefix) ? null : prefix));

        if (!started.Ok)
        {
            _logger.LogWarning("Starting the recording failed: {Error}", started.ErrorMessage);
            RecordingIndicatorTooltip = started.ErrorMessage
                ?? Localizer.Instance?["recording.bar.error"] ?? "Recording failed";
            RecordingIndicatorColor = IndicatorErrorColor;
        }
    }

    private void ApplyRecordingIndicator(RecordingState state)
    {
        _recordingState = state;

        RecordingIndicatorColor = state switch
        {
            RecordingState.Recording => IndicatorRecordingColor,
            RecordingState.Error     => IndicatorErrorColor,
            _                        => IndicatorIdleColor
        };

        RecordingIndicatorTooltip = state switch
        {
            RecordingState.Recording => Localizer.Instance?["recording.bar.active"] ?? "Recording — click to stop",
            RecordingState.Error     => Localizer.Instance?["recording.bar.error"] ?? "Recording failed",
            _                        => Localizer.Instance?["recording.bar.idle"] ?? "Start recording"
        };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task StepAsync(string name, Func<Task> step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await step();
            _logger.LogDebug("Shutdown step '{Step}' completed", name);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Shutdown step '{Step}' cancelled (timeout)", name);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Shutdown step '{Step}' failed — continuing", name);
        }
    }
}
