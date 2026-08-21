using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class ScheduledActionExecutorTests
{
    private readonly Mock<IPlaylistController>   _playlist = new();
    private readonly Mock<IAudioSettingsRepository> _settings = new();
    private readonly Mock<IAssetFormatRepository> _formats = new();
    private readonly Mock<ISubcategoryRepository> _subcategories = new();
    private readonly Mock<IExternalActionRunner> _external = new();
    private readonly StudioContext               _studioCtx = new();
    private readonly ScheduledActionExecutor     _executor;

    public ScheduledActionExecutorTests()
    {
        _studioCtx.Initialize("studio-1");
        _executor = new ScheduledActionExecutor(
            _playlist.Object,
            _settings.Object,
            _formats.Object,
            _subcategories.Object,
            _external.Object,
            _studioCtx,
            NullLogger<ScheduledActionExecutor>.Instance);
    }

    // ── Playback ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Play_CallsPlayAsync()
    {
        await _executor.ExecuteAsync("""[{"type":"PLAY"}]""", CancellationToken.None);
        _playlist.Verify(p => p.PlayAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Next_CallsNextTrackAsync()
    {
        await _executor.ExecuteAsync("""[{"type":"NEXT"}]""", CancellationToken.None);
        _playlist.Verify(p => p.NextTrackAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangeMode_MapsLiveAssist()
    {
        await _executor.ExecuteAsync(
            """[{"type":"CHANGE_PLAYLIST_MODE","payload":{"mode":"LIVE_ASSIST"}}]""",
            CancellationToken.None);

        _playlist.Verify(p => p.ChangeModeAsync(PlaylistMode.LiveAssist, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangeMode_PascalCaseWrapper_StillExecutes()
    {
        // Legacy/records-serialized rows store "Type"/"Payload" (PascalCase) — must still run.
        await _executor.ExecuteAsync(
            """[{"Type":"CHANGE_PLAYLIST_MODE","Payload":{"mode":"MANUAL"}}]""",
            CancellationToken.None);

        _playlist.Verify(p => p.ChangeModeAsync(PlaylistMode.Manual, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Queue management ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AddItem_PassesAssetIdAndPosition()
    {
        await _executor.ExecuteAsync(
            """[{"type":"ADD_ITEM","payload":{"asset_id":"a-1","position":3}}]""",
            CancellationToken.None);

        _playlist.Verify(p => p.AddItemAsync("a-1", 3, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Reorder_PassesItemIdAndNewPosition()
    {
        await _executor.ExecuteAsync(
            """[{"type":"REORDER_ITEM","payload":{"item_id":"i-9","new_position":0}}]""",
            CancellationToken.None);

        _playlist.Verify(p => p.ReorderItemAsync("i-9", 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PatchItem_PassesTypedFields()
    {
        await _executor.ExecuteAsync(
            """[{"type":"PATCH_ITEM","payload":{"item_id":"i-1","crossfade_ms":2000,"auto_link_next":true}}]""",
            CancellationToken.None);

        _playlist.Verify(p => p.PatchItemAsync(
            "i-1", 2000u, null, null, null, null, true, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Sequencing + WAIT ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Actions_RunInOrder()
    {
        var order = new List<string>();
        _playlist.Setup(p => p.ClearAsync(It.IsAny<CancellationToken>()))
                 .Callback(() => order.Add("clear")).Returns(Task.CompletedTask);
        _playlist.Setup(p => p.PlayAsync(It.IsAny<CancellationToken>()))
                 .Callback(() => order.Add("play")).Returns(Task.CompletedTask);

        await _executor.ExecuteAsync(
            """[{"type":"CLEAR_PLAYLIST"},{"type":"PLAY"}]""", CancellationToken.None);

        order.Should().Equal("clear", "play");
    }

    [Fact]
    public async Task Wait_DelaysBetweenActions()
    {
        var sw = Stopwatch.StartNew();
        await _executor.ExecuteAsync(
            """[{"type":"WAIT","payload":{"duration_ms":150}},{"type":"PLAY"}]""",
            CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(120);
        _playlist.Verify(p => p.PlayAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── External ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteFile_InvokesRunnerWithFields()
    {
        _external
            .Setup(e => e.RunFileAsync("script.ps1", "-x 1", null, 5000, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalRunResult(true, 0, "ok", null));

        await _executor.ExecuteAsync(
            """[{"type":"EXECUTE_FILE","payload":{"path":"script.ps1","arguments":"-x 1","timeout_ms":5000,"capture_output":true}}]""",
            CancellationToken.None);

        _external.Verify(e => e.RunFileAsync(
            "script.ps1", "-x 1", null, 5000, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HttpCall_InvokesRunnerWithMethodAndUrl()
    {
        _external
            .Setup(e => e.RunHttpAsync("POST", "https://x/y", "{}", It.IsAny<IReadOnlyDictionary<string, string>>(), 30000, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalRunResult(true, 200, "", null));

        await _executor.ExecuteAsync(
            """[{"type":"HTTP_CALL","payload":{"method":"POST","url":"https://x/y","body":"{}"}}]""",
            CancellationToken.None);

        _external.Verify(e => e.RunHttpAsync(
            "POST", "https://x/y", "{}", It.IsAny<IReadOnlyDictionary<string, string>>(), 30000, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Robustness ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownAction_IsSkipped_AndDoesNotThrow()
    {
        var act = () => _executor.ExecuteAsync(
            """[{"type":"WHATEVER"},{"type":"PLAY"}]""", CancellationToken.None);

        await act.Should().NotThrowAsync();
        _playlist.Verify(p => p.PlayAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmptyOrNullJson_DoesNothing()
    {
        await _executor.ExecuteAsync("", CancellationToken.None);
        await _executor.ExecuteAsync("[]", CancellationToken.None);
        _playlist.VerifyNoOtherCalls();
    }

    // ── Sweeper category / subcategory ─────────────────────────────────────────────

    [Fact]
    public async Task ChangeSweeperCategory_ResolvesNameAndPersistsFormatId_ResetsSubcategory()
    {
        _settings.Setup(s => s.GetByStudioAsync("studio-1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AudioSettings { SettingsId = "set-1", StudioId = "studio-1", SweeperSubcategoryId = "old-sub" });
        _formats.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { new AssetFormat { FormatId = "fmt-9", Name = "Poranek" } });

        await _executor.ExecuteAsync(
            """[{"type":"CHANGE_SWEEPER_CATEGORY","payload":{"format_name":"poranek"}}]""",
            CancellationToken.None);

        _settings.Verify(s => s.UpdateAsync(
            It.Is<AudioSettings>(a => a.SweeperFormatId == "fmt-9" && a.SweeperSubcategoryId == null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangeSweeperCategory_UnknownName_DoesNotPersist()
    {
        _settings.Setup(s => s.GetByStudioAsync("studio-1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AudioSettings { SettingsId = "set-1", StudioId = "studio-1" });
        _formats.Setup(f => f.GetAllAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { new AssetFormat { FormatId = "fmt-9", Name = "Poranek" } });

        await _executor.ExecuteAsync(
            """[{"type":"CHANGE_SWEEPER_CATEGORY","payload":{"format_name":"NieMa"}}]""",
            CancellationToken.None);

        _settings.Verify(s => s.UpdateAsync(It.IsAny<AudioSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangeSweeperSubcategory_ResolvesNameAgainstActiveFormat()
    {
        _settings.Setup(s => s.GetByStudioAsync("studio-1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AudioSettings { SettingsId = "set-1", StudioId = "studio-1", SweeperFormatId = "fmt-9" });
        _subcategories.Setup(s => s.GetByFormatIdAsync("fmt-9", It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new[] { new Subcategory { SubcategoryId = "sub-7", FormatId = "fmt-9", Name = "Weekend" } });

        await _executor.ExecuteAsync(
            """[{"type":"CHANGE_SWEEPER_SUBCATEGORY","payload":{"subcategory_name":"weekend"}}]""",
            CancellationToken.None);

        _settings.Verify(s => s.UpdateAsync(
            It.Is<AudioSettings>(a => a.SweeperSubcategoryId == "sub-7"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangeSweeperSubcategory_EmptyName_ClearsToNull()
    {
        _settings.Setup(s => s.GetByStudioAsync("studio-1", It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AudioSettings { SettingsId = "set-1", StudioId = "studio-1", SweeperFormatId = "fmt-9", SweeperSubcategoryId = "sub-7" });

        await _executor.ExecuteAsync(
            """[{"type":"CHANGE_SWEEPER_SUBCATEGORY","payload":{"subcategory_name":""}}]""",
            CancellationToken.None);

        _settings.Verify(s => s.UpdateAsync(
            It.Is<AudioSettings>(a => a.SweeperSubcategoryId == null),
            It.IsAny<CancellationToken>()), Times.Once);
        _subcategories.Verify(s => s.GetByFormatIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
