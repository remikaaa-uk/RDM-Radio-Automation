namespace RDM.Shared.DTOs;

/// <summary>
/// Where to record and in what format. The client supplies the target on every start rather than
/// the server holding a configured folder: a recording path is machine-specific, and the database
/// is shared by every machine in the studio. The UI remembers the last choice in rdm.config.json.
/// </summary>
public record RecordingStartRequestDto(
    string  Directory,
    string  Format       = "MP3",
    int     BitrateKbps  = 192,
    int?    SampleRateHz = null,
    byte    Channels     = 2,
    string? NamePrefix   = null);

/// <summary>Live state of the recorder. Mirrors RDM.Core.Models.RecordingStatus.</summary>
public record RecordingStatusDto(
    string    State,
    string?   FilePath,
    DateTime? StartedAt,
    string?   Error,
    long      BytesWritten);
