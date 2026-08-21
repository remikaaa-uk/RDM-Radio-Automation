using Avalonia;
using RDM.Audio;
using RDM.UI;

internal static class Program
{
    // Stored here so App.axaml.cs can pass them to the generic host builder.
    internal static string[] Args { get; private set; } = [];

    [STAThread]
    public static void Main(string[] args)
    {
        BassLibInitializer.RegisterResolver(
            Path.Combine(AppContext.BaseDirectory, "BassLib"));

        Args = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Classic redirection surface instead of Avalonia's default WinUIComposition.
            // WinUIComposition creates windows with WS_EX_NOREDIRECTIONBITMAP, and DWM then has
            // no surface on which to compose native child windows — a VST editor embedded via
            // BASS_VST (the plugin creates its GUI as a child HWND) stays invisible and the
            // window shows the desktop through it. See VstEditorWindow.
            .With(new Win32PlatformOptions
            {
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            })
            .LogToTrace();
}
