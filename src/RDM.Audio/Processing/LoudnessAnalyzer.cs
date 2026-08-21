using RDM.Core.Interfaces;
using RDM.Core.Models;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Loud;

namespace RDM.Audio.Processing;

/// <summary>
/// Analyses integrated loudness (LUFS) and True Peak (dBTP) of an audio file
/// according to EBU R128 / BS.1770 using the BASSloud add-on.
///
/// BASSloud is attached as a DSP processor to a BASS_STREAM_DECODE channel.
/// Audio is pumped through BASS_ChannelGetData; measurements accumulate as
/// data passes through the processor. Results are read after all data is consumed.
///
/// Prerequisites: Bass.BASS_PluginLoad("bassloud.dll") must be called before
/// the first call to AnalyzeAsync (done in BassAudioEngine.InitializeAsync).
/// </summary>
public sealed class LoudnessAnalyzer : ILoudnessAnalyzer
{
    public Task<LoudnessResult> AnalyzeAsync(string audioFilePath, CancellationToken ct = default)
    {
        // 1. Decode stream — no output device required
        int decodeStream = Bass.BASS_StreamCreateFile(
            audioFilePath, 0, 0, BASSFlag.BASS_STREAM_DECODE);

        if (decodeStream == 0)
            throw new InvalidOperationException(
                $"Cannot open file for loudness analysis ({Bass.BASS_ErrorGetCode()}): '{audioFilePath}'");

        // 2. Attach BASSloud DSP processor to the decode stream
        int loudHandle = BassLoud.BASS_Loudness_Start(
            decodeStream,
            BassLoudness.BASS_LOUDNESS_INTEGRATED | BassLoudness.BASS_LOUDNESS_TRUEPEAK,
            0); // priority = 0 (normal)

        if (loudHandle == 0)
        {
            Bass.BASS_StreamFree(decodeStream);
            throw new InvalidOperationException(
                $"Cannot attach BASSloud processor ({Bass.BASS_ErrorGetCode()}): '{audioFilePath}'");
        }

        try
        {
            // 3. Pump audio through — BASSloud accumulates measurements as data flows
            byte[] buf = new byte[65536];
            while (Bass.BASS_ChannelGetData(decodeStream, buf, buf.Length) > 0)
                ct.ThrowIfCancellationRequested();

            // 4. Read results (values written to ref parameters; bool indicates success)
            float lufs     = 0f;
            float truePeak = 0f;
            BassLoud.BASS_Loudness_GetLevel(loudHandle, BassLoudness.BASS_LOUDNESS_INTEGRATED, ref lufs);
            BassLoud.BASS_Loudness_GetLevel(loudHandle, BassLoudness.BASS_LOUDNESS_TRUEPEAK,   ref truePeak);

            return Task.FromResult(new LoudnessResult((decimal)lufs, (decimal)truePeak));
        }
        finally
        {
            BassLoud.BASS_Loudness_Stop(loudHandle); // free loudness processor
            Bass.BASS_StreamFree(decodeStream);
        }
    }
}
