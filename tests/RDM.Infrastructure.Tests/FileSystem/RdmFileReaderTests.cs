using FluentAssertions;
using RDM.Infrastructure.FileSystem;
using Xunit;

namespace RDM.Infrastructure.Tests.FileSystem;

/// <summary>
/// Unit tests for RdmFileReader.
/// TryReadAsync looks for {audioPath}.rdm — the audio file itself need not exist.
/// </summary>
public sealed class RdmFileReaderTests : IDisposable
{
    private readonly string _audioPath = Path.GetTempFileName();

    // Path.ChangeExtension replaces the extension → {name}.rdm
    private string RdmPath => Path.ChangeExtension(_audioPath, ".rdm");

    public void Dispose()
    {
        if (File.Exists(_audioPath)) File.Delete(_audioPath);
        if (File.Exists(RdmPath))    File.Delete(RdmPath);
    }

    private static RdmFileReader Sut => new();

    [Fact]
    public async Task TryReadAsync_WhenNoRdmFile_ReturnsNull()
    {
        // No .rdm file written — only the .tmp audio placeholder exists
        var result = await Sut.TryReadAsync(_audioPath);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_WhenValidJson_ReturnsCorrectMetadata()
    {
        await File.WriteAllTextAsync(RdmPath, """
            {
              "title":    "My Track",
              "artist":   "Artist Name",
              "album":    "Album",
              "bpm":      128.5,
              "year":     2023,
              "mood":     "Happy",
              "language": "EN",
              "comments": "Great song"
            }
            """);

        var result = await Sut.TryReadAsync(_audioPath);

        result.Should().NotBeNull();
        result!.Title.Should().Be("My Track");
        result.Artist.Should().Be("Artist Name");
        result.Album.Should().Be("Album");
        result.Bpm.Should().Be(128.5m);
        result.Year.Should().Be(2023);
        result.Mood.Should().Be("Happy");
        result.Language.Should().Be("EN");
        result.Comments.Should().Be("Great song");
    }

    [Fact]
    public async Task TryReadAsync_WhenJsonMissingFields_ReturnsNullsForMissingFields()
    {
        await File.WriteAllTextAsync(RdmPath, """{"title":"Only Title"}""");

        var result = await Sut.TryReadAsync(_audioPath);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Only Title");
        result.Artist.Should().BeNull();
        result.Album.Should().BeNull();
        result.Bpm.Should().BeNull();
        result.Year.Should().BeNull();
        result.CueStart.Should().BeNull();
        result.CueIntro.Should().BeNull();
        result.CuePoints.Should().BeEmpty();
    }

    [Fact]
    public async Task TryReadAsync_WhenMalformedJson_ReturnsNull()
    {
        await File.WriteAllTextAsync(RdmPath, "{ this is not valid json }}}");

        var result = await Sut.TryReadAsync(_audioPath);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_WhenCueMarkersPresent_ParsesCueFieldsInSeconds()
    {
        // Current sidecar format written by RdmFileWriter: snake_case document with a
        // cue_markers object whose values are in seconds, mapped to the Cue* fields.
        await File.WriteAllTextAsync(RdmPath, """
            {
              "title": "Track With Cues",
              "cue_markers": {
                "start":      2.0,
                "intro":      3.0,
                "outro":      210.0,
                "start_next": 235.5,
                "end":        240.0,
                "hook_in":    60.0,
                "hook_out":   90.0,
                "loop_in":    120.0,
                "loop_out":   150.0
              }
            }
            """);

        var result = await Sut.TryReadAsync(_audioPath);

        result.Should().NotBeNull();
        result!.CueStart.Should().Be(2.0);
        result.CueIntro.Should().Be(3.0);
        result.CueOutro.Should().Be(210.0);
        result.CueStartNext.Should().Be(235.5);
        result.CueEnd.Should().Be(240.0);
        result.CueHookIn.Should().Be(60.0);
        result.CueHookOut.Should().Be(90.0);
        result.CueLoopIn.Should().Be(120.0);
        result.CueLoopOut.Should().Be(150.0);

        // The reader no longer populates the legacy CuePoints list.
        result.CuePoints.Should().BeEmpty();
    }
}
