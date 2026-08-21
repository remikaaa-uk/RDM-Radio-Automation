using System.Data;
using MySqlConnector;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Tests.Infrastructure;

internal sealed class TestDbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public TestDbConnectionFactory(string connectionString) => _connectionString = connectionString;

    public IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<IDbConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
