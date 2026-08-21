namespace RDM.Core.Interfaces;

public interface IKeyboardInputDriver
{
    void HandleKeyDown(string keyCode, bool altPressed, bool ctrlPressed, bool shiftPressed);
}
