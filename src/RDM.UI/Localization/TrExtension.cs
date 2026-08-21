using System;
using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace RDM.UI.Localization;

/// <summary>
/// XAML markup extension for localized text: <c>Text="{i18n:Tr settings.appearance.language}"</c>.
///
/// Binds the target to the <see cref="TranslatedString.Value"/> of a small per-key object that raises
/// a normal <see cref="INotifyPropertyChanged"/> notification when the language changes. Binding to a
/// named property (not an indexer) is what makes Avalonia refresh reliably; the subscription to the
/// localizer is weak, so bindings never leak when windows close.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    /// <summary>Translation key, e.g. <c>settings.appearance.theme</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(TranslatedString.Value))
        {
            Source = new TranslatedString(Key),
            Mode   = BindingMode.OneWay
        };
}

/// <summary>Exposes the live translation of one key as a bindable <see cref="Value"/> property.</summary>
internal sealed class TranslatedString : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs ValueChanged = new(nameof(Value));

    private readonly string _key;
    private readonly Action _refresh; // strong field → alive as long as this object; localizer holds it weakly

    public TranslatedString(string key)
    {
        _key = key;
        _refresh = () => PropertyChanged?.Invoke(this, ValueChanged);
        Localizer.Instance?.AddChangeHandler(_refresh);
    }

    public string Value => Localizer.Instance is { } loc ? loc[_key] : _key;

    public event PropertyChangedEventHandler? PropertyChanged;
}
