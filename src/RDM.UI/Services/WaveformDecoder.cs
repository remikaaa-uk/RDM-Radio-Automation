using System.IO.Compression;

namespace RDM.UI.Services;

public static class WaveformDecoder
{
    /// <summary>
    /// Decompresses GZip-compressed, byte-quantized waveform data from the API
    /// into a float[] peak array (values in [-1, 1]).
    /// Returns null on error or empty input.
    /// </summary>
    public static float[]? Decode(byte[]? compressed)
    {
        if (compressed is null || compressed.Length == 0) return null;
        try
        {
            using var input  = new MemoryStream(compressed);
            using var gz     = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            byte[] raw    = output.ToArray();
            float[] peaks = new float[raw.Length];
            for (int i = 0; i < raw.Length; i++)
                peaks[i] = raw[i] / 127.5f - 1.0f;
            return peaks;
        }
        catch
        {
            return null;
        }
    }
}
