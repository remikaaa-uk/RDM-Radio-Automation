using Avalonia.Controls;
using Avalonia.Interactivity;
using RDM.UI.ViewModels;

namespace RDM.UI.Views;

/// <summary>"Add user" dialog — collects username/role/password and returns a <see cref="NewUserRequest"/>.</summary>
public partial class UserEditDialog : Window
{
    private readonly NewUserDialogViewModel _vm = new();

    public UserEditDialog()
    {
        InitializeComponent();
        DataContext = _vm;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var request = _vm.TryBuildRequest();
        if (request is not null) Close(request);
    }
}
