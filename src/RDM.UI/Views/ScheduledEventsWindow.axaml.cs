using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;

namespace RDM.UI.Views;

public partial class ScheduledEventsWindow : Window
{
    public ScheduledEventsWindow()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<ScheduledEventsViewModel>();
        DataContext = vm;
        vm.Activate();
        Closed += (_, _) => vm.Dispose();

        var api = App.Services.GetRequiredService<ApiClientService>();
        ActionItemViewModel.ShowAssetPicker = async () =>
        {
            var dlg = new CartAssetPickerDialog(api);
            return await dlg.ShowDialog<AssetPickerRow?>(this);
        };
    }

    private async void OnBrowseExecPathClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ActionItemViewModel action }) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title         = Localizer.Instance?["se.picker.exec"] ?? "Select a file / script to run",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Instance?["se.ft.scripts"] ?? "Scripts and programs")
                {
                    Patterns = ["*.ps1", "*.py", "*.sh", "*.bat", "*.cmd", "*.exe", "*.js"]
                },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            action.ExecPath = path;
    }
}
