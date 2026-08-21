using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Globalization;

namespace RDM.UI.Controls;

public sealed class AnalogClockControl : Control
{
    public static readonly StyledProperty<string> TimeTextProperty =
        AvaloniaProperty.Register<AnalogClockControl, string>(nameof(TimeText), "00:00:00");

    public string TimeText { get => GetValue(TimeTextProperty); set => SetValue(TimeTextProperty, value); }

    private static readonly IBrush BrushActive = new SolidColorBrush(Color.Parse("#4AB5FF"));

    static AnalogClockControl()
    {
        AffectsRender<AnalogClockControl>(TimeTextProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // Binary-search the largest font size that fits within bounds
        double fontSize = 8;
        for (double size = 8; size <= 200; size += 0.5)
        {
            var probe = new FormattedText(
                TimeText,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas", FontStyle.Normal, FontWeight.Bold),
                size,
                BrushActive);
            if (probe.Width > w * 0.94 || probe.Height > h * 0.90) break;
            fontSize = size;
        }

        var ft = new FormattedText(
            TimeText,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas", FontStyle.Normal, FontWeight.Bold),
            fontSize,
            BrushActive);

        ctx.DrawText(ft, new Point(w / 2.0 - ft.Width / 2.0, h / 2.0 - ft.Height / 2.0));
    }
}
