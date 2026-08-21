using RDM.Core.Models;

namespace RDM.Core.Interfaces;

public interface IImportPipeline
{
    Task<ImportResult> ImportAsync(string filePath, ImportReaderFlags? flags = null, string? formatId = null, string? subcategoryId = null, CancellationToken ct = default);

    /// Imports a pre-recorded voice track WAV file, skipping the metadata reader
    /// chain and setting AssetType = Voicetrack with the provided title.
    Task<ImportResult> ImportVoiceTrackAsync(string filePath, string title, CancellationToken ct = default);
}
