using FluentAssertions;
using RDM.Core.Services;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class AutoCueCalculatorTests
{
    // 1000 buckets, durationSec = 1000 → 1 bucket == 1 second, so an index maps directly
    // to its second. Layout: silence head, loud body, linear fade-out tail, silence tail.
    private const int    Buckets     = 1000;
    private const double DurationSec = 1000.0;

    // Thresholds the test math is built around (mirror the Silence Remover defaults).
    private const double StartDb = -25.0;
    private const double MixDb   = -15.0;
    private const double EndDb   = -28.0;

    private static float[] BuildSignal()
    {
        var peaks = new float[Buckets];
        for (int b = 0; b < Buckets; b++)
        {
            float amp;
            if (b < 100)        amp = 0f;                              // 0..99   silence
            else if (b < 800)   amp = 0.5f;                           // 100..799 loud (−6 dB)
            else if (b < 900)   amp = 0.5f * (1f - (b - 800) / 100f); // 800..899 fade to 0
            else                amp = 0f;                              // 900..999 silence
            // Mimic the generator's interleaved min/max by sign — abs() must still find it.
            peaks[b] = (b % 2 == 0) ? amp : -amp;
        }
        return peaks;
    }

    [Fact]
    public void Calculate_DetectsStart_AtFirstAudibleSample()
    {
        var result = AutoCueCalculator.Calculate(BuildSignal(), DurationSec, StartDb, MixDb, EndDb);

        result.Should().NotBeNull();
        // Loud body begins at bucket 100; START (−25 dB) should land right there.
        result!.Value.StartSec.Should().BeApproximately(100, 2);
    }

    [Fact]
    public void Calculate_NextStartIsBeforeEnd_OnFadeOut()
    {
        var result = AutoCueCalculator.Calculate(BuildSignal(), DurationSec, StartDb, MixDb, EndDb)!.Value;

        // Mix threshold (−15 dB) is louder than the end threshold (−28 dB), so the last
        // time the signal is above −15 dB happens earlier than the last audible sample.
        result.NextStartSec.Should().BeLessThan(result.EndSec);
        result.StartSec.Should().BeLessThan(result.NextStartSec);

        // Within the fade region (800..899): −15 dB ≈ amp 0.178 → bucket ~864;
        // −28 dB ≈ amp 0.040 → bucket ~892.
        result.NextStartSec.Should().BeApproximately(864, 4);
        result.EndSec.Should().BeApproximately(892, 4);
    }

    [Fact]
    public void Calculate_ReturnsNull_WhenNoPeaks()
    {
        AutoCueCalculator.Calculate(System.Array.Empty<float>(), DurationSec, StartDb, MixDb, EndDb).Should().BeNull();
    }

    [Fact]
    public void Calculate_ReturnsNull_WhenDurationNonPositive()
    {
        AutoCueCalculator.Calculate(BuildSignal(), 0, StartDb, MixDb, EndDb).Should().BeNull();
    }

    [Fact]
    public void Calculate_NextStartFallsBackToEnd_WhenNeverReachesMixThreshold()
    {
        // A quiet track that peaks at −20 dB (amp ≈ 0.1): above the end threshold (−28 dB)
        // but never above the mix threshold (−15 dB) → NEXT START defaults to END.
        var quiet = new float[Buckets];
        for (int b = 100; b < 900; b++) quiet[b] = 0.1f;

        var result = AutoCueCalculator.Calculate(quiet, DurationSec, StartDb, MixDb, EndDb)!.Value;

        result.NextStartSec.Should().Be(result.EndSec);
    }

    [Theory]
    [InlineData(-25.0, 0.0562)]
    [InlineData(-15.0, 0.1778)]
    [InlineData(-28.0, 0.0398)]
    [InlineData(0.0, 1.0)]
    public void DbToLinear_MatchesExpectedAmplitude(double db, double expected)
    {
        AutoCueCalculator.DbToLinear(db).Should().BeApproximately((float)expected, 0.0005f);
    }
}
