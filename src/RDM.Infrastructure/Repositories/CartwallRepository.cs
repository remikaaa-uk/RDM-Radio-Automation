using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Repositories;

public sealed class CartwallRepository : ICartwallRepository
{
    private readonly IDbConnectionFactory _factory;

    public CartwallRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<Cartwall?> GetByIdAsync(string cartwallId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<CartwallRow>(
            new CommandDefinition(
                """
                SELECT cartwall_id, studio_id, name, page_order, hotkey
                FROM cartwalls
                WHERE cartwall_id = @CartwallId
                """,
                new { CartwallId = cartwallId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<Cartwall>> GetByStudioAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<CartwallRow>(
            new CommandDefinition(
                """
                SELECT cartwall_id, studio_id, name, page_order, hotkey
                FROM cartwalls
                WHERE studio_id = @StudioId
                ORDER BY page_order
                """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<IReadOnlyList<CartSlot>> GetSlotsAsync(string cartwallId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<CartSlotRow>(
            new CommandDefinition(
                """
                SELECT slot_id, cartwall_id, asset_id, slot_number,
                       label, color, hotkey, `loop`, fadeout_ms, output_gain_db
                FROM cart_slots
                WHERE cartwall_id = @CartwallId
                ORDER BY slot_number
                """,
                new { CartwallId = cartwallId },
                cancellationToken: ct));
        return rows.Select(MapSlotRow).ToList();
    }

    public async Task<CartSlot?> GetSlotByIdAsync(string slotId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<CartSlotRow>(
            new CommandDefinition(
                """
                SELECT slot_id, cartwall_id, asset_id, slot_number,
                       label, color, hotkey, `loop`, fadeout_ms, output_gain_db
                FROM cart_slots
                WHERE slot_id = @SlotId
                """,
                new { SlotId = slotId },
                cancellationToken: ct));
        return row is null ? null : MapSlotRow(row);
    }

    public async Task CreateAsync(Cartwall cartwall, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO cartwalls (cartwall_id, studio_id, name, page_order, hotkey)
                VALUES (@CartwallId, @StudioId, @Name, @PageOrder, @Hotkey)
                """,
                new
                {
                    cartwall.CartwallId,
                    cartwall.StudioId,
                    cartwall.Name,
                    cartwall.PageOrder,
                    cartwall.Hotkey
                },
                cancellationToken: ct));
    }

    public async Task UpdateAsync(Cartwall cartwall, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE cartwalls SET
                    name       = @Name,
                    page_order = @PageOrder,
                    hotkey     = @Hotkey
                WHERE cartwall_id = @CartwallId
                """,
                new
                {
                    cartwall.CartwallId,
                    cartwall.Name,
                    cartwall.PageOrder,
                    cartwall.Hotkey
                },
                cancellationToken: ct));
    }

    public async Task DeleteAsync(string cartwallId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM cartwalls WHERE cartwall_id = @CartwallId",
                new { CartwallId = cartwallId },
                cancellationToken: ct));
    }

    public async Task UpsertSlotAsync(CartSlot slot, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE cart_slots SET
                    asset_id       = @AssetId,
                    label          = @Label,
                    color          = @Color,
                    hotkey         = @Hotkey,
                    `loop`         = @Loop,
                    fadeout_ms     = @FadeoutMs,
                    output_gain_db = @OutputGainDb
                WHERE cartwall_id = @CartwallId AND slot_number = @SlotNumber
                """,
                BuildSlotParams(slot),
                cancellationToken: ct));

        if (affected == 0)
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO cart_slots (
                        slot_id, cartwall_id, asset_id, slot_number,
                        label, color, hotkey, `loop`, fadeout_ms, output_gain_db
                    ) VALUES (
                        @SlotId, @CartwallId, @AssetId, @SlotNumber,
                        @Label, @Color, @Hotkey, @Loop, @FadeoutMs, @OutputGainDb
                    )
                    """,
                    BuildSlotParams(slot),
                    cancellationToken: ct));
        }
    }

    public async Task EnsureSlotsAsync(string cartwallId, byte count, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var existing = (await conn.QueryAsync<byte>(
            new CommandDefinition(
                "SELECT slot_number FROM cart_slots WHERE cartwall_id = @CartwallId",
                new { CartwallId = cartwallId },
                cancellationToken: ct))).ToHashSet();

        for (byte i = 1; i <= count; i++)
        {
            if (existing.Contains(i)) continue;
            await conn.ExecuteAsync(
                new CommandDefinition(
                    "INSERT INTO cart_slots (slot_id, cartwall_id, slot_number) VALUES (@SlotId, @CartwallId, @SlotNumber)",
                    new { SlotId = Guid.NewGuid().ToString(), CartwallId = cartwallId, SlotNumber = i },
                    cancellationToken: ct));
        }
    }

    private static object BuildSlotParams(CartSlot s) => new
    {
        s.SlotId,
        s.CartwallId,
        s.AssetId,
        s.SlotNumber,
        s.Label,
        s.Color,
        s.Hotkey,
        Loop         = s.Loop ? 1 : 0,
        s.FadeoutMs,
        s.OutputGainDb
    };

    private static Cartwall MapRow(CartwallRow r) => new()
    {
        CartwallId = r.cartwall_id,
        StudioId   = r.studio_id,
        Name       = r.name,
        PageOrder  = r.page_order,
        Hotkey     = r.hotkey
    };

    private static CartSlot MapSlotRow(CartSlotRow r) => new()
    {
        SlotId       = r.slot_id,
        CartwallId   = r.cartwall_id,
        AssetId      = r.asset_id,
        SlotNumber   = r.slot_number,
        Label        = r.label,
        Color        = r.color,
        Hotkey       = r.hotkey,
        Loop         = r.loop,
        FadeoutMs    = r.fadeout_ms,
        OutputGainDb = r.output_gain_db
    };

    private sealed record CartwallRow(
        string  cartwall_id,
        string  studio_id,
        string  name,
        byte    page_order,
        string? hotkey);

    private sealed record CartSlotRow(
        string   slot_id,
        string   cartwall_id,
        string?  asset_id,
        byte     slot_number,
        string?  label,
        string?  color,
        string?  hotkey,
        bool     loop,
        uint?    fadeout_ms,
        decimal  output_gain_db);
}
