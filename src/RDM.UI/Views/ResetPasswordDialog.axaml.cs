using Avalonia.Controls;
using Avalonia.Interactivity;
using RDM.UI.ViewModels;

namespace RDM.UI.Views;

/// <summary>Admin-side "reset this user's password" dialog. Returns the new password, or null if cancelled.</summary>
public partial class ResetPasswordDialog : Window
{
    private readonly ResetPasswordDialogViewModel _vm;

    public ResetPasswordDialog() : this("") { }

    public ResetPasswordDialog(string targetUsername)
    {
        InitializeComponent();
        _vm = new ResetPasswordDialogViewModel(targetUsername);
        DataContext = _vm;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);

    private void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        var password = _vm.TryGetPassword();
        if (password is not null) Close(password);
    }
}
