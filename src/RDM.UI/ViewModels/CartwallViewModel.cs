using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Shared.DTOs;
using RDM.UI.Services;
using RDM.UI.Views;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace RDM.UI.ViewModels;

// ── Per-slot VM ───────────────────────────────────────────────────────────────

public sealed partial class CartSlotViewModel : ObservableObject
{
    private readonly Func<string, Task>         _triggerAsync;
    private readonly Func<string, Task>         _stopAsync;
    private readonly Func<string, Task>         _assignAsync;
    private readonly Func<string, Task>         _clearAsync;
    private readonly Func<string, string, Task> _renameLabelAsync;
    private readonly Func<string, string?, Task> _changeColorAsync;

    private DispatcherTimer? _progressTimer;
    private DateTime         _startedAt;

    public string   SlotId     { get; }
    public int      SlotNumber { get; }
    public string?  AssetId    { get; }
    public string   Label      { get; }
    public string?  ColorHex   { get; }
    public string   Duration   { get; }
    public uint?    DurationMs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Background))]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Background))]
    private bool _isFlashing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    [NotifyPropertyChangedFor(nameof(RemainingText))]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private uint _positionMs;

    public double ProgressFraction =>
        DurationMs is > 0 ? Math.Min((double)PositionMs / DurationMs.Value, 1.0) : 0.0;

    public string RemainingText
    {
        get
        {
            if (!DurationMs.HasValue || DurationMs.Value == 0) return "▶";
            var rem = DurationMs.Value > PositionMs ? DurationMs.Value - PositionMs : 0;
            return "-" + FormatMs(rem);
        }
    }

    public string TimeText => IsNotEmpty
        ? (IsPlaying ? RemainingText : Duration)
        : "";

    public bool HasDuration => DurationMs.HasValue;

    public IBrush Background
    {
        get
        {
            if (IsFlashing)
                return new SolidColorBrush(Color.Parse("#FFFFFF"));
            if (IsPlaying)
                return new SolidColorBrush(Color.Parse("#1E4D2B"));
            if (!string.IsNullOrEmpty(ColorHex))
            {
                try { return new SolidColorBrush(Color.Parse(ColorHex)); }
                catch { /* fall through */ }
            }
            return IsEmpty
                ? new SolidColorBrush(Color.Parse("#141426"))
                : new SolidColorBrush(Color.Parse("#B04A00"));
        }
    }

    public bool IsEmpty    => AssetId is null;
    public bool IsNotEmpty => AssetId is not null;

    public CartSlotViewModel(
        CartSlotDto dto,
        Func<string, Task>          triggerAsync,
        Func<string, Task>          stopAsync,
        Func<string, Task>          assignAsync,
        Func<string, Task>          clearAsync,
        Func<string, string, Task>  renameLabelAsync,
        Func<string, string?, Task> changeColorAsync)
    {
        SlotId     = dto.SlotId;
        SlotNumber = dto.SlotNumber;
        AssetId    = dto.AssetId;
        Label      = dto.Label ?? dto.Title ?? "";
        ColorHex   = dto.Color;
        DurationMs = dto.DurationMs;
        Duration   = dto.DurationMs.HasValue ? FormatMs(dto.DurationMs.Value) : "";

        _triggerAsync     = triggerAsync;
        _stopAsync        = stopAsync;
        _assignAsync      = assignAsync;
        _clearAsync       = clearAsync;
        _renameLabelAsync = renameLabelAsync;
        _changeColorAsync = changeColorAsync;
    }

    partial void OnIsPlayingChanged(bool value)
    {
        if (value)
        {
            _startedAt  = DateTime.UtcNow;
            PositionMs  = 0;
            IsFlashing  = true;
            _ = ResetFlashAsync();
            _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250),
                DispatcherPriority.Normal, OnProgressTick);
            _progressTimer.Start();
        }
        else
        {
            _progressTimer?.Stop();
            _progressTimer = null;
            PositionMs = 0;
        }
    }

    private async Task ResetFlashAsync()
    {
        await Task.Delay(180);
        IsFlashing = false;
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        var elapsed = (uint)(DateTime.UtcNow - _startedAt).TotalMilliseconds;
        PositionMs = DurationMs.HasValue ? Math.Min(elapsed, DurationMs.Value) : elapsed;
    }

    [RelayCommand]
    private async Task TriggerAsync()
    {
        if (IsEmpty)
            await _assignAsync(SlotId);
        else if (IsPlaying)
            await _stopAsync(SlotId);
        else
            await _triggerAsync(SlotId);
    }

    [RelayCommand]
    private Task AssignAsync() => _assignAsync(SlotId);

    [RelayCommand(CanExecute = nameof(IsNotEmpty))]
    private Task ClearAsync() => _clearAsync(SlotId);

    [RelayCommand]
    private Task RenameLabelAsync() => _renameLabelAsync(SlotId, Label);

    [RelayCommand]
    private Task ChangeColorAsync() => _changeColorAsync(SlotId, ColorHex);

    private static string FormatMs(uint ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalMinutes >= 60 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}

// ── Per-cartwall (tab) VM ─────────────────────────────────────────────────────

public sealed class CartwallTabViewModel
{
    public string                                  CartwallId { get; }
    public string                                  Name       { get; }
    public ObservableCollection<CartSlotViewModel> Slots      { get; } = new();

    public ICommand DeleteCommand    { get; }
    public ICommand RenameCommand    { get; }
    public ICommand MoveLeftCommand  { get; }
    public ICommand MoveRightCommand { get; }

    public CartwallTabViewModel(
        CartwallDto dto,
        Func<string, Task>          trigger,
        Func<string, Task>          stop,
        Func<string, Task>          assign,
        Func<string, Task>          clear,
        Func<string, string, Task>  renameLabel,
        Func<string, string?, Task> changeColor,
        Func<string, Task>          deleteTab,
        Func<string, string, Task>  renameTab,
        Func<string, Task>          moveTabLeft,
        Func<string, Task>          moveTabRight)
    {
        CartwallId = dto.CartwallId;
        Name       = dto.Name;

        DeleteCommand    = new AsyncRelayCommand(() => deleteTab(CartwallId));
        RenameCommand    = new AsyncRelayCommand(() => renameTab(CartwallId, Name));
        MoveLeftCommand  = new AsyncRelayCommand(() => moveTabLeft(CartwallId));
        MoveRightCommand = new AsyncRelayCommand(() => moveTabRight(CartwallId));

        foreach (var slot in dto.Slots.OrderBy(s => s.SlotNumber))
            Slots.Add(new CartSlotViewModel(slot, trigger, stop, assign, clear, renameLabel, changeColor));
    }
}

// ── Top-level VM ──────────────────────────────────────────────────────────────

public sealed partial class CartwallViewModel : ObservableObject, IDisposable
{
    private readonly ApiClientService           _api;
    private readonly ILogger<CartwallViewModel> _logger;

    public ObservableCollection<CartwallTabViewModel> Tabs { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnMode))]
    [NotifyPropertyChangedFor(nameof(IsPflMode))]
    [NotifyPropertyChangedFor(nameof(IsOffMode))]
    private string _mode = "ON";

    [ObservableProperty] private int    _selectedTabIndex;
    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private double _slotWidth  = 150;
    [ObservableProperty] private double _slotHeight = 100;

    public bool IsOnMode  => Mode == "ON";
    public bool IsPflMode => Mode == "PFL";
    public bool IsOffMode => Mode == "OFF";

    /// Set by CartwallDialogHost; opens the asset picker dialog.
    public Func<Task<AssetPickerRow?>>?          ShowAssetPickerAsync  { get; set; }
    /// Set by CartwallDialogHost; shows a text-input dialog, returns new value or null.
    public Func<string, Task<string?>>?          ShowRenameLabelAsync  { get; set; }
    /// Set by CartwallDialogHost; shows a color-input dialog, returns new hex or null.
    public Func<string?, Task<string?>>?         ShowChangeColorAsync  { get; set; }
    /// Set by CartwallDialogHost; shows create-tab dialog, returns name or null.
    public Func<Task<string?>>?                  ShowCreateTabAsync    { get; set; }
    /// Set by CartwallDialogHost; shows rename-tab dialog, returns new name or null.
    public Func<string, Task<string?>>?          ShowRenameTabAsync    { get; set; }

    public CartwallViewModel(ApiClientService api, ILogger<CartwallViewModel> logger)
    {
        _api    = api;
        _logger = logger;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate()
    {
        _api.CartTriggered += OnCartTriggered;
        _api.CartStopped   += OnCartStopped;
        _ = LoadCartwallsAsync();
    }

    public void Dispose()
    {
        _api.CartTriggered -= OnCartTriggered;
        _api.CartStopped   -= OnCartStopped;
    }

    // ── Tab selection (hotkey support) ────────────────────────────────────────

    public void SelectTab(int index)
    {
        if (index >= 0 && index < Tabs.Count)
            SelectedTabIndex = index;
    }

    // ── Slot trigger (hotkey support) ─────────────────────────────────────────

    public void TriggerSlot(int slotIndex)
    {
        if (SelectedTabIndex >= Tabs.Count) return;
        var tab = Tabs[SelectedTabIndex];
        if (slotIndex >= tab.Slots.Count) return;
        _ = tab.Slots[slotIndex].TriggerCommand.ExecuteAsync(null);
    }

    // ── Mode commands ─────────────────────────────────────────────────────────

    [RelayCommand] private async Task SetModeOnAsync()  => await SetModeAsync("ON");
    [RelayCommand] private async Task SetModePflAsync() => await SetModeAsync("PFL");
    [RelayCommand] private async Task SetModeOffAsync() => await SetModeAsync("OFF");

    // ── Tab management commands ───────────────────────────────────────────────

    [RelayCommand]
    private async Task CreateTabAsync()
    {
        if (ShowCreateTabAsync is null) return;
        var name = await ShowCreateTabAsync();
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _api.CreateCartwallAsync(new CreateCartwallDto(name.Trim()));
            await LoadCartwallsAsync();
            SelectedTabIndex = Tabs.Count - 1;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "CreateTab failed"); }
    }

    public void ToggleMode()
    {
        var next = Mode switch { "ON" => "PFL", "PFL" => "OFF", _ => "ON" };
        _ = SetModeAsync(next);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task LoadCartwallsAsync()
    {
        IsBusy = true;
        try
        {
            var envelope = await _api.GetCartwallsAsync();
            if (envelope is null)
            {
                _logger.LogWarning("LoadCartwalls: GetCartwallsAsync returned null");
                return;
            }

            _logger.LogInformation("LoadCartwalls: received {Count} cartwalls", envelope.Items.Count);
            Tabs.Clear();
            foreach (var cw in envelope.Items)
            {
                _logger.LogInformation("LoadCartwalls: adding tab '{Name}' with {Slots} slots", cw.Name, cw.Slots.Count);
                Tabs.Add(new CartwallTabViewModel(cw,
                    TriggerSlotAsync, StopSlotAsync, AssignSlotAsync, ClearSlotAsync,
                    RenameLabelSlotAsync, ChangeColorSlotAsync,
                    DeleteTabAsync, RenameTabAsync, MoveTabLeftAsync, MoveTabRightAsync));
            }
            _logger.LogInformation("LoadCartwalls: done, Tabs.Count={Count}", Tabs.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load cartwalls failed"); }
        finally { IsBusy = false; }
    }

    private async Task TriggerSlotAsync(string slotId)
    {
        try { await _api.TriggerCartAsync(slotId); }
        catch (Exception ex) { _logger.LogWarning(ex, "TriggerCart {SlotId} failed", slotId); }
    }

    private async Task StopSlotAsync(string slotId)
    {
        try { await _api.StopCartAsync(slotId); }
        catch (Exception ex) { _logger.LogWarning(ex, "StopCart {SlotId} failed", slotId); }
    }

    private async Task AssignSlotAsync(string slotId)
    {
        if (ShowAssetPickerAsync is null) return;
        var picked = await ShowAssetPickerAsync();
        if (picked is null) return;
        try
        {
            await _api.PatchCartSlotAsync(slotId, new PatchCartSlotDto(AssetId: picked.AssetId));
            await LoadCartwallsAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "AssignSlot {SlotId} failed", slotId); }
    }

    private async Task ClearSlotAsync(string slotId)
    {
        try
        {
            await _api.PatchCartSlotAsync(slotId, new PatchCartSlotDto(ClearAsset: true));
            await LoadCartwallsAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ClearSlot {SlotId} failed", slotId); }
    }

    private async Task RenameLabelSlotAsync(string slotId, string currentLabel)
    {
        if (ShowRenameLabelAsync is null) return;
        var newLabel = await ShowRenameLabelAsync(currentLabel);
        if (newLabel is null) return;
        try
        {
            await _api.PatchCartSlotAsync(slotId, new PatchCartSlotDto(Label: newLabel == "" ? null : newLabel));
            await LoadCartwallsAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RenameLabel {SlotId} failed", slotId); }
    }

    private async Task ChangeColorSlotAsync(string slotId, string? currentColor)
    {
        if (ShowChangeColorAsync is null) return;
        var newColor = await ShowChangeColorAsync(currentColor);
        if (newColor is null) return;
        try
        {
            await _api.PatchCartSlotAsync(slotId, new PatchCartSlotDto(Color: newColor == "" ? null : newColor));
            await LoadCartwallsAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ChangeColor {SlotId} failed", slotId); }
    }

    private async Task DeleteTabAsync(string cartwallId)
    {
        try
        {
            var ok = await _api.DeleteCartwallAsync(cartwallId);
            if (!ok) return;
            await LoadCartwallsAsync();
            SelectedTabIndex = Math.Max(0, Math.Min(SelectedTabIndex, Tabs.Count - 1));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "DeleteTab {CartwallId} failed", cartwallId); }
    }

    private async Task RenameTabAsync(string cartwallId, string currentName)
    {
        if (ShowRenameTabAsync is null) return;
        var newName = await ShowRenameTabAsync(currentName);
        if (string.IsNullOrWhiteSpace(newName)) return;
        try
        {
            await _api.PatchCartwallAsync(cartwallId, new PatchCartwallDto(Name: newName.Trim()));
            await LoadCartwallsAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "RenameTab {CartwallId} failed", cartwallId); }
    }

    private async Task MoveTabLeftAsync(string cartwallId)
    {
        var idx = Tabs.ToList().FindIndex(t => t.CartwallId == cartwallId);
        if (idx <= 0) return;
        try
        {
            var current = Tabs[idx];
            var prev    = Tabs[idx - 1];
            await _api.PatchCartwallAsync(current.CartwallId, new PatchCartwallDto(PageOrder: idx - 1));
            await _api.PatchCartwallAsync(prev.CartwallId,    new PatchCartwallDto(PageOrder: idx));
            var selectedId = SelectedTabIndex < Tabs.Count ? Tabs[SelectedTabIndex].CartwallId : null;
            await LoadCartwallsAsync();
            if (selectedId == cartwallId) SelectedTabIndex = idx - 1;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "MoveTabLeft {CartwallId} failed", cartwallId); }
    }

    private async Task MoveTabRightAsync(string cartwallId)
    {
        var idx = Tabs.ToList().FindIndex(t => t.CartwallId == cartwallId);
        if (idx < 0 || idx >= Tabs.Count - 1) return;
        try
        {
            var current = Tabs[idx];
            var next    = Tabs[idx + 1];
            await _api.PatchCartwallAsync(current.CartwallId, new PatchCartwallDto(PageOrder: idx + 1));
            await _api.PatchCartwallAsync(next.CartwallId,    new PatchCartwallDto(PageOrder: idx));
            var selectedId = SelectedTabIndex < Tabs.Count ? Tabs[SelectedTabIndex].CartwallId : null;
            await LoadCartwallsAsync();
            if (selectedId == cartwallId) SelectedTabIndex = idx + 1;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "MoveTabRight {CartwallId} failed", cartwallId); }
    }

    private async Task SetModeAsync(string newMode)
    {
        if (Tabs.Count == 0) return;
        try
        {
            await _api.SetCartwallModeAsync(Tabs[SelectedTabIndex].CartwallId, newMode);
            Mode = newMode;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "SetCartwallMode failed"); }
    }

    private void OnCartTriggered(CartTriggeredPayload p)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var tab in Tabs)
                foreach (var slot in tab.Slots)
                    if (slot.SlotId == p.SlotId)
                        slot.IsPlaying = true;
        });
    }

    private void OnCartStopped(CartStoppedPayload p)
    {
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var tab in Tabs)
                foreach (var slot in tab.Slots)
                    if (slot.SlotId == p.SlotId)
                        slot.IsPlaying = false;
        });
    }
}
