namespace RDM.Core.Services;

/// <summary>
/// Singleton holding the identity of the studio running on this machine.
/// Populated by DatabaseBootstrapper at startup, before any hosted services start.
/// </summary>
public sealed class StudioContext
{
    private string? _studioId;

    public string StudioId => _studioId
        ?? throw new InvalidOperationException(
            "StudioContext is not initialized. Call Initialize(studioId) during startup.");

    public bool IsInitialized => _studioId is not null;

    public void Initialize(string studioId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(studioId);
        _studioId = studioId;
    }
}
