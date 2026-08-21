using RDM.Shared.Enums;

namespace RDM.Core.Models;

public sealed record CuePointData(MarkerType MarkerType, uint PositionMs, string? Label = null);
