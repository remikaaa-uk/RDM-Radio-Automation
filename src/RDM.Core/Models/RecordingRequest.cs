using RDM.Shared.Enums;

namespace RDM.Core.Models;

/// <summary>
/// What to record and where to put it. Not an entity: manual recording (2d) has no stored
/// configuration yet, so the caller supplies the target every time. The engine takes a directory
/// rather than a full path — the file name carries the start timestamp, which only the engine
/// knows at the moment recording actually begins.
/// </summary>
/// <param name="Directory">Existing or creatable folder for the recording.</param>
/// <param name="Format">Encoder to use. Must have its add-on DLL present.</param>
/// <param name="BitrateKbps">Higher than the streaming default: an archive is kept, not streamed.</param>
/// <param name="SampleRateHz">Null = follow the program bus (no resampling).</param>
/// <param name="Channels">1 = mono, 2 = stereo.</param>
/// <param name="NamePrefix">Leading part of the file name; null falls back to "rec".</param>
public sealed record RecordingRequest(
    string Directory,
    EncoderFormat Format = EncoderFormat.Mp3,
    int BitrateKbps = 192,
    int? SampleRateHz = null,
    byte Channels = 2,
    string? NamePrefix = null);
