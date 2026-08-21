using FluentAssertions;
using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CuePointBuilder"/> — pure cue-point / envelope / title logic
/// extracted from PlaylistEngine, now directly testable in isolation.
/// </summary>
public sealed class CuePointBuilderTests
{
    private static Asset MakeAsset(
        uint durationMs = 60_000, string? format = "Music",
        double? intro = null, double? outro = null, double? hook = null,
        double? end = null, double? startNext = null, double? fadeOut = null) => new()
    {
        AssetId      = "asset-1",
        Title        = "Track",
        Artist       = "Artist",
        DurationMs   = durationMs,
        FormatName   = format,
        CueIntro     = intro,
        CueOutro     = outro,
        CueHookIn    = hook,
        CueEnd       = end,
        CueStartNext = startNext,
        CueFadeOut   = fadeOut
    };

    private static PlaylistItem Item(SegueType segue) =>
        new() { ItemId = "it-1", PlaylistId = "pl-1", AssetId = "asset-1", ItemType = PlaylistItemType.Asset, SegueType = segue };

    private static bool Has(IReadOnlyList<AssetCuePoint> cues, MarkerType type, uint pos) =>
        cues.Any(c => c.MarkerType == type && c.PositionMs == pos);

    // ── Static cues ──────────────────────────────────────────────────────────────

    [Fact]
    public void Build_IncludesIntroOutroHookEnd_WhenSet()
    {
        var asset = MakeAsset(intro: 1.0, outro: 2.0, hook: 3.0, end: 55.0);

        var cues = CuePointBuilder.Build(asset, item: null, PlaylistMode.Manual, effectiveCrossfadeMs: 0);

        Has(cues, MarkerType.Intro, 1_000).Should().BeTrue();
        Has(cues, MarkerType.Outro, 2_000).Should().BeTrue();
        Has(cues, MarkerType.Hook,  3_000).Should().BeTrue();
        Has(cues, MarkerType.End,  55_000).Should().BeTrue();
    }

    [Fact]
    public void Build_EndCue_PresentEvenInManualMode()
    {
        var asset = MakeAsset(end: 42.0);

        var cues = CuePointBuilder.Build(asset, item: null, PlaylistMode.Manual, 0);

        Has(cues, MarkerType.End, 42_000).Should().BeTrue();
    }

    // ── Automation-marker gating ─────────────────────────────────────────────────

    [Fact]
    public void Build_ManualMode_OmitsAutomationMarkers()
    {
        var asset = MakeAsset(startNext: 50.0);

        var cues = CuePointBuilder.Build(asset, Item(SegueType.Auto), PlaylistMode.Manual, 0);

        cues.Any(c => c.MarkerType == MarkerType.StartNext).Should().BeFalse();
    }

    [Fact]
    public void Build_NullItem_OmitsAutomationMarkers()
    {
        var asset = MakeAsset(startNext: 50.0);

        var cues = CuePointBuilder.Build(asset, item: null, PlaylistMode.Auto, 0);

        cues.Any(c => c.MarkerType == MarkerType.StartNext).Should().BeFalse();
    }

    // ── StartNext computation (Auto mode) ────────────────────────────────────────

    [Fact]
    public void Build_TimedSegue_StartNextIsBroadcastEndMinusCrossfade()
    {
        var asset = MakeAsset(durationMs: 60_000);

        var cues = CuePointBuilder.Build(asset, Item(SegueType.Timed), PlaylistMode.Auto, effectiveCrossfadeMs: 4_000);

        Has(cues, MarkerType.StartNext, 56_000).Should().BeTrue();
    }

    [Fact]
    public void Build_AutoSegue_PrefersExplicitCueStartNext()
    {
        var asset = MakeAsset(durationMs: 60_000, startNext: 50.0);

        var cues = CuePointBuilder.Build(asset, Item(SegueType.Auto), PlaylistMode.Auto, effectiveCrossfadeMs: 4_000);

        Has(cues, MarkerType.StartNext, 50_000).Should().BeTrue();
    }

    [Fact]
    public void Build_AutoSegue_ComputesStartNext_FromEndMinusCrossfade_WhenNoMarker()
    {
        var asset = MakeAsset(durationMs: 60_000); // no explicit StartNext/FadeOut

        var cues = CuePointBuilder.Build(asset, Item(SegueType.Auto), PlaylistMode.Auto, effectiveCrossfadeMs: 4_000);

        Has(cues, MarkerType.StartNext, 56_000).Should().BeTrue();
    }

    [Fact]
    public void Build_AutoSegue_IndependentFadeOut_OnlyWhenBothMarkersSet()
    {
        var asset = MakeAsset(startNext: 50.0, fadeOut: 55.0);

        var cues = CuePointBuilder.Build(asset, Item(SegueType.Auto), PlaylistMode.Auto, effectiveCrossfadeMs: 0);

        Has(cues, MarkerType.StartNext, 50_000).Should().BeTrue();
        Has(cues, MarkerType.FadeOut,   55_000).Should().BeTrue();
    }

    [Fact]
    public void Build_NonMusicFormat_StillProducesCues_ButCallerSuppressesCrossfade()
    {
        // CuePointBuilder is format-agnostic; the "Music-only crossfade" rule lives in the engine.
        var asset = MakeAsset(format: "Jingle", durationMs: 10_000);

        var cues = CuePointBuilder.Build(asset, Item(SegueType.Timed), PlaylistMode.Auto, effectiveCrossfadeMs: 0);

        Has(cues, MarkerType.StartNext, 10_000).Should().BeTrue();
    }

    // ── CueEnd override (variable-duration assets) ───────────────────────────────

    [Fact]
    public void Build_CueEndOverride_TakesPrecedenceOverStoredCueEnd()
    {
        var asset = MakeAsset(end: 30.0); // stale stored End

        var cues = CuePointBuilder.Build(asset, Item(SegueType.Auto), PlaylistMode.Auto, 0, cueEndOverride: 58.2);

        Has(cues, MarkerType.End, 58_200).Should().BeTrue();
        Has(cues, MarkerType.End, 30_000).Should().BeFalse();
    }

    // ── Envelope / title helpers ─────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not valid json")]
    public void DeserializeEnvelope_ReturnsNull_ForNullBlankOrInvalid(string? json)
    {
        CuePointBuilder.DeserializeEnvelope(json).Should().BeNull();
    }

    [Fact]
    public void DeserializeEnvelope_ParsesValidJson()
    {
        var result = CuePointBuilder.DeserializeEnvelope(
            """[{"timeS":0,"volume":1.0},{"timeS":5,"volume":0.5}]""");

        result.Should().NotBeNull();
        result!.Should().HaveCount(2);
        result![1].TimeS.Should().Be(5);
        result![1].Volume.Should().Be(0.5);
    }

    [Theory]
    [InlineData(@"C:\music\my track.mp3", "my track")]
    [InlineData(@"C:\music\jingle.wav", "jingle")]
    public void DeriveTitleFromPath_ReturnsFileNameWithoutExtension(string path, string expected)
    {
        CuePointBuilder.DeriveTitleFromPath(path).Should().Be(expected);
    }
}
