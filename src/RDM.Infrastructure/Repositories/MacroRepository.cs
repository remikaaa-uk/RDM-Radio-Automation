using Dapper;
using RDM.Core.Entities;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Repositories;

public sealed class MacroRepository : IMacroRepository
{
    private readonly IDbConnectionFactory _dbFactory;

    public MacroRepository(IDbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<Macro?> GetByIdAsync(Guid macroId)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var macro = await conn.QueryFirstOrDefaultAsync<MacroRow>(
            "SELECT id, name, is_enabled FROM macros WHERE id = @Id AND is_enabled = 1",
            new { Id = macroId.ToString() });

        if (macro is null) return null;

        var steps = await LoadStepsAsync(conn, macroId);
        return MapMacro(macro, steps);
    }

    public async Task<IReadOnlyList<Macro>> GetAllAsync()
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var macros = (await conn.QueryAsync<MacroRow>(
            "SELECT id, name, is_enabled FROM macros ORDER BY name")).AsList();

        if (macros.Count == 0) return [];

        var allSteps = (await conn.QueryAsync<MacroStepRow>("""
            SELECT s.id, s.macro_id, s.step_order, s.action_id, s.parameter,
                   s.output_device_type, s.output_device_id, s.output_command, s.delay_after_ms
            FROM macro_steps s
            ORDER BY s.macro_id, s.step_order
            """)).AsList();

        var stepsByMacro = allSteps.ToLookup(s => s.macro_id, StringComparer.OrdinalIgnoreCase);

        return macros
            .Select(m => MapMacro(m, stepsByMacro[m.id]))
            .ToList();
    }

    public async Task<IReadOnlyList<Macro>> GetAllEnabledAsync()
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var macros = (await conn.QueryAsync<MacroRow>(
            "SELECT id, name, is_enabled FROM macros WHERE is_enabled = 1")).AsList();

        if (macros.Count == 0) return [];

        var allSteps = (await conn.QueryAsync<MacroStepRow>("""
            SELECT s.id, s.macro_id, s.step_order, s.action_id, s.parameter,
                   s.output_device_type, s.output_device_id, s.output_command, s.delay_after_ms
            FROM macro_steps s
            INNER JOIN macros m ON m.id = s.macro_id
            WHERE m.is_enabled = 1
            ORDER BY s.macro_id, s.step_order
            """)).AsList();

        var stepsByMacro = allSteps.ToLookup(s => s.macro_id, StringComparer.OrdinalIgnoreCase);

        return macros
            .Select(m => MapMacro(m, stepsByMacro[m.id]))
            .ToList();
    }

    private static async Task<IEnumerable<MacroStepRow>> LoadStepsAsync(
        System.Data.IDbConnection conn, Guid macroId)
    {
        return await conn.QueryAsync<MacroStepRow>("""
            SELECT id, macro_id, step_order, action_id, parameter,
                   output_device_type, output_device_id, output_command, delay_after_ms
            FROM macro_steps
            WHERE macro_id = @MacroId
            ORDER BY step_order
            """,
            new { MacroId = macroId.ToString() });
    }

    private static Macro MapMacro(MacroRow row, IEnumerable<MacroStepRow> stepRows)
    {
        return new Macro
        {
            Id        = Guid.Parse(row.id),
            Name      = row.name,
            IsEnabled = row.is_enabled,
            Steps     = stepRows.Select(MapStep).ToList()
        };
    }

    private static MacroStep MapStep(MacroStepRow row)
    {
        ActionId? actionId = null;
        if (!string.IsNullOrEmpty(row.action_id) && Enum.TryParse<ActionId>(row.action_id, out var parsed))
            actionId = parsed;

        return new MacroStep
        {
            Id               = Guid.Parse(row.id),
            MacroId          = Guid.Parse(row.macro_id),
            StepOrder        = row.step_order,
            ActionId         = actionId,
            Parameter        = row.parameter,
            OutputDeviceType = row.output_device_type,
            OutputDeviceId   = row.output_device_id,
            OutputCommand    = row.output_command,
            DelayAfterMs     = row.delay_after_ms
        };
    }

    public async Task SaveMacroAsync(Macro macro)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO macros (id, name, is_enabled)
            VALUES (@Id, @Name, @IsEnabled)
            ON DUPLICATE KEY UPDATE
                name       = VALUES(name),
                is_enabled = VALUES(is_enabled)
            """,
            new { Id = macro.Id.ToString(), macro.Name, macro.IsEnabled });
    }

    public async Task DeleteMacroAsync(Guid macroId)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM macros WHERE id = @Id",
            new { Id = macroId.ToString() });
    }

    public async Task SaveStepAsync(MacroStep step)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync("""
            INSERT INTO macro_steps
                (id, macro_id, step_order, action_id, parameter,
                 output_device_type, output_device_id, output_command, delay_after_ms)
            VALUES
                (@Id, @MacroId, @StepOrder, @ActionId, @Parameter,
                 @OutputDeviceType, @OutputDeviceId, @OutputCommand, @DelayAfterMs)
            ON DUPLICATE KEY UPDATE
                step_order         = VALUES(step_order),
                action_id          = VALUES(action_id),
                parameter          = VALUES(parameter),
                output_device_type = VALUES(output_device_type),
                output_device_id   = VALUES(output_device_id),
                output_command     = VALUES(output_command),
                delay_after_ms     = VALUES(delay_after_ms)
            """,
            new
            {
                Id               = step.Id.ToString(),
                MacroId          = step.MacroId.ToString(),
                step.StepOrder,
                ActionId         = step.ActionId?.ToString(),
                step.Parameter,
                step.OutputDeviceType,
                step.OutputDeviceId,
                step.OutputCommand,
                step.DelayAfterMs
            });
    }

    public async Task DeleteStepAsync(Guid stepId)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM macro_steps WHERE id = @Id",
            new { Id = stepId.ToString() });
    }

    private sealed record MacroRow(string id, string name, bool is_enabled);

    private sealed record MacroStepRow(
        string  id,
        string  macro_id,
        int     step_order,
        string? action_id,
        string? parameter,
        string? output_device_type,
        string? output_device_id,
        string? output_command,
        int     delay_after_ms);
}
