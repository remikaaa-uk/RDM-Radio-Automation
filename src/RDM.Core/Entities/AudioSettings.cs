using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed record AudioSettings
{
    public string SettingsId { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;

    // Engine
    public AudioSampleRate SampleRate { get; init; }
    public AudioBufferSize BufferSize { get; init; }
    public DriverType OutputMode { get; init; }

    // Device routing
    public string? DevicePlayerId { get; init; }
    public string? DeviceSweeperId { get; init; }
    public string? DeviceAux1Id { get; init; }
    public string? DeviceAux2Id { get; init; }
    public string? DeviceAux3Id { get; init; }
    public string? DeviceAux4Id { get; init; }
    public string? DeviceCartwallId { get; init; }
    public string? DevicePflId { get; init; }

    // Input — the recording device the mic button opens (BassAudioEngine.StartMicAsync)
    public string? DeviceInputId { get; init; }

    // Crossfade — always equal-power (quarter-cosine) fade of the outgoing track; see
    // BassAudioEngine.StartEqualPowerFadeOut. The curve is fixed, not configurable.
    public bool CrossfadeEnabled { get; init; }
    public uint CrossfadeDurationMs { get; init; }

    // Microphone ducking — applied to the music while the mic is on. The mic button is the only trigger.
    public decimal DuckingLevelDb { get; init; }
    public uint DuckingAttackMs { get; init; }
    public uint DuckingReleaseMs { get; init; }

    // Auto DJ
    public uint   StopFadeoutMs          { get; init; }
    // AUX deck fade-out on stop — applied to manual/remote Stop (AuxController) and to
    // aux.stop(index) script calls that omit an explicit fadeMs. 0 = cut instantly.
    public uint   AuxFadeoutMs           { get; init; }
    public double AutoFadeOutDurationS   { get; init; }
    public bool SilenceRemoverEnabled { get; init; }
    public decimal SilenceStartThresholdDb { get; init; }
    public decimal SilenceMixThresholdDb { get; init; }
    public decimal SilenceEndThresholdDb { get; init; }

    // Loudness
    public decimal LoudnessTargetLufs { get; init; }
    public bool LoudnessNormalization { get; init; }

    // Sweeper
    public bool SweeperEnabled { get; init; }
    public string? SweeperFormatId { get; init; }
    // Active sweeper subcategory: when set, the sweeper pool is narrowed to this subcategory
    // (e.g. "day" / "weekend"). null = randomize across the whole SweeperFormatId category.
    // Changed via the bottom-bar control or the CHANGE_SWEEPER_SUBCATEGORY event action.
    public string? SweeperSubcategoryId { get; init; }
    public string? MusicFormatId  { get; init; }
    public uint SweeperMinIntroMs { get; init; }
    // Track ducking applied while a sweeper plays. Value 0–12 = amount of attenuation in dB
    // (e.g. 6 → −6 dB, 12 → −12 dB, 0 → no ducking).
    public float SweeperDuckingDb { get; init; } = 6f;

    // Playlist UI
    public PlaylistMode DefaultMode { get; init; }
    public bool CountdownRedEnabled { get; init; }
    public uint CountdownRedThresholdS { get; init; }
    public bool CountdownGreenEnabled { get; init; }
    public bool DeadAirEnabled { get; init; }
    public uint DeadAirThresholdS { get; init; }
    // Saved playlist copied into the ON_AIR queue when dead air is detected in AUTO mode
    // (DeadAirMonitorService → PlaylistEngine.OnDeadAirTickAsync). null = warn only, no recovery.
    public string? EmergencyPlaylistId { get; init; }

    // Cartwall
    public byte CartwallSlotsPerPage { get; init; }
    public uint CartwallFadeoutMs { get; init; }
    public bool CartwallSeparateWindow { get; init; }

    // Backup
    public string? BackupPath { get; init; }
    public byte BackupIntervalH { get; init; }
    public byte BackupKeepCount { get; init; }
    public bool BackupOnClose { get; init; }
    public DateTime? BackupLastAt { get; init; }

    // API
    public bool ApiEnabled { get; init; }
    public ushort ApiPort { get; init; }
    public bool ApiAuthEnabled { get; init; }
    public string? ApiUsername { get; init; }
    public string? ApiPasswordHash { get; init; }
    public bool ApiAnonymousLocal { get; init; }

    // Appearance
    public AppTheme Theme { get; init; }

    public DateTime UpdatedAt { get; init; }
}
