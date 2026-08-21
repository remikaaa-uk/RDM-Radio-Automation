using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using RDM.UI.ViewModels;
using System;
using System.ComponentModel;
using System.Linq;

namespace RDM.UI.Views;

public partial class CartwallView : UserControl
{
    public CartwallView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;

        if (DataContext is CartwallViewModel vm)
            vm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LayoutUpdated -= OnLayoutUpdated;

        if (DataContext is CartwallViewModel vm)
            vm.PropertyChanged -= OnVmPropertyChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CartwallViewModel vm)
            vm.PropertyChanged += OnVmPropertyChanged;
        Recalculate();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => Recalculate();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CartwallViewModel.SelectedTabIndex))
            Recalculate();
    }

    private const int Cols = 4;

    private void Recalculate()
    {
        if (DataContext is not CartwallViewModel vm) return;

        var contentPresenter = InnerTabControl.GetVisualDescendants()
            .OfType<ContentPresenter>()
            .FirstOrDefault(c => c.Name == "PART_SelectedContentHost");

        double contentH = contentPresenter?.Bounds.Height ?? 0;
        double contentW = contentPresenter?.Bounds.Width  ?? 0;

        if (contentH <= 0 || contentW <= 0) return;

        var tab = vm.SelectedTabIndex >= 0 && vm.SelectedTabIndex < vm.Tabs.Count
            ? vm.Tabs[vm.SelectedTabIndex] : null;
        int numSlots = tab?.Slots.Count ?? 16;
        int numRows  = Math.Max(1, (int)Math.Ceiling(numSlots / (double)Cols));

        double newW = Math.Max(60, Math.Floor(contentW / Cols) - 12);
        double newH = Math.Max(60, (contentH - 6) / numRows - 6);

        if (Math.Abs(vm.SlotWidth  - newW) > 0.5) vm.SlotWidth  = newW;
        if (Math.Abs(vm.SlotHeight - newH) > 0.5) vm.SlotHeight = newH;
    }
}
