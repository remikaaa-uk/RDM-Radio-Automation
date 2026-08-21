using RDM.Core.Interfaces;
using RDM.Core.Models;

namespace RDM.Infrastructure.FileSystem;

/// <summary>
/// Reads cue markers from a RadioDJ .wfrm binary sidecar (track.mp3.wfrm).
/// Format: int32 version | 7-bit-length-prefixed string path | int32 pointCount |
///         int32 channels | 2 unknown bytes | 8 unknown bytes | 2×int32 unknown |
///         int32 markerCount | markers (1-byte name len + UTF-8 name + int64 byte-offset) |
///         waveform peak data (ignored here).
/// Cue positions are byte offsets in decoded PCM (16-bit samples).
/// Assumed sample rate: 44100 Hz (stereo → divisor 176400).
/// </summary>
public sealed class WfrmFileReader : IMetadataReader
{
    private const int DefaultSampleRate = 44_100;
    private const int Channels          = 2;
    private const int BytesPerSample    = 4; // RadioDJ stores BASS_SAMPLE_FLOAT (float32) offsets
    private const int Divisor           = DefaultSampleRate * Channels * BytesPerSample; // 352 800

    public MetadataReaderKind Kind => MetadataReaderKind.Wfrm;

    public Task<AssetMetadata?> TryReadAsync(string filePath, CancellationToken ct = default)
    {
        // RadioDJ keeps the original extension: "track.mp3.wfrm"
        string wfrmPath = filePath + ".wfrm";
        if (!File.Exists(wfrmPath)) return Task.FromResult<AssetMetadata?>(null);

        try
        {
            using var fs     = File.OpenRead(wfrmPath);
            using var reader = new BinaryReader(fs);

            int version = reader.ReadInt32();
            if (version != 1) return Task.FromResult<AssetMetadata?>(null);

            // 7-bit-length-prefixed string (BinaryWriter.Write(string))
            _ = reader.ReadString();

            _ = reader.ReadInt32(); // point count
            _ = reader.ReadInt32(); // channels (used for future 48kHz support if exposed)
            _ = reader.ReadByte();  // unknown
            _ = reader.ReadByte();  // unknown
            reader.ReadBytes(8);    // unknown (likely double)
            _ = reader.ReadInt32(); // unknown
            reader.ReadBytes(2);    // unknown (2 bytes, not int32)

            int markerCount = reader.ReadInt32();

            double? cueStart     = null;
            double? cueIntro     = null;
            double? cueOutro     = null;
            double? cueStartNext = null;
            double? cueEnd       = null;
            double? cueHookIn    = null;
            double? cueHookOut   = null;
            double? cueLoopIn    = null;
            double? cueLoopOut   = null;
            double? cueFadeOut   = null;
            double? cueRamp2     = null;

            for (int i = 0; i < markerCount; i++)
            {
                int nameLen  = reader.ReadByte();
                string name  = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
                long   bytes = reader.ReadInt64();
                double secs  = bytes / (double)Divisor;

                switch (name.Trim().ToUpperInvariant())
                {
                    case "START":
                        cueStart     ??= secs; break;
                    case "INTRO":
                        cueIntro     ??= secs; break;
                    case "OUTRO":
                        cueOutro     ??= secs; break;
                    case "NEXT START":
                        cueStartNext ??= secs; break;
                    case "END":
                        cueEnd       ??= secs; break;
                    case "HOOK IN":
                    case "HOOK":
                        cueHookIn    ??= secs; break;
                    case "HOOK OUT":
                        cueHookOut   ??= secs; break;
                    case "LOOP IN":
                        cueLoopIn    ??= secs; break;
                    case "LOOP OUT":
                        cueLoopOut   ??= secs; break;
                    case "FADE":
                        cueFadeOut   ??= secs; break;
                    case "RAMP":
                        cueRamp2     ??= secs; break;
                }
            }

            return Task.FromResult<AssetMetadata?>(new AssetMetadata
            {
                CueStart     = cueStart,
                CueIntro     = cueIntro,
                CueOutro     = cueOutro,
                CueStartNext = cueStartNext,
                CueEnd       = cueEnd,
                CueHookIn    = cueHookIn,
                CueHookOut   = cueHookOut,
                CueLoopIn    = cueLoopIn,
                CueLoopOut   = cueLoopOut,
                CueFadeOut   = cueFadeOut,
                CueRamp2     = cueRamp2,
            });
        }
        catch
        {
            return Task.FromResult<AssetMetadata?>(null);
        }
    }
}
