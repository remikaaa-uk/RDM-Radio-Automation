using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Repositories;

public sealed class ScriptRepository : IScriptRepository
{
    private readonly IDbConnectionFactory _dbFactory;

    public ScriptRepository(IDbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<Script?> GetByIdAsync(Guid scriptId)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var row = await conn.QueryFirstOrDefaultAsync<ScriptRow>(
            "SELECT id, name, script_body, is_enabled, language FROM scripts WHERE id = @Id AND is_enabled = 1",
            new { Id = scriptId.ToString() });

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<Script>> GetAllAsync()
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var rows = await conn.QueryAsync<ScriptRow>(
            "SELECT id, name, script_body, is_enabled, language FROM scripts ORDER BY name");

        return rows.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<Script>> GetAllEnabledAsync()
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var rows = await conn.QueryAsync<ScriptRow>(
            "SELECT id, name, script_body, is_enabled, language FROM scripts WHERE is_enabled = 1 ORDER BY name");

        return rows.Select(Map).ToList();
    }

    public async Task SaveAsync(Script script)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        await conn.ExecuteAsync("""
            INSERT INTO scripts (id, name, language, script_body, is_enabled)
            VALUES (@Id, @Name, @Language, @ScriptBody, @IsEnabled)
            ON DUPLICATE KEY UPDATE
                name        = VALUES(name),
                language    = VALUES(language),
                script_body = VALUES(script_body),
                is_enabled  = VALUES(is_enabled)
            """,
            new
            {
                Id           = script.Id.ToString(),
                script.Name,
                script.Language,
                ScriptBody   = script.ScriptBody,
                script.IsEnabled
            });
    }

    public async Task DeleteAsync(Guid scriptId)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM scripts WHERE id = @Id",
            new { Id = scriptId.ToString() });
    }

    private static Script Map(ScriptRow row) => new()
    {
        Id         = Guid.Parse(row.id),
        Name       = row.name,
        ScriptBody = row.script_body,
        IsEnabled  = row.is_enabled,
        Language   = row.language
    };

    private sealed record ScriptRow(
        string id,
        string name,
        string script_body,
        bool   is_enabled,
        string language);
}
