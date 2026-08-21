using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Services;
using Un4seen.Bass;

namespace RDM.Audio.Processing;

/// <summary>
/// Last-resort cue point source: detects START / NEXT START / END from a decoded
/// audio stream using the dBFS thresholds configured under "Silence Remover" in
/// the studio audio settings.
///   START      = first chunk ≥ Start threshold
///   NEXT START = last  chunk ≥ Mix   threshold (loud music; mix-in point)
///   END        = last  chunk ≥ End   threshold
/// Runs only when Silence Remover is enabled; otherwise returns null (no auto-cue).
/// Thresholds come exclusively from settings — there are no hardcoded fallbacks.
/// As the last reader in the chain it never overwrites cue values already provided
/// by .rdm / .mmd / .wfrm / ID3.
/// </summary>
public sealed class AutoCueDetector : IMetadataReader
{
    private readonly IAudioSettingsRepository _settingsRepository;
    private readonly StudioContext            _studioContext;

    public AutoCueDetector(IAudioSettingsRepository settingsRepository, StudioContext studioContext)
    {
        _settingsRepository = settingsRepository;
        _studioContext      = studioContext;
    }

    public MetadataReaderKind Kind => MetadataReaderKind.AutoCue;

    private const int ChunkSamples = 2048;
    private const int ChunkBytes   = ChunkSamples * sizeof(float);

    public async Task<AssetMetadata?> TryReadAsync(string filePath, CancellationToken ct = default)
    {
        // Silence Remover settings are the single source of truth for auto-cue.
        var settings = await _settingsRepository.GetByStudioAsync(_studioContext.StudioId, ct);
        if (settings is null || !settings.SilenceRemoverEnabled)
            return null;

        float startTh = AutoCueCalculator.DbToLinear((double)settings.SilenceStartThresholdDb);
        float mixTh   = AutoCueCalculator.DbToLinear((double)settings.SilenceMixThresholdDb);
        float endTh   = AutoCueCalculator.DbToLinear((double)settings.SilenceEndThresholdDb);

        int stream = Bass.BASS_StreamCreateFile(filePath, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
        if (stream == 0) return null;

        try
        {
            long totalBytes = Bass.BASS_ChannelGetLength(stream, BASSMode.BASS_POS_BYTE);
            if (totalBytes <= 0) return null;

            uint startMs     = DetectForward(stream, totalBytes, startTh);
            uint endMs       = DetectBackward(stream, totalBytes, endTh, fallback: BytesToMs(stream, totalBytes));
            uint nextStartMs = DetectBackward(stream, totalBytes, mixTh, fallback: endMs);

            if (endMs <= startMs) return null;

            // NEXT START must not exceed END
            if (nextStartMs > endMs) nextStartMs = endMs;

            return new AssetMetadata
            {
                CueStart     = startMs     / 1000.0,
                CueEnd       = endMs       / 1000.0,
                CueStartNext = nextStartMs / 1000.0,
            };
        }
        finally
        {
            Bass.BASS_StreamFree(stream);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static uint DetectForward(int stream, long totalBytes, float threshold)
    {
        Bass.BASS_ChannelSetPosition(stream, 0, BASSMode.BASS_POS_BYTE);
        float[] buf = new float[ChunkSamples];
        long pos = 0;

        while (pos < totalBytes)
        {
            long chunkStart = pos;
            int read = Bass.BASS_ChannelGetData(stream, buf, ChunkBytes);
            if (read <= 0) break;

            if (PeakAmplitude(buf, read / sizeof(float)) >= threshold)
                return BytesToMs(stream, chunkStart);

            pos += read;
        }
        return 0;
    }

    private static uint DetectBackward(int stream, long totalBytes, float threshold, uint fallback)
    {
        float[] buf = new float[ChunkSamples];
        long pos = totalBytes;

        while (pos > 0)
        {
            long chunkStart = Math.Max(0, pos - ChunkBytes);
            Bass.BASS_ChannelSetPosition(stream, chunkStart, BASSMode.BASS_POS_BYTE);

            int read = Bass.BASS_ChannelGetData(stream, buf, (int)(pos - chunkStart));
            if (read <= 0) break;

            if (PeakAmplitude(buf, read / sizeof(float)) >= threshold)
                return BytesToMs(stream, pos);

            pos = chunkStart;
        }

        return fallback;
    }

    private static float PeakAmplitude(float[] buf, int count)
    {
        float peak = 0f;
        for (int i = 0; i < count; i++) peak = Math.Max(peak, Math.Abs(buf[i]));
        return peak;
    }

    private static uint BytesToMs(int stream, long bytes) =>
        (uint)(Bass.BASS_ChannelBytes2Seconds(stream, bytes) * 1000);
}
