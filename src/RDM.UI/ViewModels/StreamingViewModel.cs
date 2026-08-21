using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

/// <summary>
/// One streaming profile as shown in the list: its stored configuration plus whatever the engine
/// currently reports about it. The two are separate concerns — a profile exists without a session —
/// so the row keeps the last known status rather than assuming one.
/// </summary>
public sealed partial class EncoderProfileRowViewModel : ObservableObject
{
    public EncoderProfileDto Profile { get; private set; }

    public string ProfileId => Profile.ProfileId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(StateBrush))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private EncoderSessionState _state = EncoderSessionState.Stopped;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string? _error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private DateTime? _connectedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private int _retryAttempt;

    /// Raised when the operator ticks "ready". The parent persists it — the row has no API client.
    public event Action<EncoderProfileRowViewModel, bool>? ArmedChanged;

    private bool _suppressArmedEcho;

    public EncoderProfileRowViewModel(EncoderProfileDto profile)
    {
        Profile      = profile;
        _isArmed     = profile.Armed;
        _isAutoStart = profile.AutoStart;
    }

    /// <summary>"Ready": the profile joins the set the bottom-bar button starts.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAutoStart))]
    private bool _isArmed;

    /// <summary>
    /// Additionally starts with the application. Only meaningful for an armed profile: one that
    /// started by itself but was skipped by the bar button could not be brought back after a stop.
    /// </summary>
    [ObservableProperty] private bool _isAutoStart;

    public bool CanAutoStart => IsArmed;

    partial void OnIsArmedChanged(bool value)
    {
        if (_suppressArmedEcho) return;

        // Unticking "ready" cannot leave auto-start behind — that is exactly the incoherent
        // combination the nesting exists to prevent.
        if (!value && IsAutoStart)
        {
            _suppressArmedEcho = true;
            IsAutoStart = false;
            _suppressArmedEcho = false;
        }

        ArmedChanged?.Invoke(this, value);
    }

    partial void OnIsAutoStartChanged(bool value)
    {
        if (_suppressArmedEcho) return;
        ArmedChanged?.Invoke(this, IsArmed);
    }

    /// Applies values that came back from the server without re-triggering a save.
    public void SetFlagsQuietly(bool armed, bool autoStart)
    {
        _suppressArmedEcho = true;
        IsArmed     = armed;
        IsAutoStart = autoStart;
        _suppressArmedEcho = false;
    }

    public void UpdateProfile(EncoderProfileDto profile)
    {
        Profile = profile;
        SetFlagsQuietly(profile.Armed, profile.AutoStart);
        OnPropertyChanged(nameof(Profile));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(FormatText));
    }

    public string Name       => Profile.Name;
    public string FormatText => $"{Profile.Format} {Profile.BitrateKbps} kbps";

    public string TargetText
    {
        get
        {
            var scheme = Profile.UseSsl ? "https" : "http";
            var mount  = string.IsNullOrWhiteSpace(Profile.Mount) ? "" : Profile.Mount;
            return $"{scheme}://{Profile.Host}:{Profile.Port}{mount}";
        }
    }

    public bool IsStreaming => State == EncoderSessionState.Streaming;

    /// Starting or connecting — a second click would do nothing useful, so both buttons rest.
    public bool IsBusy => State is EncoderSessionState.Starting or EncoderSessionState.Connecting;

    public bool CanStart => State is EncoderSessionState.Stopped or EncoderSessionState.FatalError;
    public bool CanStop  => !CanStart;

    public string StateText => Localizer.Instance?[StateKey] ?? State.ToString();

    private string StateKey => State switch
    {
        EncoderSessionState.Stopped      => "streaming.state.stopped",
        EncoderSessionState.Starting     => "streaming.state.starting",
        EncoderSessionState.Connecting   => "streaming.state.connecting",
        EncoderSessionState.Streaming    => "streaming.state.streaming",
        EncoderSessionState.Disconnected => "streaming.state.disconnected",
        EncoderSessionState.RetryWaiting => "streaming.state.retry_waiting",
        _                                => "streaming.state.fatal_error"
    };

    public IBrush StateBrush => State switch
    {
        EncoderSessionState.Streaming    => Brush.Parse("#46B450"),   // on air
        EncoderSessionState.Starting     => Brush.Parse("#E0B040"),
        EncoderSessionState.Connecting   => Brush.Parse("#E0B040"),
        EncoderSessionState.RetryWaiting => Brush.Parse("#E0B040"),   // recoverable — amber, not red
        EncoderSessionState.Disconnected => Brush.Parse("#FF6B6B"),
        EncoderSessionState.FatalError   => Brush.Parse("#FF6B6B"),
        _                                => Brush.Parse("#66667A")    // stopped — grey, not an alarm
    };

    /// <summary>
    /// The line under the state. Shows the error when there is one, because a failure the operator
    /// cannot read is a failure they cannot fix.
    /// </summary>
    public string DetailText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Error))
                return RetryAttempt > 0
                    ? $"{Error} ({Localizer.Instance?["streaming.attempt"] ?? "attempt"} {RetryAttempt})"
                    : Error!;

            if (State == EncoderSessionState.Streaming && ConnectedAt is { } since)
                return $"{Localizer.Instance?["streaming.on_air_since"] ?? "on air since"} {since:HH:mm:ss}";

            return TargetText;
        }
    }

    public void Apply(EncoderStatus status)
    {
        State        = status.State;
        Error        = status.Error;
        ConnectedAt  = status.ConnectedAt;
        RetryAttempt = status.RetryAttempt;
    }
}

/// <summary>
/// The streaming window's model. Commands go out through <see cref="ApiClientService"/> — the
/// window never touches the audio engine — while state comes back on the in-process event bus, so
/// a session that reconnects on its own updates the list without anyone polling.
/// </summary>
public sealed partial class StreamingViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService            _api;
    private readonly IEventBus                   _eventBus;
    private readonly ILogger<StreamingViewModel> _logger;
    private readonly Action<EncoderStatusChangedEvent> _statusHandler;

    public ObservableCollection<EncoderProfileRowViewModel> Profiles { get; } = new();

    [ObservableProperty] private EncoderProfileRowViewModel? _selected;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageBrush))]
    private bool _messageIsError;

    public IBrush MessageBrush => MessageIsError ? Brush.Parse("#FF6B6B") : Brush.Parse("#8888A0");

    /// Set by the window: opens the profile editor and returns true when something was saved.
    public Func<EncoderProfileDto?, Task<bool>>? EditProfileAsync { get; set; }

    /// Set by the window: asks the operator to confirm a deletion.
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public StreamingViewModel(
        ApiClientService api,
        IEventBus eventBus,
        ILogger<StreamingViewModel> logger)
    {
        _api      = api;
        _eventBus = eventBus;
        _logger   = logger;

        _statusHandler = OnEncoderStatusChanged;
        _eventBus.Subscribe(_statusHandler);
    }

    public void Dispose() => _eventBus.Unsubscribe(_statusHandler);

    public bool HasNoProfiles => Profiles.Count == 0;

    // ── Loading ──────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var envelope = await _api.GetEncoderProfilesAsync();
            if (envelope is null)
            {
                ShowError(Localizer.Instance?["streaming.load_failed"] ?? "Could not load streaming profiles.");
                return;
            }

            // Rebuilt rather than merged: the list is short, and reconciling it in place risks
            // showing a status against a profile that was edited underneath it.
            var previous = Profiles.ToDictionary(p => p.ProfileId);
            foreach (var stale in Profiles) stale.ArmedChanged -= OnRowArmedChanged;
            Profiles.Clear();
            foreach (var dto in envelope.Profiles)
            {
                var row = new EncoderProfileRowViewModel(dto);
                if (previous.TryGetValue(dto.ProfileId, out var old))
                {
                    row.State        = old.State;
                    row.Error        = old.Error;
                    row.ConnectedAt  = old.ConnectedAt;
                    row.RetryAttempt = old.RetryAttempt;
                }
                row.ArmedChanged += OnRowArmedChanged;
                Profiles.Add(row);
            }

            // Live sessions win over anything carried across: the engine is the authority.
            var statuses = await _api.GetEncoderStatusesAsync();
            if (statuses is not null)
                foreach (var s in statuses.Statuses)
                    RowFor(s.ProfileId)?.Apply(ToStatus(s));

            OnPropertyChanged(nameof(HasNoProfiles));
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Session commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StartAsync(EncoderProfileRowViewModel? row)
    {
        if (row is null) return;
        ClearMessage();

        var result = await _api.StartEncoderAsync(row.ProfileId);
        if (!result.Ok)
        {
            // The server's own words: a missing add-on, a disabled profile, a password this
            // machine cannot decrypt. Replacing them with "start failed" would hide the fix.
            ShowError(result.ErrorMessage ?? (Localizer.Instance?["streaming.start_failed"] ?? "Could not start."));
            return;
        }

        if (result.Value is not null) row.Apply(ToStatus(result.Value));
    }

    [RelayCommand]
    private async Task StopAsync(EncoderProfileRowViewModel? row)
    {
        if (row is null) return;
        ClearMessage();

        var result = await _api.StopEncoderAsync(row.ProfileId);
        if (!result.Ok)
        {
            ShowError(result.ErrorMessage ?? (Localizer.Instance?["streaming.stop_failed"] ?? "Could not stop."));
            return;
        }

        if (result.Value is not null) row.Apply(ToStatus(result.Value));
    }

    // ── Profile commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddAsync()
    {
        if (EditProfileAsync is null) return;
        if (await EditProfileAsync(null)) await LoadAsync();
    }

    [RelayCommand]
    private async Task EditAsync(EncoderProfileRowViewModel? row)
    {
        if (row is null || EditProfileAsync is null) return;
        if (await EditProfileAsync(row.Profile)) await LoadAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(EncoderProfileRowViewModel? row)
    {
        if (row is null) return;
        ClearMessage();

        if (ConfirmAsync is not null)
        {
            var question = string.Format(
                Localizer.Instance?["streaming.confirm_delete"] ?? "Delete profile '{0}'?", row.Name);
            if (!await ConfirmAsync(question)) return;
        }

        if (await _api.DeleteEncoderProfileAsync(row.ProfileId))
            await LoadAsync();
        else
            ShowError(Localizer.Instance?["streaming.delete_failed"] ?? "Could not delete the profile.");
    }

    /// <summary>
    /// Persists a "ready" tick immediately rather than waiting for the Settings Save button. The
    /// tick is a one-click act with a visible consequence — which profiles the bar button will
    /// start — and leaving it unsaved would make the bar lie about what it is about to do.
    /// </summary>
    private async void OnRowArmedChanged(EncoderProfileRowViewModel row, bool armed)
    {
        var p = row.Profile;
        // Every field the profile carries has to be echoed back: an update replaces the whole row,
        // so any value omitted here would revert to its default. Username/StreamId/UsePut and the
        // reconnect delay are as much part of the profile as its name.
        var dto = new EncoderProfileUpdateDto(
            p.Name, p.Format, p.BitrateKbps, p.ServerType, p.Host, p.Port,
            Mount: p.Mount, Username: p.Username, StreamId: p.StreamId,
            Password: null,                           // null = keep the stored password
            SampleRateHz: p.SampleRateHz, Channels: p.Channels,
            UseSsl: p.UseSsl, UsePut: p.UsePut,
            StreamName: p.StreamName, Genre: p.Genre, StreamUrl: p.StreamUrl,
            Description: p.Description, IsPublic: p.IsPublic,
            Enabled: p.Enabled, Armed: row.IsArmed, AutoStart: row.IsAutoStart,
            ReconnectDelaySeconds: p.ReconnectDelaySeconds,
            TitleMode: p.TitleMode, TitleText: p.TitleText);

        var result = await _api.UpdateEncoderProfileAsync(row.ProfileId, dto);
        if (result.Ok)
        {
            if (result.Value is not null) row.UpdateProfile(result.Value);
            ArmedCountChanged?.Invoke();
            return;
        }

        // Put the tick back where it was: a checkbox that shows a state the server rejected is
        // worse than one that refuses to move.
        row.SetFlagsQuietly(!armed, false);
        ShowError(result.ErrorMessage
                  ?? (Localizer.Instance?["streaming.arm_failed"] ?? "Could not save the profile."));
    }

    /// Raised when the set of ready profiles changes, so the bar can re-evaluate its tooltip.
    public event Action? ArmedCountChanged;

    public int ArmedCount => Profiles.Count(p => p.IsArmed);

    // ── Events ───────────────────────────────────────────────────────────────

    /// Arrives on the audio or a native callback thread — never touch the collection directly.
    private void OnEncoderStatusChanged(EncoderStatusChangedEvent e)
        => Dispatcher.UIThread.Post(() => RowFor(e.Status.ProfileId)?.Apply(e.Status));

    private EncoderProfileRowViewModel? RowFor(string profileId)
        => Profiles.FirstOrDefault(p => p.ProfileId == profileId);

    private static EncoderStatus ToStatus(EncoderStatusDto dto) => new(
        dto.ProfileId,
        dto.ProfileName,
        Enum.TryParse<EncoderSessionState>(dto.State, ignoreCase: true, out var s) ? s : EncoderSessionState.Stopped,
        dto.Error,
        dto.ConnectedAt,
        dto.RetryAttempt,
        dto.NextRetryAt,
        dto.ListenerCount);

    private void ShowError(string text)
    {
        Message        = text;
        MessageIsError = true;
        _logger.LogWarning("Streaming: {Message}", text);
    }

    private void ClearMessage()
    {
        Message        = null;
        MessageIsError = false;
    }
}
