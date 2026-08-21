namespace RDM.Infrastructure.Database;

/// <summary>
/// Adds a free per-item layout offset to playlist_items. <c>lead_in_ms</c> is the
/// signed distance the item's start precedes the previous item's end
/// (positive = overlap / earlier start, negative = gap). It is the segue editor's
/// source of truth for free positioning and is not bounded by the previous item's
/// duration, unlike <c>crossfade_ms</c>. When NULL, layout falls back to crossfade.
/// </summary>
public sealed class Migration_8_0_0_AddPlaylistItemLeadIn : IMigration
{
    public string Version     => "8.0.0";
    public string Description => "Add lead_in_ms to playlist_items for free segue positioning";

    public string UpSql => """
        ALTER TABLE playlist_items
            ADD COLUMN IF NOT EXISTS lead_in_ms INT NULL AFTER crossfade_ms;
        """;
}
