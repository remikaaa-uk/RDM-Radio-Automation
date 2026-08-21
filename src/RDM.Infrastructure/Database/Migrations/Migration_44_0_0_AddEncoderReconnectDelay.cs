namespace RDM.Infrastructure.Database;

public sealed class Migration_44_0_0_AddEncoderReconnectDelay : IMigration
{
    public string Version     => "44.0.0";
    public string Description => "Add per-profile reconnect delay for dropped cast connections";

    /// <summary>
    /// <c>reconnect_delay_seconds</c> — how long the session waits between reconnect attempts after
    /// the cast connection drops. A fixed interval, repeated until the server comes back. Default 10;
    /// the encoder clamps out-of-range values to 2–1800, so a bad row can never busy-loop the socket.
    /// </summary>
    public string UpSql => """
        ALTER TABLE encoder_profiles
            ADD COLUMN IF NOT EXISTS reconnect_delay_seconds INT NOT NULL DEFAULT 10 AFTER auto_start;
        """;
}
