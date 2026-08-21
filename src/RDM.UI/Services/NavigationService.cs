using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.Services;

public sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _services;

    // Tracks open non-modal windows per Window type.
    private readonly ConcurrentDictionary<Type, Window> _openWindows = new();

    public NavigationService(IServiceProvider services)
    {
        _services = services;
    }

    // ── Modal ─────────────────────────────────────────────────────────────────

    public async Task<TWindow> ShowModalAsync<TWindow>(object? parameter = null) where TWindow : Window
    {
        var window = _services.GetRequiredService<TWindow>();
        await InitializeWindowAsync(window, parameter);

        var owner = GetOwnerWindow();
        if (owner is not null)
            await window.ShowDialog(owner);
        else
            window.Show();

        return window;
    }

    // ── Non-modal ─────────────────────────────────────────────────────────────

    public void OpenOrFocusWindow<TWindow>(object? parameter = null) where TWindow : Window
    {
        var type = typeof(TWindow);

        if (_openWindows.TryGetValue(type, out var existing) && existing.IsVisible)
        {
            Dispatcher.UIThread.Post(() =>
            {
                existing.Activate();
                existing.Focus();
            });
            return;
        }

        var window = _services.GetRequiredService<TWindow>();
        window.Closed += (_, _) => _openWindows.TryRemove(type, out _);
        _openWindows[type] = window;

        _ = InitializeWindowAsync(window, parameter).ContinueWith(_ =>
        {
            Dispatcher.UIThread.Post(() => window.Show());
        });
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    public async Task CloseAllAsync()
    {
        foreach (var (type, window) in _openWindows)
        {
            if (window.DataContext is IHasUnsavedChanges vm && vm.HasUnsavedChanges)
            {
                // Non-blocking: close anyway during shutdown; operator was warned by MainViewModel.
                _openWindows.TryRemove(type, out _);
            }

            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }

        _openWindows.Clear();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task InitializeWindowAsync(Window window, object? parameter)
    {
        if (parameter is null) return;
        if (window is IParameterizedWindow pw)
            await pw.InitAsync(parameter);
    }

    private static Window? GetOwnerWindow()
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        if (lifetime is null) return null;

        return lifetime.Windows.FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? lifetime.MainWindow;
    }
}
