using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.UI.Localization;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

/// <summary>Self-service password change — available to any logged-in role, not just Admin.</summary>
public partial class ChangePasswordViewModel : ObservableObject
{
    private readonly IAuthService                    _authService;
    private readonly UserSessionContext               _userSessionContext;
    private readonly ILogger<ChangePasswordViewModel> _logger;

    [ObservableProperty] private string _currentPassword = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirmPassword = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isBusy;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public event Action? Saved;
    public event Action? Cancelled;

    public ChangePasswordViewModel(
        IAuthService                    authService,
        UserSessionContext               userSessionContext,
        ILogger<ChangePasswordViewModel> logger)
    {
        _authService        = authService;
        _userSessionContext = userSessionContext;
        _logger             = logger;
    }

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken ct)
    {
        ErrorMessage = "";

        if (string.IsNullOrEmpty(NewPassword) || NewPassword.Length < 8)
        {
            ErrorMessage = Localizer.Instance?["chpw.err.too_short"] ?? "New password must be at least 8 characters.";
            return;
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = Localizer.Instance?["chpw.err.mismatch"] ?? "New passwords do not match.";
            return;
        }

        var userId = _userSessionContext.CurrentUser?.UserId;
        if (userId is null)
        {
            ErrorMessage = Localizer.Instance?["chpw.err.no_session"] ?? "No active session.";
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _authService.ChangePasswordAsync(userId, CurrentPassword, NewPassword, ct);
            if (!ok)
            {
                ErrorMessage = Localizer.Instance?["chpw.err.wrong_current"] ?? "Current password is incorrect.";
                return;
            }

            _logger.LogInformation("ChangePasswordViewModel: password changed for user {UserId}", userId);
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChangePasswordViewModel: unexpected error changing password");
            ErrorMessage = string.Format(Localizer.Instance?["lg.err.generic"] ?? "Error: {0}", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke();
}
