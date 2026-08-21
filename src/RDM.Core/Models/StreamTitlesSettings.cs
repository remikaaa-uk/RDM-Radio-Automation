namespace RDM.Core.Models;

/// <summary>
/// Populated at startup from rdm.config.json section "stream_titles".
/// Injected into StreamTitlesService as a value object — no IConfiguration in Core.
/// Empty AllowedFormatIds means no format restriction (all formats pass through).
/// </summary>
public sealed record StreamTitlesSettings
{
    public bool                  Enabled          { get; init; } = true;
    public string                OutputFilePath   { get; init; } = string.Empty;
    public string                Format           { get; init; } = "$artist$ - $title$";
    public string                Encoding         { get; init; } = "UTF-8";
    public string                FallbackArtist   { get; init; } = "Radio";
    public string                FallbackTitle    { get; init; } = string.Empty;
    public IReadOnlySet<string>  AllowedFormatIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
