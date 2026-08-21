using Avalonia.Input;
using System;
using System.Threading.Tasks;

namespace RDM.UI.Services;

/// <summary>
/// Maps physical key combinations to abstract action tokens defined in hotkeys.json.
/// MainWindow.OnKeyDown calls HandleKeyDown; subscribers receive the action token.
/// </summary>
public interface IHotkeyManagerService
{
    /// Fired on the UI thread when a registered hotkey is pressed.
    event Action<string>? ActionTriggered;

    /// Loads bindings from hotkeys.json. Call once at startup before showing any window.
    Task LoadAsync(string path = "hotkeys.json");

    /// Returns true and fires ActionTriggered if the key combo is mapped; false otherwise.
    bool HandleKeyDown(Key key, KeyModifiers modifiers);
}
