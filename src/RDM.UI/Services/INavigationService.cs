using Avalonia.Controls;
using System.Threading.Tasks;

namespace RDM.UI.Services;

/// <summary>
/// Implemented by a window's ViewModel when it needs a typed initialization parameter
/// before the window becomes visible.
/// </summary>
public interface IParameterizedViewModel<in T>
{
    Task InitializeAsync(T parameter);
}

/// <summary>
/// Implemented by ViewModels that hold unsaved state — checked by NavigationService
/// before CloseAllAsync to prompt the operator.
/// </summary>
public interface IHasUnsavedChanges
{
    bool HasUnsavedChanges { get; }
}

/// <summary>
/// Implemented by windows that accept a typed initialization parameter from NavigationService.
/// Called immediately after the window is created from DI, before it is shown.
/// </summary>
public interface IParameterizedWindow
{
    Task InitAsync(object parameter);
}

/// <summary>
/// Controls opening modal and non-modal windows.
/// Non-modal windows are tracked per-type so a second call focuses the existing instance.
/// </summary>
public interface INavigationService
{
    /// <summary>Shows a modal dialog, waits until it is closed, and returns the window instance.</summary>
    Task<TWindow> ShowModalAsync<TWindow>(object? parameter = null) where TWindow : Window;

    /// <summary>Opens a non-modal window or brings the already-open instance to the foreground.</summary>
    void OpenOrFocusWindow<TWindow>(object? parameter = null) where TWindow : Window;

    /// <summary>Closes all tracked non-modal windows. Called during shutdown (step 0).</summary>
    Task CloseAllAsync();
}
