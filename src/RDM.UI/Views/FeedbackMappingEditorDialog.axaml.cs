using Avalonia.Controls;
using Avalonia.Interactivity;
using RDM.Core.Entities;
using System.Linq;

namespace RDM.UI.Views;

public partial class FeedbackMappingEditorDialog : Window
{
    private readonly FeedbackRule _rule = null!;

    public FeedbackMappingEditorDialog() => InitializeComponent();

    public FeedbackMappingEditorDialog(FeedbackRule rule)
    {
        _rule = rule;
        InitializeComponent();
        Opened += (_, _) => Populate();
    }

    private void Populate()
    {
        // EventName
        var evItems = EventNameBox.Items.Cast<ComboBoxItem>().ToList();
        EventNameBox.SelectedItem = evItems.FirstOrDefault(i => i.Content?.ToString() == _rule.EventName)
            ?? evItems.FirstOrDefault();

        // DeviceType
        var dtItems = DeviceTypeBox.Items.Cast<ComboBoxItem>().ToList();
        DeviceTypeBox.SelectedItem = dtItems.FirstOrDefault(i => i.Content?.ToString() == _rule.TargetDeviceType)
            ?? dtItems.FirstOrDefault();

        DeviceIdBox.Text  = _rule.TargetDeviceId;
        ChannelBox.Text   = _rule.Channel.ToString();
        NoteCodeBox.Text  = _rule.NoteCode.ToString();
        VelocityBox.Text  = _rule.Velocity.ToString();
        SerialCmdBox.Text = _rule.SerialCommand ?? string.Empty;
        EnabledBox.IsChecked = _rule.IsEnabled;

        UpdateFieldVisibility(_rule.TargetDeviceType);
    }

    private void OnDeviceTypeChanged(object? s, SelectionChangedEventArgs e)
    {
        var selected = (DeviceTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "MIDI";
        UpdateFieldVisibility(selected);
    }

    private void UpdateFieldVisibility(string deviceType)
    {
        bool isMidi   = deviceType == "MIDI";
        bool isSerial = deviceType == "SERIAL";

        if (ChannelLabel  is not null) ChannelLabel.IsVisible  = isMidi;
        if (ChannelBox    is not null) ChannelBox.IsVisible    = isMidi;
        if (NoteLabel     is not null) NoteLabel.IsVisible     = isMidi || deviceType == "DR_MIXER";
        if (NoteCodeBox   is not null) NoteCodeBox.IsVisible   = isMidi || deviceType == "DR_MIXER";
        if (VelocityLabel is not null) VelocityLabel.IsVisible = isMidi;
        if (VelocityBox   is not null) VelocityBox.IsVisible   = isMidi;
        if (SerialLabel   is not null) SerialLabel.IsVisible   = isSerial;
        if (SerialCmdBox  is not null) SerialCmdBox.IsVisible  = isSerial;
    }

    private void OnSave(object? s, RoutedEventArgs e)
    {
        _rule.EventName        = (EventNameBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Player.Started";
        _rule.TargetDeviceType = (DeviceTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "MIDI";
        _rule.TargetDeviceId   = DeviceIdBox.Text?.Trim() ?? string.Empty;
        _rule.IsEnabled        = EnabledBox.IsChecked == true;

        if (byte.TryParse(ChannelBox.Text,  out var ch))  _rule.Channel  = ch;
        if (byte.TryParse(NoteCodeBox.Text, out var note)) _rule.NoteCode = note;
        if (byte.TryParse(VelocityBox.Text, out var vel))  _rule.Velocity = vel;

        _rule.SerialCommand = string.IsNullOrWhiteSpace(SerialCmdBox.Text) ? null : SerialCmdBox.Text.Trim();

        if (_rule.TargetDeviceType == "DR_MIXER")
        {
            _rule.DrTarget   = "Gpo";
            _rule.DrGpoIndex = _rule.NoteCode;
        }

        Close(true);
    }

    private void OnCancel(object? s, RoutedEventArgs e) => Close(false);
}
