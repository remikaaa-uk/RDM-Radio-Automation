using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RDM.UI.Views;

public partial class SimpleConfirmDialog : Window
{
    public SimpleConfirmDialog() => InitializeComponent();

    /// <param name="title">Overrides the default "Confirm" title.</param>
    /// <param name="confirmLabel">Overrides the default "Yes" — pass the destructive verb, e.g. "Delete from disk".</param>
    /// <param name="cancelLabel">Overrides the default "No".</param>
    public SimpleConfirmDialog(string message, string? title = null, string? confirmLabel = null, string? cancelLabel = null)
    {
        InitializeComponent();
        MessageText.Text = message;
        if (title        is not null) Title              = title;
        if (confirmLabel is not null) ConfirmBtn.Content = confirmLabel;
        if (cancelLabel  is not null) CancelBtn.Content  = cancelLabel;
    }

    private void OnYes(object? s, RoutedEventArgs e) => Close(true);
    private void OnNo(object? s, RoutedEventArgs e)  => Close(false);
}
