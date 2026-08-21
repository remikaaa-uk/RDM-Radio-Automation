using Avalonia;
using Avalonia.Controls;
using System;

namespace RDM.UI.Controls;

/// <summary>
/// Panel that arranges children in a uniform grid with a fixed number of columns,
/// always filling the full available area regardless of child desired sizes.
/// Unlike UniformGrid, this panel forces equal cell sizes based on the arranged
/// (finite) size rather than the children's desired sizes.
/// </summary>
public sealed class CartGridPanel : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<CartGridPanel, int>(nameof(Columns), defaultValue: 4);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count == 0) return new Size(0, 0);

        int cols = Math.Max(1, Columns);
        int rows = (int)Math.Ceiling((double)Children.Count / cols);

        double cellW = double.IsInfinity(availableSize.Width)  ? 200 : availableSize.Width  / cols;
        double cellH = double.IsInfinity(availableSize.Height) ? 100 : availableSize.Height / rows;

        var childSize = new Size(cellW, cellH);
        foreach (var child in Children)
            child.Measure(childSize);

        // Return the full available size so the panel claims all the space.
        // When availableSize is infinite (unconstrained axis), fall back to content size.
        return new Size(
            double.IsInfinity(availableSize.Width)  ? cellW * cols : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? cellH * rows : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;

        int cols = Math.Max(1, Columns);
        int rows = (int)Math.Ceiling((double)Children.Count / cols);

        double cellW = finalSize.Width  / cols;
        double cellH = finalSize.Height / rows;

        for (int i = 0; i < Children.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            Children[i].Arrange(new Rect(col * cellW, row * cellH, cellW, cellH));
        }

        return finalSize;
    }
}
