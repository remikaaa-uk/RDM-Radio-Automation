using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RDM.Core.Models;
using RDM.UI.Localization;
using RDM.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace RDM.UI.Controls;

/// Renders an arbitrary number of clips, each in its own lane on a shared time axis.
/// Each clip is drawn as a framed box spanning its trimmed extent; clips overlap in
/// crossfade zones. Drag to pan, scroll wheel to zoom, click to move the cursor.
public sealed class SegueTimelineControl : Control
{
    // ── Styled Properties ─────────────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<SegueClipViewModel>?> ClipsProperty =
        AvaloniaProperty.Register<SegueTimelineControl, IReadOnlyList<SegueClipViewModel>?>(nameof(Clips));

    public static readonly StyledProperty<double> FocusCenterMsProperty =
        AvaloniaProperty.Register<SegueTimelineControl, double>(nameof(FocusCenterMs), double.NaN);

    /// Absolute mix position (ms) of the PFL playhead. NaN hides it.
    public static readonly StyledProperty<double> PlayheadMsProperty =
        AvaloniaProperty.Register<SegueTimelineControl, double>(nameof(PlayheadMs), double.NaN);

    /// When true the control is in VT insertion mode: hovering highlights a lane and
    /// clicking fires <see cref="VtInsertRequested"/> instead of seeking the playhead.
    public static readonly StyledProperty<bool> IsVtModeProperty =
        AvaloniaProperty.Register<SegueTimelineControl, bool>(nameof(IsVtMode));

    // ── CLR ───────────────────────────────────────────────────────────────────

    public IReadOnlyList<SegueClipViewModel>? Clips
    {
        get => GetValue(ClipsProperty);
        set => SetValue(ClipsProperty, value);
    }

    public double FocusCenterMs
    {
        get => GetValue(FocusCenterMsProperty);
        set => SetValue(FocusCenterMsProperty, value);
    }

    public double PlayheadMs
    {
        get => GetValue(PlayheadMsProperty);
        set => SetValue(PlayheadMsProperty, value);
    }

    public bool IsVtMode
    {
        get => GetValue(IsVtModeProperty);
        set => SetValue(IsVtModeProperty, value);
    }

    public event Action<double>? CursorClicked;

    /// Fired when the user clicks a lane while <see cref="IsVtMode"/> is true.
    /// Argument: index of the clip after which the voice track should be inserted.
    public event Action<int>? VtInsertRequested;

    // ── View state ────────────────────────────────────────────────────────────

    private double _viewCenterMs = double.NaN;
    private double _viewRangeMs  = 22_000;
    private bool   _needsFit;

    // Scroll range (set each render, used during scrollbar drag)
    private double _scrollMin;
    private double _scrollMax;

    // Pan state
    private Point? _dragStart;
    private double _dragStartCenter;

    // Clip-drag / scrollbar-drag state
    private enum DragMode { None, Pan, ClipDrag, ScrollBar, FadeHandle, EnvelopeNode, CueMarker }
    private DragMode _dragMode;
    private int      _dragClipIndex;
    private double   _dragLastX;

    // Fade-out handle hit zones, rebuilt every render: (clipIndex, grab rect).
    private readonly List<(int Clip, Rect Rect)> _fadeHandles = new();
    private int _dragFadeClip;

    // Envelope node hit zones, rebuilt every render: (clipIndex, nodeIndex, grab rect).
    private readonly List<(int Clip, int Node, Rect Rect)> _envNodeHandles = new();
    private int _dragEnvClip;
    private int _dragEnvNode;

    // Cue marker hit zones, rebuilt every render: (clipIndex, markerKey, grab rect).
    private readonly List<(int Clip, string Key, Rect Rect)> _cueMarkerHandles = new();
    private int    _dragCueClip;
    private string _dragCueKey = "";

    /// Lane index currently hovered during VT insertion mode (-1 = none).
    private int _vtHoverLane = -1;

    /// Fired while dragging a fade-out handle. Arguments: clip index, the new
    /// fade-start position as an absolute mix time (ms). The view model converts
    /// it back to a source-relative FadeOut marker.
    public event Action<int, double>? FadeHandleMoved;

    /// Fired when a fade-handle drag ends, so the new value can be persisted.
    public event Action<int>? FadeHandleCommitted;

    // ── Cue marker events ──────────────────────────────────────────────────────────

    /// Drag started on a cue marker line. Args: clipIndex, markerKey (e.g. "StartNext").
    public event Action<int, string>? CueMarkerDragStarted;
    /// Fired on every mouse-move while dragging a cue marker. Args: clipIndex, markerKey, timeSec.
    public event Action<int, string, double>? CueMarkerMoved;
    /// Fired when a cue marker drag ends.
    public event Action<int, string>? CueMarkerCommitted;
    /// Right-click on a cue marker line: remove it. Args: clipIndex, markerKey.
    public event Action<int, string>? CueMarkerRemoved;
    /// Shift+left-click on a clip with no StartNext: add StartNext at that position.
    /// Args: clipIndex, markerKey ("StartNext"), timeSec.
    public event Action<int, string, double>? CueMarkerAdded;

    // ── Envelope node events ──────────────────────────────────────────────────────

    /// Ctrl+left-click on a clip: add an envelope node. Args: clipIndex, timeSec, volume.
    public event Action<int, double, double>? EnvelopeNodeAdded;
    /// Drag started on an envelope node (before the first move). Used to push undo.
    public event Action<int>? EnvelopeNodeDragStarted;
    /// Fired on every mouse-move while dragging a node. Args: clipIndex, nodeIndex, timeSec, volume.
    public event Action<int, int, double, double>? EnvelopeNodeMoved;
    /// Right-click on an envelope node: remove it. Args: clipIndex, nodeIndex.
    public event Action<int, int>? EnvelopeNodeRemoved;
    /// Fired when the node drag ends.
    public event Action? EnvelopeNodeCommitted;

    /// Raised when the view is zoomed in far enough that the precomputed peaks are
    /// too sparse, asking the host to decode a sample-accurate slice. Arguments:
    /// clip index, source-window start/end (ms), desired column count.
    public event Action<int, double, double, int>? DetailWaveformRequested;

    /// Last detail window requested per clip, to avoid re-firing identical requests
    /// every render. Cleared implicitly by zooming to a different window.
    private readonly Dictionary<int, (double Start, double End, int Cols)> _detailReq = new();

    private INotifyCollectionChanged? _subscribedCollection;
    private readonly List<SegueClipViewModel> _subscribedItems = new();

    /// Fired when the user starts dragging a clip (before the first move).
    /// Argument: clip index. Subscribe once to push an undo snapshot.
    public event Action<int>? ClipDragStarted;

    /// Fired on every mouse move during a clip drag.
    /// Arguments: clip index, delta in milliseconds (positive = right / later start).
    public event Action<int, double>? ClipDragged;

    // ── Static ────────────────────────────────────────────────────────────────

    static SegueTimelineControl()
    {
        ClipsProperty.Changed.AddClassHandler<SegueTimelineControl>((c, e) => c.OnClipsChanged(e));

        FocusCenterMsProperty.Changed.AddClassHandler<SegueTimelineControl>((c, _) =>
        {
            if (!double.IsNaN(c.FocusCenterMs))
            {
                c._viewCenterMs = c.FocusCenterMs;
                c.InvalidateVisual();
            }
        });

        PlayheadMsProperty.Changed.AddClassHandler<SegueTimelineControl>((c, _) =>
        {
            // Auto-follow: keep the moving playhead on screen during playback,
            // but never fight the user while the playhead is already visible.
            if (!double.IsNaN(c.PlayheadMs) && !double.IsNaN(c._viewCenterMs))
            {
                var vs = c._viewCenterMs - c._viewRangeMs / 2;
                var ve = c._viewCenterMs + c._viewRangeMs / 2;
                if (c.PlayheadMs < vs || c.PlayheadMs > ve)
                    c._viewCenterMs = c.PlayheadMs;
            }
            c.InvalidateVisual();
        });

        IsVtModeProperty.Changed.AddClassHandler<SegueTimelineControl>((c, e) =>
        {
            c._vtHoverLane = -1;
            if (!e.GetNewValue<bool>())
                c.Cursor = new Cursor(StandardCursorType.Arrow);
            c.InvalidateVisual();
        });
    }

    public SegueTimelineControl()
    {
        ContextRequested += HandleContextRequested;
    }

    // ── Subscriptions ───────────────────────────────────────────────────────────

    private void OnClipsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_subscribedCollection is not null)
            _subscribedCollection.CollectionChanged -= OnCollectionChanged;
        UnsubscribeItems();

        _subscribedCollection = e.NewValue as INotifyCollectionChanged;
        if (_subscribedCollection is not null)
            _subscribedCollection.CollectionChanged += OnCollectionChanged;

        SubscribeItems();
        _needsFit = true;
        InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SubscribeItems();
        _needsFit = true;
        InvalidateVisual();
    }

    private void SubscribeItems()
    {
        UnsubscribeItems();
        if (Clips is null) return;
        foreach (var clip in Clips)
        {
            clip.PropertyChanged += OnClipPropertyChanged;
            _subscribedItems.Add(clip);
        }
    }

    private void UnsubscribeItems()
    {
        foreach (var clip in _subscribedItems)
            clip.PropertyChanged -= OnClipPropertyChanged;
        _subscribedItems.Clear();
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => InvalidateVisual();

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        // ── Background ─────────────────────────────────────────────────────────
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0B0B18")), null, new Rect(0, 0, w, h));

        var clips = Clips;
        const double rulerH = 28;
        const double sbH    = 14;   // scrollbar strip at very bottom
        var sbY    = h - sbH;
        var rulerY = sbY - rulerH;

        if (clips is null || clips.Count == 0)
        {
            DrawRuler(ctx, _viewCenterMs - _viewRangeMs / 2, _viewCenterMs + _viewRangeMs / 2, w, rulerY, rulerH);
            _scrollMin = 0; _scrollMax = 1;
            DrawScrollBar(ctx, w, sbY, sbH);
            return;
        }

        // ── Fit view to all clips on first layout ──────────────────────────────
        var spanStart = clips.Min(c => c.AbsoluteStartMs);
        var spanEnd   = clips.Max(c => c.AbsoluteStartMs + c.EffectiveDurationMs);
        // Pad span so there is always visible space around the clips
        var spanPad   = Math.Max(1000, (spanEnd - spanStart) * 0.05);
        var scrollMin = spanStart - spanPad;
        var scrollMax = spanEnd   + spanPad;

        if (_needsFit)
        {
            var span = Math.Max(1000, spanEnd - spanStart);
            _viewRangeMs = Math.Clamp(span * 1.1, 4_000, 600_000);
            if (double.IsNaN(_viewCenterMs))
                _viewCenterMs = (spanStart + spanEnd) / 2;
            _needsFit = false;
        }
        if (double.IsNaN(_viewCenterMs))
            _viewCenterMs = (spanStart + spanEnd) / 2;

        var vs = _viewCenterMs - _viewRangeMs / 2;
        var ve = _viewCenterMs + _viewRangeMs / 2;
        double ToX(double ms) => (ms - vs) / (ve - vs) * w;

        // ── Lanes ──────────────────────────────────────────────────────────────
        var areaH = Math.Max(1, rulerY);
        var laneH = areaH / clips.Count;

        // ── Crossfade zones (overlap of adjacent clips) ────────────────────────
        for (int i = 0; i < clips.Count - 1; i++)
        {
            var aEnd   = clips[i].AbsoluteStartMs + clips[i].EffectiveDurationMs;
            var bStart = clips[i + 1].AbsoluteStartMs;
            if (aEnd <= bStart) continue;
            var x1 = Math.Clamp(ToX(bStart), 0, w);
            var x2 = Math.Clamp(ToX(aEnd),   0, w);
            if (x2 <= x1) continue;
            var y1 = i * laneH;
            var y2 = (i + 2) * laneH;
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, 100, 100, 220)), null,
                new Rect(x1, y1, x2 - x1, Math.Min(y2, rulerY) - y1));
        }

        // ── Clips ──────────────────────────────────────────────────────────────
        _fadeHandles.Clear();
        _envNodeHandles.Clear();
        _cueMarkerHandles.Clear();
        for (int i = 0; i < clips.Count; i++)
            DrawClip(ctx, i, clips[i], vs, ve, w, i * laneH, laneH, ToX);

        // ── Transition lines (between adjacent clips) ──────────────────────────
        for (int i = 0; i < clips.Count - 1; i++)
        {
            var aEnd = clips[i].AbsoluteStartMs + clips[i].EffectiveDurationMs;
            var tx   = ToX(aEnd);
            if (tx < 0 || tx > w) continue;
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5),
                new Point(tx, 0), new Point(tx, rulerY));
        }

        // ── Playhead (full-height vertical line across every lane) ─────────────
        DrawPlayhead(ctx, vs, ve, w, rulerY, ToX);

        // ── VT insertion indicator ─────────────────────────────────────────────
        if (IsVtMode && _vtHoverLane >= 0 && _vtHoverLane < clips.Count)
            DrawVtInsertLine(ctx, _vtHoverLane, w, areaH, clips.Count);

        // ── Ruler + scrollbar ─────────────────────────────────────────────────
        DrawRuler(ctx, vs, ve, w, rulerY, rulerH);
        _scrollMin = scrollMin;
        _scrollMax = scrollMax;
        DrawScrollBar(ctx, w, sbY, sbH);
    }

    private void DrawPlayhead(DrawingContext ctx, double vs, double ve, double w,
        double rulerY, Func<double, double> toX)
    {
        var ms = PlayheadMs;
        if (double.IsNaN(ms) || ms < vs || ms > ve) return;
        var x = toX(ms);
        if (x < 0 || x > w) return;

        // Soft glow behind a crisp white line.
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null,
            new Rect(x - 1.5, 0, 3, rulerY));
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 255, 255)), 1.5),
            new Point(x, 0), new Point(x, rulerY));
    }

    // ── Clip rendering ──────────────────────────────────────────────────────────

    private void DrawClip(DrawingContext ctx, int index, SegueClipViewModel clip,
        double vs, double ve, double w, double laneY, double laneH,
        Func<double, double> toX)
    {
        const double padV = 3;
        var startMs = clip.AbsoluteStartMs;
        var endMs   = clip.AbsoluteStartMs + clip.EffectiveDurationMs;

        var x1 = toX(startMs);
        var x2 = toX(endMs);
        if (x2 < 0 || x1 > w) return; // off-screen

        var boxX = Math.Max(x1, 0);
        var boxW = Math.Min(x2, w) - boxX;
        if (boxW <= 0) return;

        var boxY = laneY + padV;
        var boxH = Math.Max(8, laneH - padV * 2);

        if (clip.IsRecording)
        {
            DrawRecordingClip(ctx, clip, boxX, boxY, boxW, boxH, toX);
            return;
        }

        // Box background + border
        ctx.DrawRectangle(
            new SolidColorBrush(Color.Parse("#1A1A35")),
            new Pen(new SolidColorBrush(Color.FromArgb(220, 90, 105, 200)), 1),
            new Rect(boxX, boxY, boxW, boxH), 2, 2);

        DrawClipWaveform(ctx, index, clip, vs, ve, w, boxX, boxY, boxW, boxH, x1, x2);

        // Markers (source-relative) — every cue point that is set, with a small label.
        if (clip.Markers is { } m)
        {
            foreach (var e in CueMarkerPalette.Enumerate(m))
                DrawClipMarker(ctx, index, e, clip, vs, ve, boxX, boxX + boxW, boxY, boxH, toX);
        }

        // Fade-out envelope + draggable handle (only when a FadeOut marker is set).
        DrawFadeOut(ctx, index, clip, boxX, boxX + boxW, boxY, boxH, toX);

        // Volume envelope line + interactive nodes.
        DrawEnvelope(ctx, index, clip, boxX, boxY, boxW, boxH, toX);

        // Labels: name top-left, trim start bottom-left, trim end bottom-right
        DrawNameLabel(ctx, clip.Label, boxX + 4, boxY + 2);
        DrawTimeLabel(ctx, FormatTime(clip.TrimStartMs),     boxX + 4,         boxY + boxH - 13, false);
        DrawTimeLabel(ctx, FormatTime(clip.EffectiveEndMs),  boxX + boxW - 4,  boxY + boxH - 13, true);
    }

    /// Draws the fade-out as a red line descending from full level at the fade start
    /// to silence at the fade end, with a square grab handle at the top of the start.
    private void DrawFadeOut(DrawingContext ctx, int index, SegueClipViewModel clip,
        double boxLeft, double boxRight, double boxY, double boxH, Func<double, double> toX)
    {
        if (clip.Markers?.FadeOut is not double foSec) return;

        var foSrcMs = foSec * 1000;
        if (foSrcMs < clip.TrimStartMs || foSrcMs > clip.EffectiveEndMs) return;

        // Fade ends at FadeEnd, else End, else the clip's audible end.
        double feSrcMs =
            clip.Markers.FadeEnd is double fe ? fe * 1000 :
            clip.Markers.End     is double en ? en * 1000 :
            clip.EffectiveEndMs;
        feSrcMs = Math.Clamp(feSrcMs, foSrcMs, clip.EffectiveEndMs);

        var foAbs = clip.AbsoluteStartMs + (foSrcMs - clip.TrimStartMs);
        var feAbs = clip.AbsoluteStartMs + (feSrcMs - clip.TrimStartMs);
        var fx1   = toX(foAbs);
        var fx2   = toX(feAbs);

        var red = new SolidColorBrush(Color.FromArgb(235, 255, 60, 60));

        // Descending fade line (clamped to the box). Drawn even if the start is off the
        // left edge so the slope stays visually correct.
        var p1 = new Point(fx1, boxY + 1);
        var p2 = new Point(fx2, boxY + boxH - 1);
        if (fx2 >= boxLeft && fx1 <= boxRight)
            ctx.DrawLine(new Pen(red, 1.6), p1, p2);

        // Grab handle at the top of the fade start.
        if (fx1 >= boxLeft - 6 && fx1 <= boxRight + 6)
        {
            var hw = 5.0;
            var handle = new Rect(fx1 - hw, boxY + 1, hw * 2, hw * 2);
            ctx.DrawRectangle(red, new Pen(new SolidColorBrush(Colors.White), 1), handle, 2, 2);
            // Wider invisible grab zone for easier hit-testing.
            _fadeHandles.Add((index, new Rect(fx1 - 8, boxY, 16, boxH * 0.5)));
        }
    }

    // ── Volume envelope ───────────────────────────────────────────────────────────

    private void DrawEnvelope(DrawingContext ctx, int index, SegueClipViewModel clip,
        double boxX, double boxY, double boxW, double boxH, Func<double, double> toX)
    {
        var env = clip.VolumeEnvelope;

        // File-seconds → absolute mix ms → screen X
        double Sec2X(double sec) => toX(clip.AbsoluteStartMs + (sec * 1000.0 - clip.TrimStartMs));
        // Volume [0..1] → Y within box (1.0 = top, 0.0 = bottom, with small padding)
        const double vPad = 4.0;
        double Vol2Y(double vol) => boxY + vPad + (1.0 - vol) * (boxH - vPad * 2.0);

        var yellow  = new SolidColorBrush(Color.FromArgb(210, 255, 200, 0));
        var linePen = new Pen(yellow, 1.5);
        const double nr = 5.0; // node radius

        var clipStartX = toX(clip.AbsoluteStartMs);
        var clipEndX   = toX(clip.AbsoluteStartMs + clip.EffectiveDurationMs);

        using (ctx.PushClip(new Rect(boxX, boxY, boxW, boxH)))
        {
            if (env is null || env.Count == 0)
            {
                // No nodes: draw flat line at full volume so the envelope layer is always visible
                var flatY = Vol2Y(1.0);
                ctx.DrawLine(linePen, new Point(clipStartX, flatY), new Point(clipEndX, flatY));
            }
            else
            {
                // Horizontal lead-in: from clip start to first node (at first node's volume)
                var firstX = Sec2X(env[0].TimeS);
                var firstY = Vol2Y(env[0].Volume);
                if (firstX > clipStartX)
                    ctx.DrawLine(linePen, new Point(clipStartX, firstY), new Point(firstX, firstY));

                // Line segments between nodes
                for (int i = 1; i < env.Count; i++)
                    ctx.DrawLine(linePen,
                        new Point(Sec2X(env[i - 1].TimeS), Vol2Y(env[i - 1].Volume)),
                        new Point(Sec2X(env[i].TimeS),     Vol2Y(env[i].Volume)));

                // Horizontal tail: from last node to clip end (at last node's volume)
                var lastIdx = env.Count - 1;
                var lastX   = Sec2X(env[lastIdx].TimeS);
                var lastY   = Vol2Y(env[lastIdx].Volume);
                if (clipEndX > lastX)
                    ctx.DrawLine(linePen, new Point(lastX, lastY), new Point(clipEndX, lastY));
            }
        }

        // Nodes drawn outside clip clip (so circles aren't cut off at the edges)
        if (env is not null)
        {
            for (int i = 0; i < env.Count; i++)
            {
                var x = Sec2X(env[i].TimeS);
                var y = Vol2Y(env[i].Volume);
                if (x < boxX - nr - 2 || x > boxX + boxW + nr + 2) continue;

                ctx.DrawEllipse(yellow, new Pen(new SolidColorBrush(Colors.White), 1),
                    new Point(x, y), nr, nr);

                _envNodeHandles.Add((index, i, new Rect(x - nr - 3, y - nr - 3, (nr + 3) * 2, (nr + 3) * 2)));
            }
        }
    }

    /// Returns (clipIndex, nodeIndex) of the envelope node under pt, or (-1,-1).
    private (int Clip, int Node) HitTestEnvelopeNode(Point pt)
    {
        for (int i = _envNodeHandles.Count - 1; i >= 0; i--)
            if (_envNodeHandles[i].Rect.Contains(pt))
                return (_envNodeHandles[i].Clip, _envNodeHandles[i].Node);
        return (-1, -1);
    }

    /// Returns (clipIndex, markerKey) of the cue marker under pt, or (-1, "").
    private (int Clip, string Key) HitTestCueMarker(Point pt)
    {
        for (int i = _cueMarkerHandles.Count - 1; i >= 0; i--)
            if (_cueMarkerHandles[i].Rect.Contains(pt))
                return (_cueMarkerHandles[i].Clip, _cueMarkerHandles[i].Key);
        return (-1, "");
    }

    /// Converts a screen X position to file-absolute seconds for clip at clipIndex.
    private double ScreenToTimeSec(Point pt, int clipIndex)
    {
        var clips = Clips;
        if (clips is null || clipIndex < 0 || clipIndex >= clips.Count) return 0;
        var clip = clips[clipIndex];
        if (Bounds.Width <= 0) return 0;
        var vs      = _viewCenterMs - _viewRangeMs / 2;
        var absMs   = vs + pt.X / Bounds.Width * _viewRangeMs;
        var timeSec = (absMs - clip.AbsoluteStartMs + clip.TrimStartMs) / 1000.0;
        return Math.Clamp(timeSec, clip.TrimStartMs / 1000.0, clip.EffectiveEndMs / 1000.0);
    }

    /// Converts a screen point to (fileTimeSec, volume) for clip at clipIndex.
    private (double TimeSec, double Volume) ScreenToEnvelope(Point pt, int clipIndex)
    {
        var clips = Clips;
        if (clips is null || clipIndex < 0 || clipIndex >= clips.Count)
            return (0.0, 1.0);

        var clip = clips[clipIndex];
        var w    = Bounds.Width;
        var h    = Bounds.Height;
        if (w <= 0 || h <= 0) return (0.0, 1.0);

        const double rulerH = 28;
        const double sbH    = 14;
        const double padV   = 3;
        const double vPad   = 4;
        var rulerY = h - sbH - rulerH;
        var laneH  = Math.Max(1, rulerY) / clips.Count;
        var boxY   = clipIndex * laneH + padV;
        var boxH   = Math.Max(8, laneH - padV * 2);

        var vs      = _viewCenterMs - _viewRangeMs / 2;
        var absMs   = vs + pt.X / w * _viewRangeMs;
        var timeSec = (absMs - clip.AbsoluteStartMs + clip.TrimStartMs) / 1000.0;
        timeSec     = Math.Clamp(timeSec, clip.TrimStartMs / 1000.0, clip.EffectiveEndMs / 1000.0);

        var volume = 1.0 - (pt.Y - boxY - vPad) / (boxH - vPad * 2);
        volume     = Math.Clamp(volume, 0.0, 1.0);

        return (timeSec, volume);
    }

    private static void DrawRecordingClip(DrawingContext ctx, SegueClipViewModel clip,
        double boxX, double boxY, double boxW, double boxH, Func<double, double> toX)
    {
        // Dark red background + vivid red border
        ctx.DrawRectangle(
            new SolidColorBrush(Color.Parse("#1E0606")),
            new Pen(new SolidColorBrush(Color.FromArgb(220, 210, 55, 55)), 1.5),
            new Rect(boxX, boxY, boxW, boxH), 2, 2);

        var midY  = boxY + boxH / 2;
        var halfH = boxH / 2 * 0.82;

        // Live waveform: each sampled peak (0..1) spread evenly across the recorded
        // duration, drawn as a symmetric stem. Lets the speaker see where speech began
        // and gauge how far to drag the clip on the timeline.
        var peaks = clip.RecordingPeaks;
        var n     = peaks.Count;
        if (n > 0 && clip.RecordingDurationMs > 0)
        {
            using (ctx.PushClip(new Rect(boxX, boxY, boxW, boxH)))
            {
                // Faint zero line.
                ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 90, 90)), 1),
                    new Point(boxX, midY), new Point(boxX + boxW, midY));

                var pen = new Pen(new SolidColorBrush(Color.FromArgb(235, 255, 120, 120)), 1.0);
                for (int i = 0; i < n; i++)
                {
                    var srcMs = (i + 0.5) / n * clip.RecordingDurationMs;
                    var x     = toX(clip.AbsoluteStartMs + srcMs);
                    if (x < boxX - 0.5 || x > boxX + boxW + 0.5) continue;

                    var amp = Math.Clamp(peaks[i], 0f, 1f) * halfH;
                    var y1  = midY - amp;
                    var y2  = midY + amp;
                    if (y2 - y1 < 1.0) { y1 = midY - 0.5; y2 = midY + 0.5; }
                    var px = Math.Round(x) + 0.5;
                    ctx.DrawLine(pen, new Point(px, y1), new Point(px, y2));
                }
            }
        }
        else
        {
            // No samples yet — dim centre line so the box is visible.
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(80, 255, 90, 90)), 1),
                new Point(boxX, midY), new Point(boxX + boxW, midY));
        }

        // ● REC mm:ss label, top-left so it doesn't hide the waveform.
        var elapsed = TimeSpan.FromMilliseconds(clip.RecordingDurationMs);
        var label   = $"● REC  {(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
        var brush   = new SolidColorBrush(Color.FromArgb(240, 255, 140, 140));
        var ft = new FormattedText(label,
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 11, brush);
        if (boxW > ft.Width + 6)
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null,
                new Rect(boxX + 3, boxY + 2, ft.Width + 4, ft.Height + 1));
            ctx.DrawText(ft, new Point(boxX + 5, boxY + 3));
        }
    }

    private static void DrawVtInsertLine(DrawingContext ctx, int laneIdx, double w, double areaH, int laneCount)
    {
        var laneH = areaH / laneCount;
        var lineY = Math.Min((laneIdx + 1) * laneH, areaH);

        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, 255, 50, 50)), null,
            new Rect(0, lineY - 5, w, 10));
        ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(230, 255, 70, 70)), 2),
            new Point(0, lineY), new Point(w, lineY));

        var brush = new SolidColorBrush(Color.FromArgb(230, 255, 110, 110));
        var ft = new FormattedText("▶ WSTAW VT ◀",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), 11, brush);
        var lx = w / 2 - ft.Width / 2;
        var ly = lineY - ft.Height - 3;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), null,
            new Rect(lx - 4, ly - 1, ft.Width + 8, ft.Height + 2));
        ctx.DrawText(ft, new Point(lx, ly));
    }

    private void DrawClipWaveform(DrawingContext ctx, int index, SegueClipViewModel clip,
        double vs, double ve, double w,
        double boxX, double boxY, double boxW, double boxH,
        double clipX1, double clipX2)
    {
        var peaks = clip.Peaks;
        var midY  = boxY + boxH / 2;

        if ((peaks is null || peaks.Length == 0 || clip.DurationMs == 0) && clip.Detail is null)
        {
            // No data yet — draw a dim centre line so the box is clearly visible
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(80, 46, 204, 112)), 1),
                new Point(boxX, midY), new Point(boxX + boxW, midY));
            return;
        }

        var halfH     = boxH / 2 * 0.82;
        var trimStart = (double)clip.TrimStartMs;
        var effEnd    = (double)clip.EffectiveEndMs;
        var range     = ve - vs;
        if (range <= 0) return;

        // Visible source-time window, clamped to the audible (trimmed) region.
        var srcLeft  = Math.Clamp(trimStart + (vs - clip.AbsoluteStartMs), trimStart, effEnd);
        var srcRight = Math.Clamp(trimStart + (ve - clip.AbsoluteStartMs), trimStart, effEnd);
        var visSpan  = srcRight - srcLeft;

        // Coarse peaks: 1000 min/max pairs across the whole file. When the visible
        // window spans fewer coarse points than there are pixels, the per-point stems
        // spread into sparse vertical lines — so we switch to a decoded sample slice.
        bool isMinMax   = peaks is { Length: 2000 };
        int  pointCount = peaks is null ? 0 : (isMinMax ? peaks.Length / 2 : peaks.Length);
        double msPerPoint = pointCount > 1 ? clip.DurationMs / (double)(pointCount - 1) : double.MaxValue;
        // Engage the decoded view only once zoomed into a window small enough to decode
        // cheaply (≤ 60 s) and where the coarse peaks have run out of resolution.
        bool wantDetail = boxW > 1 && visSpan > 0 && visSpan <= 60_000
                          && !string.IsNullOrEmpty(clip.FilePath)
                          && (peaks is null || (visSpan / msPerPoint) < boxW);

        if (wantDetail)
        {
            MaybeRequestDetail(index, srcLeft, srcRight, visSpan, boxW, trimStart, effEnd, clip.Detail);

            var d = clip.Detail;
            if (d is not null && d.StartMs <= srcLeft + 1 && d.EndMs >= srcRight - 1)
            {
                DrawDetailWaveform(ctx, clip, d, vs, range, w, boxX, boxY, boxW, boxH, midY, halfH);
                return;
            }
            // Detail not ready yet → fall through to coarse peaks as a placeholder.
        }

        if (peaks is null || pointCount < 2) return;

        // RadioDJ-style coarse view: each stored peak point drawn as a min/max stem.
        var pen      = new Pen(new SolidColorBrush(Color.Parse("#2ECC70")), 1.0);
        var jStart   = Math.Clamp((int)Math.Floor(srcLeft  / msPerPoint), 0, pointCount - 1);
        var jEnd     = Math.Clamp((int)Math.Ceiling(srcRight / msPerPoint), 0, pointCount - 1);
        var boxRight = boxX + boxW;

        for (int j = jStart; j <= jEnd; j++)
        {
            var srcMs = j * msPerPoint;
            var absMs = clip.AbsoluteStartMs + (srcMs - trimStart);
            var x     = (absMs - vs) / range * w;
            if (x < boxX - 0.5 || x > boxRight + 0.5) continue;

            float minAmp, maxAmp;
            if (isMinMax)
            {
                minAmp = peaks[j * 2];
                maxAmp = peaks[j * 2 + 1];
            }
            else
            {
                maxAmp = peaks[j];
                minAmp = -maxAmp;
            }

            var y1 = midY - maxAmp * halfH;
            var y2 = midY - minAmp * halfH; // minAmp ≤ 0 → y2 ≥ midY
            if (y2 - y1 < 1.0) { y1 = midY - 0.5; y2 = midY + 0.5; }

            var px = Math.Round(x) + 0.5; // crisp 1px line
            ctx.DrawLine(pen, new Point(px, y1), new Point(px, y2));
        }
    }

    /// Renders a decoded slice: a continuous connected line at sample level
    /// (Audacity-style), or a dense min/max envelope when several samples share a column.
    private void DrawDetailWaveform(DrawingContext ctx, SegueClipViewModel clip,
        WaveformWindow d, double vs, double range, double w,
        double boxX, double boxY, double boxW, double boxH, double midY, double halfH)
    {
        int cols = d.MinMax.Length / 2;
        if (cols < 1) return;

        var trimStart = (double)clip.TrimStartMs;
        var span      = d.EndMs - d.StartMs;
        if (span <= 0) return;

        double ColX(int c)
        {
            var srcMs = d.StartMs + (c + 0.5) / cols * span;
            var absMs = clip.AbsoluteStartMs + (srcMs - trimStart);
            return (absMs - vs) / range * w;
        }

        using (ctx.PushClip(new Rect(boxX, boxY, boxW, boxH)))
        {
            // Faint zero line for the Audacity look.
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(70, 46, 204, 112)), 1),
                new Point(boxX, midY), new Point(boxX + boxW, midY));

            var green = new SolidColorBrush(Color.Parse("#2ECC70"));

            if (d.SampleLevel)
            {
                // Connect consecutive sample values into one polyline.
                var pen = new Pen(green, 1.2, lineJoin: PenLineJoin.Round);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    bool started = false;
                    for (int c = 0; c < cols; c++)
                    {
                        var x = ColX(c);
                        var y = midY - d.MinMax[c * 2 + 1] * halfH; // min == max == sample
                        var p = new Point(x, y);
                        if (!started) { g.BeginFigure(p, false); started = true; }
                        else g.LineTo(p);
                    }
                    if (started) g.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, geo);
            }
            else
            {
                // Dense min/max envelope — one stem per column, no gaps.
                var pen = new Pen(green, 1.0);
                for (int c = 0; c < cols; c++)
                {
                    var x  = ColX(c);
                    var y1 = midY - d.MinMax[c * 2 + 1] * halfH;
                    var y2 = midY - d.MinMax[c * 2]     * halfH;
                    if (y2 - y1 < 1.0) { y1 = midY - 0.5; y2 = midY + 0.5; }
                    var px = Math.Round(x) + 0.5;
                    ctx.DrawLine(pen, new Point(px, y1), new Point(px, y2));
                }
            }
        }
    }

    /// Fires <see cref="DetailWaveformRequested"/> when the visible window is not
    /// adequately covered by the clip's current detail slice, de-duplicating so the
    /// same window isn't requested repeatedly across renders.
    private void MaybeRequestDetail(int index, double srcLeft, double srcRight,
        double visSpan, double boxW, double trimStart, double effEnd, WaveformWindow? detail)
    {
        bool needFetch =
            detail is null ||
            srcLeft  < detail.StartMs - 1 ||
            srcRight > detail.EndMs   + 1 ||
            (detail.EndMs - detail.StartMs) > visSpan * 3; // far more zoomed-in than the slice
        if (!needFetch) return;

        var margin   = visSpan * 0.5;
        var reqStart = Math.Max(trimStart, srcLeft  - margin);
        var reqEnd   = Math.Min(effEnd,    srcRight + margin);
        if (reqEnd <= reqStart) return;

        var reqSpan = reqEnd - reqStart;
        int cols    = (int)(boxW * (reqSpan / visSpan));
        cols        = Math.Clamp(cols / 64 * 64, 64, 4096);

        if (_detailReq.TryGetValue(index, out var last)
            && Math.Abs(last.Start - reqStart) < 1
            && Math.Abs(last.End   - reqEnd)   < 1
            && last.Cols == cols)
            return; // identical request already in flight

        _detailReq[index] = (reqStart, reqEnd, cols);
        DetailWaveformRequested?.Invoke(index, reqStart, reqEnd, cols);
    }

    private void DrawClipMarker(DrawingContext ctx, int clipIndex, CueMarkerPalette.Entry entry,
        SegueClipViewModel clip, double vs, double ve, double boxLeft, double boxRight,
        double y0, double h, Func<double, double> toX)
    {
        var sourceMs = entry.Seconds * 1000;
        if (sourceMs < clip.TrimStartMs || sourceMs > clip.EffectiveEndMs) return;
        var absMs = clip.AbsoluteStartMs + (sourceMs - clip.TrimStartMs);
        if (absMs < vs || absMs > ve) return;

        var x = toX(absMs);
        if (x < boxLeft || x > boxRight) return; // outside this clip's box

        var brush = new SolidColorBrush(entry.Color);
        ctx.DrawLine(new Pen(brush, 1.5), new Point(x, y0), new Point(x, y0 + h));

        // Small label hugging the line; flip to the left near the box's right edge.
        var ft = new FormattedText(entry.Label,
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 8, brush);
        var lx = x + ft.Width + 3 > boxRight ? x - ft.Width - 2 : x + 2;
        var ly = y0 + 13;
        ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), null,
            new Rect(lx - 1, ly, ft.Width + 2, ft.Height));
        ctx.DrawText(ft, new Point(lx, ly));

        // Invisible 12 px wide grab zone spanning the full clip height.
        _cueMarkerHandles.Add((clipIndex, entry.Key, new Rect(x - 6, y0, 12, h)));
    }

    // ── Label helpers ─────────────────────────────────────────────────────────

    private static void DrawNameLabel(DrawingContext ctx, string text, double x, double y)
    {
        if (string.IsNullOrEmpty(text)) return;
        var brush = new SolidColorBrush(Color.FromArgb(230, 200, 200, 220));
        var ft = new FormattedText(text,
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", FontStyle.Normal, FontWeight.SemiBold), 10, brush);
        var bg = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
        ctx.DrawRectangle(bg, null, new Rect(x - 1, y, ft.Width + 6, ft.Height + 2));
        ctx.DrawText(ft, new Point(x + 2, y + 1));
    }

    private static void DrawTimeLabel(DrawingContext ctx, string text, double x, double y, bool rightAlign)
    {
        var brush = new SolidColorBrush(Color.FromArgb(220, 150, 220, 170));
        var ft = new FormattedText(text,
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Consolas"), 9, brush);
        var px = rightAlign ? x - ft.Width : x;
        ctx.DrawText(ft, new Point(px, y));
    }

    private static string FormatTime(uint ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    // ── Ruler ─────────────────────────────────────────────────────────────────

    private static void DrawRuler(DrawingContext ctx, double vs, double ve, double w, double y0, double h)
    {
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0D0D1A")), null, new Rect(0, y0, w, h));

        var range    = ve - vs;
        if (range <= 0) return;
        var interval = PickInterval(range, w);
        var vs0  = Math.Max(0, vs);   // ruler never shows negative time
        var first = Math.Ceiling(vs0 / interval) * interval;
        var textBrush = new SolidColorBrush(Color.Parse("#6666AA"));
        var majorPen  = new Pen(new SolidColorBrush(Color.Parse("#333355")), 1);
        var minorPen  = new Pen(new SolidColorBrush(Color.Parse("#22223A")), 1);
        var minor     = interval / 5;

        for (var t = Math.Ceiling(vs0 / minor) * minor; t <= ve; t += minor)
        {
            if (Math.Abs(t % interval) < 1) continue;
            var x = (t - vs) / range * w;
            if (x >= 0 && x <= w)
                ctx.DrawLine(minorPen, new Point(x, y0), new Point(x, y0 + 3));
        }

        for (var t = first; t <= ve; t += interval)
        {
            var x = (t - vs) / range * w;
            if (x < 0 || x > w) continue;
            ctx.DrawLine(majorPen, new Point(x, y0), new Point(x, y0 + 5));
            var ts    = TimeSpan.FromMilliseconds(t);
            var label = interval < 1000
                ? $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds:D3}"
                : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
            var ft    = new FormattedText(label,
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Consolas"), 9, textBrush);
            ctx.DrawText(ft, new Point(x - ft.Width / 2, y0 + 7));
        }
    }

    private static double PickInterval(double rangeMs, double w)
    {
        var target = rangeMs / (w / 110);
        double[] steps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 30000, 60000, 120000 };
        foreach (var s in steps)
            if (s >= target) return s;
        return 300_000;
    }

    // ── Scrollbar ────────────────────────────────────────────────────────────────

    /// Moves the view center so the clicked x-coordinate on the scrollbar track maps to that position.
    private void ScrollBarJumpTo(double px)
    {
        var w = Bounds.Width;
        if (w <= 0) return;
        var totalRange = _scrollMax - _scrollMin;
        if (totalRange <= 0) return;
        _viewCenterMs = _scrollMin + px / w * totalRange;
        InvalidateVisual();
    }

    private void DrawScrollBar(DrawingContext ctx, double w, double sbY, double sbH)
    {
        ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#0D0D1A")), null, new Rect(0, sbY, w, sbH));

        var totalRange = _scrollMax - _scrollMin;
        if (totalRange <= 0 || w <= 0) return;

        var vs = _viewCenterMs - _viewRangeMs / 2;
        var ve = _viewCenterMs + _viewRangeMs / 2;

        var thumbLeft  = (Math.Max(vs, _scrollMin) - _scrollMin) / totalRange * w;
        var thumbRight = (Math.Min(ve, _scrollMax) - _scrollMin) / totalRange * w;
        thumbLeft  = Math.Clamp(thumbLeft,  0, w);
        thumbRight = Math.Clamp(thumbRight, 0, w);
        var thumbW = Math.Max(16, thumbRight - thumbLeft);
        // Clamp right edge so thumb never overflows the track
        if (thumbLeft + thumbW > w) thumbLeft = w - thumbW;

        const double padV = 2;
        ctx.DrawRectangle(
            new SolidColorBrush(Color.FromArgb(160, 80, 100, 200)),
            new Pen(new SolidColorBrush(Color.FromArgb(100, 120, 140, 255)), 1),
            new Rect(thumbLeft, sbY + padV, thumbW, sbH - padV * 2), 3, 3);
    }

    // ── Zoom API (called from toolbar buttons) ──────────────────────────────────

    public void ZoomIn()  => ApplyZoom(0.65);
    public void ZoomOut() => ApplyZoom(1.55);

    /// Resets the view so every clip fits on screen.
    public void ZoomToFit()
    {
        _viewCenterMs = double.NaN;
        _needsFit     = true;
        InvalidateVisual();
    }

    // Deepest interactive zoom — low enough to reach the individual-sample view
    // (Audacity-style connected line) for the decoded detail waveform.
    private const double MinViewRangeMs = 10;
    private const double MaxViewRangeMs = 600_000;

    private void ApplyZoom(double factor)
    {
        if (double.IsNaN(_viewCenterMs)) return;
        _viewRangeMs = Math.Clamp(_viewRangeMs * factor, MinViewRangeMs, MaxViewRangeMs);
        InvalidateVisual();
    }

    // ── Hit-testing ───────────────────────────────────────────────────────────

    /// Returns the clip index whose fade handle is under <paramref name="pt"/>, or -1.
    private int HitTestFadeHandle(Point pt)
    {
        for (int i = _fadeHandles.Count - 1; i >= 0; i--)
            if (_fadeHandles[i].Rect.Contains(pt))
                return _fadeHandles[i].Clip;
        return -1;
    }

    /// Converts a screen x to an absolute mix time and reports the new fade start.
    private void RaiseFadeMoved(int clipIndex, double x)
    {
        if (Bounds.Width <= 0) return;
        var vs    = _viewCenterMs - _viewRangeMs / 2;
        var absMs = vs + Math.Clamp(x, 0, Bounds.Width) / Bounds.Width * _viewRangeMs;
        FadeHandleMoved?.Invoke(clipIndex, absMs);
    }

    /// Returns the index of the clip at screen point, or -1 if none.
    private int HitTestClip(Point pt)
    {
        var clips = Clips;
        if (clips is null || clips.Count == 0 || Bounds.Width <= 0 || Bounds.Height <= 0)
            return -1;

        const double rulerH = 28;
        const double sbH    = 14;
        const double padV   = 3;
        var sbY    = Bounds.Height - sbH;
        var rulerY = sbY - rulerH;
        if (pt.Y >= rulerY) return -1;  // ruler or scrollbar zone
        var laneH  = Math.Max(1, rulerY) / clips.Count;
        var vs     = _viewCenterMs - _viewRangeMs / 2;
        var ve     = _viewCenterMs + _viewRangeMs / 2;
        if (ve == vs) return -1;

        for (int i = 0; i < clips.Count; i++)
        {
            var boxY = i * laneH + padV;
            var boxH = Math.Max(8, laneH - padV * 2);
            if (pt.Y < boxY || pt.Y > boxY + boxH) continue;

            var x1   = (clips[i].AbsoluteStartMs - vs) / (ve - vs) * Bounds.Width;
            var x2   = (clips[i].AbsoluteStartMs + clips[i].EffectiveDurationMs - vs) / (ve - vs) * Bounds.Width;
            var boxX = Math.Max(x1, 0);
            var boxW = Math.Min(x2, Bounds.Width) - boxX;
            if (boxW <= 0) continue;

            if (pt.X >= boxX && pt.X <= boxX + boxW)
                return i;
        }
        return -1;
    }

    /// Returns the lane (clip) index under <paramref name="pt"/>, or -1 if over ruler/scrollbar.
    private int HitTestLane(Point pt)
    {
        var clips = Clips;
        if (clips is null || clips.Count == 0 || Bounds.Height <= 0) return -1;
        const double rulerH = 28;
        const double sbH    = 14;
        var rulerY = Bounds.Height - sbH - rulerH;
        if (pt.Y >= rulerY) return -1;
        var laneH = Math.Max(1, rulerY) / clips.Count;
        return Math.Clamp((int)(pt.Y / laneH), 0, clips.Count - 1);
    }

    // ── Pointer ───────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos   = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;

        // VT insertion mode: a click fires VtInsertRequested; no drag starts.
        if (IsVtMode)
        {
            if (!props.IsLeftButtonPressed) return;
            var lane = HitTestLane(pos);
            if (lane >= 0)
                VtInsertRequested?.Invoke(lane);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // Shift+left-click: add StartNext marker on the clip under cursor (if not present).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var clipIdx = HitTestClip(pos);
            if (clipIdx >= 0)
            {
                var timeSec = ScreenToTimeSec(pos, clipIdx);
                CueMarkerAdded?.Invoke(clipIdx, "StartNext", timeSec);
                e.Handled = true;
                return;
            }
        }

        // Ctrl+left-click: add envelope node on the clip under cursor.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var clipIdx = HitTestClip(pos);
            if (clipIdx >= 0)
            {
                var (timeSec, volume) = ScreenToEnvelope(pos, clipIdx);
                EnvelopeNodeAdded?.Invoke(clipIdx, timeSec, volume);
                e.Handled = true;
                return;
            }
        }

        // Scrollbar zone at the very bottom
        const double sbH = 14;
        if (pos.Y >= Bounds.Height - sbH)
        {
            _dragMode  = DragMode.ScrollBar;
            _dragLastX = pos.X;
            ScrollBarJumpTo(pos.X);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Envelope node drag takes priority over clip drag / fade handle / pan.
        var (ec, en) = HitTestEnvelopeNode(pos);
        if (ec >= 0)
        {
            _dragMode    = DragMode.EnvelopeNode;
            _dragEnvClip = ec;
            _dragEnvNode = en;
            EnvelopeNodeDragStarted?.Invoke(ec);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Fade-out handle (takes priority over cue marker drag for the same x position).
        var fadeClip = HitTestFadeHandle(pos);
        if (fadeClip >= 0)
        {
            _dragMode     = DragMode.FadeHandle;
            _dragFadeClip = fadeClip;
            RaiseFadeMoved(fadeClip, pos.X);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        // Cue marker drag.
        var (cc, ck) = HitTestCueMarker(pos);
        if (cc >= 0)
        {
            _dragMode    = DragMode.CueMarker;
            _dragCueClip = cc;
            _dragCueKey  = ck;
            CueMarkerDragStarted?.Invoke(cc, ck);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var clipHit = HitTestClip(pos);

        // Clip index 0 has no predecessor → treat as pan.
        if (clipHit > 0)
        {
            _dragMode      = DragMode.ClipDrag;
            _dragClipIndex = clipHit;
            _dragLastX     = pos.X;
            ClipDragStarted?.Invoke(clipHit);
        }
        else
        {
            _dragMode        = DragMode.Pan;
            _dragStart       = pos;
            _dragStartCenter = double.IsNaN(_viewCenterMs) ? 0 : _viewCenterMs;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);

        if (_dragMode == DragMode.FadeHandle)
        {
            RaiseFadeMoved(_dragFadeClip, pos.X);
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.CueMarker)
        {
            var timeSec = ScreenToTimeSec(pos, _dragCueClip);
            CueMarkerMoved?.Invoke(_dragCueClip, _dragCueKey, timeSec);
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.EnvelopeNode)
        {
            var (timeSec, volume) = ScreenToEnvelope(pos, _dragEnvClip);
            EnvelopeNodeMoved?.Invoke(_dragEnvClip, _dragEnvNode, timeSec, volume);
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.ClipDrag)
        {
            if (Bounds.Width > 0)
            {
                var deltaMs = (pos.X - _dragLastX) / Bounds.Width * _viewRangeMs;
                _dragLastX = pos.X;
                if (Math.Abs(deltaMs) > 0.5)
                    ClipDragged?.Invoke(_dragClipIndex, deltaMs);
            }
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.ScrollBar && Bounds.Width > 0)
        {
            var dx = pos.X - _dragLastX;
            _dragLastX = pos.X;
            var totalRange = _scrollMax - _scrollMin;
            if (totalRange > 0)
                _viewCenterMs += dx / Bounds.Width * totalRange;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_dragMode == DragMode.Pan && _dragStart is not null && Bounds.Width > 0)
        {
            var dx = pos.X - _dragStart.Value.X;
            _viewCenterMs = _dragStartCenter - dx / Bounds.Width * _viewRangeMs;
            InvalidateVisual();
        }

        // Update cursor / VT hover lane
        if (_dragMode == DragMode.None)
        {
            if (IsVtMode)
            {
                var lane = HitTestLane(pos);
                if (_vtHoverLane != lane)
                {
                    _vtHoverLane = lane;
                    InvalidateVisual();
                }
                Cursor = new Cursor(StandardCursorType.Cross);
            }
            else
            {
                var overEnvNode  = HitTestEnvelopeNode(pos).Clip >= 0;
                var overFade     = HitTestFadeHandle(pos) >= 0;
                var overCueMark  = HitTestCueMarker(pos).Clip >= 0;
                var idx          = HitTestClip(pos);
                if (overEnvNode)
                    Cursor = new Cursor(StandardCursorType.SizeAll);
                else if (overFade || overCueMark)
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && idx >= 0)
                    Cursor = new Cursor(StandardCursorType.Cross);
                else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && idx >= 0)
                    Cursor = new Cursor(StandardCursorType.Cross);
                else if (idx > 0)
                    Cursor = new Cursor(StandardCursorType.SizeWestEast);
                else
                    Cursor = new Cursor(StandardCursorType.Arrow);
            }
        }

        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_dragMode == DragMode.Pan && _dragStart is { } ds && Bounds.Width > 0)
        {
            if (Math.Abs(e.GetPosition(this).X - ds.X) < 4)
            {
                var ms = _viewCenterMs - _viewRangeMs / 2
                       + e.GetPosition(this).X / Bounds.Width * _viewRangeMs;
                CursorClicked?.Invoke(ms);
            }
        }
        else if (_dragMode == DragMode.FadeHandle)
        {
            FadeHandleCommitted?.Invoke(_dragFadeClip);
        }
        else if (_dragMode == DragMode.CueMarker)
        {
            CueMarkerCommitted?.Invoke(_dragCueClip, _dragCueKey);
        }
        else if (_dragMode == DragMode.EnvelopeNode)
        {
            EnvelopeNodeCommitted?.Invoke();
        }

        _dragMode  = DragMode.None;
        _dragStart = null;
        Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Arrow);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void HandleContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(this, out var pos)) { e.Handled = true; return; }

        var menu = BuildContextMenu(pos);
        if (menu is null) { e.Handled = true; return; }

        menu.Open(this);
        e.Handled = true;
    }

    private ContextMenu? BuildContextMenu(Point pos)
    {
        var (ec, en) = HitTestEnvelopeNode(pos);
        if (ec >= 0)
        {
            var m    = new ContextMenu();
            var item = new MenuItem { Header = Localizer.Instance?["sg.ctx.remove_envelope_node"] ?? "Remove envelope node" };
            item.Click += (_, _) => EnvelopeNodeRemoved?.Invoke(ec, en);
            m.Items.Add(item);
            return m;
        }

        var (mc, mk) = HitTestCueMarker(pos);
        if (mc >= 0)
        {
            var label = CueMarkerPalette.GetLabel(mk);
            var m     = new ContextMenu();
            var item  = new MenuItem { Header = string.Format(Localizer.Instance?["sg.ctx.remove_marker"] ?? "Remove {0}", label) };
            item.Click += (_, _) => CueMarkerRemoved?.Invoke(mc, mk);
            m.Items.Add(item);
            return m;
        }

        var clipIdx = HitTestClip(pos);
        if (clipIdx >= 0)
        {
            var timeSec = ScreenToTimeSec(pos, clipIdx);
            var m       = new ContextMenu();
            AddCueMenuItem(m, "StartNext", clipIdx, "StartNext", timeSec);
            AddCueMenuItem(m, "Fade Out",  clipIdx, "FadeOut",   timeSec);
            m.Items.Add(new Avalonia.Controls.Separator());
            AddCueMenuItem(m, "Start",     clipIdx, "Start",     timeSec);
            AddCueMenuItem(m, "Fade End",  clipIdx, "FadeEnd",   timeSec);
            AddCueMenuItem(m, "End",       clipIdx, "End",       timeSec);
            return m;
        }

        return null;
    }

    private void AddCueMenuItem(ContextMenu m, string markerName, int clipIdx, string key, double timeSec)
    {
        var item = new MenuItem
        {
            Header = string.Format(Localizer.Instance?["sg.ctx.add_marker"] ?? "Add {0}", markerName)
        };
        item.Click += (_, _) => CueMarkerAdded?.Invoke(clipIdx, key, timeSec);
        m.Items.Add(item);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Bounds.Width <= 0) return;

        // Shift + wheel pans horizontally instead of zooming.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _viewCenterMs -= e.Delta.Y * 0.18 * _viewRangeMs;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var norm      = e.GetPosition(this).X / Bounds.Width;
        var cursorMs  = _viewCenterMs - _viewRangeMs / 2 + norm * _viewRangeMs;
        var factor    = e.Delta.Y > 0 ? 0.65 : 1.55;
        var newRange  = Math.Clamp(_viewRangeMs * factor, MinViewRangeMs, MaxViewRangeMs);
        _viewCenterMs = cursorMs + (0.5 - norm) * newRange;
        _viewRangeMs  = newRange;
        InvalidateVisual();
        e.Handled = true;
    }
}
