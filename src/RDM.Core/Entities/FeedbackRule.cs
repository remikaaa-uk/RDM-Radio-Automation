namespace RDM.Core.Entities;

public class FeedbackRule
{
    public Guid    Id               { get; set; }
    public string  EventName        { get; set; } = string.Empty;
    public string  TargetDeviceId   { get; set; } = string.Empty;
    public string  TargetDeviceType { get; set; } = string.Empty;

    // ── MIDI output fields ────────────────────────────────────────────────────
    public byte    Channel          { get; set; } = 1;
    public byte    NoteCode         { get; set; }
    public byte    Velocity         { get; set; } = 127;

    // ── D&R Mixer output fields ───────────────────────────────────────────────
    public string? DrTarget         { get; set; }   // "Gpo", "Fader", etc.
    public int?    DrGpoIndex       { get; set; }   // GPO/GPI numer wyjścia (1-based)

    // ── Serial output fields ──────────────────────────────────────────────────
    public string? SerialCommand    { get; set; }   // komenda ASCII wysyłana do SerialDriver

    public bool    IsEnabled        { get; set; } = true;
}
