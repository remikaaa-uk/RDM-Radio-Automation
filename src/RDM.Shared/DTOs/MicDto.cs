namespace RDM.Shared.DTOs;

public record MicLevelDto(double LevelDb);
public record MicStatusDto(bool IsActive);

public record MicFxDto(int SlotId, string FxType, Dictionary<string, float> Parameters);
public record MicVstDto(int SlotId, string PluginName, string DllPath);

/// One editable parameter of a BFX effect, so a client can build an editor without hardcoding
/// the ranges: Key matches the keys in Parameters above.
public record MicFxParamDto(string Key, float Min, float Max, float Default, string Unit);

public record AddMicFxRequestDto(string FxType);
public record UpdateMicFxRequestDto(Dictionary<string, float> Parameters);
public record AddMicVstRequestDto(string DllPath);

public record MicFxAddedDto(int SlotId);
public record MicVstAddedDto(int SlotId);
