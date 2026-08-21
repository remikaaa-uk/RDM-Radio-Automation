using System;

namespace RDM.UI.Services;

public interface ILibrarySelectionService
{
    string? SelectedAssetId { get; }
    bool    HasSelection    { get; }
    void    SetSelection(string? assetId);
}

public sealed class LibrarySelectionService : ILibrarySelectionService
{
    public string? SelectedAssetId { get; private set; }
    public bool    HasSelection    => SelectedAssetId is not null;

    public void SetSelection(string? assetId)
    {
        SelectedAssetId = assetId;
    }
}
