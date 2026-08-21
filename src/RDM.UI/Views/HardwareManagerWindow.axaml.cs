using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Hardware;
using RDM.UI.Localization;
using RDM.UI.ViewModels;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class HardwareManagerWindow : Window
{
    private HardwareManagerViewModel _vm = null!;
    private ILogger<HardwareManagerWindow> _log = null!;

    private static int _instanceCounter;
    private readonly int _instanceId = ++_instanceCounter;

    public HardwareManagerWindow()
    {
        InitializeComponent();
        _vm  = App.Services.GetRequiredService<HardwareManagerViewModel>();
        _log = App.Services.GetRequiredService<ILogger<HardwareManagerWindow>>();
        DataContext = _vm;

        Opened += async (_, _) =>
        {
            _log.LogDebug("HardwareManagerWindow #{Id} Opened — TriggerMappings.Count={Count}, SelectedTriggerMapping={Sel}",
                _instanceId, _vm.TriggerMappings.Count, _vm.SelectedTriggerMapping?.Name ?? "null");
            await _vm.LoadAsync();
        };

        Closed += (_, _) =>
        {
            _log.LogDebug("HardwareManagerWindow #{Id} Closed — SelectedTriggerMapping={Sel}",
                _instanceId, _vm.SelectedTriggerMapping?.Name ?? "null");
            _vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    // ── Trigger Mappings ──────────────────────────────────────────────────────

    private void OnTriggerSelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        var selected = ((DataGrid)s!).SelectedItem as TriggerMappingItem;
        _log.LogDebug("HardwareManagerWindow #{Id} TriggerGrid.SelectionChanged → {Sel}",
            _instanceId, selected?.Name ?? "null");
        _vm.SelectedTriggerMapping = selected;
    }

    private async void OnAddTriggerMapping(object? s, RoutedEventArgs e)
    {
        var mapping = new TriggerActionMapping { Id = Guid.NewGuid(), IsEnabled = true };
        var dialog  = new TriggerMappingEditorDialog(mapping);
        var ok      = await dialog.ShowDialog<bool>(this);
        if (ok) await _vm.SaveTriggerMappingAsync(mapping);
    }

    private async void OnEditTriggerMapping(object? s, RoutedEventArgs e)
    {
        _log.LogDebug("HardwareManagerWindow #{Id} OnEditTriggerMapping — SelectedTriggerMapping={Sel}",
            _instanceId, _vm.SelectedTriggerMapping?.Name ?? "null");
        if (_vm.SelectedTriggerMapping is not { Model: var m }) return;
        var dialog = new TriggerMappingEditorDialog(m);
        var ok     = await dialog.ShowDialog<bool>(this);
        if (ok) await _vm.SaveTriggerMappingAsync(m);
    }

    private async void OnDeleteTriggerMapping(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedTriggerMapping is not { } item) return;
        var confirmed = await ConfirmDeleteAsync(string.Format(
            Localizer.Instance?["hw.confirm.delete_trigger"] ?? "Delete mapping '{0}'?", item.Name));
        if (confirmed) await _vm.DeleteTriggerMappingAsync(item);
    }

    private async void OnLearnTriggerMapping(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedTriggerMapping is not { Model: var m }) return;

        var dialog = new LearnModeDialog(_vm);
        await dialog.ShowDialog(this);

        // After dialog closes, apply learned values if captured
        if (!string.IsNullOrEmpty(_vm.LearnedSignature))
        {
            m.TargetSignature  = _vm.LearnedSignature;
            m.SourceDeviceType = _vm.LearnedDeviceType;
            await _vm.SaveTriggerMappingAsync(m);
        }
    }

    // ── Feedback Mappings ─────────────────────────────────────────────────────

    private async void OnAddFeedbackMapping(object? s, RoutedEventArgs e)
    {
        var rule   = new FeedbackRule { Id = Guid.NewGuid(), Channel = 1, Velocity = 127, IsEnabled = true };
        var dialog = new FeedbackMappingEditorDialog(rule);
        var ok     = await dialog.ShowDialog<bool>(this);
        if (ok) await _vm.SaveFeedbackMappingAsync(rule);
    }

    private async void OnEditFeedbackMapping(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedFeedbackMapping is not { Model: var r }) return;
        var dialog = new FeedbackMappingEditorDialog(r);
        var ok     = await dialog.ShowDialog<bool>(this);
        if (ok) await _vm.SaveFeedbackMappingAsync(r);
    }

    private async void OnDeleteFeedbackMapping(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedFeedbackMapping is not { } item) return;
        var confirmed = await ConfirmDeleteAsync(string.Format(
            Localizer.Instance?["hw.confirm.delete_feedback"] ?? "Delete feedback mapping '{0}'?", item.EventName));
        if (confirmed) await _vm.DeleteFeedbackMappingAsync(item);
    }

    // ── Macros ────────────────────────────────────────────────────────────────

    private async void OnAddMacro(object? s, RoutedEventArgs e)
    {
        var name = await new SimpleInputDialog(
            Localizer.Instance?["hw.macro.new_title"]   ?? "New macro",
            Localizer.Instance?["hw.macro.name_prompt"] ?? "Macro name:").ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name)) return;
        var macro = new Macro { Id = Guid.NewGuid(), Name = name, IsEnabled = true };
        await _vm.SaveMacroAsync(macro);
    }

    private async void OnEditMacro(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedMacro is not { Model: var m }) return;
        var name = await new SimpleInputDialog(
            Localizer.Instance?["hw.macro.edit_title"]  ?? "Edit macro",
            Localizer.Instance?["hw.macro.name_prompt"] ?? "Macro name:", m.Name)
            .ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(name)) return;
        m.Name = name;
        await _vm.SaveMacroAsync(m);
    }

    private async void OnDeleteMacro(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedMacro is not { } item) return;
        var confirmed = await ConfirmDeleteAsync(string.Format(
            Localizer.Instance?["hw.confirm.delete_macro"] ?? "Delete macro '{0}' (including its steps)?", item.Name));
        if (confirmed) await _vm.DeleteMacroAsync(item);
    }

    private async void OnAddMacroStep(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedMacro is not { } macro) return;
        var nextOrder = macro.Steps.Count > 0 ? macro.Steps.Max(x => x.StepOrder) + 10 : 10;
        var step      = new MacroStep { Id = Guid.NewGuid(), MacroId = macro.Model.Id, StepOrder = nextOrder };
        var dialog    = new MacroStepEditorDialog(step);
        var ok        = await dialog.ShowDialog<bool>(this);
        if (ok) await _vm.SaveStepAsync(step);
    }

    private async void OnEditMacroStep(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedMacroStep is not { } step) return;
        var dialog = new MacroStepEditorDialog(step);
        var ok     = await dialog.ShowDialog<bool>(this);
        if (ok) await _vm.SaveStepAsync(step);
    }

    private async void OnDeleteMacroStep(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedMacroStep is not { } step) return;
        var confirmed = await ConfirmDeleteAsync(string.Format(
            Localizer.Instance?["hw.confirm.delete_step"] ?? "Delete step {0}?", step.StepOrder));
        if (confirmed) await _vm.DeleteStepAsync(step);
    }

    // ── Scripts ───────────────────────────────────────────────────────────────

    private async void OnAddScript(object? s, RoutedEventArgs e)
    {
        var script = new Core.Entities.Script
        {
            Id         = Guid.NewGuid(),
            Name       = "Nowy skrypt",
            Language   = "js",
            ScriptBody = string.Empty,
            IsEnabled  = true
        };
        await _vm.SaveScriptAsync(script);
        // Skrypt jest teraz zaznaczony — wstaw szablon do edytora
        if (ScriptBodyBox is not null)
            ScriptBodyBox.Text = "// Dostępne API:\n// player.play(); player.stop(); player.next();\n// mic.toggle();\n// cart.selectTab(0); cart.triggerSlot(0);\n// rdm.sendHttp('POST|http://localhost:9300/api/v1/aux/0/play|{}');\n// rdm.delay(500);\n// rdm.log('info');\n";
    }

    private async void OnDeleteScript(object? s, RoutedEventArgs e)
    {
        if (_vm.SelectedScript is not { } item) return;
        var confirmed = await ConfirmDeleteAsync(string.Format(
            Localizer.Instance?["hw.confirm.delete_script"] ?? "Delete script '{0}'?", item.Name));
        if (confirmed) { await _vm.DeleteScriptAsync(item); ClearScriptEditor(); }
    }

    private async void OnSaveScript(object? s, RoutedEventArgs e)
    {
        if (ScriptNameBox?.Text is not { } name || string.IsNullOrWhiteSpace(name)) return;

        var script = _vm.SelectedScript?.Model ?? new Core.Entities.Script { Id = Guid.NewGuid() };
        script.Name       = name.Trim();
        script.ScriptBody = ScriptBodyBox?.Text ?? string.Empty;
        script.IsEnabled  = ScriptEnabledBox?.IsChecked == true;
        script.Language   = "js";

        await _vm.SaveScriptAsync(script);
    }

    private void LoadScriptToEditor(Core.Entities.Script script)
    {
        if (ScriptNameBox    is not null) ScriptNameBox.Text        = script.Name;
        if (ScriptBodyBox    is not null) ScriptBodyBox.Text        = script.ScriptBody;
        if (ScriptEnabledBox is not null) ScriptEnabledBox.IsChecked = script.IsEnabled;
    }

    private void ClearScriptEditor()
    {
        if (ScriptNameBox    is not null) ScriptNameBox.Text        = string.Empty;
        if (ScriptBodyBox    is not null) ScriptBodyBox.Text        = string.Empty;
        if (ScriptEnabledBox is not null) ScriptEnabledBox.IsChecked = false;
    }

    // ── ObservableProperty changed — load script into editor when selected ───

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _log?.LogDebug("HardwareManagerWindow #{Id} OnDataContextChanged — subscribing PropertyChanged", _instanceId);
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(_vm.SelectedScript) && _vm.SelectedScript is { Model: var m })
            LoadScriptToEditor(m);
    }

    // ── Close / helpers ───────────────────────────────────────────────────────

    private void OnClose(object? s, RoutedEventArgs e) => Close();

    private async Task<bool> ConfirmDeleteAsync(string message)
    {
        var dialog = new SimpleConfirmDialog(message);
        return await dialog.ShowDialog<bool>(this);
    }
}
