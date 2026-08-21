namespace RDM.Core.Hardware;

public interface IHardwarePayload
{
    string Signature { get; }
}

// ── Znormalizowany Payload Analogowy ──────────────────────────────────────────

public record NormalizedAnalogPayload : IHardwarePayload
{
    public float Value { get; }
    public string Signature => "NormalizedAnalog";

    public NormalizedAnalogPayload(float value)
    {
        Value = Math.Clamp(value, 0.0f, 1.0f);
    }
}

// ── MIDI Payloads ─────────────────────────────────────────────────────────────

public record MidiNotePayload : IHardwarePayload
{
    public byte Channel { get; }
    public byte Note { get; }
    public byte Velocity { get; }
    public bool IsNoteOn { get; }
    public string Signature { get; }

    public MidiNotePayload(byte channel, byte note, byte velocity, bool isNoteOn)
    {
        Channel = channel;
        Note = note;
        Velocity = velocity;
        IsNoteOn = isNoteOn;
        Signature = $"MidiNote_Ch{channel}_N{note}";
    }
}

public record MidiCcPayload : IHardwarePayload
{
    public byte Channel { get; }
    public byte Controller { get; }
    public byte Value { get; }
    public string Signature { get; }

    public MidiCcPayload(byte channel, byte controller, byte value)
    {
        Channel = channel;
        Controller = controller;
        Value = value;
        Signature = $"MidiCC_Ch{channel}_C{controller}";
    }
}

// ── Keyboard Payload ──────────────────────────────────────────────────────────

public record KeyboardPayload : IHardwarePayload
{
    public string KeyCode { get; }
    public bool AltPressed { get; }
    public bool CtrlPressed { get; }
    public bool ShiftPressed { get; }
    public string Signature { get; }

    public KeyboardPayload(string keyCode, bool altPressed, bool ctrlPressed, bool shiftPressed)
    {
        KeyCode = keyCode;
        AltPressed = altPressed;
        CtrlPressed = ctrlPressed;
        ShiftPressed = shiftPressed;
        Signature = $"Key_{keyCode}"
                  + (ctrlPressed  ? "_Ctrl"  : "")
                  + (altPressed   ? "_Alt"   : "")
                  + (shiftPressed ? "_Shift" : "");
    }
}

// ── D&R Mixer Payload ─────────────────────────────────────────────────────────

public record DrMixerPayload : IHardwarePayload
{
    public string Target { get; }
    public int Index { get; }
    public float Value { get; }
    public bool IsActive { get; }
    public string Signature { get; }

    public DrMixerPayload(string target, int index, float value, bool isActive)
    {
        Target = target;
        Index = index;
        Value = value;
        IsActive = isActive;
        Signature = $"DR_{target}_{index}";
    }
}

// ── Serial Command Payload ─────────────────────────────────────────────────────

public record SerialCommandPayload : IHardwarePayload
{
    public string Command  { get; }
    public bool   IsActive { get; }
    public string Signature { get; }

    public SerialCommandPayload(string command, bool isActive = true)
    {
        Command   = command;
        IsActive  = isActive;
        Signature = $"Serial_{command}";
    }
}

// ── Parameterized Payload ─────────────────────────────────────────────────────
// Wraps any payload with a TargetParameter string from TriggerActionMapping.
// Used to pass per-mapping configuration (e.g. macro GUID, HTTP URL) to action delegates.

public record ParameterizedPayload(IHardwarePayload Inner, string Parameter) : IHardwarePayload
{
    public string Signature => Inner.Signature;
}
