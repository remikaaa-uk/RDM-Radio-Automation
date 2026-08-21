using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;

namespace RDM.Audio.Processing;

public sealed class BpmAnalyzer : IBpmAnalyzer
{
    private readonly ILogger<BpmAnalyzer> _logger;

    public BpmAnalyzer(ILogger<BpmAnalyzer> logger) => _logger = logger;

    public Task<decimal?> AnalyzeAsync(string filePath, CancellationToken ct = default)
    {
        int stream = Bass.BASS_StreamCreateFile(filePath, 0, 0, BASSFlag.BASS_STREAM_DECODE);
        if (stream == 0)
        {
            _logger.LogDebug("Cannot open {Path} for BPM analysis: {Error}", filePath, Bass.BASS_ErrorGetCode());
            return Task.FromResult<decimal?>(null);
        }

        // Get explicit duration — passing endSec=0 to BASS_FX_BPM_DecodeGet is treated
        // as "0 seconds" in some BASS_FX builds, not "end of file". Pass the real length.
        long byteLen  = Bass.BASS_ChannelGetLength(stream, BASSMode.BASS_POS_BYTE);
        double endSec = byteLen > 0
            ? Bass.BASS_ChannelBytes2Seconds(stream, byteLen)
            : 0;

        _logger.LogDebug("BPM analysis: file={Path} duration={Dur:F1}s", filePath, endSec);

        // minMaxBPM: LOWORD=min(60), HIWORD=max(160)
        // Narrower range prevents BPM doubling (e.g. 197 instead of 99).
        // Covers ballads (60) through fast dance (160).
        int minMaxBpm = (160 << 16) | 60;

        // BASS_FX_FREESOURCE frees the decode stream automatically after analysis
        float bpm = BassFx.BASS_FX_BPM_DecodeGet(
            stream, 0, endSec, minMaxBpm,
            BASSFXBpm.BASS_FX_BPM_DEFAULT | BASSFXBpm.BASS_FX_FREESOURCE,
            null, IntPtr.Zero);

        if (bpm <= 0)
        {
            _logger.LogDebug("BPM detection returned {Bpm} (BASS error={BassErr}) for {Path}", bpm, Bass.BASS_ErrorGetCode(), filePath);
            Bass.BASS_StreamFree(stream); // free in case FREESOURCE didn't work on error
            return Task.FromResult<decimal?>(null);
        }

        _logger.LogDebug("BPM detected: {Bpm} for {Path}", bpm, filePath);
        return Task.FromResult<decimal?>((decimal)Math.Round(bpm));
    }
}
