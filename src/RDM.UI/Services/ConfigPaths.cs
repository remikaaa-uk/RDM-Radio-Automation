using System;
using System.IO;

namespace RDM.UI.Services;

/// <summary>
/// Single source of truth for the location of the writable <c>rdm.config.json</c> and
/// <c>rdm.log</c>.
///
/// The application is installed into Program Files, which a standard (non-elevated)
/// user cannot write to. Persisting anything there — "remember me" credentials, UI
/// state, settings edited in the app — silently fails. The live config therefore
/// lives in a per-machine writable location: <c>%ProgramData%\RDM\rdm.config.json</c>.
///
/// On first run <see cref="EnsureInitialized"/> seeds that file from the template the
/// installer wrote (or, in a dev build, the copy next to the executable), so the very
/// first configuration read (DB connection) already finds it.
/// </summary>
public static class ConfigPaths
{
    public const string FileName = "rdm.config.json";

    /// <summary>Writable, machine-wide config: <c>%ProgramData%\RDM\rdm.config.json</c>.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "RDM", FileName);

    /// <summary>Read-only template shipped next to the executable (installer / dev output).</summary>
    public static string SeedPath { get; } = Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// Ensures the ProgramData config exists and returns its path. On first run the file
    /// is seeded from <see cref="SeedPath"/> when present. Safe to call repeatedly and
    /// never throws — callers still read whatever exists if seeding is not possible.
    /// </summary>
    /// <summary>
    /// Writable log file. Stays next to the executable when that folder accepts writes — dev
    /// builds keep logging into <c>bin\Debug\...\rdm.log</c> — and falls back to
    /// <c>%ProgramData%\RDM\rdm.log</c> for installed builds, where the app sits in Program Files
    /// and a standard user cannot write beside the exe. Before this fallback existed, an installed
    /// build produced no log at all: every write failed and the failure was swallowed.
    /// </summary>
    public static string LogPath { get; } = ResolveLogPath();

    private const string LogFileName = "rdm.log";

    private static string ResolveLogPath()
    {
        string besideExe = Path.Combine(AppContext.BaseDirectory, LogFileName);

        // Probe instead of inspecting the folder's ACLs: with inherited permissions and UAC
        // virtualisation, "may I write here" cannot be answered reliably from metadata.
        try
        {
            using (new FileStream(besideExe, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { }
            return besideExe;
        }
        catch
        {
            // Not writable — installed build.
        }

        try
        {
            string dir = Path.GetDirectoryName(FilePath)!;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, LogFileName);
        }
        catch
        {
            // Nothing writable anywhere; logging stays best-effort and silently does nothing.
            return besideExe;
        }
    }

    public static string EnsureInitialized()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            if (!File.Exists(FilePath) && File.Exists(SeedPath))
                File.Copy(SeedPath, FilePath);
        }
        catch
        {
            // Best-effort: a locked-down machine may block folder creation. The installer
            // is the primary path that provisions %ProgramData%\RDM with write access.
        }

        return FilePath;
    }
}
