using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using RDM.UI.Localization;

namespace RDM.UI.Controls;

/// <summary>
/// Adds a clear ("✕") button inside the left edge of a search <see cref="TextBox"/>:
/// <c>ctrl:SearchBoxBehavior.ShowClearButton="True"</c>.
///
/// The button only clears <see cref="TextBox.Text"/> — it never touches a ViewModel. Every search
/// box in the app either two-way-binds Text or handles TextChanged, so clearing propagates and the
/// existing search path re-runs on its own. That also makes this work in views without a DataContext
/// (e.g. CartAssetPickerDialog).
/// </summary>
public static class SearchBoxBehavior
{
    public static readonly AttachedProperty<bool> ShowClearButtonProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>(
            "ShowClearButton", typeof(SearchBoxBehavior));

    public static bool GetShowClearButton(TextBox box) => box.GetValue(ShowClearButtonProperty);
    public static void SetShowClearButton(TextBox box, bool value) => box.SetValue(ShowClearButtonProperty, value);

    static SearchBoxBehavior()
    {
        ShowClearButtonProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            if (e.NewValue is true) Attach(box);
        });
    }

    private static void Attach(TextBox box)
    {
        if (box.InnerLeftContent is Button) return; // already attached

        var button = new Button
        {
            Content = "✕",
            Classes = { "searchclear" },
            Focusable = false   // clicking must not steal focus from the text box
        };
        ToolTip.SetTip(button, Localizer.Instance?["common.tip.clear_search"] ?? "Clear search (ESC)");

        // Visible only when there is something to clear.
        button.Bind(Visual.IsVisibleProperty, new Binding
        {
            Path      = nameof(TextBox.Text),
            Source    = box,
            Converter = StringConverters.IsNotNullOrEmpty
        });

        button.Click += (_, _) => Clear(box);
        box.InnerLeftContent = button;
        box.KeyDown += OnKeyDown;
    }

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not TextBox box) return;

        // Nothing to clear → let Escape bubble, so it still reaches things like a dialog's
        // IsCancel button (CartAssetPickerDialog closes on Escape).
        if (string.IsNullOrEmpty(box.Text)) return;

        Clear(box);
        e.Handled = true;
    }

    private static void Clear(TextBox box)
    {
        box.Text = string.Empty;
        box.Focus();
    }
}
