using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class StreamTitlesServiceTests : IAsyncDisposable
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly InMemoryEventBus             _eventBus  = new();
    private readonly Mock<IAssetRepository>       _assetRepo = new();
    private readonly Mock<IAssetFormatRepository> _formatRepo = new();
    private readonly string                       _outputFile;

    // Default settings — overridden per test with BuildSettings()
    private StreamTitlesSettings DefaultSettings => BuildSettings(
        format: "$artist$ - $title$");

    public StreamTitlesServiceTests()
    {
        _outputFile = Path.Combine(
            Path.GetTempPath(), $"rdm_streamtitles_{Guid.NewGuid():N}.txt");

        _assetRepo
            .Setup(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
    }

    public ValueTask DisposeAsync()
    {
        if (File.Exists(_outputFile)) File.Delete(_outputFile);
        return ValueTask.CompletedTask;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StreamTitlesService_UsesFallback_WhenArtistMissing()
    {
        await using var svc = Build(DefaultSettings);

        await _eventBus.PublishAsync(
            Evt("item-1", "asset-1", title: "My Track", artist: null, durationMs: 180_000));
        await Task.Delay(300);

        var content = await File.ReadAllTextAsync(_outputFile);
        content.Should().Be("Radio - My Track");   // "Radio" = fallback_artist default
    }

    [Fact]
    public async Task StreamTitlesService_UsesFallback_WhenTitleMissing()
    {
        await using var svc = Build(BuildSettings(
            format: "$artist$ - $title$",
            fallbackTitle: "Unknown Track"));

        await _eventBus.PublishAsync(
            Evt("item-1", "asset-1", title: string.Empty, artist: "DJ Test", durationMs: 0));
        await Task.Delay(300);

        var content = await File.ReadAllTextAsync(_outputFile);
        content.Should().Be("DJ Test - Unknown Track");
    }

    [Fact]
    public async Task StreamTitlesService_FormatsTokens_Correctly()
    {
        await using var svc = Build(BuildSettings(
            format: "$title$ by $artist$ [$duration$]"));

        // 3 minutes 42 seconds = 222 000ms
        await _eventBus.PublishAsync(
            Evt("item-1", "asset-1", title: "My Song", artist: "The Band", durationMs: 222_000));
        await Task.Delay(300);

        var content = await File.ReadAllTextAsync(_outputFile);
        content.Should().Be("My Song by The Band [03:42]");
    }

    [Fact]
    public async Task StreamTitlesService_DoesNotThrow_WhenFileWriteFails()
    {
        // Point to a path in a non-existent directory
        var badPath = Path.Combine(Path.GetTempPath(), "no_such_dir_xyz", "file.txt");
        await using var svc = Build(BuildSettings(
            format: "$artist$ - $title$",
            outputFilePath: badPath));

        var act = async () =>
        {
            await _eventBus.PublishAsync(
                Evt("item-1", "asset-1", title: "Track", artist: "Artist", durationMs: 0));
            await Task.Delay(300);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StreamTitlesService_DoesNothing_WhenDisabled()
    {
        await using var svc = Build(BuildSettings(
            format: "$artist$ - $title$", enabled: false));

        await _eventBus.PublishAsync(
            Evt("item-1", "asset-1", title: "Track", artist: "Artist", durationMs: 0));
        await Task.Delay(300);

        File.Exists(_outputFile).Should().BeFalse();
    }

    [Fact]
    public async Task StreamTitlesService_WritesUtf8_ByDefault()
    {
        await using var svc = Build(BuildSettings(
            format: "$artist$ - $title$"));

        await _eventBus.PublishAsync(
            Evt("item-1", "asset-1", title: "Piosenka", artist: "Artysta Ą", durationMs: 0));
        await Task.Delay(300);

        var bytes   = await File.ReadAllBytesAsync(_outputFile);
        var content = System.Text.Encoding.UTF8.GetString(bytes);
        content.Should().Be("Artysta Ą - Piosenka");

        // No BOM — first three bytes must not be the UTF-8 BOM sequence
        bytes.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private StreamTitlesService Build(StreamTitlesSettings settings) =>
        new(_eventBus, settings, _assetRepo.Object, _formatRepo.Object, NullLogger<StreamTitlesService>.Instance);

    private StreamTitlesSettings BuildSettings(
        string  format          = "$artist$ - $title$",
        string? outputFilePath  = null,
        string  fallbackArtist  = "Radio",
        string  fallbackTitle   = "",
        bool    enabled         = true) =>
        new()
        {
            Enabled        = enabled,
            OutputFilePath = outputFilePath ?? _outputFile,
            Format         = format,
            Encoding       = "UTF-8",
            FallbackArtist = fallbackArtist,
            FallbackTitle  = fallbackTitle
        };

    private static PlaylistItemStartedEvent Evt(
        string  itemId,
        string  assetId,
        string  title,
        string? artist,
        uint    durationMs) =>
        new(itemId, assetId, title, artist, durationMs, AssetType.Track, null);
}
