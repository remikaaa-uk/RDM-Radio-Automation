using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.Controls;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class TrackEditorWindow : Window, IParameterizedWindow
{
    public TrackEditorWindow()
    {
        InitializeComponent();

        DataContext = App.Services.GetRequiredService<TrackEditorViewModel>();

        CancelButton.Click += (_, _) => Close();

        CueWaveform.PositionClicked += async normalizedPos =>
        {
            if (DataContext is TrackEditorViewModel vm)
                await vm.CueEditor.OnWaveformClickedAsync(normalizedPos);
        };

        Closing += (_, _) =>
        {
            if (DataContext is TrackEditorViewModel vm)
                vm.Dispose();
        };
    }

    public async Task InitAsync(object parameter)
    {
        if (DataContext is not TrackEditorViewModel vm) return;

        if (parameter is TrackEditorNavContext ctx)
        {
            vm.SetNavContext(ctx);
            await vm.InitializeAsync(ctx.AssetIds[ctx.CurrentIndex]);
        }
        else if (parameter is string assetId)
        {
            await vm.InitializeAsync(assetId);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (DataContext is not TrackEditorViewModel vm) return;
        if (CueEditorTab?.IsSelected != true) return;

        // Route keyboard events to CueEditor when Cue Editor tab is active.
        // Only intercept if no TextBox in this window currently has focus.
        if (FocusManager?.GetFocusedElement() is TextBox) return;

        vm.CueEditor.HandleKey(e.Key, e.KeyModifiers);
        e.Handled = true;
    }
}
