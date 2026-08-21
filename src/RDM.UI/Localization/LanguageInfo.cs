namespace RDM.UI.Localization;

/// <summary>One available UI language: <paramref name="Code"/> is the file/BCP-47 short code
/// (e.g. "en", "pl") or the sentinel "system"; <paramref name="Name"/> is the display name.</summary>
public sealed record LanguageInfo(string Code, string Name);
