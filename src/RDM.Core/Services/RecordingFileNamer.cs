using System.Globalization;
using RDM.Shared.Enums;

namespace RDM.Core.Services;

/// <summary>
/// Turns a directory plus a start time into the file a recording will be written to.
///
/// Pure and side-effect free — existence is asked of the caller through a predicate — so the naming
/// rules (timestamp shape, sanitising, collision handling) are testable without touching a disk or
/// starting BASS.
/// </summary>
public static class RecordingFileNamer
{
    private const string DefaultPrefix = "rec";

    /// Maximum collision suffixes tried before giving up. Two recordings can legitimately start in
    /// the same second; a thousand cannot, and looping forever on a broken predicate would hang.
    private const int MaxCollisionAttempts = 1000;

    public static string ExtensionFor(EncoderFormat format) => format switch
    {
        EncoderFormat.Mp3  => "mp3",
        EncoderFormat.Ogg  => "ogg",
        EncoderFormat.Opus => "opus",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    /// <summary>
    /// Builds a full path of the shape <c>{directory}\{prefix}_{yyyy-MM-dd_HH-mm-ss}.{ext}</c>,
    /// appending <c>_2</c>, <c>_3</c>… while <paramref name="exists"/> reports a clash.
    /// </summary>
    /// <param name="startedAt">Local start time — a recording is filed by wall clock, not UTC.</param>
    /// <param name="exists">Returns true when that path is already taken.</param>
    public static string Build(
        string directory,
        EncoderFormat format,
        string? namePrefix,
        DateTime startedAt,
        Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(exists);

        var prefix = Sanitise(namePrefix);
        // Invariant, not the current culture: a Polish UI must not produce a different file name
        // than an English one, and the timestamp has to stay sortable byte-for-byte.
        var stamp     = startedAt.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var extension = ExtensionFor(format);

        var candidate = Path.Combine(directory, $"{prefix}_{stamp}.{extension}");
        for (int n = 2; exists(candidate) && n <= MaxCollisionAttempts; n++)
            candidate = Path.Combine(directory, $"{prefix}_{stamp}_{n}.{extension}");

        return candidate;
    }

    /// <summary>
    /// Strips whatever the filesystem would reject. A prefix reaching here can come from a user
    /// field, so it is treated as untrusted: separators included, to keep the result inside the
    /// requested directory.
    /// </summary>
    private static string Sanitise(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return DefaultPrefix;

        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(prefix.Trim()
            .Select(c => invalid.Contains(c) || c is '/' or '\\' ? '-' : c)
            .ToArray())
            .Trim('-', '.', ' ');

        return cleaned.Length == 0 ? DefaultPrefix : cleaned;
    }
}
