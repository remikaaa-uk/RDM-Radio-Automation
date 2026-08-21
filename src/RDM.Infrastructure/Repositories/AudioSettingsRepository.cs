using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Repositories.Helpers;

namespace RDM.Infrastructure.Repositories;

public sealed class AudioSettingsRepository : IAudioSettingsRepository
{
    private readonly IDbConnectionFactory _factory;

    public AudioSettingsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<AudioSettings?> GetByStudioAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AudioSettingsRow>(
            new CommandDefinition(
                """
                SELECT
                    settings_id, studio_id,
                    sample_rate, buffer_size, output_mode,
                    device_player_id, device_sweeper_id,
                    device_aux1_id, device_aux2_id, device_aux3_id, device_aux4_id,
                    device_cartwall_id, device_pfl_id, device_input_id,
                    crossfade_enabled, crossfade_duration_ms,
                    ducking_level_db, ducking_attack_ms, ducking_release_ms,
                    stop_fadeout_ms, aux_fadeout_ms, auto_fade_out_duration_s, silence_remover_enabled,
                    silence_start_threshold_db, silence_mix_threshold_db, silence_end_threshold_db,
                    loudness_target_lufs, loudness_normalization,
                    sweeper_enabled, sweeper_format_id, sweeper_subcategory_id, music_format_id, sweeper_min_intro_ms, sweeper_ducking_db,
                    default_mode,
                    countdown_red_enabled, countdown_red_threshold_s,
                    countdown_green_enabled,
                    dead_air_enabled, dead_air_threshold_s, emergency_playlist_id,
                    cartwall_slots_per_page, cartwall_fadeout_ms, cartwall_separate_window,
                    backup_path, backup_interval_h, backup_keep_count, backup_on_close, backup_last_at,
                    api_enabled, api_port, api_auth_enabled,
                    api_username, api_password_hash, api_anonymous_local,
                    theme, updated_at
                FROM audio_settings
                WHERE studio_id = @StudioId
                """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task CreateAsync(AudioSettings s, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO audio_settings (
                    settings_id, studio_id,
                    sample_rate, buffer_size, output_mode,
                    device_player_id, device_sweeper_id,
                    device_aux1_id, device_aux2_id, device_aux3_id, device_aux4_id,
                    device_cartwall_id, device_pfl_id, device_input_id,
                    crossfade_enabled, crossfade_duration_ms,
                    ducking_level_db, ducking_attack_ms, ducking_release_ms,
                    stop_fadeout_ms, aux_fadeout_ms, auto_fade_out_duration_s, silence_remover_enabled,
                    silence_start_threshold_db, silence_mix_threshold_db, silence_end_threshold_db,
                    loudness_target_lufs, loudness_normalization,
                    sweeper_enabled, sweeper_format_id, sweeper_subcategory_id, music_format_id, sweeper_min_intro_ms, sweeper_ducking_db,
                    default_mode,
                    countdown_red_enabled, countdown_red_threshold_s,
                    countdown_green_enabled,
                    dead_air_enabled, dead_air_threshold_s, emergency_playlist_id,
                    cartwall_slots_per_page, cartwall_fadeout_ms, cartwall_separate_window,
                    backup_path, backup_interval_h, backup_keep_count, backup_on_close, backup_last_at,
                    api_enabled, api_port, api_auth_enabled,
                    api_username, api_password_hash, api_anonymous_local,
                    theme, updated_at
                ) VALUES (
                    @SettingsId, @StudioId,
                    @SampleRate, @BufferSize, @OutputMode,
                    @DevicePlayerId, @DeviceSweeperId,
                    @DeviceAux1Id, @DeviceAux2Id, @DeviceAux3Id, @DeviceAux4Id,
                    @DeviceCartwallId, @DevicePflId, @DeviceInputId,
                    @CrossfadeEnabled, @CrossfadeDurationMs,
                    @DuckingLevelDb, @DuckingAttackMs, @DuckingReleaseMs,
                    @StopFadeoutMs, @AuxFadeoutMs, @AutoFadeOutDurationS, @SilenceRemoverEnabled,
                    @SilenceStartThresholdDb, @SilenceMixThresholdDb, @SilenceEndThresholdDb,
                    @LoudnessTargetLufs, @LoudnessNormalization,
                    @SweeperEnabled, @SweeperFormatId, @SweeperSubcategoryId, @MusicFormatId, @SweeperMinIntroMs, @SweeperDuckingDb,
                    @DefaultMode,
                    @CountdownRedEnabled, @CountdownRedThresholdS,
                    @CountdownGreenEnabled,
                    @DeadAirEnabled, @DeadAirThresholdS, @EmergencyPlaylistId,
                    @CartwallSlotsPerPage, @CartwallFadeoutMs, @CartwallSeparateWindow,
                    @BackupPath, @BackupIntervalH, @BackupKeepCount, @BackupOnClose, @BackupLastAt,
                    @ApiEnabled, @ApiPort, @ApiAuthEnabled,
                    @ApiUsername, @ApiPasswordHash, @ApiAnonymousLocal,
                    @Theme, @UpdatedAt
                )
                """,
                BuildParams(s),
                cancellationToken: ct));
    }

    public async Task UpdateAsync(AudioSettings s, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE audio_settings SET
                    sample_rate                 = @SampleRate,
                    buffer_size                 = @BufferSize,
                    output_mode                 = @OutputMode,
                    device_player_id            = @DevicePlayerId,
                    device_sweeper_id           = @DeviceSweeperId,
                    device_aux1_id              = @DeviceAux1Id,
                    device_aux2_id              = @DeviceAux2Id,
                    device_aux3_id              = @DeviceAux3Id,
                    device_aux4_id              = @DeviceAux4Id,
                    device_cartwall_id          = @DeviceCartwallId,
                    device_pfl_id               = @DevicePflId,
                    device_input_id             = @DeviceInputId,
                    crossfade_enabled           = @CrossfadeEnabled,
                    crossfade_duration_ms       = @CrossfadeDurationMs,
                    ducking_level_db            = @DuckingLevelDb,
                    ducking_attack_ms           = @DuckingAttackMs,
                    ducking_release_ms          = @DuckingReleaseMs,
                    stop_fadeout_ms             = @StopFadeoutMs,
                    aux_fadeout_ms              = @AuxFadeoutMs,
                    auto_fade_out_duration_s    = @AutoFadeOutDurationS,
                    silence_remover_enabled     = @SilenceRemoverEnabled,
                    silence_start_threshold_db  = @SilenceStartThresholdDb,
                    silence_mix_threshold_db    = @SilenceMixThresholdDb,
                    silence_end_threshold_db    = @SilenceEndThresholdDb,
                    loudness_target_lufs        = @LoudnessTargetLufs,
                    loudness_normalization      = @LoudnessNormalization,
                    sweeper_enabled             = @SweeperEnabled,
                    sweeper_format_id           = @SweeperFormatId,
                    sweeper_subcategory_id      = @SweeperSubcategoryId,
                    music_format_id             = @MusicFormatId,
                    sweeper_min_intro_ms        = @SweeperMinIntroMs,
                    sweeper_ducking_db          = @SweeperDuckingDb,
                    default_mode                = @DefaultMode,
                    countdown_red_enabled       = @CountdownRedEnabled,
                    countdown_red_threshold_s   = @CountdownRedThresholdS,
                    countdown_green_enabled     = @CountdownGreenEnabled,
                    dead_air_enabled            = @DeadAirEnabled,
                    dead_air_threshold_s        = @DeadAirThresholdS,
                    emergency_playlist_id       = @EmergencyPlaylistId,
                    cartwall_slots_per_page     = @CartwallSlotsPerPage,
                    cartwall_fadeout_ms         = @CartwallFadeoutMs,
                    cartwall_separate_window    = @CartwallSeparateWindow,
                    backup_path                 = @BackupPath,
                    backup_interval_h           = @BackupIntervalH,
                    backup_keep_count           = @BackupKeepCount,
                    backup_on_close             = @BackupOnClose,
                    backup_last_at              = @BackupLastAt,
                    api_enabled                 = @ApiEnabled,
                    api_port                    = @ApiPort,
                    api_auth_enabled            = @ApiAuthEnabled,
                    api_username                = @ApiUsername,
                    api_password_hash           = @ApiPasswordHash,
                    api_anonymous_local         = @ApiAnonymousLocal,
                    theme                       = @Theme
                WHERE settings_id = @SettingsId
                """,
                BuildParams(s),
                cancellationToken: ct));
    }

    public async Task UpdateBackupLastAtAsync(string settingsId, DateTime lastAt, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE audio_settings SET backup_last_at = @LastAt WHERE settings_id = @SettingsId",
                new { LastAt = lastAt, SettingsId = settingsId },
                cancellationToken: ct));
    }

    private static object BuildParams(AudioSettings s) => new
    {
        s.SettingsId,
        s.StudioId,
        SampleRate                = EnumMapper.ToDb(s.SampleRate),
        BufferSize                = EnumMapper.ToDb(s.BufferSize),
        OutputMode                = EnumMapper.ToDb(s.OutputMode),
        s.DevicePlayerId,
        s.DeviceSweeperId,
        s.DeviceAux1Id,
        s.DeviceAux2Id,
        s.DeviceAux3Id,
        s.DeviceAux4Id,
        s.DeviceCartwallId,
        s.DevicePflId,
        s.DeviceInputId,
        CrossfadeEnabled          = s.CrossfadeEnabled          ? 1 : 0,
        s.CrossfadeDurationMs,
        s.DuckingLevelDb,
        s.DuckingAttackMs,
        s.DuckingReleaseMs,
        s.StopFadeoutMs,
        s.AuxFadeoutMs,
        s.AutoFadeOutDurationS,
        SilenceRemoverEnabled     = s.SilenceRemoverEnabled     ? 1 : 0,
        s.SilenceStartThresholdDb,
        s.SilenceMixThresholdDb,
        s.SilenceEndThresholdDb,
        s.LoudnessTargetLufs,
        LoudnessNormalization     = s.LoudnessNormalization     ? 1 : 0,
        SweeperEnabled            = s.SweeperEnabled            ? 1 : 0,
        s.SweeperFormatId,
        s.SweeperSubcategoryId,
        s.MusicFormatId,
        s.SweeperMinIntroMs,
        s.SweeperDuckingDb,
        DefaultMode               = EnumMapper.ToDb(s.DefaultMode),
        CountdownRedEnabled       = s.CountdownRedEnabled       ? 1 : 0,
        s.CountdownRedThresholdS,
        CountdownGreenEnabled     = s.CountdownGreenEnabled     ? 1 : 0,
        DeadAirEnabled            = s.DeadAirEnabled            ? 1 : 0,
        s.DeadAirThresholdS,
        s.EmergencyPlaylistId,
        s.CartwallSlotsPerPage,
        s.CartwallFadeoutMs,
        CartwallSeparateWindow    = s.CartwallSeparateWindow    ? 1 : 0,
        s.BackupPath,
        s.BackupIntervalH,
        s.BackupKeepCount,
        BackupOnClose             = s.BackupOnClose             ? 1 : 0,
        s.BackupLastAt,
        ApiEnabled                = s.ApiEnabled                ? 1 : 0,
        s.ApiPort,
        ApiAuthEnabled            = s.ApiAuthEnabled            ? 1 : 0,
        s.ApiUsername,
        s.ApiPasswordHash,
        ApiAnonymousLocal         = s.ApiAnonymousLocal         ? 1 : 0,
        Theme                     = EnumMapper.ToDb(s.Theme),
        s.UpdatedAt
    };

    private static AudioSettings MapRow(AudioSettingsRow r) => new()
    {
        SettingsId              = r.settings_id,
        StudioId                = r.studio_id,
        SampleRate              = EnumMapper.ToAudioSampleRate(r.sample_rate),
        BufferSize              = EnumMapper.ToAudioBufferSize(r.buffer_size),
        OutputMode              = EnumMapper.ToDriverType(r.output_mode),
        DevicePlayerId          = r.device_player_id,
        DeviceSweeperId         = r.device_sweeper_id,
        DeviceAux1Id            = r.device_aux1_id,
        DeviceAux2Id            = r.device_aux2_id,
        DeviceAux3Id            = r.device_aux3_id,
        DeviceAux4Id            = r.device_aux4_id,
        DeviceCartwallId        = r.device_cartwall_id,
        DevicePflId             = r.device_pfl_id,
        DeviceInputId           = r.device_input_id,
        CrossfadeEnabled        = r.crossfade_enabled,
        CrossfadeDurationMs     = r.crossfade_duration_ms,
        DuckingLevelDb          = r.ducking_level_db,
        DuckingAttackMs         = r.ducking_attack_ms,
        DuckingReleaseMs        = r.ducking_release_ms,
        StopFadeoutMs           = r.stop_fadeout_ms,
        AuxFadeoutMs            = r.aux_fadeout_ms,
        AutoFadeOutDurationS    = r.auto_fade_out_duration_s,
        SilenceRemoverEnabled   = r.silence_remover_enabled,
        SilenceStartThresholdDb = r.silence_start_threshold_db,
        SilenceMixThresholdDb   = r.silence_mix_threshold_db,
        SilenceEndThresholdDb   = r.silence_end_threshold_db,
        LoudnessTargetLufs      = r.loudness_target_lufs,
        LoudnessNormalization   = r.loudness_normalization,
        SweeperEnabled          = r.sweeper_enabled,
        SweeperFormatId         = r.sweeper_format_id,
        SweeperSubcategoryId    = r.sweeper_subcategory_id,
        MusicFormatId           = r.music_format_id,
        SweeperMinIntroMs       = r.sweeper_min_intro_ms,
        SweeperDuckingDb        = r.sweeper_ducking_db,
        DefaultMode             = EnumMapper.ToPlaylistMode(r.default_mode),
        CountdownRedEnabled     = r.countdown_red_enabled,
        CountdownRedThresholdS  = r.countdown_red_threshold_s,
        CountdownGreenEnabled   = r.countdown_green_enabled,
        DeadAirEnabled          = r.dead_air_enabled,
        DeadAirThresholdS       = r.dead_air_threshold_s,
        EmergencyPlaylistId     = r.emergency_playlist_id,
        CartwallSlotsPerPage    = r.cartwall_slots_per_page,
        CartwallFadeoutMs       = r.cartwall_fadeout_ms,
        CartwallSeparateWindow  = r.cartwall_separate_window,
        BackupPath              = r.backup_path,
        BackupIntervalH         = r.backup_interval_h,
        BackupKeepCount         = r.backup_keep_count,
        BackupOnClose           = r.backup_on_close,
        BackupLastAt            = r.backup_last_at,
        ApiEnabled              = r.api_enabled,
        ApiPort                 = r.api_port,
        ApiAuthEnabled          = r.api_auth_enabled,
        ApiUsername             = r.api_username,
        ApiPasswordHash         = r.api_password_hash,
        ApiAnonymousLocal       = r.api_anonymous_local,
        Theme                   = EnumMapper.ToAppTheme(r.theme),
        UpdatedAt               = r.updated_at
    };

    // Class (not record) so Dapper maps by property name — missing columns get default values.
    // This makes the mapping resilient to schema differences across DB versions.
    private sealed class AudioSettingsRow
    {
        public string    settings_id              { get; init; } = "";
        public string    studio_id                { get; init; } = "";
        public string    sample_rate              { get; init; } = "";
        public string    buffer_size              { get; init; } = "";
        public string    output_mode              { get; init; } = "";
        public string?   device_player_id         { get; init; }
        public string?   device_sweeper_id        { get; init; }
        public string?   device_aux1_id           { get; init; }
        public string?   device_aux2_id           { get; init; }
        public string?   device_aux3_id           { get; init; }
        public string?   device_aux4_id           { get; init; }
        public string?   device_cartwall_id       { get; init; }
        public string?   device_pfl_id            { get; init; }
        public string?   device_input_id          { get; init; }
        public bool      crossfade_enabled         { get; init; }
        public uint      crossfade_duration_ms     { get; init; }
        public decimal   ducking_level_db          { get; init; }
        public uint      ducking_attack_ms         { get; init; }
        public uint      ducking_release_ms        { get; init; }
        public uint      stop_fadeout_ms           { get; init; }
        public uint      aux_fadeout_ms            { get; init; }
        public double    auto_fade_out_duration_s  { get; init; }
        public bool      silence_remover_enabled   { get; init; }
        public decimal   silence_start_threshold_db{ get; init; }
        public decimal   silence_mix_threshold_db  { get; init; }
        public decimal   silence_end_threshold_db  { get; init; }
        public decimal   loudness_target_lufs      { get; init; }
        public bool      loudness_normalization    { get; init; }
        public bool      sweeper_enabled           { get; init; }
        public string?   sweeper_format_id         { get; init; }
        public string?   sweeper_subcategory_id    { get; init; }
        public string?   music_format_id           { get; init; }
        public uint      sweeper_min_intro_ms      { get; init; }
        public float     sweeper_ducking_db        { get; init; }
        public string    default_mode              { get; init; } = "";
        public bool      countdown_red_enabled     { get; init; }
        public uint      countdown_red_threshold_s { get; init; }
        public bool      countdown_green_enabled   { get; init; }
        public bool      dead_air_enabled          { get; init; }
        public uint      dead_air_threshold_s      { get; init; }
        public string?   emergency_playlist_id     { get; init; }
        public byte      cartwall_slots_per_page   { get; init; }
        public uint      cartwall_fadeout_ms       { get; init; }
        public bool      cartwall_separate_window  { get; init; }
        public string?   backup_path               { get; init; }
        public byte      backup_interval_h         { get; init; }
        public byte      backup_keep_count         { get; init; }
        public bool      backup_on_close           { get; init; }
        public DateTime? backup_last_at            { get; init; }
        public bool      api_enabled               { get; init; }
        public ushort    api_port                  { get; init; }
        public bool      api_auth_enabled          { get; init; }
        public string?   api_username              { get; init; }
        public string?   api_password_hash         { get; init; }
        public bool      api_anonymous_local       { get; init; }
        public string    theme                     { get; init; } = "";
        public DateTime  updated_at                { get; init; }
    }
}
