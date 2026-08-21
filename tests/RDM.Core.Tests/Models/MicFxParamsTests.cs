using System.Collections.Generic;
using FluentAssertions;
using RDM.Core.Models;
using Xunit;

namespace RDM.Core.Tests.Models;

public class MicFxParamsTests
{
    [Theory]
    [InlineData(MicFxType.Compressor)]
    [InlineData(MicFxType.PeakEq)]
    [InlineData(MicFxType.VolumeGain)]
    [InlineData(MicFxType.FreeVerb)]
    public void Every_effect_defines_parameters_and_defaults_inside_their_range(MicFxType fxType)
    {
        var defs = MicFxParams.For(fxType);

        defs.Should().NotBeEmpty();
        foreach (var def in defs)
        {
            def.Min.Should().BeLessThan(def.Max);
            def.Default.Should().BeInRange(def.Min, def.Max);
        }
    }

    [Fact]
    public void Defaults_cover_exactly_the_declared_parameters()
    {
        var defaults = MicFxParams.Defaults(MicFxType.Compressor);

        defaults.Keys.Should().BeEquivalentTo(
            new[] { "threshold", "ratio", "attack", "release", "gain" });
    }

    [Fact]
    public void Sanitize_clamps_values_into_range()
    {
        var result = MicFxParams.Sanitize(MicFxType.Compressor, new Dictionary<string, float>
        {
            ["threshold"] = -500f,   // below Min
            ["ratio"]     = 5000f    // above Max
        });

        result["threshold"].Should().Be(-60f);
        result["ratio"].Should().Be(100f);
    }

    [Fact]
    public void Sanitize_drops_unknown_keys_and_falls_back_to_defaults()
    {
        var result = MicFxParams.Sanitize(MicFxType.VolumeGain, new Dictionary<string, float>
        {
            ["nonsense"] = 42f
        });

        result.Should().NotContainKey("nonsense");
        result["volume"].Should().Be(1.2f);
    }

    // Regression guard: an infinite value from a plugin once broke serialisation of the whole
    // config, taking the list of configured effects and plugins with it.
    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(float.NaN)]
    public void Sanitize_rejects_non_finite_values(float bad)
    {
        var result = MicFxParams.Sanitize(MicFxType.PeakEq, new Dictionary<string, float>
        {
            ["gain"] = bad
        });

        float.IsFinite(result["gain"]).Should().BeTrue();
        result["gain"].Should().Be(3f);   // the declared default
    }

    [Fact]
    public void A_new_slot_starts_from_the_defaults()
    {
        var slot = new MicFxSlot(1, MicFxType.FreeVerb);

        slot.Parameters["drymix"].Should().Be(0.9f);
        slot.Parameters["wetmix"].Should().Be(0.1f);
    }
}
