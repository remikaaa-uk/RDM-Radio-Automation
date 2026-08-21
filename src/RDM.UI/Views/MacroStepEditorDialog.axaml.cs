using Avalonia.Controls;
using Avalonia.Interactivity;
using RDM.Core.Entities;
using RDM.Core.Hardware;
using System;
using System.Linq;

namespace RDM.UI.Views;

public partial class MacroStepEditorDialog : Window
{
    private readonly MacroStep _step = null!;

    public MacroStepEditorDialog() => InitializeComponent();

    public MacroStepEditorDialog(MacroStep step)
    {
        _step = step;
        InitializeComponent();
        Opened += (_, _) => Populate();
    }

    private void Populate()
    {
        OrderBox.Text = _step.StepOrder.ToString();
        DelayBox.Text = _step.DelayAfterMs.ToString();

        foreach (var id in Enum.GetValues<ActionId>().OrderBy(a => a.ToString()))
            ActionIdBox.Items.Add(id.ToString());

        var isActionMode = _step.ActionId.HasValue || string.IsNullOrEmpty(_step.OutputDeviceType);
        ModeBox.SelectedIndex = isActionMode ? 0 : 1;
        UpdateVisibility(isActionMode);

        if (_step.ActionId.HasValue)
            ActionIdBox.SelectedItem = _step.ActionId.Value.ToString();

        ParamBox.Text  = _step.Parameter ?? string.Empty;
        OutIdBox.Text  = _step.OutputDeviceId ?? string.Empty;
        OutCmdBox.Text = _step.OutputCommand  ?? string.Empty;

        var outTypeItems = OutTypeBox.Items.Cast<ComboBoxItem>().ToList();
        OutTypeBox.SelectedItem = outTypeItems.FirstOrDefault(i =>
            i.Content?.ToString() == _step.OutputDeviceType)
            ?? outTypeItems.FirstOrDefault();
    }

    private void OnModeChanged(object? s, SelectionChangedEventArgs e)
    {
        UpdateVisibility(ModeBox.SelectedIndex == 0);
    }

    private void UpdateVisibility(bool isAction)
    {
        if (ActionLabel  is not null) ActionLabel.IsVisible  = isAction;
        if (ActionIdBox  is not null) ActionIdBox.IsVisible  = isAction;
        if (ParamLabel   is not null) ParamLabel.IsVisible   = isAction;
        if (ParamBox     is not null) ParamBox.IsVisible     = isAction;
        if (OutTypeLabel is not null) OutTypeLabel.IsVisible = !isAction;
        if (OutTypeBox   is not null) OutTypeBox.IsVisible   = !isAction;
        if (OutIdLabel   is not null) OutIdLabel.IsVisible   = !isAction;
        if (OutIdBox     is not null) OutIdBox.IsVisible     = !isAction;
        if (OutCmdLabel  is not null) OutCmdLabel.IsVisible  = !isAction;
        if (OutCmdBox    is not null) OutCmdBox.IsVisible    = !isAction;
    }

    private void OnSave(object? s, RoutedEventArgs e)
    {
        if (int.TryParse(OrderBox.Text, out var order)) _step.StepOrder = order;
        if (int.TryParse(DelayBox.Text, out var delay)) _step.DelayAfterMs = delay;

        var isAction = ModeBox.SelectedIndex == 0;
        if (isAction)
        {
            _step.ActionId         = Enum.TryParse<ActionId>(ActionIdBox.SelectedItem?.ToString(), out var aid) ? aid : null;
            _step.Parameter        = string.IsNullOrWhiteSpace(ParamBox.Text) ? null : ParamBox.Text.Trim();
            _step.OutputDeviceType = null;
            _step.OutputDeviceId   = null;
            _step.OutputCommand    = null;
        }
        else
        {
            _step.ActionId         = null;
            _step.Parameter        = null;
            _step.OutputDeviceType = (OutTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            _step.OutputDeviceId   = string.IsNullOrWhiteSpace(OutIdBox.Text) ? null : OutIdBox.Text.Trim();
            _step.OutputCommand    = string.IsNullOrWhiteSpace(OutCmdBox.Text) ? null : OutCmdBox.Text.Trim();
        }

        Close(true);
    }

    private void OnCancel(object? s, RoutedEventArgs e) => Close(false);
}
