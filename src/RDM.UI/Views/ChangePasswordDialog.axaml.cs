using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.ViewModels;

namespace RDM.UI.Views;

/// <summary>Self-service "change my password" dialog, reachable from the user menu in MainWindow.</summary>
public partial class ChangePasswordDialog : Window
{
    public ChangePasswordDialog()
    {
        InitializeComponent();

        var vm = App.Services.GetRequiredService<ChangePasswordViewModel>();
        DataContext = vm;

        vm.Saved     += () => Close(true);
        vm.Cancelled += () => Close(false);
    }
}
