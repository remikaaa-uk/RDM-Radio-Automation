using System.ComponentModel;
using RDM.Core.Entities;
using RDM.Shared.Enums;

namespace RDM.Core.Services;

/// <summary>
/// Singleton that holds the currently authenticated user.
/// Implements INotifyPropertyChanged so Avalonia bindings re-evaluate permissions
/// (CanAccessSettings, IsLoggedIn) immediately after login/logout — no manual
/// UI refresh needed. (Bug 3 fix: volatile was insufficient for Avalonia binding.)
///
/// Thread safety: setter is guarded by _lock; PropertyChanged is fired outside
/// the lock to prevent deadlocks with the UI dispatcher.
/// </summary>
public sealed class UserSessionContext : INotifyPropertyChanged
{
    private User?        _currentUser;
    private readonly object _lock = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Public state ──────────────────────────────────────────────────────────

    public User? CurrentUser
    {
        get { lock (_lock) return _currentUser; }
    }

    public bool IsLoggedIn       => CurrentUser is not null;
    public bool CanAccessSettings => CurrentUser?.Role == UserRole.Admin;

    /// <summary>
    /// Studio the logged-in user belongs to.
    /// Throws if called before Login().
    /// </summary>
    public string StudioId => CurrentUser?.StudioId
        ?? throw new InvalidOperationException(
            "No user is logged in. Call Login() first.");

    // ── Mutations ─────────────────────────────────────────────────────────────

    public void Login(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_lock) { _currentUser = user; }

        // Notify outside the lock — Avalonia dispatcher may call back synchronously
        RaisePropertyChanged(nameof(CurrentUser));
        RaisePropertyChanged(nameof(IsLoggedIn));
        RaisePropertyChanged(nameof(CanAccessSettings));
        RaisePropertyChanged(nameof(StudioId));
    }

    public void Logout()
    {
        lock (_lock) { _currentUser = null; }

        RaisePropertyChanged(nameof(CurrentUser));
        RaisePropertyChanged(nameof(IsLoggedIn));
        RaisePropertyChanged(nameof(CanAccessSettings));
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
