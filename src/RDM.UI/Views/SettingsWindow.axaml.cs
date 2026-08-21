using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        _vm = App.Services.GetRequiredService<SettingsViewModel>();
        DataContext = _vm;

        // The view models cannot open windows or pickers — the view answers those requests.
        _vm.PickRecordingFolderAsync   = PickRecordingFolderAsync;
        _vm.Streaming.EditProfileAsync = ShowProfileEditorAsync;
        _vm.Streaming.ConfirmAsync     = ConfirmAsync;
        _vm.Users.AddUserAsync             = ShowAddUserDialogAsync;
        _vm.Users.ResetPasswordDialogAsync = ShowResetPasswordDialogAsync;

        _vm.Activate();
        CancelButton.Click      += (_, _) => Close();
        MicFxWindowButton.Click += OnMicFxWindowClicked;

        // Unsubscribes the child from the event bus; the sessions themselves keep running.
        Closed += (_, _) => _vm.Streaming.Dispose();
    }

    private void OnMicFxWindowClicked(object? sender, RoutedEventArgs e)
    {
        var win = App.Services.GetRequiredService<MicDspChainWindow>();
        win.Show(this);
    }

    private async Task<bool> ShowProfileEditorAsync(EncoderProfileDto? existing)
    {
        var api = App.Services.GetRequiredService<ApiClientService>();
        return await new EncoderProfileDialog(api, existing).ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmAsync(string question)
    {
        var dialog = new SimpleConfirmDialog(
            question,
            title:        Localizer.Instance?["settings.tab.streaming"],
            confirmLabel: Localizer.Instance?["common.delete"]);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<NewUserRequest?> ShowAddUserDialogAsync()
        => await new UserEditDialog().ShowDialog<NewUserRequest?>(this);

    private async Task<string?> ShowResetPasswordDialogAsync(UserRowViewModel row)
        => await new ResetPasswordDialog(row.Username).ShowDialog<string?>(this);

    private async Task<string?> PickRecordingFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title         = Localizer.Instance?["settings.recording.picker"] ?? "Select recording folder",
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
