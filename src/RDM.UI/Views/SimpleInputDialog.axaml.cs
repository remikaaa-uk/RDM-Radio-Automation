using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace RDM.UI.Views;

public partial class SimpleInputDialog : Window
{
    public SimpleInputDialog() => InitializeComponent();

    public SimpleInputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title           = title;
        PromptText.Text = prompt;
        InputBox.Text   = initialValue;
        Opened += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Confirm();

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Confirm();
    }

    private void Confirm() => Close(InputBox.Text?.Trim() ?? string.Empty);
}
