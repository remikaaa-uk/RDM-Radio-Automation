using System;
using RDM.Core.Entities;
using RDM.Shared.Enums;

namespace RDM.Core.Models;

public record NowPlayingInfo(
    Asset?        CurrentAsset,
    uint          PositionMs,        // calculated under lock
    uint?         OutroCueMs,        // null = no OUTRO marker
    DateTime?     TrackStartedAt,    // UTC moment of first track start
    PlaylistItem? NextItem,
    PlaylistMode  Mode,
    SessionState  State,             // Idle | Playing | Paused
    string?       CurrentItemId,     // null when Idle
    bool          LoopCurrent = false); // repeat-current-track armed (survives Stop)
