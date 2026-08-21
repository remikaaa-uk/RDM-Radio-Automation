namespace RDM.Infrastructure.Database;

public sealed class Migration_43_0_0_AddEncoderServerDetails : IMigration
{
    public string Version     => "43.0.0";
    public string Description => "Add cast username, SHOUTcast 2 stream id and Icecast PUT method";

    /// <summary>
    /// Three settings that BASSenc needs and the original schema had no room for:
    ///
    /// <c>username</c> — Icecast and SHOUTcast 2 accept one, sent as "username:password". Without
    /// it only Icecast's default <c>source</c> account can be used.
    ///
    /// <c>stream_id</c> — SHOUTcast 2 addresses a stream within the server as
    /// "address:port,streamid". A v2 profile without it cannot connect at all.
    ///
    /// <c>use_put</c> — Icecast 2.4 and later accept the HTTP PUT method; older builds want SOURCE.
    /// BASSenc uses SOURCE unless told otherwise, which is why the default here is 0.
    /// </summary>
    public string UpSql => """
        ALTER TABLE encoder_profiles
            ADD COLUMN IF NOT EXISTS username  VARCHAR(255) NULL AFTER mount,
            ADD COLUMN IF NOT EXISTS stream_id VARCHAR(32)  NULL AFTER username,
            ADD COLUMN IF NOT EXISTS use_put   TINYINT(1)   NOT NULL DEFAULT 0 AFTER use_ssl;
        """;
}
