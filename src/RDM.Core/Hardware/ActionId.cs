namespace RDM.Core.Hardware;

public enum ActionId
{
    // Player
    PlayerPlay,
    PlayerStop,
    PlayerStopFade,
    PlayerPause,
    PlayerNext,
    PlayerSetVolume,
    PlayerPlayStopToggle,
    PlayerLoopToggle,
    PlayerRemoveFromPlayer,

    // Playlist
    PlaylistModeAuto,
    PlaylistModeManual,
    PlaylistModeLiveAssist,
    PlaylistModeCycle,
    PlaylistSkipNextEvent,
    PlaylistRemoveSelected,
    PlaylistClear,

    // Cartwall
    CartwallPlaySlot,
    CartwallStopSlot,
    CartwallStopAll,
    CartwallToggleLoop,
    CartwallSetBank,
    CartwallToggleMode,
    CartwallTab1,
    CartwallTab2,
    CartwallTab3,
    CartwallTab4,
    CartwallTab5,
    CartwallTab6,
    CartwallTab7,
    CartwallTriggerSlot1,
    CartwallTriggerSlot2,
    CartwallTriggerSlot3,
    CartwallTriggerSlot4,
    CartwallTriggerSlot5,
    CartwallTriggerSlot6,
    CartwallTriggerSlot7,
    CartwallTriggerSlot8,
    CartwallTriggerSlot9,
    CartwallTriggerSlot10,
    CartwallTriggerSlot11,
    CartwallTriggerSlot12,
    CartwallTriggerSlot13,
    CartwallTriggerSlot14,
    CartwallTriggerSlot15,
    CartwallTriggerSlot16,

    // Aux players (1–4)
    AuxPlay1,
    AuxPlay2,
    AuxPlay3,
    AuxPlay4,
    AuxStop1,
    AuxStop2,
    AuxStop3,
    AuxStop4,
    AuxToggleOn1,
    AuxToggleOn2,
    AuxToggleOn3,
    AuxToggleOn4,
    AuxToggleLoop1,
    AuxToggleLoop2,
    AuxToggleLoop3,
    AuxToggleLoop4,
    AuxTogglePfl1,
    AuxTogglePfl2,
    AuxTogglePfl3,
    AuxTogglePfl4,
    AuxEject1,
    AuxEject2,
    AuxEject3,
    AuxEject4,

    // PFL
    PflStart,
    PflStop,
    PflSeek,

    // Mic / Mixer
    MicOn,
    MicOff,
    MicToggle,
    MicTalkback,
    MixerFaderStart,

    // Window navigation
    WindowTracksManager,
    WindowTrackEditor,
    WindowPlaylistBuilder,
    WindowScheduledEvents,
    WindowHardwareManager,

    // Editors
    Save,
    Undo,
    Redo,

    // Automation
    AutomationTriggerMacro,
    AutomationSendHttp,
    AutomationRunScript,
    AutomationEmergencyPanic,
    RecorderStart,
    RecorderStop,
    VisualTriggerTimer
}
