using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RDM.Shared.DTOs;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class SegueEditorWindow : Window, IParameterizedWindow
{
    public SegueEditorWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SegueEditorViewModel>();
        OkButton.Click += (_, _) => Close();

        Timeline.ClipDragStarted += idx =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.BeginClipDrag(idx);
        };
        Timeline.ClipDragged += (idx, deltaMs) =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.ShiftClipFrom(idx, deltaMs);
        };
        Timeline.CursorClicked += ms =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.SeekPreview(ms);
        };
        Timeline.FadeHandleMoved += (idx, absMs) =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.SetFadeOut(idx, absMs);
        };
        Timeline.FadeHandleCommitted += idx =>
        {
            if (DataContext is SegueEditorViewModel vm) _ = vm.CommitFadeOutAsync(idx);
        };
        Timeline.DetailWaveformRequested += (idx, startMs, endMs, cols) =>
        {
            if (DataContext is SegueEditorViewModel vm)
                _ = vm.RequestDetailWaveformAsync(idx, startMs, endMs, cols);
        };
        Timeline.VtInsertRequested += afterIndex =>
        {
            if (DataContext is SegueEditorViewModel vm)
                _ = vm.StartVtRecordingAsync(afterIndex);
        };

        Timeline.EnvelopeNodeAdded += (clipIdx, timeSec, volume) =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.AddEnvelopeNode(clipIdx, timeSec, volume);
        };
        Timeline.EnvelopeNodeDragStarted += clipIdx =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.BeginEnvelopeNodeDrag(clipIdx);
        };
        Timeline.EnvelopeNodeMoved += (clipIdx, nodeIdx, timeSec, volume) =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.MoveEnvelopeNode(clipIdx, nodeIdx, timeSec, volume);
        };
        Timeline.EnvelopeNodeRemoved += (clipIdx, nodeIdx) =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.RemoveEnvelopeNode(clipIdx, nodeIdx);
        };
        Timeline.EnvelopeNodeCommitted += () =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.CommitEnvelopeNodeDrag();
        };

        Timeline.CueMarkerDragStarted += (clipIdx, key) => { };  // preview only — no VM call needed
        Timeline.CueMarkerMoved += (clipIdx, key, timeSec) =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.MoveCueMarker(clipIdx, key, timeSec);
        };
        Timeline.CueMarkerCommitted += (clipIdx, key) =>
        {
            if (DataContext is SegueEditorViewModel vm) _ = vm.CommitCueMarkerAsync(clipIdx);
        };
        Timeline.CueMarkerRemoved += (clipIdx, key) =>
        {
            if (DataContext is SegueEditorViewModel vm)
            {
                vm.RemoveCueMarker(clipIdx, key);
                _ = vm.CommitCueMarkerAsync(clipIdx);
            }
        };
        Timeline.CueMarkerAdded += (clipIdx, key, timeSec) =>
        {
            if (DataContext is SegueEditorViewModel vm) _ = vm.AddCueMarkerAsync(clipIdx, key, timeSec);
        };

        Closing += (_, _) =>
        {
            if (DataContext is SegueEditorViewModel vm) vm.StopPreview();
        };
    }

    private void OnZoomInClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Timeline.ZoomIn();

    private void OnZoomOutClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Timeline.ZoomOut();

    private void OnZoomFitClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Timeline.ZoomToFit();

    public async Task InitAsync(object parameter)
    {
        if (DataContext is not SegueEditorViewModel vm) return;

        if (parameter is IReadOnlyList<PlaylistItemDto> items)
            await vm.InitializeAsync(items);
    }
}
