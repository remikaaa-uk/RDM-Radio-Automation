using CommunityToolkit.Mvvm.ComponentModel;
using RDM.Shared.DTOs;
using System.Collections.Generic;

namespace RDM.UI.ViewModels;

/// <summary>
/// The editable form behind the profile dialog. Deliberately a flat bag of strings and numbers
/// rather than the DTO itself: the dialog must be able to hold a half-typed, invalid state without
/// that being expressible as a profile.
///
/// The password is write-only in both directions. Editing an existing profile starts with an empty
/// box and <see cref="HadPassword"/> telling the operator one is stored; leaving it empty sends
/// null, which the API reads as "keep the stored one".
/// </summary>
public sealed partial class EncoderProfileEditViewModel : ObservableObject
{
    public IReadOnlyList<string> Formats     { get; } = ["MP3", "OGG", "OPUS"];
    public IReadOnlyList<string> ServerTypes { get; } = ["ICECAST", "SHOUTCAST", "SHOUTCAST2"];
    public IReadOnlyList<int>    Bitrates    { get; } = [64, 96, 128, 192, 256, 320];

    public string? ProfileId { get; }
    public bool    IsNew     => ProfileId is null;
    public bool    HadPassword { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _format = "MP3";
    [ObservableProperty] private int _bitrateKbps = 128;
    [ObservableProperty] private string _serverType = "ICECAST";
    [ObservableProperty] private string _host = "";
    [ObservableProperty] private int _port = 8000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MountRequired))]
    private string _mount = "/live";

    /// <summary>Icecast and SHOUTcast 2 accept a source account; SHOUTcast 1 has no concept of one.</summary>
    [ObservableProperty] private string _username = "";

    /// <summary>SHOUTcast 2 only: which stream within the server to feed. Blank means stream 1.</summary>
    [ObservableProperty] private string _streamId = "";

    /// <summary>Icecast only: PUT (2.4 and later) instead of the older SOURCE method.</summary>
    [ObservableProperty] private bool _usePut;

    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _useSsl;

    [ObservableProperty] private string _streamName = "";
    [ObservableProperty] private string _genre = "";
    [ObservableProperty] private string _streamUrl = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isPublic;

    [ObservableProperty] private bool _enabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAutoStart))]
    private bool _armed;

    [ObservableProperty] private bool _autoStart;

    /// <summary>
    /// Seconds between reconnect attempts after the connection drops. Repeated until the server
    /// comes back — a nightly server restart should not need an operator. Server-side bounds: 2–1800.
    /// </summary>
    [ObservableProperty] private int _reconnectDelaySeconds = 10;

    /// <summary>Auto-start only makes sense for a profile that is ready in the first place.</summary>
    public bool CanAutoStart => Armed;

    partial void OnArmedChanged(bool value)
    {
        if (!value) AutoStart = false;
    }

    // ── Stream title ─────────────────────────────────────────────────────────

    public IReadOnlyList<string> TitleModes { get; } = ["NOW_PLAYING", "STATIC", "NONE"];

    /// <summary>
    /// What this profile pushes to the cast server: the playing track, a fixed string, or nothing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStaticTitle))]
    private string _titleMode = "NOW_PLAYING";

    [ObservableProperty] private string _titleText = "";

    public bool IsStaticTitle => TitleMode == "STATIC";

    partial void OnTitleModeChanged(string value) => OnPropertyChanged(nameof(IsStaticTitle));

    [ObservableProperty] private string? _error;

    // Each field is shown only where the protocol actually carries it. Hiding beats disabling
    // here: a visible box invites the operator to fill in something the server never receives.
    public bool MountRequired  => ServerType == "ICECAST";
    public bool SupportsUser   => ServerType is "ICECAST" or "SHOUTCAST2";
    public bool NeedsStreamId  => ServerType == "SHOUTCAST2";
    public bool SupportsPut    => ServerType == "ICECAST";

    partial void OnServerTypeChanged(string value)
    {
        OnPropertyChanged(nameof(MountRequired));
        OnPropertyChanged(nameof(SupportsUser));
        OnPropertyChanged(nameof(NeedsStreamId));
        OnPropertyChanged(nameof(SupportsPut));
    }

    public EncoderProfileEditViewModel() { }

    public EncoderProfileEditViewModel(EncoderProfileDto existing)
    {
        ProfileId   = existing.ProfileId;
        HadPassword = existing.HasPassword;

        Name        = existing.Name;
        Format      = existing.Format;
        BitrateKbps = existing.BitrateKbps;
        ServerType  = existing.ServerType;
        Host        = existing.Host;
        Port        = existing.Port;
        Mount       = existing.Mount ?? "";
        Username    = existing.Username ?? "";
        StreamId    = existing.StreamId ?? "";
        UseSsl      = existing.UseSsl;
        UsePut      = existing.UsePut;
        StreamName  = existing.StreamName ?? "";
        Genre       = existing.Genre ?? "";
        StreamUrl   = existing.StreamUrl ?? "";
        Description = existing.Description ?? "";
        IsPublic    = existing.IsPublic;
        Enabled     = existing.Enabled;
        Armed       = existing.Armed;
        AutoStart   = existing.AutoStart;
        ReconnectDelaySeconds = existing.ReconnectDelaySeconds;
        TitleMode   = existing.TitleMode;
        TitleText   = existing.TitleText ?? "";
    }

    public EncoderProfileCreateDto ToCreateDto() => new(
        Name.Trim(), Format, BitrateKbps, ServerType, Host.Trim(), Port,
        Mount:       NullIfBlank(Mount),
        Username:    NullIfBlank(Username),
        StreamId:    NullIfBlank(StreamId),
        Password:    NullIfBlank(Password),
        StreamName:  NullIfBlank(StreamName),
        Genre:       NullIfBlank(Genre),
        StreamUrl:   NullIfBlank(StreamUrl),
        Description: NullIfBlank(Description),
        UseSsl:      UseSsl,
        UsePut:      UsePut,
        IsPublic:    IsPublic,
        Enabled:     Enabled,
        Armed:       Armed,
        AutoStart:   AutoStart,
        ReconnectDelaySeconds: ReconnectDelaySeconds,
        TitleMode:   TitleMode,
        TitleText:   NullIfBlank(TitleText));

    public EncoderProfileUpdateDto ToUpdateDto() => new(
        Name.Trim(), Format, BitrateKbps, ServerType, Host.Trim(), Port,
        Mount:       NullIfBlank(Mount),
        Username:    NullIfBlank(Username),
        StreamId:    NullIfBlank(StreamId),
        // Empty means "unchanged", not "clear it": the box always starts empty, so treating it as
        // a deliberate blank would wipe the credential on every plain rename.
        Password:    NullIfBlank(Password),
        StreamName:  NullIfBlank(StreamName),
        Genre:       NullIfBlank(Genre),
        StreamUrl:   NullIfBlank(StreamUrl),
        Description: NullIfBlank(Description),
        UseSsl:      UseSsl,
        UsePut:      UsePut,
        IsPublic:    IsPublic,
        Enabled:     Enabled,
        Armed:       Armed,
        AutoStart:   AutoStart,
        ReconnectDelaySeconds: ReconnectDelaySeconds,
        TitleMode:   TitleMode,
        TitleText:   NullIfBlank(TitleText));

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
