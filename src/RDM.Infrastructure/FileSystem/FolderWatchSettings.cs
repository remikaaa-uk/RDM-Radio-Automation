using RDM.Core.Constants;

namespace RDM.Infrastructure.FileSystem;

/// <summary>
/// Configuration for FolderWatchService. Registered as singleton at the composition root.
/// Values sourced from rdm.config.json via IConfiguration — never hardcoded.
/// </summary>
public sealed record FolderWatchSettings(
    string                 WatchPath,
    IReadOnlyList<string>  SupportedExtensions)
{
    /// <summary>Default audio extensions recognised by the import pipeline.</summary>
    public static IReadOnlyList<string> DefaultExtensions { get; } = SupportedAudioExtensions.All;
}
