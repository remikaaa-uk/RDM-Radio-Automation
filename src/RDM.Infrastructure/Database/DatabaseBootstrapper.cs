using Dapper;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using RDM.Core.Services;
using BC = BCrypt.Net.BCrypt;

namespace RDM.Infrastructure.Database;

public class DatabaseBootstrapper
{
    private readonly string _rootConnectionString;
    private readonly string _dbConnectionString;
    private readonly string _databaseName;
    private readonly MigrationRunner _migrationRunner;

    private static readonly IReadOnlyList<IMigration> Migrations =
    [
        new Migration_1_0_0_InitialSchema(),
        new Migration_3_6_0_AddUsers(),
        new Migration_4_0_0_AddCueMarkers(),
        new Migration_5_0_0_AddAutoFadeOutDuration(),
        new Migration_6_0_0_DropLegacyCuePoints(),
        new Migration_7_0_0_AddPlaylistType(),
        new Migration_8_0_0_AddPlaylistItemLeadIn(),
        new Migration_9_0_0_AddGenre(),
        new Migration_10_0_0_AddSubcategoriesAndGenres(),
        new Migration_11_0_0_SeedGenres(),
        new Migration_12_0_0_AddAssetWaveforms(),
        new Migration_13_0_0_DropWaveformColumns(),
        new Migration_14_0_0_AddMicSettings(),
        new Migration_15_0_0_AddVolumeEnvelopeToPlaylistItems(),
        new Migration_16_0_0_FixPlayoutLogFkOnDeleteSetNull(),
        new Migration_17_0_0_AddSweeperGainDb(),
        new Migration_18_0_0_AddStreamUrl(),
        new Migration_19_0_0_AddInternetStreamAssetType(),
        new Migration_20_0_0_WideChecksumColumn(),
        new Migration_21_0_0_AddTriggerActionMappings(),
        new Migration_22_0_0_AddFeedbackMappings(),
        new Migration_23_0_0_AddDrMixerFeedbackFields(),
        new Migration_24_0_0_AddMacros(),
        new Migration_25_0_0_AddSerialFeedbackField(),
        new Migration_26_0_0_AddScripts(),
        new Migration_27_0_0_AddMusicFormatId(),
        new Migration_28_0_0_RenameSweeperGainToDucking(),
        new Migration_29_0_0_AddSweeperSubcategoryId(),
        new Migration_30_0_0_AddAssetVariableDuration(),
        // UWAGA: wersja 31.0.0 celowo pominięta (nie ma zgubionego pliku). MigrationRunner
        // śledzi migracje po stringu Version w db_migrations i sortuje przez Version.Parse —
        // nie zakłada ciągłości numeracji, więc luka jest nieszkodliwa. Kolejny numer: 44.0.0.
        new Migration_32_0_0_DropMicVoxDucking(),
        new Migration_33_0_0_DropInputFadeDownMusic(),
        new Migration_34_0_0_DropDuckingEnabled(),
        new Migration_35_0_0_DropInputLineFade(),
        new Migration_36_0_0_AddEmergencyPlaylistId(),
        new Migration_37_0_0_DropBitDepth(),
        new Migration_38_0_0_DropCrossfadeCurve(),
        new Migration_39_0_0_DropVoicetrackDevice(),
        new Migration_40_0_0_AddAuxFadeoutMs(),
        new Migration_41_0_0_AddEncoderProfiles(),
        new Migration_42_0_0_AddEncoderArmedAndTitles(),
        new Migration_43_0_0_AddEncoderServerDetails(),
        new Migration_44_0_0_AddEncoderReconnectDelay()
    ];

    private static readonly string[] DefaultAssetFormats =
        ["Music", "Jingle", "Sweeper", "Voiceover", "Commercial", "News", "Promo", "Podcast", "Effect", "Other"];


    public DatabaseBootstrapper(IConfiguration configuration, MigrationRunner migrationRunner)
    {
        _migrationRunner = migrationRunner;

        var host = configuration["database:host"]
            ?? throw new InvalidOperationException("database:host is not configured.");
        var port = configuration["database:port"] ?? "3306";
        _databaseName = configuration["database:name"] ?? "rdm";
        var username = configuration["database:username"]
            ?? throw new InvalidOperationException("database:username is not configured.");
        var password = configuration["database:password"]
            ?? throw new InvalidOperationException("database:password is not configured.");

        var rootBuilder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = uint.Parse(port),
            UserID = username,
            Password = password,
            CharacterSet = "utf8mb4"
        };
        _rootConnectionString = rootBuilder.ConnectionString;

        var dbBuilder = new MySqlConnectionStringBuilder
        {
            Server = host,
            Port = uint.Parse(port),
            Database = _databaseName,
            UserID = username,
            Password = password,
            CharacterSet = "utf8mb4"
        };
        _dbConnectionString = dbBuilder.ConnectionString;
    }

    public virtual async Task<BootstrapResult> RunAsync(CancellationToken ct = default)
    {
        await CreateDatabaseIfNotExistsAsync(ct);
        await using var connection = new MySqlConnection(_dbConnectionString);
        await connection.OpenAsync(ct);

        await CreateMigrationsTableAsync(connection, ct);

        var appliedBefore = (await connection.QueryAsync<string>(
            "SELECT version FROM db_migrations")).ToHashSet();

        bool isFirstRun = appliedBefore.Count == 0;

        await _migrationRunner.RunAsync(connection, Migrations, ct);

        string? adminPassword = null;
        if (isFirstRun)
            adminPassword = await SeedAsync(connection, ct);

        return new BootstrapResult(isFirstRun, adminPassword);
    }

    private async Task CreateDatabaseIfNotExistsAsync(CancellationToken ct)
    {
        await using var connection = new MySqlConnection(_rootConnectionString);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(
            $"CREATE DATABASE IF NOT EXISTS `{_databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;");
    }

    private static async Task CreateMigrationsTableAsync(MySqlConnection connection, CancellationToken ct)
    {
        await connection.ExecuteAsync(
            new CommandDefinition("""
                CREATE TABLE IF NOT EXISTS db_migrations (
                    migration_id  INT UNSIGNED    NOT NULL AUTO_INCREMENT,
                    version       VARCHAR(20)     NOT NULL,
                    description   VARCHAR(255)    NOT NULL,
                    applied_at    DATETIME        NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (migration_id),
                    UNIQUE KEY uq_migration_version (version)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
                """, cancellationToken: ct));
    }

    private static async Task<string> SeedAsync(MySqlConnection connection, CancellationToken ct)
    {
        foreach (var name in DefaultAssetFormats)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO asset_formats (format_id, name) VALUES (@FormatId, @Name)",
                    new { FormatId = Guid.NewGuid().ToString(), Name = name },
                    cancellationToken: ct));
        }

        var studioId = Guid.NewGuid().ToString();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO studios (studio_id, name) VALUES (@StudioId, @Name)",
                new { StudioId = studioId, Name = "Domyślne studio" },
                cancellationToken: ct));

        var settingsId = Guid.NewGuid().ToString();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO audio_settings (settings_id, studio_id) VALUES (@SettingsId, @StudioId)",
                new { SettingsId = settingsId, StudioId = studioId },
                cancellationToken: ct));

        var slotsPerPage = await connection.ExecuteScalarAsync<byte>(
            new CommandDefinition(
                "SELECT cartwall_slots_per_page FROM audio_settings WHERE settings_id = @SettingsId",
                new { SettingsId = settingsId },
                cancellationToken: ct));

        var cartwallId = Guid.NewGuid().ToString();
        await connection.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO cartwalls (cartwall_id, studio_id, name, page_order) VALUES (@CartwallId, @StudioId, @Name, 0)",
                new { CartwallId = cartwallId, StudioId = studioId, Name = "Cartwall 1" },
                cancellationToken: ct));

        for (byte i = 1; i <= slotsPerPage; i++)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO cart_slots (slot_id, cartwall_id, slot_number)
                    VALUES (@SlotId, @CartwallId, @SlotNumber)
                    """,
                    new { SlotId = Guid.NewGuid().ToString(), CartwallId = cartwallId, SlotNumber = i },
                    cancellationToken: ct));
        }

        var adminPassword = PasswordGenerator.Generate();
        var passwordHash = BC.HashPassword(adminPassword, workFactor: 12);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO users (user_id, studio_id, username, password_hash, role)
                VALUES (@UserId, @StudioId, @Username, @PasswordHash, @Role)
                """,
                new
                {
                    UserId = Guid.NewGuid().ToString(),
                    StudioId = studioId,
                    Username = "admin",
                    PasswordHash = passwordHash,
                    Role = "ADMINISTRATOR"
                },
                cancellationToken: ct));

        return adminPassword;
    }
}

public record BootstrapResult(bool IsFirstRun, string? AdminPassword);
