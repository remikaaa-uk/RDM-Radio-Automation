using System.IO.Compression;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using Un4seen.Bass;

namespace RDM.Audio.Processing;

/// <summary>
/// Generates a compressed waveform from an audio file using BASS_STREAM_DECODE.
/// Sequential single-pass read — no seeks.
/// Produces WaveformConstants.NPoints min/max pairs, quantized to byte[0-255] and GZip-compressed.
/// Format version: WaveformConstants.Version.
/// </summary>
public sealed class WaveformGenerator : IWaveformGenerator
{
    private const int ChunkSamples = 8192;
    private const int ChunkBytes   = ChunkSamples * sizeof(float);

    public Task<byte[]> GenerateAsync(string audioFilePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        int stream = Bass.BASS_StreamCreateFile(audioFilePath, 0, 0,
            BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);

        if (stream == 0)
            throw new InvalidOperationException(
                $"BASS_StreamCreateFile failed ({Bass.BASS_ErrorGetCode()}): '{audioFilePath}'");

        try
        {
            long totalBytes = Bass.BASS_ChannelGetLength(stream, BASSMode.BASS_POS_BYTE);
            if (totalBytes <= 0)
                throw new InvalidOperationException(
                    $"Cannot determine file length for waveform: '{audioFilePath}'");

            float[] peaks = ScanPeaks(stream, totalBytes, ct);
            return Task.FromResult(Compress(peaks));
        }
        finally
        {
            Bass.BASS_StreamFree(stream);
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static float[] ScanPeaks(int stream, long totalBytes, CancellationToken ct)
    {
        long bytesPerWindow = Math.Max(totalBytes / WaveformConstants.NPoints, 1);

        float[] winMin = new float[WaveformConstants.NPoints]; // default 0f
        float[] winMax = new float[WaveformConstants.NPoints];

        float[] buf     = new float[ChunkSamples];
        long    bytePos = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int read = Bass.BASS_ChannelGetData(stream, buf, ChunkBytes);
            if (read <= 0) break;

            int samples = read / sizeof(float);
            for (int j = 0; j < samples; j++)
            {
                int win = (int)Math.Min(
                    (bytePos + (long)j * sizeof(float)) / bytesPerWindow,
                    WaveformConstants.NPoints - 1);

                float v = buf[j];
                if (v < winMin[win]) winMin[win] = v;
                if (v > winMax[win]) winMax[win] = v;
            }

            bytePos += read;
        }

        float[] data = new float[WaveformConstants.NPoints * 2];
        for (int i = 0; i < WaveformConstants.NPoints; i++)
        {
            data[i * 2]     = winMin[i];
            data[i * 2 + 1] = winMax[i];
        }
        return data;
    }

    private static byte[] Compress(float[] peaks)
    {
        int    n         = peaks.Length; // NPoints * 2
        byte[] quantized = new byte[n];
        for (int i = 0; i < n; i++)
            quantized[i] = FloatToByte(peaks[i]);

        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(quantized, 0, quantized.Length);
        return ms.ToArray();
    }

    private static byte FloatToByte(float v)
        => (byte)Math.Clamp((int)((v + 1.0f) * 127.5f), 0, 255);
}
