using RDM.Shared.Enums;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Enc;
using Un4seen.Bass.AddOn.EncMp3;
using Un4seen.Bass.AddOn.EncOgg;
using Un4seen.Bass.AddOn.EncOpus;

namespace RDM.Audio.Engine;

/// <summary>
/// Everything format-specific about BASSenc in one place: which add-on DLL a format needs, what it
/// is called on the wire and on disk, how to start it, and what it cannot do.
///
/// Both consumers — <see cref="EncoderSession"/> (cast) and <see cref="RecordingSession"/> (file) —
/// differ only in the target, never in the format knowledge.
///
/// The one asymmetry worth knowing: <b>only MP3 can change the sample rate or channel count.</b>
/// LAME resamples and downmixes internally, so bassenc_mp3 exposes switches for both. The OGG and
/// OPUS add-ons document no such switch (verified against the un4seen BASS_Encode_OGG_Start and
/// BASS_Encode_OPUS_Start references), and BASSenc itself feeds the encoder the source channel's
/// data untouched. For those two formats the output therefore *is* the program bus format, and a
/// profile asking for something else is refused by <see cref="Unsupported"/> rather than silently
/// ignored.
/// </summary>
internal static class BassEncoderFormats
{
    /// <summary>What to encode. Sample rate null = follow the program bus (no resampling).</summary>
    internal readonly record struct Spec(
        EncoderFormat Format,
        int BitrateKbps,
        int? SampleRateHz,
        byte Channels);

    /// <summary>The add-on DLL a format needs, or null when this build cannot produce it at all.</summary>
    public static string? RequiredLibrary(EncoderFormat format) => format switch
    {
        EncoderFormat.Mp3  => "bassenc_mp3.dll",
        EncoderFormat.Ogg  => "bassenc_ogg.dll",
        EncoderFormat.Opus => "bassenc_opus.dll",
        _                  => null
    };

    /// <summary>
    /// MIME type advertised to the cast server. Opus is served inside an Ogg container, so Icecast
    /// expects the same audio/ogg type as Vorbis — the codec is identified by the stream headers.
    /// </summary>
    public static string ContentType(EncoderFormat format) => format switch
    {
        EncoderFormat.Mp3  => "audio/mpeg",
        EncoderFormat.Ogg  => "audio/ogg",
        EncoderFormat.Opus => "audio/ogg",
        _                  => "audio/mpeg"
    };

    /// <summary>
    /// Why <paramref name="spec"/> cannot be produced from <paramref name="sourceHandle"/>, or null
    /// when it can.
    ///
    /// Checked against the source's real format rather than refused outright: a profile that asks
    /// for 48 kHz stereo on a 48 kHz stereo bus is asking for nothing, and failing it would be as
    /// wrong as silently ignoring a genuine mismatch.
    /// </summary>
    public static string? Unsupported(Spec spec, int sourceHandle)
    {
        // LAME converts internally — anything goes.
        if (spec.Format == EncoderFormat.Mp3) return null;

        var info = Bass.BASS_ChannelGetInfo(sourceHandle);
        if (info is null) return null;   // cannot verify; let BASS report the real failure

        if (spec.SampleRateHz is { } hz && hz != info.freq)
            return $"{spec.Format} cannot resample: the program bus runs at {info.freq} Hz and this " +
                   $"profile asks for {hz} Hz. Set the profile to {info.freq} Hz, or use MP3.";

        if (spec.Channels != info.chans)
            return $"{spec.Format} cannot change the channel count: the program bus is " +
                   $"{ChannelWord(info.chans)} and this profile asks for {ChannelWord(spec.Channels)}. " +
                   $"Set the profile to {ChannelWord(info.chans)}, or use MP3.";

        return null;
    }

    /// <summary>Starts an encoder whose output BASSenc routes itself (cast). 0 = failed.</summary>
    public static int StartToCast(Spec spec, int sourceHandle) => spec.Format switch
    {
        // No AUTOFREE: the caller must control teardown so encoders stop before the mixer is freed
        // at shutdown. Letting BASS free them implicitly would invert that order.
        EncoderFormat.Mp3 => BassEnc_Mp3.BASS_Encode_MP3_Start(
            sourceHandle, Options(spec), BASSEncode.BASS_ENCODE_DEFAULT, null, IntPtr.Zero),

        EncoderFormat.Ogg => BassEnc_Ogg.BASS_Encode_OGG_Start(
            sourceHandle, Options(spec), BASSEncode.BASS_ENCODE_DEFAULT, null, IntPtr.Zero),

        EncoderFormat.Opus => BassEnc_Opus.BASS_Encode_OPUS_Start(
            sourceHandle, Options(spec), BASSEncode.BASS_ENCODE_DEFAULT, null, IntPtr.Zero),

        _ => 0
    };

    /// <summary>Starts an encoder that writes straight to <paramref name="filePath"/>. 0 = failed.</summary>
    public static int StartToFile(Spec spec, int sourceHandle, string filePath) => spec.Format switch
    {
        // BASS_ENCODE_QUEUE moves encoding and the disk write off the audio thread. A recording
        // target can stall in ways a socket does not — antivirus, spin-up, a full disk — and without
        // the queue that stall would be felt in the program bus itself.
        EncoderFormat.Mp3 => BassEnc_Mp3.BASS_Encode_MP3_StartFile(
            sourceHandle, Options(spec), BASSEncode.BASS_ENCODE_QUEUE, filePath),

        EncoderFormat.Ogg => BassEnc_Ogg.BASS_Encode_OGG_StartFile(
            sourceHandle, Options(spec), BASSEncode.BASS_ENCODE_QUEUE, filePath),

        EncoderFormat.Opus => BassEnc_Opus.BASS_Encode_OPUS_StartFile(
            sourceHandle, Options(spec), BASSEncode.BASS_ENCODE_QUEUE, filePath),

        _ => 0
    };

    /// <summary>
    /// Switches for the chosen add-on. Each mirrors a different command-line encoder — LAME, OGGENC
    /// and OPUSENC — so the spelling genuinely differs per format and cannot be shared.
    /// </summary>
    private static string Options(Spec spec) => spec.Format switch
    {
        // LAME: -b is the bitrate, -m m forces mono, --resample takes kHz.
        EncoderFormat.Mp3 => Mp3Options(spec),

        // OGGENC: -b is the nominal bitrate in kbps. Downmix and resample are deliberately absent —
        // see the class summary.
        EncoderFormat.Ogg => $"-b {spec.BitrateKbps}",

        // OPUSENC: --bitrate in kbps. Opus is always VBR unless told otherwise, which is the right
        // default for a broadcast stream: constant quality matters more than a constant rate.
        EncoderFormat.Opus => $"--bitrate {spec.BitrateKbps}",

        _ => string.Empty
    };

    private static string Mp3Options(Spec spec)
    {
        var opts = $"-b {spec.BitrateKbps}";
        if (spec.Channels == 1) opts += " -m m";
        if (spec.SampleRateHz is { } hz) opts += $" --resample {hz / 1000.0:0.###}";
        return opts;
    }

    private static string ChannelWord(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        _ => $"{channels}-channel"
    };
}
