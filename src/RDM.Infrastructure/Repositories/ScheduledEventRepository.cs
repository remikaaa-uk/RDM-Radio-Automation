using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Repositories.Helpers;

namespace RDM.Infrastructure.Repositories;

public sealed class ScheduledEventRepository : IScheduledEventRepository
{
    private readonly IDbConnectionFactory _factory;

    public ScheduledEventRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<ScheduledEvent?> GetByIdAsync(string eventId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<ScheduledEventRow>(
            new CommandDefinition(
                """
                SELECT event_id, studio_id, name, event_type, category, enabled,
                       event_hour, only_on_date, days, hours, smart_timing,
                       actions, last_fired_at, skip_next, created_at
                FROM scheduled_events
                WHERE event_id = @EventId
                """,
                new { EventId = eventId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<ScheduledEvent>> GetByStudioAsync(
        string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<ScheduledEventRow>(
            new CommandDefinition(
                """
                SELECT event_id, studio_id, name, event_type, category, enabled,
                       event_hour, only_on_date, days, hours, smart_timing,
                       actions, last_fired_at, skip_next, created_at
                FROM scheduled_events
                WHERE studio_id = @StudioId
                ORDER BY event_hour
                """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<IReadOnlyList<ScheduledEvent>> GetEnabledAsync(
        string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<ScheduledEventRow>(
            new CommandDefinition(
                """
                SELECT event_id, studio_id, name, event_type, category, enabled,
                       event_hour, only_on_date, days, hours, smart_timing,
                       actions, last_fired_at, skip_next, created_at
                FROM scheduled_events
                WHERE studio_id = @StudioId AND enabled = 1
                ORDER BY event_hour
                """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task CreateAsync(ScheduledEvent e, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO scheduled_events (
                    event_id, studio_id, name, event_type, category, enabled,
                    event_hour, only_on_date, days, hours, smart_timing,
                    actions, last_fired_at, skip_next, created_at
                ) VALUES (
                    @EventId, @StudioId, @Name, @EventType, @Category, @Enabled,
                    @EventHour, @OnlyOnDate, @Days, @Hours, @SmartTiming,
                    @Actions, @LastFiredAt, @SkipNext, @CreatedAt
                )
                """,
                BuildParams(e),
                cancellationToken: ct));
    }

    public async Task UpdateAsync(ScheduledEvent e, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE scheduled_events SET
                    name          = @Name,
                    event_type    = @EventType,
                    category      = @Category,
                    enabled       = @Enabled,
                    event_hour    = @EventHour,
                    only_on_date  = @OnlyOnDate,
                    days          = @Days,
                    hours         = @Hours,
                    smart_timing  = @SmartTiming,
                    actions       = @Actions,
                    last_fired_at = @LastFiredAt,
                    skip_next     = @SkipNext
                WHERE event_id = @EventId
                """,
                BuildParams(e),
                cancellationToken: ct));
    }

    public async Task UpdateSkipNextAsync(string eventId, bool skipNext, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE scheduled_events SET skip_next = @SkipNext WHERE event_id = @EventId",
                new { EventId = eventId, SkipNext = skipNext ? 1 : 0 },
                cancellationToken: ct));
    }

    public async Task UpdateLastFiredAsync(string eventId, DateTime firedAt, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE scheduled_events SET last_fired_at = @FiredAt WHERE event_id = @EventId",
                new { EventId = eventId, FiredAt = firedAt },
                cancellationToken: ct));
    }

    public async Task DeleteAsync(string eventId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM scheduled_events WHERE event_id = @EventId",
                new { EventId = eventId },
                cancellationToken: ct));
    }

    private static object BuildParams(ScheduledEvent e) => new
    {
        e.EventId,
        e.StudioId,
        e.Name,
        EventType    = EnumMapper.ToDb(e.EventType),
        e.Category,
        Enabled      = e.Enabled     ? 1 : 0,
        e.EventHour,
        // DateOnly? → DateTime? for Dapper 2.1 compatibility
        OnlyOnDate   = e.OnlyOnDate.HasValue
                           ? (DateTime?)e.OnlyOnDate.Value.ToDateTime(TimeOnly.MinValue)
                           : null,
        e.Days,
        e.Hours,
        SmartTiming  = e.SmartTiming ? 1 : 0,
        e.Actions,
        e.LastFiredAt,
        SkipNext     = e.SkipNext    ? 1 : 0,
        e.CreatedAt
    };

    private static ScheduledEvent MapRow(ScheduledEventRow r) => new()
    {
        EventId     = r.event_id,
        StudioId    = r.studio_id,
        Name        = r.name,
        EventType   = EnumMapper.ToScheduledEventType(r.event_type),
        Category    = r.category,
        Enabled     = r.enabled,
        EventHour   = r.event_hour,
        // DateTime? → DateOnly? conversion
        OnlyOnDate  = r.only_on_date.HasValue
                          ? DateOnly.FromDateTime(r.only_on_date.Value)
                          : null,
        Days        = r.days,
        Hours       = r.hours,
        SmartTiming = r.smart_timing,
        Actions     = r.actions,
        LastFiredAt = r.last_fired_at,
        SkipNext    = r.skip_next,
        CreatedAt   = r.created_at
    };

    private sealed record ScheduledEventRow(
        string    event_id,
        string    studio_id,
        string    name,
        string    event_type,
        string    category,
        bool      enabled,
        TimeSpan? event_hour,
        DateTime? only_on_date,
        string    days,
        string    hours,
        bool      smart_timing,
        string    actions,
        DateTime? last_fired_at,
        bool      skip_next,
        DateTime  created_at);
}
