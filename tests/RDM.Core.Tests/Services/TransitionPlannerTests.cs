using FluentAssertions;
using RDM.Core.Services;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class TransitionPlannerTests
{
    private const uint AEnd = 180_000; // current track effective end (180 s)

    private static PlannerItem Track(int? leadIn = null, uint? crossfade = null, uint dur = 200_000)
        => new(IsDummy: false, IsSweeper: false, EffectiveDurationMs: dur, LeadInMs: leadIn, CrossfadeMs: crossfade);

    private static PlannerItem Sweeper(int? leadIn, uint dur)
        => new(IsDummy: false, IsSweeper: true, EffectiveDurationMs: dur, LeadInMs: leadIn, CrossfadeMs: null);

    private static PlannerItem Dummy()
        => new(IsDummy: true, IsSweeper: false, EffectiveDurationMs: 0, LeadInMs: null, CrossfadeMs: null);

    [Fact]
    public void Plan_NormalTrack_UsesLeadInOverlap_NoOverlays()
    {
        var plan = TransitionPlanner.Plan(AEnd, new[] { Track(leadIn: 6000) }, defaultCrossfadeMs: 2000);

        plan.MainTrackOffset.Should().Be(1);
        plan.CrossfadeMs.Should().Be(6000);
        plan.Overlays.Should().BeEmpty();
    }

    [Fact]
    public void Plan_FallsBackToCrossfadeMs_ThenDefault_WhenLeadInNull()
    {
        TransitionPlanner.Plan(AEnd, new[] { Track(crossfade: 4000) }, 2000)
            .CrossfadeMs.Should().Be(4000);

        TransitionPlanner.Plan(AEnd, new[] { Track() }, 2000)
            .CrossfadeMs.Should().Be(2000);
    }

    [Fact]
    public void Plan_SweeperOverlappingCurrent_FloatsAsOverlay_AndCrossfadesToFollowingTrack()
    {
        // A (ends 180s) → sweeper S (leadIn 8s into A's tail, 3s long) → track B (leadIn 6s)
        var plan = TransitionPlanner.Plan(
            AEnd,
            new[] { Sweeper(leadIn: 8000, dur: 3000), Track(leadIn: 6000) },
            defaultCrossfadeMs: 2000);

        // B is the next sequential track (offset 2), reached past the sweeper.
        plan.MainTrackOffset.Should().Be(2);

        // Deep overlap = leadIn[S] + leadIn[B] − dur[S] = 8000 + 6000 − 3000 = 11000,
        // i.e. B overlaps A by far more than the short sweeper's own length.
        plan.CrossfadeMs.Should().Be(11_000);

        // The sweeper floats over A's tail: start = 180000 − 8000 = 172000.
        plan.Overlays.Should().ContainSingle();
        plan.Overlays[0].Offset.Should().Be(1);
        plan.Overlays[0].TriggerAtMs.Should().Be(172_000);
    }

    [Fact]
    public void Plan_NonOverlappingSweeper_StaysSequential()
    {
        // Negative leadIn → a gap: the sweeper starts after the current track ends, so it is
        // a normal sequential item, not a floating overlay.
        var plan = TransitionPlanner.Plan(AEnd, new[] { Sweeper(leadIn: -2000, dur: 3000) }, 2000);

        plan.Overlays.Should().BeEmpty();
        plan.MainTrackOffset.Should().Be(1);
        plan.CrossfadeMs.Should().Be(0); // starts 2s after A ends → no overlap
    }

    [Fact]
    public void Plan_SkipsDummies_WithoutConsumingTime()
    {
        var plan = TransitionPlanner.Plan(
            AEnd,
            new[] { Dummy(), Track(leadIn: 5000), },
            2000);

        plan.MainTrackOffset.Should().Be(2);  // dummy at offset 1 skipped, track at offset 2
        plan.CrossfadeMs.Should().Be(5000);
        plan.Overlays.Should().BeEmpty();
    }

    [Fact]
    public void Plan_TriggerClampedToZero_WhenSweeperReachesBeforeCurrentStart()
    {
        // Sweeper with a huge lead-in would start before the current track (start < 0):
        // its overlay trigger clamps to 0 (fire at the very start of the current track).
        var plan = TransitionPlanner.Plan(
            AEnd,
            new[] { Sweeper(leadIn: 200_000, dur: 3000), Track(leadIn: 1000) },
            2000);

        plan.Overlays.Should().ContainSingle();
        plan.Overlays[0].TriggerAtMs.Should().Be(0);
    }

    [Fact]
    public void Plan_NoUpcomingTrack_ReturnsMinusOne()
    {
        TransitionPlanner.Plan(AEnd, System.Array.Empty<PlannerItem>(), 2000)
            .MainTrackOffset.Should().Be(-1);
    }

    [Fact]
    public void Plan_CrossfadeNeverExceedsCurrentDuration()
    {
        // Even an enormous lead-in cannot make B overlap more than the whole current track.
        var plan = TransitionPlanner.Plan(AEnd, new[] { Track(leadIn: 999_999) }, 2000);
        plan.CrossfadeMs.Should().Be(AEnd);
    }
}
