namespace RDM.Shared.Enums;

/// <summary>
/// What a streaming profile pushes to the cast server as the "now playing" title.
/// Persisted as a string ("NOW_PLAYING"/"STATIC"/"NONE"), so member order carries no meaning.
/// </summary>
public enum StreamTitleMode
{
    /// <summary>Formatted from the playing track, using the Stream Titles format and filters.</summary>
    NowPlaying,

    /// <summary>A fixed string the operator supplies — a station name, a show name.</summary>
    Static,

    /// <summary>Send nothing. The server keeps whatever it had, or shows nothing at all.</summary>
    None
}
