using Dapper;
using MySqlConnector;

namespace RDM.Infrastructure.Database;

public sealed class MigrationRunner
{
    public async Task<IReadOnlyList<string>> RunAsync(
        MySqlConnection connection,
        IReadOnlyList<IMigration> migrations,
        CancellationToken ct)
    {
        var appliedVersions = (await connection.QueryAsync<string>(
            "SELECT version FROM db_migrations")).ToHashSet();

        var pending = migrations
            .Where(m => !appliedVersions.Contains(m.Version))
            .OrderBy(m => System.Version.Parse(m.Version))
            .ToList();

        var newlyApplied = new List<string>(pending.Count);

        foreach (var migration in pending)
        {
            ct.ThrowIfCancellationRequested();

            await using var tx = await connection.BeginTransactionAsync(ct);
            try
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(migration.UpSql, transaction: tx, cancellationToken: ct));

                await connection.ExecuteAsync(
                    new CommandDefinition(
                        "INSERT INTO db_migrations (version, description) VALUES (@Version, @Description)",
                        new { migration.Version, migration.Description },
                        transaction: tx,
                        cancellationToken: ct));

                await tx.CommitAsync(ct);
                newlyApplied.Add(migration.Version);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }

        return newlyApplied;
    }

    public static IReadOnlyList<IMigration> GetPendingMigrations(
        IReadOnlyList<IMigration> allMigrations,
        IReadOnlySet<string> appliedVersions)
    {
        return allMigrations
            .Where(m => !appliedVersions.Contains(m.Version))
            .OrderBy(m => System.Version.Parse(m.Version))
            .ToList();
    }
}
