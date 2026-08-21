using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using RDM.Core.Entities;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class TriggerMappingEditorDialog : Window
{
    private readonly TriggerActionMapping _mapping = null!;

    private List<NamedItem> _scripts = [];
    private List<NamedItem> _macros  = [];

    public TriggerMappingEditorDialog() => InitializeComponent();

    public TriggerMappingEditorDialog(TriggerActionMapping mapping)
    {
        _mapping = mapping;
        InitializeComponent();
        Opened += async (_, _) =>
        {
            await LoadPickerItemsAsync();
            Populate();
            ActionIdBox.SelectionChanged += OnActionChanged;
        };
    }

    private async Task LoadPickerItemsAsync()
    {
        var scriptRepo = App.Services.GetRequiredService<IScriptRepository>();
        var macroRepo  = App.Services.GetRequiredService<IMacroRepository>();

        var scripts = await scriptRepo.GetAllAsync();
        var macros  = await macroRepo.GetAllAsync();

        _scripts = scripts.Select(s => new NamedItem(s.Id, s.Name)).ToList();
        _macros  = macros.Select(m  => new NamedItem(m.Id, m.Name)).ToList();
    }

    private void Populate()
    {
        NameBox.Text = _mapping.Name;

        var dtItems = DeviceTypeBox.Items.Cast<ComboBoxItem>().ToList();
        DeviceTypeBox.SelectedItem = dtItems.FirstOrDefault(i =>
            i.Content?.ToString() == _mapping.SourceDeviceType)
            ?? dtItems.FirstOrDefault();

        DeviceIdBox.Text     = _mapping.SourceDeviceId ?? string.Empty;
        SignatureBox.Text    = _mapping.TargetSignature;
        ParameterBox.Text   = _mapping.TargetParameter ?? string.Empty;
        EnabledBox.IsChecked = _mapping.IsEnabled;

        foreach (var id in Enum.GetValues<ActionId>().OrderBy(a => a.ToString()))
            ActionIdBox.Items.Add(id.ToString());

        ActionIdBox.SelectedItem = _mapping.TargetActionId.ToString();

        // Trigger initial picker update (without firing SelectionChanged yet)
        UpdateParameterPicker(_mapping.TargetActionId, _mapping.TargetParameter);
    }

    private void OnActionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Enum.TryParse<ActionId>(ActionIdBox.SelectedItem?.ToString(), out var action))
            UpdateParameterPicker(action, null);
    }

    private void UpdateParameterPicker(ActionId action, string? existingGuid)
    {
        bool isScript = action == ActionId.AutomationRunScript;
        bool isMacro  = action == ActionId.AutomationTriggerMacro;
        bool usePicker = isScript || isMacro;

        ParameterBox.IsVisible       = !usePicker;
        ParameterPickerBox.IsVisible =  usePicker;

        if (!usePicker) return;

        var items = isScript ? _scripts : _macros;
        ParameterPickerBox.Items.Clear();
        foreach (var item in items)
            ParameterPickerBox.Items.Add(item);

        if (existingGuid is not null && Guid.TryParse(existingGuid, out var guid))
            ParameterPickerBox.SelectedItem = items.FirstOrDefault(i => i.Id == guid);
        else if (ParameterPickerBox.SelectedItem is null && items.Count > 0)
            ParameterPickerBox.SelectedItem = items[0];
    }

    private void OnSave(object? s, RoutedEventArgs e)
    {
        _mapping.Name             = NameBox.Text?.Trim() ?? string.Empty;
        _mapping.SourceDeviceType = (DeviceTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "KEYBOARD";
        _mapping.SourceDeviceId   = string.IsNullOrWhiteSpace(DeviceIdBox.Text) ? null : DeviceIdBox.Text.Trim();
        _mapping.TargetSignature  = SignatureBox.Text?.Trim() ?? string.Empty;
        _mapping.IsEnabled        = EnabledBox.IsChecked == true;

        if (ParameterPickerBox.IsVisible)
        {
            _mapping.TargetParameter = (ParameterPickerBox.SelectedItem as NamedItem)?.Id.ToString();
        }
        else
        {
            _mapping.TargetParameter = string.IsNullOrWhiteSpace(ParameterBox.Text)
                ? null
                : ParameterBox.Text.Trim();
        }

        if (Enum.TryParse<ActionId>(ActionIdBox.SelectedItem?.ToString(), out var action))
            _mapping.TargetActionId = action;

        Close(true);
    }

    private void OnCancel(object? s, RoutedEventArgs e) => Close(false);

    private sealed record NamedItem(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }
}
