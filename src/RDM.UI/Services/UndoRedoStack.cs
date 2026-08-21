using System.Collections.Generic;

namespace RDM.UI.Services;

/// <summary>
/// Generic undo/redo stack.
/// Push() records a new state; Undo() rolls back; Redo() re-applies.
/// </summary>
public sealed class UndoRedoStack<T>
{
    private readonly Stack<T> _undoStack = new();
    private readonly Stack<T> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>Records the current state before a change.</summary>
    public void Push(T state)
    {
        _undoStack.Push(state);
        _redoStack.Clear();
    }

    /// <summary>Returns the previous state and pushes the current state onto the redo stack.</summary>
    public T Undo(T currentState)
    {
        var previous = _undoStack.Pop();
        _redoStack.Push(currentState);
        return previous;
    }

    /// <summary>Returns the next (redo) state and pushes the current state onto the undo stack.</summary>
    public T Redo(T currentState)
    {
        var next = _redoStack.Pop();
        _undoStack.Push(currentState);
        return next;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }
}
