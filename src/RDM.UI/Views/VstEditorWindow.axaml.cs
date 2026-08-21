using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using System;
using System.Threading.Tasks;

namespace RDM.UI.Views;

/// <summary>
/// A window dedicated to a single mic VST plugin editor.
/// </summary>
/// <remarks>
/// A VST 2.x plugin does not open a window of its own: BASS_VST passes this window's HWND straight
/// to the plugin's effEditOpen, and the plugin creates its GUI as a child window at (0,0) sized to
/// its own editor rect. Because Avalonia windows use WS_CLIPCHILDREN, that child window blanks out
/// everything Avalonia draws underneath it — which is why the editor cannot share a window with
/// other controls and gets one of these per plugin instead.
/// </remarks>
public partial class VstEditorWindow : Window
{
    private readonly int                           _slotId;
    private readonly IAudioEngine?                 _engine;
    private readonly ILogger<VstEditorWindow>?     _logger;
    private readonly TaskCompletionSource<string?> _embedResult = new();

    private bool _embedded;

    /// Designer / runtime-loader only — an instance built this way hosts no plugin.
    public VstEditorWindow() => InitializeComponent();

    public VstEditorWindow(int slotId, string pluginName, IAudioEngine engine, ILogger<VstEditorWindow> logger)
    {
        InitializeComponent();

        _slotId = slotId;
        _engine = engine;
        _logger = logger;

        Title = pluginName;
    }

    /// <summary>The slot this window hosts — used to keep one editor window per plugin.</summary>
    public int SlotId => _slotId;

    /// <summary>Completes once embedding finished: null on success, otherwise the error message.</summary>
    public Task<string?> EmbedResult => _embedResult.Task;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _ = EmbedAsync();
    }

    private async Task EmbedAsync()
    {
        if (_engine is null) return;   // parameterless ctor — nothing to host

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            _logger?.LogWarning("VstEditorWindow: no platform handle for slot {SlotId}", _slotId);
            _embedResult.TrySetResult("no window handle");
            Close();
            return;
        }

        try
        {
            var (width, height) = await _engine.OpenMicVstEditorAsync(_slotId, hwnd);
            _embedded = true;
            _engine.MicVstEditorResized += OnEditorResized;
            _logger?.LogInformation("VstEditorWindow: embedded slot {SlotId} into hwnd={Hwnd}, editor {W}x{H} px",
                _slotId, hwnd, width, height);

            ApplyEditorSize(width, height);
            _embedResult.TrySetResult(null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "VstEditorWindow: embedding slot {SlotId} failed", _slotId);
            _embedResult.TrySetResult(ex.Message);
            Close();
        }
    }

    /// <summary>Follows a resize the plugin asked for. Raised on a BASS thread.</summary>
    private void OnEditorResized(int slotId, int widthPx, int heightPx)
    {
        if (slotId != _slotId) return;
        Dispatcher.UIThread.Post(() => ApplyEditorSize(widthPx, heightPx));
    }

    /// <summary>
    /// Resizes the window to the editor, which reports pixels while Avalonia sizes in DIPs, and
    /// re-centres it on the screen: the plugin never resizes or moves its parent, so a mismatch
    /// here means a permanently clipped GUI.
    /// </summary>
    private void ApplyEditorSize(int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0)
        {
            _logger?.LogWarning("VstEditorWindow: slot {SlotId} reported editor size {W}x{H} — keeping default window size",
                _slotId, widthPx, heightPx);
            return;
        }

        double scale  = RenderScaling <= 0 ? 1.0 : RenderScaling;
        var    screen = Screens.ScreenFromWindow(this);

        int maxWidthPx  = screen?.WorkingArea.Width  ?? widthPx;
        int maxHeightPx = screen?.WorkingArea.Height ?? heightPx;

        int finalWidthPx  = Math.Min(widthPx,  maxWidthPx);
        int finalHeightPx = Math.Min(heightPx, maxHeightPx);

        if (finalWidthPx < widthPx || finalHeightPx < heightPx)
            _logger?.LogWarning("VstEditorWindow: editor {W}x{H} px does not fit the screen ({MW}x{MH}) — " +
                "the plugin GUI will be clipped", widthPx, heightPx, maxWidthPx, maxHeightPx);

        Width  = finalWidthPx  / scale;
        Height = finalHeightPx / scale;

        if (screen is not null)
        {
            var area = screen.WorkingArea;
            Position = new PixelPoint(
                area.X + Math.Max(0, (area.Width  - finalWidthPx)  / 2),
                area.Y + Math.Max(0, (area.Height - finalHeightPx) / 2));
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Unembed while this window still exists: destroying the parent without telling the plugin
        // leaves its editor flagged as open, and every later attempt fails with BASS_ERROR_ALREADY.
        if (_embedded && _engine is not null)
        {
            _embedded = false;
            _engine.MicVstEditorResized -= OnEditorResized;
            try
            {
                _engine.CloseMicVstEditorAsync(_slotId).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "VstEditorWindow: unembedding slot {SlotId} failed", _slotId);
            }
        }

        base.OnClosing(e);
    }
}
