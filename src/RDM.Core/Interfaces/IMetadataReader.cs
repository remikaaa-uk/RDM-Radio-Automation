using RDM.Core.Models;

namespace RDM.Core.Interfaces;

public interface IMetadataReader
{
    MetadataReaderKind Kind { get; }
    Task<AssetMetadata?> TryReadAsync(string filePath, CancellationToken ct = default);
}
