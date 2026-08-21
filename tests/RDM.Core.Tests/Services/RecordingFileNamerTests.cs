using System.Globalization;
using FluentAssertions;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class RecordingFileNamerTests
{
    private const string Dir = @"C:\rec";
    private static readonly DateTime Start = new(2026, 7, 20, 9, 5, 3);

    private static string Build(
        string? prefix = null,
        EncoderFormat format = EncoderFormat.Mp3,
        Func<string, bool>? exists = null,
        DateTime? at = null)
        => RecordingFileNamer.Build(Dir, format, prefix, at ?? Start, exists ?? (_ => false));

    [Fact]
    public void Build_uses_sortable_timestamp_and_default_prefix()
    {
        Build().Should().Be(Path.Combine(Dir, "rec_2026-07-20_09-05-03.mp3"));
    }

    [Fact]
    public void Build_keeps_the_supplied_prefix()
    {
        Build("poranek").Should().Be(Path.Combine(Dir, "poranek_2026-07-20_09-05-03.mp3"));
    }

    [Theory]
    [InlineData(EncoderFormat.Mp3,  "mp3")]
    [InlineData(EncoderFormat.Ogg,  "ogg")]
    [InlineData(EncoderFormat.Opus, "opus")]
    public void Build_uses_the_extension_of_the_format(EncoderFormat format, string extension)
    {
        Build(format: format).Should().EndWith("." + extension);
    }

    [Fact]
    public void Build_appends_a_counter_while_the_name_is_taken()
    {
        var taken = new HashSet<string>
        {
            Path.Combine(Dir, "rec_2026-07-20_09-05-03.mp3"),
            Path.Combine(Dir, "rec_2026-07-20_09-05-03_2.mp3")
        };

        Build(exists: taken.Contains)
            .Should().Be(Path.Combine(Dir, "rec_2026-07-20_09-05-03_3.mp3"));
    }

    [Fact]
    public void Build_gives_up_after_the_collision_limit_instead_of_looping_forever()
    {
        // A predicate that always reports a clash would hang a naive loop.
        var act = () => Build(exists: _ => true);
        act.Should().NotThrow();
    }

    // The prefix can come from a user field, so it must not be able to escape the target folder.
    [Theory]
    [InlineData(@"..\..\etc",   "etc")]     // leading separators are trimmed, not kept as dashes
    [InlineData("a/b",          "a-b")]
    [InlineData("a:b*c?",       "a-b-c")]   // trailing dash from the stripped '?' is trimmed too
    [InlineData("   ",          "rec")]
    [InlineData(null,           "rec")]
    [InlineData("...",          "rec")]
    public void Build_sanitises_the_prefix(string? prefix, string expected)
    {
        Build(prefix).Should().Be(Path.Combine(Dir, $"{expected}_2026-07-20_09-05-03.mp3"));
    }

    [Fact]
    public void Build_stays_inside_the_requested_directory_for_a_traversal_prefix()
    {
        var path = Build(@"..\..\somewhere");
        Path.GetDirectoryName(path).Should().Be(Dir);
    }

    [Fact]
    public void Build_timestamp_does_not_follow_the_current_culture()
    {
        // Regression guard: a Polish UI must produce byte-identical names to an English one.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
            var polish = Build();
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            var english = Build();

            polish.Should().Be(english);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Build_rejects_an_empty_directory()
    {
        var act = () => RecordingFileNamer.Build("  ", EncoderFormat.Mp3, null, Start, _ => false);
        act.Should().Throw<ArgumentException>();
    }
}
