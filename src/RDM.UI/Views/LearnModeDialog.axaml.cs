using Avalonia.Controls;
using Avalonia.Interactivity;
using RDM.UI.ViewModels;

namespace RDM.UI.Views;

public partial class LearnModeDialog : Window
{
    private readonly HardwareManagerViewModel _vm = null!;

    public LearnModeDialog() => InitializeComponent();

    public LearnModeDialog(HardwareManagerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
        Opened += (_, _) => _vm.StartLearn((_, _) => { /* UI auto-updates via bindings */ });
        Closed += (_, _) => _vm.CancelLearn();
    }

    private void OnApply(object? s, RoutedEventArgs e) => Close();

    private void OnCancel(object? s, RoutedEventArgs e)
    {
        _vm.CancelLearn();
        // Wyczyść wykryte wartości żeby wywołujący wiedział, że anulowano
        _vm.LearnedSignature  = string.Empty;
        _vm.LearnedDeviceType = string.Empty;
        Close();
    }
}
