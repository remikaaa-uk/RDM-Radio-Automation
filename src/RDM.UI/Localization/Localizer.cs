using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RDM.UI.Services;

namespace RDM.UI.Localization;

/// <summary>
/// JSON-backed UI localizer. Discovers <c>lang/*.json</c> from two folders:
///   1. bundled — next to the executable (<c>AppContext.BaseDirectory\lang</c>),
///   2. user    — <c>%ProgramData%\RDM\lang</c> (writable without admin; extends/overrides bundled by code).
///
/// English (<c>en</c>) is the canonical fallback. A missing key falls back en → key-as-text.
/// Switching language raises <see cref="INotifyPropertyChanged"/> with a null property name, so every
/// <c>{i18n:Tr}</c> binding re-evaluates live.
///
/// <see cref="Instance"/> is the single app-wide instance used by the XAML markup extension; the same
/// object is registered in DI. Construct it once, early (before any localized window is shown).
/// </summary>
public sealed class Localizer : ILocalizer
{
    public static Localizer? Instance { get; private set; }

    private readonly ILogger<Localizer> _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _langs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LanguageInfo> _available = new();

    private Dictionary<string, string> _current  = new(StringComparer.Ordinal);
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);

    public string CurrentLanguage { get; private set; } = "en";
    public IReadOnlyList<LanguageInfo> AvailableLanguages => _available;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Live {i18n:Tr} updates go through weakly-referenced refresh callbacks, not an event or INPC on
    // an indexer (Avalonia does not reliably re-evaluate "[key]" bindings). Each TranslatedString
    // registers its refresh here; dead ones are pruned on raise, so bindings never leak.
    private readonly object _handlersGate = new();
    private readonly List<WeakReference<Action>> _changeHandlers = new();

    internal void AddChangeHandler(Action handler)
    {
        lock (_handlersGate) _changeHandlers.Add(new WeakReference<Action>(handler));
    }

    private void RaiseLanguageChanged()
    {
        List<Action> alive = new();
        lock (_handlersGate)
        {
            for (int i = _changeHandlers.Count - 1; i >= 0; i--)
            {
                if (_changeHandlers[i].TryGetTarget(out var h)) alive.Add(h);
                else _changeHandlers.RemoveAt(i);
            }
        }
        foreach (var h in alive) h();
    }

    public Localizer(ILogger<Localizer> logger)
    {
        _logger = logger;
        Instance = this;
        Discover();
    }

    private static IEnumerable<string> LanguageDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "lang");

        var cfgDir = Path.GetDirectoryName(ConfigPaths.FilePath);
        if (!string.IsNullOrEmpty(cfgDir))
            yield return Path.Combine(cfgDir, "lang");
    }

    private void Discover()
    {
        _langs.Clear();
        _available.Clear();

        foreach (var dir in LanguageDirectories())
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject root) continue;

                    var meta = root["_meta"] as JsonObject;
                    var code = meta?["code"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file);
                    var name = meta?["name"]?.GetValue<string>() ?? code;

                    if (!_langs.TryGetValue(code, out var dict))
                    {
                        dict = new Dictionary<string, string>(StringComparer.Ordinal);
                        _langs[code] = dict;
                    }

                    foreach (var kv in root)
                    {
                        if (kv.Key == "_meta") continue;
                        if (kv.Value is JsonValue v && v.TryGetValue<string>(out var s))
                            dict[kv.Key] = s; // user dir loaded after bundled → overrides
                    }

                    if (_available.All(l => !string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)))
                        _available.Add(new LanguageInfo(code, name));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Localizer: failed to load language file {File}", file);
                }
            }
        }

        _available.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _fallback = _langs.TryGetValue("en", out var en) ? en : new Dictionary<string, string>(StringComparer.Ordinal);

        _logger.LogInformation("Localizer: discovered {Count} language(s): {Codes}",
            _available.Count, string.Join(", ", _available.Select(l => l.Code)));
    }

    /// <summary>Apply the configured language at startup ("system"/"auto"/code/null).</summary>
    public void Load(string? configuredLanguage) => SetLanguage(configuredLanguage ?? "system");

    public void SetLanguage(string codeOrSystem)
    {
        var code = Resolve(codeOrSystem);
        _current = _langs.TryGetValue(code, out var d) ? d : _fallback;
        CurrentLanguage = code;
        _logger.LogInformation("Localizer: active language = {Code}", code);
        RaiseLanguageChanged();                                            // drives live {i18n:Tr} updates
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); // for any INPC consumers
    }

    private string Resolve(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (v is "" or "system" or "auto")
            v = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        if (_langs.ContainsKey(v)) return v;
        if (_langs.ContainsKey("en")) return "en";
        return _available.Count > 0 ? _available[0].Code : "en";
    }

    public string this[string key]
    {
        get
        {
            if (_current.TryGetValue(key, out var v)) return v;
            if (_fallback.TryGetValue(key, out var f)) return f;
            return key;
        }
    }

    public string Format(string key, params object?[] args)
    {
        var template = this[key];
        try { return string.Format(template, args); }
        catch (FormatException) { return template; }
    }
}
