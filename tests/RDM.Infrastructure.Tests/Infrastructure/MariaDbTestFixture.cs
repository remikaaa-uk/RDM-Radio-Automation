using Dapper;
using MySqlConnector;
using RDM.Core.Interfaces;
using Xunit;

namespace RDM.Infrastructure.Tests.Infrastructure;

[CollectionDefinition("MariaDb", DisableParallelization = true)]
public sealed class MariaDbCollection : ICollectionFixture<MariaDbTestFixture> { }

/// <summary>
/// Connects to the real MariaDB configured in rdm.config.json.
/// Migrations are already applied — no Docker/Testcontainers needed.
/// Each fixture instance gets a unique StudioId for test isolation;
/// the studio row is deleted in DisposeAsync after all tests in the
/// collection have cleaned up their own data.
/// </summary>
public sealed class MariaDbTestFixture : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("RDM_TEST_DB_CONNECTION_STRING")
        ?? "Server=localhost;Port=3306;Database=rdm;User=changeme;Password=changeme;GuidFormat=None;";

    public IDbConnectionFactory Factory { get; private set; } = null!;

    public string StudioId { get; } = Guid.NewGuid().ToString();

    public async Task InitializeAsync()
    {
        Factory = new TestDbConnectionFactory(ConnectionString);

        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(
            "INSERT INTO studios (studio_id, name) VALUES (@StudioId, 'Test Studio')",
            new { StudioId });
    }

    public async Task DisposeAsync()
    {
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            "DELETE FROM studios WHERE studio_id = @StudioId", new { StudioId });
    }
}
