using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthService            _authService;
    private readonly StudioContext           _studioContext;
    private readonly UserSessionContext      _userSessionContext;
    private readonly SettingsConfigService   _configService;
    private readonly ILogger<LoginViewModel> _logger;

    // ── Observable properties ─────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private bool _isLoggingIn;

    [ObservableProperty]
    private bool _rememberMe;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    // ── Public events ─────────────────────────────────────────────────────────

    /// <summary>Raised after AuthService confirms credentials and UserSessionContext is updated.</summary>
    public event Action? LoginSuccessful;

    /// <summary>Raised when operator cancels. Caller shuts down the application.</summary>
    public event Action? LoginCancelled;

    // ── Constructor ───────────────────────────────────────────────────────────

    public LoginViewModel(
        IAuthService            authService,
        StudioContext           studioContext,
        UserSessionContext      userSessionContext,
        SettingsConfigService   configService,
        ILogger<LoginViewModel> logger)
    {
        _authService        = authService;
        _studioContext      = studioContext;
        _userSessionContext = userSessionContext;
        _configService      = configService;
        _logger             = logger;

        // Pre-fill username if saved credentials exist
        _ = LoadSavedCredentialsAsync();
    }

    private async Task LoadSavedCredentialsAsync()
    {
        try
        {
            var cfg = await _configService.LoadAsync();
            var saved = cfg["auto_login"];
            if (saved is null) return;
            Username   = saved["username"]?.GetValue<string>() ?? "";
            RememberMe = !string.IsNullOrEmpty(Username);
        }
        catch { /* ignore — non-critical */ }
    }

    private async Task SaveCredentialsAsync(string username, string password)
    {
        try
        {
            var cfg = await _configService.LoadAsync();
            cfg["auto_login"] = new System.Text.Json.Nodes.JsonObject
            {
                ["username"] = username,
                ["password"] = password
            };
            await _configService.SaveAsync(cfg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save auto-login credentials");
        }
    }

    private async Task ClearSavedCredentialsAsync()
    {
        try
        {
            var cfg = await _configService.LoadAsync();
            cfg.Remove("auto_login");
            await _configService.SaveAsync(cfg);
        }
        catch { /* ignore */ }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private bool CanLogin() => !IsLoggingIn && !string.IsNullOrWhiteSpace(Username);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync(CancellationToken ct)
    {
        IsLoggingIn  = true;
        ErrorMessage = "";

        try
        {
            var user = await _authService.AuthenticateAsync(
                _studioContext.StudioId, Username, Password, ct);

            if (user is null)
            {
                ErrorMessage = Localizer.Instance?["lg.err.invalid_credentials"] ?? "Invalid username or password.";
                return;
            }

            _userSessionContext.Login(user);

            _logger.LogInformation(
                "User '{Username}' logged in (role={Role})", user.Username, user.Role);

            if (RememberMe)
                await SaveCredentialsAsync(Username, Password);
            else
                await ClearSavedCredentialsAsync();

            // Credentials are exposed via LoginWindow.SuccessCredentials for
            // ApiClientService.SetCredentials() wiring in UI-001E.
            LoginSuccessful?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = Localizer.Instance?["lg.err.cancelled"] ?? "Login cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoginViewModel: unexpected error during authentication");
            ErrorMessage = string.Format(Localizer.Instance?["lg.err.generic"] ?? "Error: {0}", ex.Message);
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private void Cancel() => LoginCancelled?.Invoke();
}
