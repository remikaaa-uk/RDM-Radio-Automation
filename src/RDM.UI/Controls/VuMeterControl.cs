using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;

namespace RDM.UI.Controls;

/// <summary>
/// Stereo LED-segment VU meter.
/// Two vertical bars (L/R), 20 segments each, with green/yellow/red zones
/// and a peak-hold indicator per channel.
/// </summary>
public sealed class VuMeterControl : Control
{
    // ── Avalonia properties ───────────────────────────────────────────────────

    public static readonly StyledProperty<double> ValueLeftProperty =
        AvaloniaProperty.Register<VuMeterControl, double>(nameof(ValueLeft));

    public static readonly StyledProperty<double> ValueRightProperty =
        AvaloniaProperty.Register<VuMeterControl, double>(nameof(ValueRight));

    public double ValueLeft  { get => GetValue(ValueLeftProperty);  set => SetValue(ValueLeftProperty,  value); }
    public double ValueRight { get => GetValue(ValueRightProperty); set => SetValue(ValueRightProperty, value); }

    // ── Segment layout ────────────────────────────────────────────────────────

    private const int    Segments       = 20;
    private const double SegmentGap     = 1.5;
    private const double ChannelGap     = 4.0;
    private const double PeakBarHeight  = 2.0;

    // Zone thresholds (segment index from bottom, 0-based)
    private const int YellowFrom = 14; // segments 0-13 green, 14-17 yellow, 18-19 red
    private const int RedFrom    = 18;

    private static readonly IBrush BrushGreen  = new SolidColorBrush(Color.Parse("#5DDC8A"));
    private static readonly IBrush BrushYellow = new SolidColorBrush(Color.Parse("#FFD700"));
    private static readonly IBrush BrushRed    = new SolidColorBrush(Color.Parse("#FF4040"));
    private static readonly IBrush BrushDimG   = new SolidColorBrush(Color.FromArgb(40,  93, 220, 138));
    private static readonly IBrush BrushDimY   = new SolidColorBrush(Color.FromArgb(40, 255, 215,   0));
    private static readonly IBrush BrushDimR   = new SolidColorBrush(Color.FromArgb(40, 255,  64,  64));
    private static readonly IBrush BrushPeak   = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush BrushBg     = new SolidColorBrush(Color.Parse("#0C0C14"));

    // ── Peak hold state ───────────────────────────────────────────────────────

    private double _peakLeft;
    private double _peakRight;
    private int    _peakHoldLeft;   // remaining ticks before decay
    private int    _peakHoldRight;
    private const int PeakHoldTicks  = 8;   // ~8 × 125 ms = 1 s hold
    private const double PeakDecay   = 3.0; // units per tick after hold expires

    private DispatcherTimer? _peakTimer;

    // ── Overrides ─────────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _peakTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(125) };
        _peakTimer.Tick += OnPeakTick;
        _peakTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _peakTimer?.Stop();
        _peakTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    static VuMeterControl()
    {
        ValueLeftProperty.Changed.AddClassHandler<VuMeterControl>((c, _) => c.UpdatePeaks());
        ValueRightProperty.Changed.AddClassHandler<VuMeterControl>((c, _) => c.UpdatePeaks());
        AffectsRender<VuMeterControl>(ValueLeftProperty, ValueRightProperty);
    }

    private void UpdatePeaks()
    {
        if (ValueLeft > _peakLeft)  { _peakLeft  = ValueLeft;  _peakHoldLeft  = PeakHoldTicks; }
        if (ValueRight > _peakRight){ _peakRight = ValueRight; _peakHoldRight = PeakHoldTicks; }
    }

    private void OnPeakTick(object? sender, EventArgs e)
    {
        bool changed = false;

        if (_peakHoldLeft > 0)
            _peakHoldLeft--;
        else if (_peakLeft > 0)
        {
            _peakLeft = Math.Max(0, _peakLeft - PeakDecay);
            changed = true;
        }

        if (_peakHoldRight > 0)
            _peakHoldRight--;
        else if (_peakRight > 0)
        {
            _peakRight = Math.Max(0, _peakRight - PeakDecay);
            changed = true;
        }

        if (changed) InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;

        ctx.DrawRectangle(BrushBg, null, new Rect(0, 0, w, h));

        var chW = (w - ChannelGap) / 2.0;
        DrawChannel(ctx, ValueLeft,  _peakLeft,  0,          chW, h);
        DrawChannel(ctx, ValueRight, _peakRight, chW + ChannelGap, chW, h);
    }

    private static void DrawChannel(DrawingContext ctx, double value, double peak,
                                     double x, double chW, double h)
    {
        var segH   = (h - Segments * SegmentGap) / Segments;
        var litSeg = (int)Math.Round(value / 100.0 * Segments);

        for (int s = 0; s < Segments; s++)
        {
            // s=0 is bottom, s=Segments-1 is top
            var y = h - (s + 1) * (segH + SegmentGap) + SegmentGap;
            var r = new Rect(x, y, chW, segH);

            bool lit    = s < litSeg;
            IBrush fill = s >= RedFrom
                ? (lit ? BrushRed    : BrushDimR)
                : s >= YellowFrom
                    ? (lit ? BrushYellow : BrushDimY)
                    : (lit ? BrushGreen  : BrushDimG);

            ctx.DrawRectangle(fill, null, r);
        }

        // Peak-hold bar
        if (peak > 0)
        {
            var peakSeg = (int)Math.Round(peak / 100.0 * Segments) - 1;
            if (peakSeg >= 0 && peakSeg < Segments)
            {
                var py = h - (peakSeg + 1) * (segH + SegmentGap) + SegmentGap;
                ctx.DrawRectangle(BrushPeak, null, new Rect(x, py, chW, PeakBarHeight));
            }
        }
    }
}
