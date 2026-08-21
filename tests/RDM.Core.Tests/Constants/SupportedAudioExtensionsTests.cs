using FluentAssertions;
using RDM.Core.Constants;
using Xunit;

namespace RDM.Core.Tests.Constants;

public sealed class SupportedAudioExtensionsTests
{
    [Theory]
    [InlineData("song.mp3")]
    [InlineData("song.wav")]
    [InlineData("song.flac")]
    [InlineData("song.ogg")]
    [InlineData("song.aac")]
    [InlineData("song.m4a")]
    [InlineData("song.wma")]
    [InlineData("song.aiff")]
    [InlineData("song.aif")]
    [InlineData(@"C:\music\artist\track.MP3")]   // case-insensitive + full path
    [InlineData("track.FlAc")]
    public void IsSupported_ReturnsTrue_ForSupportedAudioFiles(string path)
        => SupportedAudioExtensions.IsSupported(path).Should().BeTrue();

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("notes.txt")]
    [InlineData("playlist.m3u")]
    [InlineData("archive.zip")]
    [InlineData("no_extension")]
    [InlineData("")]
    public void IsSupported_ReturnsFalse_ForNonAudioFiles(string path)
        => SupportedAudioExtensions.IsSupported(path).Should().BeFalse();

    [Fact]
    public void All_ContainsOnlyLowercaseDottedExtensions()
    {
        SupportedAudioExtensions.All.Should().OnlyContain(
            ext => ext.StartsWith('.') && ext == ext.ToLowerInvariant());
        SupportedAudioExtensions.All.Should().OnlyHaveUniqueItems();
    }
}
