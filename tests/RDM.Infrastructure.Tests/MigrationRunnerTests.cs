using FluentAssertions;
using RDM.Infrastructure.Database;
using Xunit;

namespace RDM.Infrastructure.Tests;

public sealed class MigrationRunnerTests
{
    private static IMigration MakeMigration(string version) =>
        new FakeMigration(version, $"Migration {version}", "SELECT 1;");

    [Fact]
    public void GetPendingMigrations_ReturnsAll_WhenNoneApplied()
    {
        var migrations = new[]
        {
            MakeMigration("1.0.0"),
            MakeMigration("3.6.0")
        };

        var pending = MigrationRunner.GetPendingMigrations(migrations, new HashSet<string>());

        pending.Should().HaveCount(2);
    }

    [Fact]
    public void GetPendingMigrations_SkipsAlreadyApplied()
    {
        var migrations = new[]
        {
            MakeMigration("1.0.0"),
            MakeMigration("3.6.0")
        };
        var applied = new HashSet<string> { "1.0.0" };

        var pending = MigrationRunner.GetPendingMigrations(migrations, applied);

        pending.Should().ContainSingle()
            .Which.Version.Should().Be("3.6.0");
    }

    [Fact]
    public void GetPendingMigrations_ReturnsEmpty_WhenAllApplied()
    {
        var migrations = new[]
        {
            MakeMigration("1.0.0"),
            MakeMigration("3.6.0")
        };
        var applied = new HashSet<string> { "1.0.0", "3.6.0" };

        var pending = MigrationRunner.GetPendingMigrations(migrations, applied);

        pending.Should().BeEmpty();
    }

    [Fact]
    public void GetPendingMigrations_ReturnsSortedByVersion()
    {
        var migrations = new[]
        {
            MakeMigration("3.6.0"),
            MakeMigration("1.0.0"),
            MakeMigration("2.0.0")
        };

        var pending = MigrationRunner.GetPendingMigrations(migrations, new HashSet<string>());

        pending.Select(m => m.Version).Should().Equal("1.0.0", "2.0.0", "3.6.0");
    }

    [Fact]
    public void GetPendingMigrations_SortsNumerically_NotLexicographically()
    {
        var migrations = new[]
        {
            MakeMigration("1.0.0"),
            MakeMigration("1.10.0"),
            MakeMigration("1.2.0")
        };

        var pending = MigrationRunner.GetPendingMigrations(migrations, new HashSet<string>());

        pending.Select(m => m.Version).Should().Equal("1.0.0", "1.2.0", "1.10.0");
    }

    private sealed record FakeMigration(string Version, string Description, string UpSql) : IMigration;
}
