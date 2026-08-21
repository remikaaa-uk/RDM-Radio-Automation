using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Linq;

namespace RDM.UI.Controls;

/// <summary>
/// Scrolls a list while the user drags a row near its top or bottom edge.
/// </summary>
/// <remarks>
/// Without this the reachable drop range is exactly what happens to be on screen: the list never
/// scrolls under a held pointer, and a virtualizing panel only realizes containers for visible
/// rows, so drop-indicator hit-testing cannot address anything else. Moving an item from
/// position 30 to the top then took several release/scroll/re-grab rounds.
///
/// Shared by <see cref="Views.PlaylistView"/> and <see cref="Views.PlaylistBuilderWindow"/>,
/// which implement the same grab-and-drag reorder pattern over their own lists.
/// </remarks>
internal sealed class DragAutoScroller
{
    /// Distance from the top/bottom edge, in px, within which scrolling is armed.
    private const double EdgeZone = 28.0;

    /// Pixels per tick at the very edge; ramped down towards the inner boundary of the zone.
    private const double MaxScrollStep = 24.0;

    private readonly Control       _list;
    private readonly Action<Point> _onScrolled;

    private DispatcherTimer? _timer;
    private ScrollViewer?    _scrollViewer;
    private Point?           _lastPointerInList;

    /// <param name="list">The list control to scroll (its templated ScrollViewer is used).</param>
    /// <param name="onScrolled">
    /// Called after each scroll step with the (unchanged) pointer position in <paramref name="list"/>
    /// coordinates. Content moved under a stationary pointer, so the caller must recompute its drop
    /// indicator — no pointer event fires to prompt that.
    /// </param>
    public DragAutoScroller(Control list, Action<Point> onScrolled)
    {
        _list       = list;
        _onScrolled = onScrolled;
    }

    /// <summary>
    /// Reports the current pointer position (in list coordinates) and arms or disarms scrolling
    /// depending on whether it sits in an edge zone. Call from the drag-move handler.
    /// </summary>
    public void Track(Point listPos)
    {
        _lastPointerInList = listPos;

        if (ComputeStep(listPos) == 0) Stop();
        else                           Start();
    }

    /// <summary>Stops scrolling. Call when the drag ends, is cancelled, or leaves the list.</summary>
    public void Stop()
    {
        _timer?.Stop();
        _lastPointerInList = null;
    }

    /// Signed pixels to scroll per tick: negative scrolls up, 0 means "not in an edge zone".
    /// Ramped by how deep into the zone the pointer is, so a small overshoot creeps (making a
    /// specific row reachable) while pushing hard against the edge travels fast. Dragging past
    /// the edge entirely counts as full speed.
    private double ComputeStep(Point listPos)
    {
        var height = _list.Bounds.Height;
        if (height <= 0) return 0;

        if (listPos.Y < EdgeZone)
            return -MaxScrollStep * Math.Clamp((EdgeZone - listPos.Y) / EdgeZone, 0, 1);

        if (listPos.Y > height - EdgeZone)
            return MaxScrollStep * Math.Clamp((listPos.Y - (height - EdgeZone)) / EdgeZone, 0, 1);

        return 0;
    }

    private void Start()
    {
        if (_timer is not null) { _timer.Start(); return; }

        // A timer rather than scrolling straight from the move handler: the pointer can rest
        // motionless at the edge, and no further move events would arrive to keep it going.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_lastPointerInList is not { } listPos) { Stop(); return; }

        var scroller = ResolveScrollViewer();
        if (scroller is null) { Stop(); return; }

        var step = ComputeStep(listPos);
        if (step == 0) { Stop(); return; }

        var maxOffset = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var newY      = Math.Clamp(scroller.Offset.Y + step, 0, maxOffset);

        // Already at the end: keep the timer alive so scrolling resumes if the drag pulls the
        // list the other way without leaving the zone.
        if (Math.Abs(newY - scroller.Offset.Y) < 0.01) return;

        scroller.Offset = scroller.Offset.WithY(newY);
        _onScrolled(listPos);
    }

    private ScrollViewer? ResolveScrollViewer()
    {
        // Resolved lazily — the template is not applied when the owning view is constructed.
        // Cached once attached: the templated ScrollViewer lives as long as the list does.
        if (_scrollViewer is null || _scrollViewer.GetVisualRoot() is null)
            _scrollViewer = _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        return _scrollViewer;
    }
}
