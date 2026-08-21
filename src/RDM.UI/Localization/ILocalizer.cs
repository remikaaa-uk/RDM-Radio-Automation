using System.Collections.Generic;
using System.ComponentModel;

namespace RDM.UI.Localization;

/// <summary>
/// UI string localization. Backed by JSON files in a <c>lang/</c> folder (see <see cref="Localizer"/>).
/// Implements <see cref="INotifyPropertyChanged"/> so <c>{i18n:Tr key}</c> bindings refresh live when
/// the language changes — no restart.
/// </summary>
public interface ILocalizer : INotifyPropertyChanged
{
    /// <summary>Translated value for <paramref name="key"/>; falls back to English, then the key itself.</summary>
    string this[string key] { get; }

    /// <summary><c>string.Format</c> of the translated template with <paramref name="args"/>.</summary>
    string Format(string key, params object?[] args);

    /// <summary>Currently active resolved language code (never "system").</summary>
    string CurrentLanguage { get; }

    /// <summary>Languages discovered in the lang folders (does not include the "system" sentinel).</summary>
    IReadOnlyList<LanguageInfo> AvailableLanguages { get; }

    /// <summary>Switch language. Accepts a code ("en"/"pl"/…) or "system"/"auto" to follow the OS.</summary>
    void SetLanguage(string codeOrSystem);
}
