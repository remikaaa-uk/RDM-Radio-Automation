using System.Runtime.InteropServices;
using Un4seen.Bass;

namespace RDM.Audio;

/// <summary>
/// Redirects Bass.Net P/Invoke calls to the BassLib\ subfolder.
/// Must be called before any BASS API is used (i.e., at process start).
/// </summary>
public static class BassLibInitializer
{
    /// <summary>Directory the resolver was pointed at, or null before registration.</summary>
    public static string? BassLibDir { get; private set; }

    public static void RegisterResolver(string bassLibDir)
    {
        BassLibDir = bassLibDir;

        NativeLibrary.SetDllImportResolver(
            typeof(Bass).Assembly,
            (name, _, _) =>
            {
                var path = Path.Combine(bassLibDir, name + ".dll");
                return File.Exists(path) ? NativeLibrary.Load(path) : IntPtr.Zero;
            });
    }

    /// <summary>
    /// True when an optional native library is present. Callers must check before touching an
    /// optional add-on: a missing DLL makes the P/Invoke throw at the call site rather than
    /// returning an error code, so it has to be caught before the first use, not after.
    /// </summary>
    public static bool HasLibrary(string fileName) =>
        BassLibDir is not null && File.Exists(Path.Combine(BassLibDir, fileName));
}
