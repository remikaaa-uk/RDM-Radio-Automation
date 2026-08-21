using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Collections.Generic;

namespace RDM.UI.Controls;

/// Represents a single cue marker drawn on the waveform.
public readonly record struct CueMarker(uint PositionMs, Color Color, string Label);

/// <summary>
/// Renders a float32 peak waveform from a .wvf file (1000 samples, little-endian).
/// Shows colored intro/vocal/outro zones and cue point markers.
/// Fires PositionClicked with a normalized 0.0–1.0 position on left-click.
/// Mouse wheel zooms in/out centered on the cursor position.
/// When the Markers list is set, it replaces the three hardcoded INTRO/OUTRO/HOOK lines.
/// </summary>
public sealed class WaveformControl : Control
{
    // ── Avalonia Styled Properties ────────────────────────────────────────────

    public static readonly StyledProperty<float[]?> PeaksProperty =
        AvaloniaProperty.Register<WaveformControl, float[]?>(nameof(Peaks));

    public static readonly StyledProperty<uint> DurationMsProperty =
        AvaloniaProperty.Register<WaveformControl, uint>(nameof(DurationMs), defaultValue: 1u);

    public static readonly StyledProperty<uint?> IntroCueMsProperty =
        AvaloniaProperty.Register<WaveformControl, uint?>(nameof(IntroCueMs));

    public static readonly StyledProperty<uint?> OutroCueMsProperty =
        AvaloniaProperty.Register<WaveformControl, uint?>(nameof(OutroCueMs));

    public static readonly StyledProperty<uint?> HookCueMsProperty =
        AvaloniaProperty.Register<WaveformControl, uint?>(nameof(HookCueMs));

    public static readonly StyledProperty<uint> PositionMsProperty =
        AvaloniaProperty.Register<WaveformControl, uint>(nameof(PositionMs));

    public static readonly StyledProperty<IReadOnlyList<CueMarker>?> MarkersProperty =
        AvaloniaProperty.Register<WaveformControl, IReadOnlyList<CueMarker>?>(nameof(Markers));

    /// When set, the played portion of the waveform is drawn with this brush
    /// (e.g. the AUX deck accent colour) instead of the zone-based colouring.
    public static readonly StyledProperty<IBrush?> PlayedBrushProperty =
        AvaloniaProperty.Register<WaveformControl, IBrush?>(nameof(PlayedBrush));

    // ── CLR accessors ─────────────────────────────────────────────────────────

    public float[]? Peaks      { get => GetValue(PeaksProperty);     set => SetValue(PeaksProperty, value); }
    public uint     DurationMs { get => GetValue(DurationMsProperty);set => SetValue(DurationMsProperty, value); }
    public uint?    IntroCueMs { get => GetValue(IntroCueMsProperty);set => SetValue(IntroCueMsProperty, value); }
    public uint?    OutroCueMs { get => GetValue(OutroCueMsProperty);set => SetValue(OutroCueMsProperty, value); }
    public uint?    HookCueMs  { get => GetValue(HookCueMsProperty); set => SetValue(HookCueMsProperty, value); }
    public uint     PositionMs { get => GetValue(PositionMsProperty);set => SetValue(PositionMsProperty, value); }
    public IReadOnlyList<CueMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public IBrush? PlayedBrush { get => GetValue(PlayedBrushProperty); set => SetValue(PlayedBrushProperty, value); }

    // ── Zoom state (internal, not bindable) ───────────────────────────────────

    private double _viewStart = 0.0;
    private double _viewEnd   = 1.0;

    // ── Event: user clicked at a normalized position ──────────────────────────

    public event Action<double>? PositionClicked;

    // ── Invalidate on property change ─────────────────────────────────────────

    static WaveformControl()
    {
        AffectsRender<WaveformControl>(
            PeaksProperty, DurationMsProperty,
            IntroCueMsProperty, OutroCueMsProperty, HookCueMsProperty,
            PositionMsProperty, MarkersProperty, PlayedBrushProperty);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        var viewStart = _viewStart;
        var viewEnd   = _viewEnd;
        var viewRange = Math.Max(viewEnd - viewStart, 1e-9);
        var totalMs   = (double)(DurationMs > 0 ? DurationMs : 1u);

        // Converts a global 0-1 normalized position to screen X.
        double ToScreen(double norm) => (norm - viewStart) / viewRange * w;

        // Background
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#11111C")), null, new Rect(0, 0, w, h));

        // Zone background colors based on Intro/Outro cue positions
        double introNorm = IntroCueMs.HasValue ? IntroCueMs.Value / totalMs : -1;
        double outroNorm = OutroCueMs.HasValue ? OutroCueMs.Value / totalMs : -1;
        double introPx   = introNorm >= 0 ? Math.Clamp(ToScreen(introNorm), 0, w) : 0;
        double outroPx   = outroNorm >= 0 ? Math.Clamp(ToScreen(outroNorm), 0, w) : w;

        if (introPx > 0)
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#151525")), null, new Rect(0, 0, introPx, h));

        if (introNorm >= 0 || outroNorm >= 0)
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0D1A0D")), null,
                new Rect(introPx, 0, Math.Max(0, outroPx - introPx), h));

        if (outroNorm >= 0 && outroPx < w)
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#1E0F0F")), null,
                new Rect(outroPx, 0, w - outroPx, h));

        // Waveform — thin vertical min/max lines (RadioDJ/DAW style)
        var peaks = Peaks;
        if (peaks is { Length: > 0 })
        {
            // New format: 2000 floats = 1000 min/max pairs [min0,max0,min1,max1,...]
            // Old format: 1000 floats = peak absolute values (rendered symmetrically)
            bool isMinMax   = peaks.Length == 2000;
            int  pointCount = isMinMax ? 1000 : peaks.Length;

            var unplayedPen = new Pen(GetBrush("WaveformUnplayedBrush", "#FF333344"), 1.0);
            var introPen    = new Pen(GetBrush("WaveformIntroZoneBrush", "#FFAAAAAA"), 1.0);
            var vocalPen    = new Pen(GetBrush("WaveformVocalZoneBrush", "#FF5DDC8A"), 1.0);
            var outroPen    = new Pen(GetBrush("WaveformOutroZoneBrush", "#FFFF8C42"), 1.0);

            // When PlayedBrush is set, the played portion uses a single accent colour
            // (AUX deck style) instead of the intro/vocal/outro zone colouring.
            Pen? playedPen = PlayedBrush is { } pb ? new Pen(pb, 1.0) : null;

            double viewPeakStart  = viewStart * pointCount;
            double pointsPerPixel = Math.Max((viewEnd - viewStart) * pointCount / w, 1e-9);
            double midY           = h / 2.0;
            double halfH          = h / 2.0 * 0.9;
            double posNorm        = PositionMs / totalMs;
            int    pixelWidth     = (int)Math.Ceiling(w);

            for (int px = 0; px < pixelWidth; px++)
            {
                double fStart = viewPeakStart + px * pointsPerPixel;
                double fEnd   = fStart + pointsPerPixel;

                int i0 = Math.Clamp((int)Math.Floor(fStart), 0, pointCount - 1);
                int i1 = Math.Clamp((int)Math.Floor(fEnd),   0, pointCount - 1);

                float minAmp = 0f;
                float maxAmp = 0f;

                if (isMinMax)
                {
                    for (int i = i0; i <= i1; i++)
                    {
                        if (peaks[i * 2]     < minAmp) minAmp = peaks[i * 2];
                        if (peaks[i * 2 + 1] > maxAmp) maxAmp = peaks[i * 2 + 1];
                    }
                }
                else
                {
                    for (int i = i0; i <= i1; i++)
                        if (peaks[i] > maxAmp) maxAmp = peaks[i];
                    minAmp = -maxAmp;
                }

                double y1 = midY - maxAmp * halfH;
                double y2 = midY - minAmp * halfH; // minAmp ≤ 0 → y2 ≥ midY
                if (y2 - y1 < 1.0) { y1 = midY - 0.5; y2 = midY + 0.5; }

                double globalNorm = fStart / pointCount;
                Pen pen;
                if (globalNorm >= posNorm)
                    pen = unplayedPen;
                else if (playedPen is not null)
                    pen = playedPen;
                else if (introNorm >= 0 && globalNorm < introNorm)
                    pen = introPen;
                else if (outroNorm >= 0 && globalNorm >= outroNorm)
                    pen = outroPen;
                else
                    pen = vocalPen;

                ctx.DrawLine(pen, new Point(px + 0.5, y1), new Point(px + 0.5, y2));
            }
        }

        // Marker lines — either generic Markers list or legacy INTRO/OUTRO/HOOK
        var customMarkers = Markers;
        if (customMarkers is { Count: > 0 })
        {
            foreach (var m in customMarkers)
            {
                var norm = m.PositionMs / totalMs;
                if (norm < viewStart || norm > viewEnd) continue;
                DrawCueMarker(ctx, m, ToScreen(norm), h);
            }
        }
        else
        {
            DrawMarker(ctx, IntroCueMs, totalMs, viewStart, viewEnd, w, h,
                GetBrush("WaveformMarkerIntroBrush", "#4AB5FF"), "INTRO");
            DrawMarker(ctx, OutroCueMs, totalMs, viewStart, viewEnd, w, h,
                GetBrush("WaveformMarkerOutroBrush", "#FF8C42"), "OUTRO");
            DrawMarker(ctx, HookCueMs,  totalMs, viewStart, viewEnd, w, h,
                GetBrush("WaveformMarkerHookBrush",  "#FFFFFF"), "HOOK");
        }

        // Zoom indicator: thin strip at bottom showing current view range
        if (viewStart > 0.0 || viewEnd < 1.0)
        {
            var stripBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
            ctx.DrawRectangle(stripBrush, null, new Rect(viewStart * w, h - 3, (viewEnd - viewStart) * w, 3));
        }
    }

    // ── Pointer events ────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var localNorm  = Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1);
            var globalNorm = _viewStart + localNorm * (_viewEnd - _viewStart);
            PositionClicked?.Invoke(Math.Clamp(globalNorm, 0, 1));
            e.Handled = true;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        var delta = e.Delta.Y;
        if (delta == 0 || Bounds.Width <= 0) return;

        var range  = _viewEnd - _viewStart;
        // Zoom factor: scroll up = zoom in (smaller range), scroll down = zoom out
        var factor = delta > 0 ? 0.65 : 1.55;
        var newRange = Math.Clamp(range * factor, 0.01, 1.0);

        // Keep cursor position fixed in global coordinates
        var localNorm  = Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1);
        var cursorGlobal = _viewStart + localNorm * range;

        var newStart = cursorGlobal - localNorm * newRange;
        newStart = Math.Clamp(newStart, 0.0, 1.0 - newRange);
        _viewStart = newStart;
        _viewEnd   = newStart + newRange;

        InvalidateVisual();
        e.Handled = true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void DrawCueMarker(DrawingContext ctx, CueMarker marker, double x, double h)
    {
        var brush = new SolidColorBrush(marker.Color);
        ctx.DrawLine(new Pen(brush, 2.0), new Point(x, 0), new Point(x, h));

        var ft = new FormattedText(
            marker.Label,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            9,
            brush);
        ctx.DrawText(ft, new Point(x + 2, 2));
    }

    private static void DrawMarker(
        DrawingContext ctx,
        uint? cueMs, double totalMs,
        double viewStart, double viewEnd,
        double w, double h,
        IBrush brush, string label)
    {
        if (!cueMs.HasValue) return;
        var norm = cueMs.Value / totalMs;
        if (norm < viewStart || norm > viewEnd) return;
        var x = (norm - viewStart) / (viewEnd - viewStart) * w;
        ctx.DrawLine(new Pen(brush, 2.0), new Point(x, 0), new Point(x, h));

        if (h >= 50)
        {
            var ft = new FormattedText(
                label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"),
                9,
                brush);
            ctx.DrawText(ft, new Point(x + 2, 2));
        }
    }

    private static IBrush GetBrush(string key, string fallbackHex)
    {
        if (Application.Current?.Resources.TryGetResource(key, null, out var res) == true
            && res is IBrush b)
            return b;
        return new SolidColorBrush(Color.Parse(fallbackHex));
    }
}
