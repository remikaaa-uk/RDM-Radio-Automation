using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.Enums;
using RDM.UI.Localization;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

/// <summary>Request coming back from the "add user" dialog.</summary>
public sealed record NewUserRequest(string Username, UserRole Role, string Password);

/// <summary>
/// One account row. Role/Enabled are two-way bound (ComboBox/CheckBox); the parent view model is
/// notified through the *ChangeRequested events and either persists the change or reverts it
/// quietly — same revert-on-reject shape as EncoderProfileRowViewModel.IsArmed in StreamingViewModel.
/// </summary>
public sealed partial class UserRowViewModel : ObservableObject
{
    public User User { get; private set; }

    public string UserId   => User.UserId;
    public string Username => User.Username;

    public string LastLoginText => User.LastLoginAt.HasValue
        ? User.LastLoginAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
        : "—";

    [ObservableProperty] private UserRole _role;
    [ObservableProperty] private bool     _enabled;

    public event Action<UserRowViewModel, UserRole>? RoleChangeRequested;
    public event Action<UserRowViewModel, bool>?     EnabledChangeRequested;

    private bool _suppressEcho;

    public UserRowViewModel(User user)
    {
        User     = user;
        _role    = user.Role;
        _enabled = user.Enabled;
    }

    partial void OnRoleChanged(UserRole value)
    {
        if (_suppressEcho) return;
        RoleChangeRequested?.Invoke(this, value);
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_suppressEcho) return;
        EnabledChangeRequested?.Invoke(this, value);
    }

    /// Puts the control back where it was after the parent rejected the change (e.g. last admin guard).
    public void SetRoleQuietly(UserRole role)
    {
        _suppressEcho = true;
        Role = role;
        _suppressEcho = false;
    }

    public void SetEnabledQuietly(bool enabled)
    {
        _suppressEcho = true;
        Enabled = enabled;
        _suppressEcho = false;
    }
}

/// <summary>
/// "Użytkownicy" tab in Settings — Admin-only account management (the whole Settings window already
/// requires Admin to open, per UserSessionContext.CanAccessSettings). Talks to IUserRepository/
/// IAuthService directly: RDM.UI has its own composition root with both registered, same as every
/// other tab on this window — no API round-trip needed for a local desktop action.
/// </summary>
public sealed partial class UsersViewModel : ObservableObject
{
    private readonly IUserRepository        _userRepo;
    private readonly IAuthService           _authService;
    private readonly StudioContext          _studioContext;
    private readonly ILogger<UsersViewModel> _logger;

    public ObservableCollection<UserRowViewModel> Users { get; } = new();

    /// <summary>Backs the per-row role ComboBox in the Users tab.</summary>
    public IReadOnlyList<UserRole> Roles { get; } = [UserRole.Operator, UserRole.Admin];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageBrush))]
    private bool _messageIsError;

    public IBrush MessageBrush => MessageIsError ? Brush.Parse("#FF6B6B") : Brush.Parse("#8888A0");

    /// Set by the window: opens the "new user" dialog. Returns null if the operator cancelled.
    public Func<Task<NewUserRequest?>>? AddUserAsync { get; set; }

    /// Set by the window: opens the reset-password dialog for an existing user. Returns the new
    /// password, or null if the operator cancelled.
    public Func<UserRowViewModel, Task<string?>>? ResetPasswordDialogAsync { get; set; }

    public UsersViewModel(
        IUserRepository         userRepo,
        IAuthService            authService,
        StudioContext           studioContext,
        ILogger<UsersViewModel> logger)
    {
        _userRepo      = userRepo;
        _authService   = authService;
        _studioContext = studioContext;
        _logger        = logger;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var users = await _userRepo.GetByStudioAsync(_studioContext.StudioId);

            foreach (var stale in Users)
            {
                stale.RoleChangeRequested    -= OnRoleChangeRequested;
                stale.EnabledChangeRequested -= OnEnabledChangeRequested;
            }
            Users.Clear();

            foreach (var u in users.OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase))
            {
                var row = new UserRowViewModel(u);
                row.RoleChangeRequested    += OnRoleChangeRequested;
                row.EnabledChangeRequested += OnEnabledChangeRequested;
                Users.Add(row);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (AddUserAsync is null) return;

        var request = await AddUserAsync();
        if (request is null) return;

        ClearMessage();
        try
        {
            await _authService.CreateUserAsync(_studioContext.StudioId, request.Username, request.Password, request.Role);
            await LoadAsync();
        }
        catch (InvalidOperationException ex)
        {
            // Username already taken — AuthService's own check-then-insert guard.
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            // The check-then-insert in AuthService.CreateUserAsync still leaves a narrow race against
            // the DB's own unique constraint (two admins adding the same username at once) — that
            // surfaces here as a plain DB exception, not InvalidOperationException.
            _logger.LogError(ex, "UsersViewModel.Add: unexpected error creating user '{Username}'", request.Username);
            ShowError(string.Format(Localizer.Instance?["lg.err.generic"] ?? "Error: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private async Task ResetPasswordAsync(UserRowViewModel? row)
    {
        if (row is null || ResetPasswordDialogAsync is null) return;

        var newPassword = await ResetPasswordDialogAsync(row);
        if (string.IsNullOrEmpty(newPassword)) return;

        ClearMessage();
        try
        {
            await _authService.ResetPasswordAsync(row.UserId, newPassword);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UsersViewModel.ResetPassword: unexpected error for user {UserId}", row.UserId);
            ShowError(string.Format(Localizer.Instance?["lg.err.generic"] ?? "Error: {0}", ex.Message));
        }
    }

    /// <summary>
    /// The ComboBox binding has already flipped row.Role to the attempted value by the time this
    /// fires, so "would this leave zero enabled admins" is answered by looking at the collection as
    /// it stands right now — no separate before/after bookkeeping needed.
    /// </summary>
    private async void OnRoleChangeRequested(UserRowViewModel row, UserRole role)
    {
        ClearMessage();

        if (role != UserRole.Admin && !Users.Any(u => u.Role == UserRole.Admin && u.Enabled))
        {
            row.SetRoleQuietly(UserRole.Admin);
            ShowError(Localizer.Instance?["users.err.last_admin_demote"]
                      ?? "Cannot demote the last active Administrator.");
            return;
        }

        await _userRepo.UpdateRoleAsync(row.UserId, role);
    }

    private async void OnEnabledChangeRequested(UserRowViewModel row, bool enabled)
    {
        ClearMessage();

        if (!enabled && !Users.Any(u => u.Role == UserRole.Admin && u.Enabled))
        {
            row.SetEnabledQuietly(true);
            ShowError(Localizer.Instance?["users.err.last_admin_disable"]
                      ?? "Cannot disable the last active Administrator.");
            return;
        }

        await _userRepo.UpdateEnabledAsync(row.UserId, enabled);
    }

    private void ShowError(string text)
    {
        Message        = text;
        MessageIsError = true;
        _logger.LogWarning("Users: {Message}", text);
    }

    private void ClearMessage()
    {
        Message        = null;
        MessageIsError = false;
    }
}
