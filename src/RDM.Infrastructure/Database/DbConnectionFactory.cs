using System.Data;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Database;

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public DbConnectionFactory(IConfiguration configuration)
    {
        var host = configuration["database:host"]
            ?? throw new InvalidOperationException("database:host is not configured.");
        var port = configuration["database:port"] ?? "3306";
        var name = configuration["database:name"]
            ?? throw new InvalidOperationException("database:name is not configured.");
        var username = configuration["database:username"]
            ?? throw new InvalidOperationException("database:username is not configured.");
        var password = configuration["database:password"]
            ?? throw new InvalidOperationException("database:password is not configured.");

        _connectionString = new MySqlConnectionStringBuilder
        {
            Server       = host,
            Port         = uint.Parse(port),
            Database     = name,
            UserID       = username,
            Password     = password,
            CharacterSet = "utf8mb4",
            // Return CHAR(36) columns as plain string, not as Guid.
            // Without this MySqlConnector auto-detects GUIDs and returns
            // Guid objects that Dapper cannot convert via IConvertible.
            GuidFormat   = MySqlGuidFormat.None
        }.ConnectionString;
    }

    public IDbConnection CreateConnection() => new MySqlConnection(_connectionString);

    public async Task<IDbConnection> CreateOpenConnectionAsync(CancellationToken ct = default)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }
}
