using FluentAssertions;
using RDM.Infrastructure.Database;
using Xunit;

namespace RDM.Infrastructure.Tests;

public sealed class DatabaseBootstrapperTests
{
    [Fact]
    public void BootstrapResult_IsFirstRun_True_WhenNoMigrationsApplied()
    {
        var result = new BootstrapResult(IsFirstRun: true, AdminPassword: "secret");

        result.IsFirstRun.Should().BeTrue();
        result.AdminPassword.Should().Be("secret");
    }

    [Fact]
    public void BootstrapResult_IsFirstRun_False_WhenMigrationsAlreadyApplied()
    {
        var result = new BootstrapResult(IsFirstRun: false, AdminPassword: null);

        result.IsFirstRun.Should().BeFalse();
        result.AdminPassword.Should().BeNull();
    }

    [Fact]
    public void RegisteredMigrations_ContainBothExpectedVersions()
    {
        var migrations = new IMigration[]
        {
            new Migration_1_0_0_InitialSchema(),
            new Migration_3_6_0_AddUsers()
        };

        migrations.Should().HaveCount(2);
        migrations.Select(m => m.Version).Should().Contain("1.0.0").And.Contain("3.6.0");
    }

    [Fact]
    public void InitialSchema_Version_Is_1_0_0()
    {
        new Migration_1_0_0_InitialSchema().Version.Should().Be("1.0.0");
    }

    [Fact]
    public void AddUsers_Version_Is_3_6_0()
    {
        new Migration_3_6_0_AddUsers().Version.Should().Be("3.6.0");
    }

    [Fact]
    public void Migrations_AreInAscendingVersionOrder()
    {
        var migrations = new IMigration[]
        {
            new Migration_1_0_0_InitialSchema(),
            new Migration_3_6_0_AddUsers()
        };

        var versions = migrations.Select(m => System.Version.Parse(m.Version)).ToList();

        versions.Should().BeInAscendingOrder();
    }

    [Fact]
    public void InitialSchema_UpSql_ContainsAllRequiredTables()
    {
        var sql = new Migration_1_0_0_InitialSchema().UpSql;

        var expectedTables = new[]
        {
            "studios", "audio_devices", "asset_formats", "audio_settings",
            "assets", "asset_cue_points", "playlists", "playlist_items",
            "cartwalls", "cart_slots", "playback_sessions", "playout_log",
            "scheduled_events"
        };

        foreach (var table in expectedTables)
            sql.Should().Contain(table, because: $"table {table} must be in the initial schema");
    }

    [Fact]
    public void InitialSchema_UpSql_ContainsAllRequiredIndexes()
    {
        var sql = new Migration_1_0_0_InitialSchema().UpSql;

        var expectedIndexes = new[]
        {
            "idx_assets_type", "idx_assets_format", "idx_assets_title", "idx_assets_artist",
            "idx_assets_damaged", "idx_assets_status", "idx_assets_dates", "idx_assets_played",
            "idx_cue_asset", "idx_items_position", "idx_items_type",
            "idx_cartwalls_studio", "idx_log_started", "idx_log_asset",
            "idx_devices_studio", "idx_events_studio"
        };

        foreach (var index in expectedIndexes)
            sql.Should().Contain(index, because: $"index {index} must be in the initial schema");
    }

    [Fact]
    public void AddUsers_UpSql_ContainsUsersTable()
    {
        var sql = new Migration_3_6_0_AddUsers().UpSql;

        sql.Should().Contain("CREATE TABLE IF NOT EXISTS users");
        sql.Should().Contain("password_hash");
        sql.Should().Contain("ADMINISTRATOR");
        sql.Should().Contain("OPERATOR");
    }

    [Fact]
    public void InitialSchema_UpSql_UsesEventHourColumn_NotTime()
    {
        var sql = new Migration_1_0_0_InitialSchema().UpSql;

        sql.Should().Contain("event_hour");
        sql.Should().Contain("idx_events_studio");
        sql.Should().NotContain("enabled, time)", because: "the corrected index uses event_hour, not time");
    }
}
