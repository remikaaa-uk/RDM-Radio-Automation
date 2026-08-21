namespace RDM.Core.Constants;

/// <summary>
/// Single source of truth for the audio file extensions recognised across the
/// application — the import pipeline, the Import Folder / Update Tracks scanners
/// and the folder-watch service. Extensions are lowercase and include the
/// leading dot. Do not duplicate this list elsewhere; add new formats here.
/// </summary>
public static class SupportedAudioExtensions
{
    /// <summary>Canonical, ordered list of supported audio extensions.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ".mp3", ".wav", ".flac", ".ogg",
        ".aac", ".m4a", ".wma", ".aiff", ".aif"
    ];

    private static readonly HashSet<string> Lookup =
        new(All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the file at <paramref name="path"/> has a supported
    /// audio extension. Matching is case-insensitive.
    /// </summary>
    public static bool IsSupported(string path)
        => Lookup.Contains(Path.GetExtension(path));
}
