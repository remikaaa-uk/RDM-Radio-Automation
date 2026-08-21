using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;
using RDM.Shared.Enums;

namespace RDM.Core.Services;

public sealed class ImportPipeline : IImportPipeline
{
    private readonly IReadOnlyList<IMetadataReader> _readers;
    private readonly IAssetRepository              _assetRepository;
    private readonly IRdmFileWriter                _rdmFileWriter;
    private readonly WaveformQueue                 _waveformQueue;
    private readonly LoudnessQueue                 _loudnessQueue;
    private readonly IEventBus                     _eventBus;
    private readonly ImportPipelineSettings        _settings;
    private readonly IChecksumService              _checksumService;

    public ImportPipeline(
        IEnumerable<IMetadataReader> readers,
        IAssetRepository             assetRepository,
        IRdmFileWriter               rdmFileWriter,
        WaveformQueue                waveformQueue,
        LoudnessQueue                loudnessQueue,
        IEventBus                    eventBus,
        ImportPipelineSettings       settings,
        IChecksumService             checksumService)
    {
        _readers         = readers.ToList(); // materialises DI-order priority
        _assetRepository = assetRepository;
        _rdmFileWriter   = rdmFileWriter;
        _waveformQueue   = waveformQueue;
        _loudnessQueue   = loudnessQueue;
        _eventBus        = eventBus;
        _settings        = settings;
        _checksumService = checksumService;
    }

    public async Task<ImportResult> ImportAsync(string filePath, ImportReaderFlags? flags = null, string? formatId = null, string? subcategoryId = null, CancellationToken ct = default)
    {
        // 1. Checksum — fast duplicate path (avoids full metadata scan for known files)
        string checksum = await _checksumService.ComputeAsync(filePath, ct);

        var existing = await _assetRepository.GetByChecksumAsync(checksum, ct);
        if (existing is not null)
            return new ImportResult.Duplicate(existing.AssetId);

        // 2. Metadata chain — merge in registration order (higher priority wins per field)
        var activeFlags = flags ?? new ImportReaderFlags();
        var merged = new AssetMetadata();
        foreach (var reader in _readers)
        {
            if (!IsReaderEnabled(reader.Kind, activeFlags)) continue;
            try
            {
                var meta = await reader.TryReadAsync(filePath, ct);
                if (meta is null) continue;
                merged = merged.MergeWith(meta);
            }
            catch { /* reader failure is non-fatal — next reader in chain */ }
        }

        // 3. Build Asset entity
        string title    = merged.Title ?? Path.GetFileNameWithoutExtension(filePath);
        uint durationMs = merged.DurationMs ?? 0;
        string assetId  = Guid.NewGuid().ToString();
        var now         = DateTime.UtcNow;

        string? imagePath = null;
        if (merged.PictureBytes is { Length: > 0 })
        {
            try
            {
                Directory.CreateDirectory(_settings.ImageCachePath);
                string ext = ".jpg";
                if (merged.PictureMimeType?.Contains("png", StringComparison.OrdinalIgnoreCase) == true)
                    ext = ".png";
                else if (merged.PictureMimeType?.Contains("gif", StringComparison.OrdinalIgnoreCase) == true)
                    ext = ".gif";
                else if (merged.PictureMimeType?.Contains("bmp", StringComparison.OrdinalIgnoreCase) == true)
                    ext = ".bmp";

                string targetPath = Path.Combine(_settings.ImageCachePath, $"{assetId}{ext}");
                await File.WriteAllBytesAsync(targetPath, merged.PictureBytes, ct);
                imagePath = targetPath;
            }
            catch
            {
                // Non-fatal if picture save fails
            }
        }

        var asset = new Asset
        {
            AssetId       = assetId,
            AssetType     = AssetType.Track, // default; operator changes in Track Manager
            FormatId      = formatId,
            SubcategoryId = subcategoryId,
            Title         = title,
            Artist        = merged.Artist,
            Album         = merged.Album,
            DurationMs    = durationMs,
            Checksum      = checksum,
            RdmFilePath   = filePath,        // audio file path — PlaylistEngine reads this for playback
            ImagePath     = imagePath,
            Bpm           = merged.Bpm,
            Year          = SanitizeYear(merged.Year),
            Mood          = merged.Mood,
            Language      = merged.Language,
            Genre         = merged.Genre,
            Comments      = merged.Comments,
            CueStart     = merged.CueStart,
            CueIntro     = merged.CueIntro,
            CueRamp2     = merged.CueRamp2,
            CueRamp3     = merged.CueRamp3,
            CueOutro     = merged.CueOutro,
            CueStartNext = merged.CueStartNext,
            CueFadeOut   = merged.CueFadeOut,
            CueFadeEnd   = merged.CueFadeEnd,
            CueEnd       = merged.CueEnd,
            CueHookIn    = merged.CueHookIn,
            CueHookFade  = merged.CueHookFade,
            CueHookOut   = merged.CueHookOut,
            CueLoopIn    = merged.CueLoopIn,
            CueLoopOut   = merged.CueLoopOut,
            CueAnchor    = merged.CueAnchor,
            Status      = AssetStatus.Active,
            IsDamaged   = durationMs == 0,
            PlayCount   = 0,
            CreatedAt   = now,
            UpdatedAt   = now
        };

        // 4. Persist — DB UNIQUE(checksum) is the final guard against race conditions
        try
        {
            await _assetRepository.CreateAsync(asset, ct);
        }
        catch (DuplicateAssetException)
        {
            // Another import thread won the race — look up the winner's id
            var winner = await _assetRepository.GetByChecksumAsync(checksum, ct);
            return new ImportResult.Duplicate(winner?.AssetId ?? string.Empty);
        }

        // 5. Write .rdm sidecar next to the audio file
        try { await _rdmFileWriter.WriteAsync(asset, ct); }
        catch { /* .rdm write is non-fatal */ }

        // 6. Enqueue background tasks
        await _waveformQueue.Writer.WriteAsync(new WaveformTask(assetId, filePath), ct);
        await _loudnessQueue.Writer.WriteAsync(new LoudnessTask(assetId, filePath), ct);

        // 7. Domain event
        await _eventBus.PublishAsync(new AssetCreatedEvent(assetId, title, merged.Artist), ct);

        return new ImportResult.Success(asset);
    }

    public async Task<ImportResult> ImportVoiceTrackAsync(string filePath, string title, CancellationToken ct = default)
    {
        string checksum = await _checksumService.ComputeAsync(filePath, ct);
        var existing = await _assetRepository.GetByChecksumAsync(checksum, ct);
        if (existing is not null)
            return new ImportResult.Duplicate(existing.AssetId);

        string assetId = Guid.NewGuid().ToString();
        var now        = DateTime.UtcNow;

        // Read duration via TagLib / ATL — reuse the same readers chain but only extract duration
        uint durationMs = 0;
        foreach (var reader in _readers)
        {
            try
            {
                var meta = await reader.TryReadAsync(filePath, ct);
                if (meta?.DurationMs is > 0) { durationMs = meta.DurationMs.Value; break; }
            }
            catch { /* non-fatal */ }
        }

        var asset = new Asset
        {
            AssetId     = assetId,
            AssetType   = AssetType.Voicetrack,
            Title       = title,
            Artist      = null,
            DurationMs  = durationMs,
            Checksum    = checksum,
            RdmFilePath = filePath,
            Status      = AssetStatus.Active,
            IsDamaged   = durationMs == 0,
            PlayCount   = 0,
            CreatedAt   = now,
            UpdatedAt   = now
        };

        try { await _assetRepository.CreateAsync(asset, ct); }
        catch (DuplicateAssetException)
        {
            var winner = await _assetRepository.GetByChecksumAsync(checksum, ct);
            return new ImportResult.Duplicate(winner?.AssetId ?? string.Empty);
        }

        try { await _rdmFileWriter.WriteAsync(asset, ct); }
        catch { /* non-fatal */ }

        await _waveformQueue.Writer.WriteAsync(new WaveformTask(assetId, filePath), ct);
        await _loudnessQueue.Writer.WriteAsync(new LoudnessTask(assetId, filePath), ct);

        await _eventBus.PublishAsync(new AssetCreatedEvent(assetId, title, null), ct);

        return new ImportResult.Success(asset);
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static bool IsReaderEnabled(MetadataReaderKind kind, ImportReaderFlags flags) => kind switch
    {
        MetadataReaderKind.Rdm  => flags.ReadRdm,
        MetadataReaderKind.Mmd  => flags.ReadMmd,
        MetadataReaderKind.Wfrm => flags.ReadWfrm,
        MetadataReaderKind.Id3  => flags.ReadId3,
        // AutoCue (Silence Remover) is always in the chain; it self-gates on the
        // SilenceRemoverEnabled flag in audio settings.
        MetadataReaderKind.AutoCue => true,
        _                          => true
    };

    // MySQL YEAR accepts 1901–2155 (plus 0). ID3 tags frequently carry garbage
    // years (0, misparsed dates, values > 2155) that make the INSERT throw
    // "Out of range value for column 'year'" and fail the whole track import.
    // Anything outside the storable range is dropped to null.
    private static int? SanitizeYear(int? year)
        => year is >= 1901 and <= 2155 ? year : null;
}
