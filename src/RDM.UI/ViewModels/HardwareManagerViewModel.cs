using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using RDM.UI.Localization;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

// ── Item wrappers ─────────────────────────────────────────────────────────────

public class TriggerMappingItem : ObservableObject
{
    public TriggerActionMapping Model { get; }
    public string  Name             => Model.Name;
    public string  DeviceType       => Model.SourceDeviceType;
    public string? DeviceId         => Model.SourceDeviceId;
    public string  Signature        => Model.TargetSignature;
    public string  ActionId         => Model.TargetActionId.ToString();
    public string? Parameter        => Model.TargetParameter;
    public bool    IsEnabled        => Model.IsEnabled;
    public TriggerMappingItem(TriggerActionMapping m) => Model = m;
}

public class FeedbackMappingItem : ObservableObject
{
    public FeedbackRule Model { get; }
    public string  EventName        => Model.EventName;
    public string  TargetDeviceType => Model.TargetDeviceType;
    public string  TargetDeviceId   => Model.TargetDeviceId;
    public byte    Channel          => Model.Channel;
    public byte    NoteCode         => Model.NoteCode;
    public string? SerialCommand    => Model.SerialCommand;
    public bool    IsEnabled        => Model.IsEnabled;
    public FeedbackMappingItem(FeedbackRule m) => Model = m;
}

public class MacroItem : ObservableObject
{
    public Macro Model { get; }
    public string Name      => Model.Name;
    public bool   IsEnabled => Model.IsEnabled;
    public ObservableCollection<MacroStep> Steps { get; }
    public MacroItem(Macro m)
    {
        Model = m;
        Steps = new ObservableCollection<MacroStep>(m.Steps.OrderBy(s => s.StepOrder));
    }
}

public class ScriptItem : ObservableObject
{
    public Script Model { get; }
    public string Name      => Model.Name;
    public string Language  => Model.Language;
    public bool   IsEnabled => Model.IsEnabled;
    public ScriptItem(Script m) => Model = m;
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public sealed partial class HardwareManagerViewModel : ObservableObject, IDisposable
{
    private readonly ITriggerMappingRepository  _triggerRepo;
    private readonly IFeedbackMappingRepository _feedbackRepo;
    private readonly IMacroRepository           _macroRepo;
    private readonly IScriptRepository          _scriptRepo;
    private readonly ITriggerMappingCache       _triggerCache;
    private readonly IFeedbackMappingCache      _feedbackCache;
    private readonly IHardwareLearnService      _learnService;
    private readonly IHardwareMetrics           _metrics;
    private readonly ILogger<HardwareManagerViewModel> _logger;
    private readonly System.Timers.Timer _metricsTimer;

    // ── Trigger Mappings ──────────────────────────────────────────────────────
    public ObservableCollection<TriggerMappingItem> TriggerMappings { get; } = [];

    [ObservableProperty] private TriggerMappingItem? _selectedTriggerMapping;

    // ── Feedback Mappings ─────────────────────────────────────────────────────
    public ObservableCollection<FeedbackMappingItem> FeedbackMappings { get; } = [];

    [ObservableProperty] private FeedbackMappingItem? _selectedFeedbackMapping;

    // ── Macros ────────────────────────────────────────────────────────────────
    public ObservableCollection<MacroItem> Macros { get; } = [];

    [ObservableProperty] private MacroItem? _selectedMacro;
    [ObservableProperty] private MacroStep? _selectedMacroStep;

    // ── Scripts ───────────────────────────────────────────────────────────────
    public ObservableCollection<ScriptItem> Scripts { get; } = [];

    [ObservableProperty] private ScriptItem? _selectedScript;

    // ── Learn mode ────────────────────────────────────────────────────────────
    private static string LearnPrompt =>
        Localizer.Instance?["hw.learn_mode.prompt"] ?? "Press a key or a MIDI/D&R button…";

    [ObservableProperty] private bool   _isLearning;
    [ObservableProperty] private string _learnStatus = LearnPrompt;
    [ObservableProperty] private string _learnedDeviceType = string.Empty;
    [ObservableProperty] private string _learnedSignature  = string.Empty;

    // ── Hardware metrics (IHardwareMetrics — polling co 1s) ──────────────────
    [ObservableProperty] private int  _queueLength;
    [ObservableProperty] private long _executionTimeMs;
    [ObservableProperty] private long _throttledEventsCount;
    [ObservableProperty] private long _deduplicatedEventsCount;
    [ObservableProperty] private long _hardwareErrorCount;

    public HardwareManagerViewModel(
        ITriggerMappingRepository  triggerRepo,
        IFeedbackMappingRepository feedbackRepo,
        IMacroRepository           macroRepo,
        IScriptRepository          scriptRepo,
        ITriggerMappingCache       triggerCache,
        IFeedbackMappingCache      feedbackCache,
        IHardwareLearnService      learnService,
        IHardwareMetrics           metrics,
        ILogger<HardwareManagerViewModel> logger)
    {
        _triggerRepo   = triggerRepo;
        _feedbackRepo  = feedbackRepo;
        _macroRepo     = macroRepo;
        _scriptRepo    = scriptRepo;
        _triggerCache  = triggerCache;
        _feedbackCache = feedbackCache;
        _learnService  = learnService;
        _metrics       = metrics;
        _logger        = logger;

        _metricsTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _metricsTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(RefreshMetrics);
        _metricsTimer.Start();
    }

    private void RefreshMetrics()
    {
        QueueLength              = _metrics.QueueLength;
        ExecutionTimeMs          = _metrics.ExecutionTimeMs;
        ThrottledEventsCount     = _metrics.ThrottledEventsCount;
        DeduplicatedEventsCount  = _metrics.DeduplicatedEventsCount;
        HardwareErrorCount       = _metrics.HardwareErrorCount;
    }

    public void Dispose()
    {
        _metricsTimer.Stop();
        _metricsTimer.Dispose();
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        _logger.LogDebug("HardwareManager: LoadAsync START");
        await Task.WhenAll(LoadTriggerMappingsAsync(), LoadFeedbackMappingsAsync(),
                           LoadMacrosAsync(), LoadScriptsAsync());
        _logger.LogDebug("HardwareManager: LoadAsync DONE — triggers={T}, feedback={F}, macros={M}, scripts={S}",
            TriggerMappings.Count, FeedbackMappings.Count, Macros.Count, Scripts.Count);
    }

    // ── Property change hooks ─────────────────────────────────────────────────

    partial void OnSelectedTriggerMappingChanged(TriggerMappingItem? value) =>
        _logger.LogDebug("HardwareManager: SelectedTriggerMapping → {Value} (action={Action}, sig={Sig}, enabled={En})",
            value?.Name ?? "null", value?.ActionId ?? "-", value?.Signature ?? "-", value?.IsEnabled);

    partial void OnSelectedFeedbackMappingChanged(FeedbackMappingItem? value) =>
        _logger.LogDebug("HardwareManager: SelectedFeedbackMapping → {Value}", value?.EventName ?? "null");

    partial void OnSelectedMacroChanged(MacroItem? value) =>
        _logger.LogDebug("HardwareManager: SelectedMacro → {Value}", value?.Name ?? "null");

    partial void OnSelectedMacroStepChanged(MacroStep? value) =>
        _logger.LogDebug("HardwareManager: SelectedMacroStep → {Value}", value?.ActionId.ToString() ?? "null");

    partial void OnSelectedScriptChanged(ScriptItem? value) =>
        _logger.LogDebug("HardwareManager: SelectedScript → {Value}", value?.Name ?? "null");

    // ── Trigger Mappings CRUD ─────────────────────────────────────────────────

    public async Task LoadTriggerMappingsAsync()
    {
        _logger.LogDebug("HardwareManager: LoadTriggerMappings START — count={Count}, selected={Sel}",
            TriggerMappings.Count, SelectedTriggerMapping?.Name ?? "null");
        var all = await _triggerRepo.GetAllAsync();
        SelectedTriggerMapping = null;
        TriggerMappings.Clear();
        foreach (var m in all) TriggerMappings.Add(new TriggerMappingItem(m));
        _logger.LogDebug("HardwareManager: LoadTriggerMappings DONE — loaded={Count}", TriggerMappings.Count);
        foreach (var item in TriggerMappings)
            _logger.LogDebug("  trigger: name='{Name}' device={Dev} sig='{Sig}' action={Action} enabled={En}",
                item.Name, item.DeviceType, item.Signature, item.ActionId, item.IsEnabled);
    }

    public async Task SaveTriggerMappingAsync(TriggerActionMapping mapping)
    {
        _logger.LogInformation("HardwareManager: SaveTriggerMapping '{Name}' — action={Action}, sig='{Sig}', device={Dev}, enabled={En}",
            mapping.Name, mapping.TargetActionId, mapping.TargetSignature, mapping.SourceDeviceType, mapping.IsEnabled);
        await _triggerRepo.SaveAsync(mapping);
        await _triggerCache.ReloadAsync();
        await LoadTriggerMappingsAsync();
        _logger.LogInformation("HardwareManager: SaveTriggerMapping '{Name}' — zapisano i przeładowano cache", mapping.Name);
    }

    public async Task DeleteTriggerMappingAsync(TriggerMappingItem item)
    {
        _logger.LogInformation("HardwareManager: DeleteTriggerMapping '{Name}' (id={Id})", item.Name, item.Model.Id);
        await _triggerRepo.DeleteAsync(item.Model.Id);
        await _triggerCache.ReloadAsync();
        TriggerMappings.Remove(item);
        if (SelectedTriggerMapping == item) SelectedTriggerMapping = null;
        _logger.LogInformation("HardwareManager: DeleteTriggerMapping '{Name}' — usunięto", item.Name);
    }

    // ── Feedback Mappings CRUD ────────────────────────────────────────────────

    public async Task LoadFeedbackMappingsAsync()
    {
        _logger.LogDebug("HardwareManager: LoadFeedbackMappings START — count={Count}", FeedbackMappings.Count);
        var all = await _feedbackRepo.GetAllAsync();
        SelectedFeedbackMapping = null;
        FeedbackMappings.Clear();
        foreach (var r in all) FeedbackMappings.Add(new FeedbackMappingItem(r));
        _logger.LogDebug("HardwareManager: LoadFeedbackMappings DONE — loaded={Count}", FeedbackMappings.Count);
    }

    public async Task SaveFeedbackMappingAsync(FeedbackRule rule)
    {
        _logger.LogInformation("HardwareManager: SaveFeedbackMapping event='{Event}' → device={Dev}/{Id} ch={Ch} note={Note} enabled={En}",
            rule.EventName, rule.TargetDeviceType, rule.TargetDeviceId, rule.Channel, rule.NoteCode, rule.IsEnabled);
        await _feedbackRepo.SaveAsync(rule);
        await _feedbackCache.ReloadAsync();
        await LoadFeedbackMappingsAsync();
        _logger.LogInformation("HardwareManager: SaveFeedbackMapping '{Event}' — zapisano", rule.EventName);
    }

    public async Task DeleteFeedbackMappingAsync(FeedbackMappingItem item)
    {
        _logger.LogInformation("HardwareManager: DeleteFeedbackMapping '{Event}' (id={Id})", item.EventName, item.Model.Id);
        await _feedbackRepo.DeleteAsync(item.Model.Id);
        await _feedbackCache.ReloadAsync();
        FeedbackMappings.Remove(item);
        if (SelectedFeedbackMapping == item) SelectedFeedbackMapping = null;
        _logger.LogInformation("HardwareManager: DeleteFeedbackMapping '{Event}' — usunięto", item.EventName);
    }

    // ── Macros CRUD ───────────────────────────────────────────────────────────

    public async Task LoadMacrosAsync()
    {
        _logger.LogDebug("HardwareManager: LoadMacros START — count={Count}", Macros.Count);
        var all = await _macroRepo.GetAllAsync();
        SelectedMacro = null;
        SelectedMacroStep = null;
        Macros.Clear();
        foreach (var m in all) Macros.Add(new MacroItem(m));
        _logger.LogDebug("HardwareManager: LoadMacros DONE — loaded={Count}", Macros.Count);
    }

    public async Task SaveMacroAsync(Macro macro)
    {
        _logger.LogInformation("HardwareManager: SaveMacro '{Name}' (id={Id}, enabled={En}, steps={Steps})",
            macro.Name, macro.Id, macro.IsEnabled, macro.Steps.Count);
        await _macroRepo.SaveMacroAsync(macro);
        await LoadMacrosAsync();
        SelectedMacro = Macros.FirstOrDefault(m => m.Model.Id == macro.Id);
        _logger.LogInformation("HardwareManager: SaveMacro '{Name}' — zapisano", macro.Name);
    }

    public async Task DeleteMacroAsync(MacroItem item)
    {
        _logger.LogInformation("HardwareManager: DeleteMacro '{Name}' (id={Id})", item.Name, item.Model.Id);
        await _macroRepo.DeleteMacroAsync(item.Model.Id);
        Macros.Remove(item);
        if (SelectedMacro == item) { SelectedMacro = null; SelectedMacroStep = null; }
        _logger.LogInformation("HardwareManager: DeleteMacro '{Name}' — usunięto", item.Name);
    }

    public async Task SaveStepAsync(MacroStep step)
    {
        _logger.LogInformation("HardwareManager: SaveStep macro={MacroId} order={Order} action={Action} param='{Param}'",
            step.MacroId, step.StepOrder, step.ActionId, step.Parameter ?? "-");
        await _macroRepo.SaveStepAsync(step);
        if (SelectedMacro?.Model.Id == step.MacroId)
        {
            var existing = SelectedMacro.Steps.FirstOrDefault(s => s.Id == step.Id);
            if (existing is not null)
            {
                var idx = SelectedMacro.Steps.IndexOf(existing);
                SelectedMacro.Steps[idx] = step;
            }
            else
            {
                SelectedMacro.Steps.Add(step);
            }
        }
        _logger.LogInformation("HardwareManager: SaveStep macro={MacroId} order={Order} — zapisano", step.MacroId, step.StepOrder);
    }

    public async Task DeleteStepAsync(MacroStep step)
    {
        _logger.LogInformation("HardwareManager: DeleteStep macro={MacroId} order={Order} action={Action}",
            step.MacroId, step.StepOrder, step.ActionId);
        await _macroRepo.DeleteStepAsync(step.Id);
        SelectedMacro?.Steps.Remove(step);
        if (SelectedMacroStep == step) SelectedMacroStep = null;
        _logger.LogInformation("HardwareManager: DeleteStep order={Order} — usunięto", step.StepOrder);
    }

    // ── Scripts CRUD ──────────────────────────────────────────────────────────

    public async Task LoadScriptsAsync()
    {
        _logger.LogDebug("HardwareManager: LoadScripts START — count={Count}", Scripts.Count);
        var all = await _scriptRepo.GetAllAsync();
        Scripts.Clear();
        foreach (var s in all) Scripts.Add(new ScriptItem(s));
        _logger.LogDebug("HardwareManager: LoadScripts DONE — loaded={Count}", Scripts.Count);
    }

    public async Task SaveScriptAsync(Script script)
    {
        _logger.LogInformation("HardwareManager: SaveScript '{Name}' (id={Id}, lang={Lang}, enabled={En}, bodyLen={Len})",
            script.Name, script.Id, script.Language, script.IsEnabled, script.ScriptBody?.Length ?? 0);
        await _scriptRepo.SaveAsync(script);
        await LoadScriptsAsync();
        SelectedScript = Scripts.FirstOrDefault(s => s.Model.Id == script.Id);
        _logger.LogInformation("HardwareManager: SaveScript '{Name}' — zapisano", script.Name);
    }

    public async Task DeleteScriptAsync(ScriptItem item)
    {
        _logger.LogInformation("HardwareManager: DeleteScript '{Name}' (id={Id})", item.Name, item.Model.Id);
        await _scriptRepo.DeleteAsync(item.Model.Id);
        Scripts.Remove(item);
        if (SelectedScript == item) SelectedScript = null;
        _logger.LogInformation("HardwareManager: DeleteScript '{Name}' — usunięto", item.Name);
    }

    // ── Learn Mode ────────────────────────────────────────────────────────────

    public void StartLearn(Action<string, string> onDetected)
    {
        if (IsLearning) return;

        _logger.LogInformation("HardwareManager: Learn mode START");
        IsLearning   = true;
        LearnStatus  = LearnPrompt;
        LearnedDeviceType = string.Empty;
        LearnedSignature  = string.Empty;

        _learnService.StartLearning(Guid.NewGuid(), (_, evt) =>
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLearning        = false;
                LearnedDeviceType = evt.DeviceType;
                LearnedSignature  = evt.Payload.Signature;
                LearnStatus       = $"Wykryto: {evt.DeviceType} / {evt.Payload.Signature}";
                _logger.LogInformation("HardwareManager: Learn mode — wykryto device={Dev}, sig='{Sig}'",
                    evt.DeviceType, evt.Payload.Signature);
                onDetected(evt.DeviceType, evt.Payload.Signature);
            });
        });
    }

    public void CancelLearn()
    {
        _logger.LogInformation("HardwareManager: Learn mode CANCEL");
        _learnService.CancelLearning();
        IsLearning  = false;
        LearnStatus = "Anulowano.";
    }
}
