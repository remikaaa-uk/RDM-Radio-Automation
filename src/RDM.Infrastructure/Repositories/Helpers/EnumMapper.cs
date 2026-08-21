using RDM.Shared.Enums;

namespace RDM.Infrastructure.Repositories.Helpers;

internal static class EnumMapper
{
    public static string ToDb(AssetType v) => v switch
    {
        AssetType.Track          => "TRACK",
        AssetType.Cart           => "CART",
        AssetType.Sweeper        => "SWEEPER",
        AssetType.Voicetrack     => "VOICETRACK",
        AssetType.InternetStream => "INTERNET_STREAM",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static AssetType ToAssetType(string v) => v switch
    {
        "TRACK"           => AssetType.Track,
        "CART"            => AssetType.Cart,
        "SWEEPER"         => AssetType.Sweeper,
        "VOICETRACK"      => AssetType.Voicetrack,
        "INTERNET_STREAM" => AssetType.InternetStream,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(AssetStatus v) => v switch
    {
        AssetStatus.PendingReview => "PENDING_REVIEW",
        AssetStatus.Active        => "ACTIVE",
        AssetStatus.Disabled      => "DISABLED",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static AssetStatus ToAssetStatus(string v) => v switch
    {
        "PENDING_REVIEW" => AssetStatus.PendingReview,
        "ACTIVE"         => AssetStatus.Active,
        "DISABLED"       => AssetStatus.Disabled,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(MarkerType v) => v switch
    {
        MarkerType.Intro  => "INTRO",
        MarkerType.Outro  => "OUTRO",
        MarkerType.Hook   => "HOOK",
        MarkerType.Custom => "CUSTOM",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static MarkerType ToMarkerType(string v) => v switch
    {
        "INTRO"  => MarkerType.Intro,
        "OUTRO"  => MarkerType.Outro,
        "HOOK"   => MarkerType.Hook,
        "CUSTOM" => MarkerType.Custom,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(SegueType v) => v switch
    {
        SegueType.Auto   => "AUTO",
        SegueType.Manual => "MANUAL",
        SegueType.Timed  => "TIMED",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static SegueType ToSegueType(string v) => v switch
    {
        "AUTO"   => SegueType.Auto,
        "MANUAL" => SegueType.Manual,
        "TIMED"  => SegueType.Timed,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(PlaylistItemType v) => v switch
    {
        PlaylistItemType.Asset => "ASSET",
        PlaylistItemType.Dummy => "DUMMY",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static PlaylistItemType ToPlaylistItemType(string v) => v switch
    {
        "ASSET" => PlaylistItemType.Asset,
        "DUMMY" => PlaylistItemType.Dummy,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(PlaylistType v) => v switch
    {
        PlaylistType.OnAir => "ON_AIR",
        PlaylistType.Saved => "SAVED",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static PlaylistType ToPlaylistType(string v) => v switch
    {
        "ON_AIR" => PlaylistType.OnAir,
        "SAVED"  => PlaylistType.Saved,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(SourceType v) => v switch
    {
        SourceType.Playlist => "PLAYLIST",
        SourceType.Cart     => "CART",
        SourceType.Sweeper  => "SWEEPER",
        SourceType.Manual   => "MANUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static SourceType ToSourceType(string v) => v switch
    {
        "PLAYLIST" => SourceType.Playlist,
        "CART"     => SourceType.Cart,
        "SWEEPER"  => SourceType.Sweeper,
        "MANUAL"   => SourceType.Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(SessionState v) => v switch
    {
        SessionState.Idle    => "IDLE",
        SessionState.Playing => "PLAYING",
        SessionState.Paused  => "PAUSED",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static SessionState ToSessionState(string v) => v switch
    {
        "IDLE"    => SessionState.Idle,
        "PLAYING" => SessionState.Playing,
        "PAUSED"  => SessionState.Paused,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(PlaylistMode v) => v switch
    {
        PlaylistMode.Auto       => "AUTO",
        PlaylistMode.LiveAssist => "LIVE_ASSIST",
        PlaylistMode.Manual     => "MANUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static PlaylistMode ToPlaylistMode(string v) => v switch
    {
        "AUTO"        => PlaylistMode.Auto,
        "LIVE_ASSIST" => PlaylistMode.LiveAssist,
        "MANUAL"      => PlaylistMode.Manual,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(DriverType v) => v switch
    {
        DriverType.WasapiShared    => "WASAPI_SHARED",
        DriverType.WasapiExclusive => "WASAPI_EXCLUSIVE",
        DriverType.Asio            => "ASIO",
        DriverType.DirectSound     => "DIRECTSOUND",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static DriverType ToDriverType(string v) => v switch
    {
        "WASAPI_SHARED"    => DriverType.WasapiShared,
        "WASAPI_EXCLUSIVE" => DriverType.WasapiExclusive,
        "ASIO"             => DriverType.Asio,
        "DIRECTSOUND"      => DriverType.DirectSound,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(AudioSampleRate v) => v switch
    {
        AudioSampleRate.Hz44100 => "44100",
        AudioSampleRate.Hz48000 => "48000",
        AudioSampleRate.Hz96000 => "96000",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static AudioSampleRate ToAudioSampleRate(string v) => v switch
    {
        "44100" => AudioSampleRate.Hz44100,
        "48000" => AudioSampleRate.Hz48000,
        "96000" => AudioSampleRate.Hz96000,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(AudioBufferSize v) => v switch
    {
        AudioBufferSize.Samples256  => "256",
        AudioBufferSize.Samples512  => "512",
        AudioBufferSize.Samples1024 => "1024",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static AudioBufferSize ToAudioBufferSize(string v) => v switch
    {
        "256"  => AudioBufferSize.Samples256,
        "512"  => AudioBufferSize.Samples512,
        "1024" => AudioBufferSize.Samples1024,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(AppTheme v) => v switch
    {
        AppTheme.Dark  => "DARK",
        AppTheme.Light => "LIGHT",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static AppTheme ToAppTheme(string v) => v switch
    {
        "DARK"  => AppTheme.Dark,
        "LIGHT" => AppTheme.Light,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(ScheduledEventType v) => v switch
    {
        ScheduledEventType.Repeat  => "REPEAT",
        ScheduledEventType.OneTime => "ONE_TIME",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static ScheduledEventType ToScheduledEventType(string v) => v switch
    {
        "REPEAT"   => ScheduledEventType.Repeat,
        "ONE_TIME" => ScheduledEventType.OneTime,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(UserRole v) => v switch
    {
        UserRole.Admin    => "ADMINISTRATOR",
        UserRole.Operator => "OPERATOR",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static UserRole ToUserRole(string v) => v switch
    {
        "ADMINISTRATOR" => UserRole.Admin,
        "OPERATOR"      => UserRole.Operator,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(EncoderFormat v) => v switch
    {
        EncoderFormat.Mp3  => "MP3",
        EncoderFormat.Ogg  => "OGG",
        EncoderFormat.Opus => "OPUS",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static EncoderFormat ToEncoderFormat(string v) => v switch
    {
        "MP3"  => EncoderFormat.Mp3,
        "OGG"  => EncoderFormat.Ogg,
        "OPUS" => EncoderFormat.Opus,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(StreamTitleMode v) => v switch
    {
        StreamTitleMode.NowPlaying => "NOW_PLAYING",
        StreamTitleMode.Static     => "STATIC",
        StreamTitleMode.None       => "NONE",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static StreamTitleMode ToStreamTitleMode(string v) => v switch
    {
        "NOW_PLAYING" => StreamTitleMode.NowPlaying,
        "STATIC"      => StreamTitleMode.Static,
        "NONE"        => StreamTitleMode.None,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static string ToDb(CastServerType v) => v switch
    {
        CastServerType.Shoutcast   => "SHOUTCAST",
        CastServerType.ShoutcastV2 => "SHOUTCAST2",
        CastServerType.Icecast     => "ICECAST",
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };

    public static CastServerType ToCastServerType(string v) => v switch
    {
        "SHOUTCAST"  => CastServerType.Shoutcast,
        "SHOUTCAST2" => CastServerType.ShoutcastV2,
        "ICECAST"    => CastServerType.Icecast,
        _ => throw new ArgumentOutOfRangeException(nameof(v), v, null)
    };
}
