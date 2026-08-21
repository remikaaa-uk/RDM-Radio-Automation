using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Models;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

/// <summary>Display names for the BFX effect types, resolved on access so they follow the UI language.</summary>
internal static class MicFxLabels
{
    public static string For(string fxType) => fxType switch
    {
        "Compressor" => Localizer.Instance?["md.fx.compressor"] ?? "Compressor",
        "PeakEq"     => Localizer.Instance?["md.fx.peak_eq"]    ?? "Equalizer (Peak EQ)",
        "VolumeGain" => Localizer.Instance?["md.fx.volume"]     ?? "Volume",
        "FreeVerb"   => Localizer.Instance?["md.fx.freeverb"]   ?? "Reverb (FreeVerb)",
        _            => fxType
    };
}

/// <summary>
/// A selectable BFX effect type. Only the key is stored — <see cref="Label"/> resolves through the
/// localizer on access, because <see cref="MicDspChainViewModel.FxOptions"/> is static and would
/// otherwise freeze the labels at the language active when the type was first loaded.
/// </summary>
public sealed record FxTypeOption(string Key)
{
    public string Label => MicFxLabels.For(Key);
}

/// <summary>One editable BFX parameter. Range and unit come from <see cref="MicFxParams"/>.</summary>
public sealed partial class MicFxParamViewModel : ObservableObject
{
    private readonly Action _onChanged;

    public string Key  { get; }
    public double Min  { get; }
    public double Max  { get; }
    public string Unit { get; }

    public string Label   => Localizer.Instance?[$"md.fx.param.{Key}"] ?? Key;
    public string Display => string.IsNullOrEmpty(Unit) ? $"{Value:0.##}" : $"{Value:0.##} {Unit}";

    [ObservableProperty] private double _value;

    public MicFxParamViewModel(MicFxParam def, double value, Action onChanged)
    {
        Key        = def.Key;
        Min        = def.Min;
        Max        = def.Max;
        Unit       = def.Unit;
        _value     = value;
        _onChanged = onChanged;
    }

    partial void OnValueChanged(double value)
    {
        OnPropertyChanged(nameof(Display));
        _onChanged();
    }
}

public sealed partial class MicFxRowViewModel : ObservableObject
{
    // Dragging a slider raises a change per pixel; without this every one would be an HTTP round
    // trip plus a config write. Short enough that the effect still follows the slider by ear.
    private readonly DispatcherTimer _pushTimer =
        new() { Interval = TimeSpan.FromMilliseconds(150) };

    public int    SlotId { get; }
    public string FxType { get; }
    public string Label  => $"{MicFxLabels.For(FxType)}  [#{SlotId}]";

    public ObservableCollection<MicFxParamViewModel> Parameters { get; } = [];

    /// <summary>Collapsed by default — the chain stays readable with several effects in it.</summary>
    [ObservableProperty] private bool _isExpanded;

    public MicFxRowViewModel(int slotId, string fxType, IReadOnlyDictionary<string, float>? values,
                             Action<MicFxRowViewModel> onParametersChanged)
    {
        SlotId = slotId;
        FxType = fxType;

        _pushTimer.Tick += (_, _) =>
        {
            _pushTimer.Stop();
            onParametersChanged(this);
        };

        if (!Enum.TryParse<MicFxType>(fxType, out var parsed)) return;

        foreach (var def in MicFxParams.For(parsed))
        {
            double value = values is not null && values.TryGetValue(def.Key, out float v) ? v : def.Default;
            Parameters.Add(new MicFxParamViewModel(def, value, RestartPushTimer));
        }
    }

    private void RestartPushTimer()
    {
        _pushTimer.Stop();
        _pushTimer.Start();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public Dictionary<string, float> ToParameterDictionary()
    {
        var result = new Dictionary<string, float>();
        foreach (var p in Parameters)
            result[p.Key] = (float)p.Value;
        return result;
    }
}

public sealed class MicVstRowViewModel
{
    public int    SlotId     { get; }
    public string PluginName { get; }
    public string DllPath    { get; }
    public string Label      => $"{PluginName}  [#{SlotId}]";

    public MicVstRowViewModel(int slotId, string pluginName, string dllPath)
    {
        SlotId     = slotId;
        PluginName = pluginName;
        DllPath    = dllPath;
    }
}

public sealed partial class MicDspChainViewModel : ObservableObject, IDisposable
{
    private static readonly IBrush GreenBrush  = new SolidColorBrush(Color.Parse("#33CC55"));
    private static readonly IBrush OrangeBrush = new SolidColorBrush(Color.Parse("#FFAA00"));
    private static readonly IBrush RedBrush    = new SolidColorBrush(Color.Parse("#FF3333"));
    private const double VuBarHeight = 180.0;

    public static FxTypeOption[] FxOptions { get; } =
    [
        new("Compressor"),
        new("PeakEq"),
        new("VolumeGain"),
        new("FreeVerb")
    ];

    private readonly ApiClientService              _api;
    private readonly MicDspChainStore              _store;
    private readonly ILogger<MicDspChainViewModel> _logger;
    private readonly DispatcherTimer               _levelTimer;
    private bool                                   _isPolling;

    [ObservableProperty] private double              _micLevelDb      = -60.0;
    [ObservableProperty] private bool                _isMicActive;
    [ObservableProperty] private string?             _errorMessage;
    [ObservableProperty] private string?             _vstPathInput;
    [ObservableProperty] private FxTypeOption        _selectedFxOption = FxOptions[0];
    [ObservableProperty] private MicFxRowViewModel?  _selectedFx;
    [ObservableProperty] private MicVstRowViewModel? _selectedVst;

    public string MicStatusText => IsMicActive ? "AKTYWNY" : "NIEAKTYWNY";
    public IBrush MicStatusColor => IsMicActive
        ? new SolidColorBrush(Color.Parse("#33CC55"))
        : new SolidColorBrush(Color.Parse("#FF6B6B"));

    partial void OnIsMicActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(MicStatusText));
        OnPropertyChanged(nameof(MicStatusColor));
    }

    public ObservableCollection<MicFxRowViewModel>  FxSlots  { get; } = new();
    public ObservableCollection<MicVstRowViewModel> VstSlots { get; } = new();

    public double VuHeight => Math.Clamp((MicLevelDb + 60.0) / 60.0 * VuBarHeight, 0, VuBarHeight);
    public IBrush VuBrush  => MicLevelDb >= -3  ? RedBrush
                            : MicLevelDb >= -12 ? OrangeBrush
                            : GreenBrush;
    public string LevelText => $"{MicLevelDb:F1} dB";

    partial void OnMicLevelDbChanged(double value)
    {
        OnPropertyChanged(nameof(VuHeight));
        OnPropertyChanged(nameof(VuBrush));
        OnPropertyChanged(nameof(LevelText));
    }

    /// Set by the view: opens a window dedicated to this plugin's editor and embeds it there.
    /// Returns null on success, or the error message to show. The editor cannot be hosted in the
    /// DSP chain window itself — the plugin covers its whole client area (see VstEditorWindow).
    public Func<MicVstRowViewModel, Task<string?>>? ShowVstEditorAsync { get; set; }

    public MicDspChainViewModel(
        ApiClientService              api,
        MicDspChainStore              store,
        ILogger<MicDspChainViewModel> logger)
    {
        _api         = api;
        _store       = store;
        _logger      = logger;

        _levelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _levelTimer.Tick += OnLevelTimerTick;
    }

    public void Activate()
    {
        _logger.LogInformation("MicDspChainViewModel.Activate: starting load + level timer.");
        _ = LoadAsync();
        _levelTimer.Start();
    }

    private async void OnLevelTimerTick(object? sender, EventArgs e)
    {
        if (_isPolling) return;
        _isPolling = true;
        try
        {
            var levelTask  = _api.GetMicLevelAsync();
            var statusTask = _api.GetMicStatusAsync();
            await Task.WhenAll(levelTask, statusTask);

            if (levelTask.Result  is { } level)  MicLevelDb  = level.LevelDb;
            if (statusTask.Result is { } status) IsMicActive = status.IsActive;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mic polling failed");
        }
        finally
        {
            _isPolling = false;
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        ErrorMessage = null;
        try
        {
            var fxList = await _api.GetMicFxListAsync();
            FxSlots.Clear();
            if (fxList is not null)
                foreach (var f in fxList)
                    FxSlots.Add(new MicFxRowViewModel(f.SlotId, f.FxType, f.Parameters, PushFxParameters));

            var vstList = await _api.GetMicVstListAsync();
            VstSlots.Clear();
            if (vstList is not null)
                foreach (var v in vstList)
                    VstSlots.Add(new MicVstRowViewModel(v.SlotId, v.PluginName, v.DllPath));

            _logger.LogInformation("MicDspChainViewModel.LoadAsync: loaded {FxCount} FX, {VstCount} VST (fxList null={FxNull}, vstList null={VstNull})",
                FxSlots.Count, VstSlots.Count, fxList is null, vstList is null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load FX/VST list");
            ErrorMessage = Localizer.Instance?["md.err.load_fx_list"] ?? "Failed to load the effects list.";
        }
    }

    [RelayCommand]
    private async Task AddFxAsync()
    {
        ErrorMessage = null;
        var result = await _api.AddMicFxAsync(SelectedFxOption.Key);
        if (result is null)
        {
            ErrorMessage = Localizer.Instance?["md.err.add_fx"]
                           ?? "Cannot add the effect. Make sure bass_fx.dll is in the application folder.";
            return;
        }
        // Reload rather than append: the engine seeds the slot with its default parameters and
        // the row has to show those, not an empty editor.
        await LoadAsync();
        await _store.SaveAsync();
    }

    /// <summary>Sends one slot's parameters to the engine and persists them. Debounced by the row.</summary>
    private async void PushFxParameters(MicFxRowViewModel row)
    {
        try
        {
            bool ok = await _api.UpdateMicFxAsync(row.SlotId, row.ToParameterDictionary());
            if (!ok)
            {
                ErrorMessage = string.Format(
                    Localizer.Instance?["md.err.update_fx"] ?? "Cannot apply the settings of effect #{0}.", row.SlotId);
                return;
            }

            ErrorMessage = null;
            await _store.SaveAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Applying FX parameters failed for slot {SlotId}", row.SlotId);
        }
    }

    [RelayCommand]
    private async Task RemoveFxAsync(MicFxRowViewModel slot)
    {
        ErrorMessage = null;
        bool ok = await _api.RemoveMicFxAsync(slot.SlotId);
        if (ok)
        {
            FxSlots.Remove(slot);
            await _store.SaveAsync();
        }
        else ErrorMessage = string.Format(Localizer.Instance?["md.err.remove_fx"] ?? "Cannot remove effect #{0}.", slot.SlotId);
    }

    [RelayCommand]
    private async Task AddVstAsync()
    {
        if (string.IsNullOrWhiteSpace(VstPathInput)) return;
        ErrorMessage = null;
        var result = await _api.AddMicVstAsync(VstPathInput.Trim());
        if (result is null)
        {
            ErrorMessage = Localizer.Instance?["md.err.add_vst"]
                           ?? "Cannot load the VST plugin. Check the path and that bass_vst.dll is available.";
            return;
        }
        // Refresh to get plugin name resolved from BASS_VST_GetInfo (if mic was active)
        await LoadAsync();
        VstPathInput = null;
        await _store.SaveAsync();
    }

    [RelayCommand]
    private async Task RemoveVstAsync(MicVstRowViewModel slot)
    {
        ErrorMessage = null;
        bool ok = await _api.RemoveMicVstAsync(slot.SlotId);
        if (ok)
        {
            VstSlots.Remove(slot);
            await _store.SaveAsync();
        }
        else ErrorMessage = string.Format(Localizer.Instance?["md.err.remove_vst"] ?? "Cannot remove VST #{0}.", slot.SlotId);
    }

    [RelayCommand]
    private async Task OpenVstEditorAsync(MicVstRowViewModel slot)
    {
        _logger.LogInformation("OpenVstEditor clicked: slot={SlotId} '{Name}', micActive={Active}",
            slot.SlotId, slot.PluginName, IsMicActive);

        if (!IsMicActive)
        {
            _logger.LogWarning("OpenVstEditor: blocked — mic not active");
            ErrorMessage = Localizer.Instance?["md.err.vst_needs_mic"]
                           ?? "The VST editor requires an active microphone. Turn MIC on in the playback window first.";
            return;
        }
        if (ShowVstEditorAsync is null)
        {
            _logger.LogWarning("OpenVstEditor: no view attached to ShowVstEditorAsync");
            return;
        }

        ErrorMessage = null;
        string? error = await ShowVstEditorAsync(slot);
        if (error is not null)
            ErrorMessage = string.Format(Localizer.Instance?["md.err.open_vst_editor"] ?? "Cannot open the VST editor: {0}", error);
    }

    public void Dispose()
    {
        _levelTimer.Stop();
    }
}
