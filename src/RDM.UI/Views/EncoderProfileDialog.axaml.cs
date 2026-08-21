using Avalonia.Controls;
using Avalonia.Interactivity;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System.Threading.Tasks;

namespace RDM.UI.Views;

/// <summary>
/// Create/edit dialog for one streaming profile.
///
/// Saving goes through the API, not straight back to the caller, so the server's validation is the
/// only validation there is. Duplicating the rules here would create a second set that could drift
/// — and the server's is the one that also guards the HTTP surface.
/// </summary>
public partial class EncoderProfileDialog : Window
{
    private readonly ApiClientService? _api;

    public EncoderProfileDialog() => InitializeComponent();

    public EncoderProfileDialog(ApiClientService api, EncoderProfileDto? existing = null)
    {
        InitializeComponent();
        _api        = api;
        DataContext = existing is null
            ? new EncoderProfileEditViewModel()
            : new EncoderProfileEditViewModel(existing);
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EncoderProfileEditViewModel vm || _api is null)
        {
            Close(false);
            return;
        }

        vm.Error = null;

        var ok = vm.IsNew
            ? await CreateAsync(vm)
            : await UpdateAsync(vm);

        if (ok) Close(true);
    }

    private async Task<bool> CreateAsync(EncoderProfileEditViewModel vm)
    {
        var result = await _api!.CreateEncoderProfileAsync(vm.ToCreateDto());
        if (result.Ok) return true;

        vm.Error = result.ErrorMessage
                   ?? Localizer.Instance?["streaming.editor.save_failed"]
                   ?? "Could not save the profile.";
        return false;
    }

    private async Task<bool> UpdateAsync(EncoderProfileEditViewModel vm)
    {
        var result = await _api!.UpdateEncoderProfileAsync(vm.ProfileId!, vm.ToUpdateDto());
        if (result.Ok) return true;

        vm.Error = result.ErrorMessage
                   ?? Localizer.Instance?["streaming.editor.save_failed"]
                   ?? "Could not save the profile.";
        return false;
    }
}
