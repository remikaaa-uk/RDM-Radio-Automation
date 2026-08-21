using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.Enums;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

// ── Option types for dropdowns ────────────────────────────────────────────────

public sealed record AudioDeviceOption(string? DeviceId, string DisplayName);
public sealed record FormatOption(string? FormatId, string Name);
public sealed record EmergencyPlaylistOption(string? PlaylistId, string Name);

/// <summary>
/// One entry in the output-mode dropdown. <see cref="Value"/> is the <see cref="DriverType"/>
/// name (round-tripped to the DB); <see cref="IsEnabled"/> is false for driver modes that are
/// not yet implemented, so the ComboBox greys them out and blocks selection.
/// </summary>
public sealed record OutputModeOption(string Value, string Display, bool IsEnabled);

public sealed partial class StreamTitleFormatItem : ObservableObject
{
    public string FormatId { get; init; } = string.Empty;
    public string Name     { get; init; } = string.Empty;
    [ObservableProperty] private bool _isChecked;
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IAudioSettingsRepository  _settingsRepo;
    private readonly IPlaylistRepository       _playlistRepo;
    private readonly IAudioDeviceRepository    _deviceRepo;
    private readonly IAudioDeviceEnumerator    _deviceEnumerator;
    private readonly IAssetFormatRepository    _formatRepo;
    private readonly IBackupService            _backupService;
    private readonly IAudioEngine              _audioEngine;
    private readonly IPlaylistController       _playlistController;
    private readonly StreamTitlesService       _streamTitlesService;
    private readonly CountdownService          _countdown;
    private readonly StudioContext             _studioContext;
    private readonly SettingsConfigService     _configService;
    private readonly ILocalizer                _localizer;
    private readonly ILogger<SettingsViewModel> _logger;

    private AudioSettings _current = null!;

    // Maps every stored DeviceId (including duplicates) to the canonical DeviceId
    // shown in OutputDevices, so saved settings still resolve after DB deduplication.
    private readonly Dictionary<string, string> _deviceIdAliases =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Static option lists ───────────────────────────────────────────────────

    /// <summary>Language dropdown: "System (auto)" sentinel + every language discovered by the localizer.</summary>
    public IReadOnlyList<LanguageInfo> LanguageOptions { get; }
    public IReadOnlyList<string> DateFormats     { get; } = ["dddd, d MMMM yyyy", "dd.MM.yyyy", "MM/dd/yyyy"];
    public IReadOnlyList<string> Priorities      { get; } = ["Above Normal", "Normal", "Below Normal"];
    // All four output backends are wired into the engine.
    public IReadOnlyList<OutputModeOption> OutputModes { get; } =
    [
        new("DirectSound",     "DirectSound",      true),
        new("WasapiShared",    "WASAPI shared",    true),
        new("WasapiExclusive", "WASAPI exclusive", true),
        new("Asio",            "ASIO",             true),
    ];
    public IReadOnlyList<string> SampleRates     { get; } = ["Hz48000", "Hz44100", "Hz96000"];
    public IReadOnlyList<string> BufferSizes     { get; } = ["Samples512", "Samples256", "Samples1024"];
    public IReadOnlyList<string> PlaylistModes   { get; } = ["LiveAssist", "Auto", "Manual"];
    public IReadOnlyList<string> SlotCounts      { get; } = ["8", "12", "16", "24", "32"];
    public IReadOnlyList<string> EncodingOptions { get; } = ["UTF-8", "ANSI"];
    public IReadOnlyList<string> UpdateOnOptions { get; } = ["TRACK_STARTED", "TRACK_OUTRO_REACHED"];
    public IReadOnlyList<string> AppThemes       { get; } = ["Dark", "Light"];
    public IReadOnlyList<string> BackupIntervals { get; } = ["6", "12", "24", "0"];

    /// <summary>Shown on the About tab. Read from InformationalVersion (set in version.props).</summary>
    public string AppVersionText { get; } =
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "?";

    // ── Device + format dropdowns (populated on load) ─────────────────────────

    public ObservableCollection<AudioDeviceOption>    OutputDevices    { get; } = new();
    public ObservableCollection<AudioDeviceOption>    InputDevices     { get; } = new();
    public ObservableCollection<FormatOption>         FormatOptions    { get; } = new();
    public ObservableCollection<EmergencyPlaylistOption> EmergencyPlaylists { get; } = new();
    public ObservableCollection<StreamTitleFormatItem> StreamTitleFormats { get; } = new();

    // ── Status ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    // ── General (rdm.config.json) ─────────────────────────────────────────────

    [ObservableProperty] private string _logoPath        = "";
    [ObservableProperty] private bool   _showClock       = true;
    [ObservableProperty] private bool   _showDate        = true;
    [ObservableProperty] private string _language        = "pl";
    [ObservableProperty] private string _dateFormat      = "dddd, d MMMM yyyy";
    [ObservableProperty] private string _processPriority = "Normal";
    [ObservableProperty] private int    _resultsToShow   = 100;
    [ObservableProperty] private int    _libraryPageSize = 50;
    [ObservableProperty] private bool   _allowOneInstance= true;
    [ObservableProperty] private bool   _minimizeToTray  = false;
    [ObservableProperty] private bool   _alwaysOnTop     = false;
    [ObservableProperty] private bool   _enableErrorLog  = true;

    // ── Output Device (DB + rdm.config.json buffers) ─────────────────────────

    [ObservableProperty] private string            _outputMode            = "WasapiExclusive";
    [ObservableProperty] private AudioDeviceOption? _selectedPlayerDevice;
    [ObservableProperty] private AudioDeviceOption? _selectedSweeperDevice;
    [ObservableProperty] private AudioDeviceOption? _selectedPflDevice;
    [ObservableProperty] private AudioDeviceOption? _selectedCartwallDevice;
    [ObservableProperty] private AudioDeviceOption? _selectedAux1Device;
    [ObservableProperty] private AudioDeviceOption? _selectedAux2Device;
    [ObservableProperty] private AudioDeviceOption? _selectedAux3Device;
    [ObservableProperty] private AudioDeviceOption? _selectedAux4Device;
    [ObservableProperty] private AudioDeviceOption? _selectedInputDevice;
    [ObservableProperty] private int     _playbackBufferMs   = 450;
    [ObservableProperty] private int     _mixerBuffer        = 3;
    [ObservableProperty] private int     _inputBufferMs      = 2500;
    [ObservableProperty] private int     _preloadBeforeS     = 10;

    // ── Audio (DB) ────────────────────────────────────────────────────────────

    [ObservableProperty] private string  _sampleRate         = "Hz48000";
    [ObservableProperty] private string  _bufferSize         = "Samples512";
    [ObservableProperty] private bool    _crossfadeEnabled   = true;
    [ObservableProperty] private uint    _crossfadeDurationMs= 2000;
    [ObservableProperty] private decimal _duckingLevelDb     = -12;
    [ObservableProperty] private uint    _duckingAttackMs    = 200;
    [ObservableProperty] private uint    _duckingReleaseMs   = 500;

    // ── Auto DJ (DB + rdm.config.json) ───────────────────────────────────────

    [ObservableProperty] private uint    _stopFadeoutMs             = 1250;
    [ObservableProperty] private uint    _auxFadeoutMs              = 0;
    [ObservableProperty] private double  _autoFadeOutDurationS      = 10.0;
    [ObservableProperty] private int     _fadeInManualMs            = 900;
    [ObservableProperty] private bool    _silenceRemoverEnabled     = false;
    [ObservableProperty] private decimal _silenceStartThresholdDb   = -25;
    [ObservableProperty] private decimal _silenceMixThresholdDb     = -15;
    [ObservableProperty] private decimal _silenceEndThresholdDb     = -28;
    [ObservableProperty] private bool    _sweeperEnabled            = true;
    [ObservableProperty] private string? _sweeperFormatId;
    [ObservableProperty] private string? _musicFormatId;
    [ObservableProperty] private uint    _sweeperMinIntroMs         = 5000;
    [ObservableProperty] private float   _sweeperDuckingDb          = 6f;

    // ── Playlist (DB) ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _defaultMode           = "LiveAssist";
    [ObservableProperty] private bool   _countdownRedEnabled   = true;
    [ObservableProperty] private uint   _countdownRedThresholdS= 30;
    [ObservableProperty] private bool   _countdownGreenEnabled = true;
    [ObservableProperty] private bool   _deadAirEnabled        = true;
    [ObservableProperty] private uint   _deadAirThresholdS     = 5;
    [ObservableProperty] private EmergencyPlaylistOption? _selectedEmergencyPlaylist;

    // ── Cartwall (DB) ─────────────────────────────────────────────────────────

    [ObservableProperty] private string _cartwallSlotsPerPage  = "16";
    [ObservableProperty] private uint   _cartwallFadeoutMs     = 1000;
    [ObservableProperty] private bool   _cartwallSeparateWindow= false;

    // ── Stream Titles (rdm.config.json) ──────────────────────────────────────

    [ObservableProperty] private bool   _streamTitlesEnabled       = false;
    [ObservableProperty] private string _streamTitlesPath          = "";
    [ObservableProperty] private string _streamTitlesFormat        = "$artist$ - $title$";
    [ObservableProperty] private string _streamTitlesEncoding      = "UTF-8";
    [ObservableProperty] private string _streamTitlesUpdateOn      = "TRACK_STARTED";
    [ObservableProperty] private string _streamTitlesFallbackArtist= "Radio";
    [ObservableProperty] private string _streamTitlesFallbackTitle = "";

    // ── API (DB) ─────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _apiEnabled         = true;
    [ObservableProperty] private int    _apiPort            = 9300;
    [ObservableProperty] private bool   _apiAuthEnabled     = true;
    [ObservableProperty] private string _apiUsername        = "";
    [ObservableProperty] private string _apiNewPassword     = "";   // plain text; hashed on save if non-empty
    [ObservableProperty] private bool   _apiAnonymousLocal  = false;

    // ── Appearance (DB) ───────────────────────────────────────────────────────

    [ObservableProperty] private string _theme = "Dark";

    // ── Backup (DB) ──────────────────────────────────────────────────────────

    [ObservableProperty] private string _backupPath        = "";
    [ObservableProperty] private string _backupIntervalH   = "24";
    [ObservableProperty] private int    _backupKeepCount   = 7;
    [ObservableProperty] private bool   _backupOnClose     = true;
    [ObservableProperty] private string _backupLastAtText  = "—";

    // ── Streaming and recording modules (rdm.config.json) ────────────────────

    /// <summary>
    /// Whether the module exists as far as the operator is concerned. Off means no button in the
    /// bottom bar at all — not a greyed-out one. A station that never streams should not carry a
    /// control for it.
    /// </summary>
    [ObservableProperty] private bool _streamingModuleEnabled;

    [ObservableProperty] private bool _recordingModuleEnabled;

    [ObservableProperty] private string _recordingDirectory   = "";
    [ObservableProperty] private string _recordingFormat      = "MP3";
    [ObservableProperty] private int    _recordingBitrateKbps = 192;
    [ObservableProperty] private string _recordingNamePrefix  = "rec";

    public IReadOnlyList<string> RecordingFormats  { get; } = ["MP3", "OGG", "OPUS"];
    public IReadOnlyList<int>    RecordingBitrates { get; } = [96, 128, 192, 256, 320];

    /// <summary>
    /// The streaming profile list shown on the Streaming tab. A child view model rather than a
    /// hundred more properties here: it already knows how to list profiles, show their live state
    /// and edit them, and this class is long enough.
    /// </summary>
    public StreamingViewModel Streaming { get; }

    /// <summary>"Użytkownicy" tab — admin-only account management, same child-VM shape as <see cref="Streaming"/>.</summary>
    public UsersViewModel Users { get; }

    /// Next to the user's own music: always exists, always writable.
    private static string DefaultRecordingDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "RDM");

    /// Treats a blank config value as absent, so a hand-cleared field falls back to the default
    /// instead of becoming an empty path.
    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// Set by the window: opens the folder picker for the recording target.
    public Func<Task<string?>>? PickRecordingFolderAsync { get; set; }

    [RelayCommand]
    private async Task BrowseRecordingFolderAsync()
    {
        if (PickRecordingFolderAsync is null) return;
        var picked = await PickRecordingFolderAsync();
        if (!string.IsNullOrWhiteSpace(picked)) RecordingDirectory = picked;
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(
        StreamingViewModel         streaming,
        UsersViewModel             users,
        IAudioSettingsRepository   settingsRepo,
        IPlaylistRepository        playlistRepo,
        IAudioDeviceRepository     deviceRepo,
        IAudioDeviceEnumerator     deviceEnumerator,
        IAssetFormatRepository     formatRepo,
        IBackupService             backupService,
        IAudioEngine               audioEngine,
        IPlaylistController        playlistController,
        StreamTitlesService        streamTitlesService,
        CountdownService           countdown,
        StudioContext              studioContext,
        SettingsConfigService      configService,
        ILocalizer                 localizer,
        ILogger<SettingsViewModel> logger)
    {
        Streaming            = streaming;
        Users                = users;
        _settingsRepo        = settingsRepo;
        _playlistRepo        = playlistRepo;
        _deviceRepo          = deviceRepo;
        _deviceEnumerator    = deviceEnumerator;
        _formatRepo          = formatRepo;
        _backupService       = backupService;
        _audioEngine         = audioEngine;
        _playlistController   = playlistController;
        _streamTitlesService = streamTitlesService;
        _countdown           = countdown;
        _studioContext       = studioContext;
        _configService       = configService;
        _localizer           = localizer;
        _logger              = logger;

        // "System (auto)" first, then every discovered language (en, pl, user-added…).
        var languages = new List<LanguageInfo>
        {
            new("system", _localizer["settings.appearance.language_system"])
        };
        languages.AddRange(_localizer.AvailableLanguages);
        LanguageOptions = languages;
    }

    /// <summary>Live language switch when the dropdown changes (persisted on Save).</summary>
    partial void OnLanguageChanged(string value) => _localizer.SetLanguage(value);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate() => _ = LoadAsync();

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "Wczytywanie…";
        try
        {
            // Enumerate audio devices from BASS (works without BASS_Init) and sync to DB
            await RefreshDevicesAsync();

            // Load asset formats for Sweeper dropdown + Stream Titles filter
            var formats = await _formatRepo.GetAllAsync();
            FormatOptions.Clear();
            FormatOptions.Add(new FormatOption(null, (Localizer.Instance?["settings.device.not_assigned"] ?? "— Not assigned —")));
            StreamTitleFormats.Clear();
            foreach (var f in formats.OrderBy(f => f.Name))
            {
                FormatOptions.Add(new FormatOption(f.FormatId, f.Name));
                StreamTitleFormats.Add(new StreamTitleFormatItem { FormatId = f.FormatId, Name = f.Name });
            }

            // Load saved playlists for the dead-air emergency-playlist dropdown (exclude ON_AIR).
            var playlists = await _playlistRepo.GetByStudioAsync(_studioContext.StudioId);
            EmergencyPlaylists.Clear();
            EmergencyPlaylists.Add(new EmergencyPlaylistOption(null, "— Brak (tylko alert) —"));
            foreach (var p in playlists
                         .Where(p => p.PlaylistType == RDM.Shared.Enums.PlaylistType.Saved)
                         .OrderBy(p => p.Name))
            {
                EmergencyPlaylists.Add(new EmergencyPlaylistOption(p.PlaylistId, p.Name));
            }

            // Load DB settings
            var settings = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId);
            if (settings is not null)
            {
                _current = settings;
                MapFromSettings(settings);
            }

            // Load rdm.config.json
            var cfg = await _configService.LoadAsync();
            MapFromConfig(cfg);

            await Streaming.LoadAsync();
            await Users.LoadAsync();

            StatusMessage = "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settings load failed");
            StatusMessage = Localizer.Instance?["settings.msg.load_error"] ?? "Error loading settings";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_current is null)
        {
            StatusMessage = Localizer.Instance?["settings.msg.not_loaded"] ?? "Error: settings were not loaded — reopen the window.";
            return;
        }
        IsBusy = true;
        StatusMessage = Localizer.Instance?["settings.msg.saving"] ?? "Saving…";
        try
        {
            // Save AudioSettings (DB)
            var newSettings = BuildAudioSettings();
            _logger.LogDebug("SaveAsync — saving Player={P} Sweeper={Sw} PFL={Pfl} Cartwall={Cw} Input={In}",
                newSettings.DevicePlayerId, newSettings.DeviceSweeperId,
                newSettings.DevicePflId, newSettings.DeviceCartwallId, newSettings.DeviceInputId);
            await _settingsRepo.UpdateAsync(newSettings);

            // Save rdm.config.json sections
            var cfg = await _configService.LoadAsync();
            ApplyToConfig(cfg);
            await _configService.SaveAsync(cfg);

            // Hot-apply the settings that the running engine reads live so the user does not
            // have to restart for ducking / crossfade / sweeper-ducking changes. Engine-level
            // fields (sample rate, buffers, output mode, devices) are ignored by these calls
            // and still require a restart — see the ⚠ notices in the Settings window.
            await _audioEngine.UpdateSettingsAsync(newSettings);
            await _playlistController.UpdateAudioSettingsAsync(newSettings);
            _streamTitlesService.UpdateSettings(BuildStreamTitlesSettings());
            _countdown.ApplySettings(newSettings);

            _logger.LogDebug("SaveAsync — DB updated OK, settingsId={Id}", newSettings.SettingsId);
            StatusMessage = Localizer.Instance?["settings.msg.saved"]
                            ?? "Saved. Some changes applied immediately; changes marked ⚠ require an application restart.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Settings save failed");
            StatusMessage = string.Format(Localizer.Instance?["settings.msg.save_error"] ?? "Save error: {0}", ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task CreateBackupNowAsync()
    {
        IsBusy = true;
        StatusMessage = Localizer.Instance?["settings.msg.backup_creating"] ?? "Creating backup…";
        try
        {
            await _backupService.BackupNowAsync(default);
            BackupLastAtText = DateTime.Now.ToString("dd.MM.yyyy HH:mm");
            StatusMessage    = Localizer.Instance?["settings.msg.backup_ok"] ?? "Backup created successfully";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backup failed");
            StatusMessage = string.Format(Localizer.Instance?["settings.msg.backup_error"] ?? "Backup error: {0}", ex.Message);
        }
        finally { IsBusy = false; }
    }

    // ── Device enumeration ────────────────────────────────────────────────────

    private async Task RefreshDevicesAsync()
    {
        // Enumerate physical devices via BASS (works before BASS_Init)
        var enumerated = _deviceEnumerator.GetOutputDevices();
        _logger.LogDebug("BASS enumerated {Count} device(s):", enumerated.Count);
        foreach (var (sysId, name) in enumerated)
            _logger.LogDebug("  BASS device: [{Name}] id={Id}", name, sysId);

        // Load stored devices. Group by SystemDeviceId to handle duplicates created by
        // earlier BASS enumeration bugs. Build an alias map so saved settings that
        // reference a non-canonical duplicate UUID still resolve to the shown entry.
        var stored     = await _deviceRepo.GetByStudioAsync(_studioContext.StudioId);
        _logger.LogDebug("DB has {Count} stored device(s):", stored.Count);
        foreach (var d in stored)
            _logger.LogDebug("  DB device: [{Name}] deviceId={DeviceId} sysId={SysId}",
                d.FriendlyName, d.DeviceId, d.SystemDeviceId);

        // Key = "sysId|friendlyName" so two endpoints on the same hardware card
        // (e.g. "Wyjście cyfrowe" and "PFL" on the same Realtek adapter) are treated
        // as distinct devices, while true duplicates (same name + same sysId) are merged.
        var byComposite = new Dictionary<string, AudioDevice>(StringComparer.OrdinalIgnoreCase);
        _deviceIdAliases.Clear();
        foreach (var group in stored.GroupBy(
            d => $"{d.SystemDeviceId}|{d.FriendlyName.Trim()}",
            StringComparer.OrdinalIgnoreCase))
        {
            var canonical = group.First();
            byComposite[group.Key] = canonical;
            foreach (var d in group)
            {
                _deviceIdAliases[d.DeviceId] = canonical.DeviceId;
                if (d.DeviceId != canonical.DeviceId)
                    _logger.LogDebug("  Alias: {DupId} → {CanonId} (true duplicate key={Key})",
                        d.DeviceId, canonical.DeviceId, group.Key);
            }
        }

        OutputDevices.Clear();
        OutputDevices.Add(new AudioDeviceOption(null, (Localizer.Instance?["settings.device.not_assigned"] ?? "— Not assigned —")));

        // Deduplicate BASS by composite (sysId|name): skip only truly identical entries,
        // not different endpoints that happen to share a hardware ID.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (systemId, friendlyName) in enumerated)
        {
            var compositeKey = $"{systemId}|{friendlyName.Trim()}";
            if (!seen.Add(compositeKey))
            {
                _logger.LogDebug("  Skipping true duplicate BASS entry: [{Name}] sysId={Id}", friendlyName, systemId);
                continue;
            }

            string deviceId;
            if (byComposite.TryGetValue(compositeKey, out var existing))
            {
                deviceId = existing.DeviceId;
                await _deviceRepo.UpdateAvailabilityAsync(deviceId, true, DateTime.UtcNow);
                _logger.LogDebug("  → matched DB: [{Name}] deviceId={Id}", friendlyName, deviceId);
            }
            else
            {
                deviceId = Guid.NewGuid().ToString();
                await _deviceRepo.UpsertAsync(new AudioDevice
                {
                    DeviceId       = deviceId,
                    StudioId       = _studioContext.StudioId,
                    SystemDeviceId = systemId,
                    FriendlyName   = friendlyName,
                    DriverType     = DriverType.WasapiShared,
                    IsAvailable    = true,
                    LastSeenAt     = DateTime.UtcNow
                });
                _deviceIdAliases[deviceId] = deviceId;
                _logger.LogDebug("  → new device: [{Name}] deviceId={Id}", friendlyName, deviceId);
            }

            OutputDevices.Add(new AudioDeviceOption(deviceId, friendlyName));
        }

        _logger.LogDebug("OutputDevices populated: {Count} entries (incl. 'Nie przypisano')",
            OutputDevices.Count);

        // Enumerate recording devices (microphone, line-in, etc.) and persist to audio_devices
        // with UUIDs — same approach as output devices so device_input_id FK constraint is satisfied.
        var inputEnumerated = _deviceEnumerator.GetInputDevices();
        _logger.LogDebug("BASS enumerated {Count} recording device(s):", inputEnumerated.Count);
        foreach (var (sysId, name) in inputEnumerated)
            _logger.LogDebug("  BASS rec device: [{Name}] id={Id}", name, sysId);

        InputDevices.Clear();
        InputDevices.Add(new AudioDeviceOption(null, (Localizer.Instance?["settings.device.not_assigned"] ?? "— Not assigned —")));

        var inputSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (systemId, friendlyName) in inputEnumerated)
        {
            var compositeKey = $"{systemId}|{friendlyName.Trim()}";
            if (!inputSeen.Add(compositeKey)) continue;

            string deviceId;
            if (byComposite.TryGetValue(compositeKey, out var existing))
            {
                deviceId = existing.DeviceId;
                await _deviceRepo.UpdateAvailabilityAsync(deviceId, true, DateTime.UtcNow);
                _logger.LogDebug("  → matched DB rec device: [{Name}] deviceId={Id}", friendlyName, deviceId);
            }
            else
            {
                deviceId = Guid.NewGuid().ToString();
                await _deviceRepo.UpsertAsync(new AudioDevice
                {
                    DeviceId       = deviceId,
                    StudioId       = _studioContext.StudioId,
                    SystemDeviceId = systemId,
                    FriendlyName   = friendlyName,
                    DriverType     = DriverType.WasapiShared,
                    IsAvailable    = true,
                    LastSeenAt     = DateTime.UtcNow
                });
                _deviceIdAliases[deviceId] = deviceId;
                _logger.LogDebug("  → new rec device: [{Name}] deviceId={Id}", friendlyName, deviceId);
            }

            InputDevices.Add(new AudioDeviceOption(deviceId, friendlyName));
        }

        _logger.LogDebug("InputDevices populated: {Count} entries (incl. 'Nie przypisano')",
            InputDevices.Count);
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private AudioDeviceOption? FindDevice(string? savedId)
    {
        if (savedId is null) return null;
        var id = _deviceIdAliases.TryGetValue(savedId, out var canonical) ? canonical : savedId;
        var found = OutputDevices.FirstOrDefault(d => d.DeviceId == id);
        if (found is null)
            _logger.LogWarning("FindDevice: saved={Saved} → resolved={Resolved} → NOT FOUND in OutputDevices",
                savedId, id);
        else
            _logger.LogDebug("FindDevice: saved={Saved} → [{Name}]", savedId, found.DisplayName);
        return found;
    }

    private AudioDeviceOption? FindInputDevice(string? savedId)
    {
        if (savedId is null) return null;
        var id = _deviceIdAliases.TryGetValue(savedId, out var canonical) ? canonical : savedId;
        var found = InputDevices.FirstOrDefault(d => d.DeviceId == id);
        if (found is null)
            _logger.LogWarning("FindInputDevice: saved={Saved} → resolved={Resolved} → NOT FOUND in InputDevices",
                savedId, id);
        else
            _logger.LogDebug("FindInputDevice: saved={Saved} → [{Name}]", savedId, found.DisplayName);
        return found;
    }

    private void MapFromSettings(AudioSettings s)
    {
        _logger.LogDebug("MapFromSettings — saved device IDs: Player={P} Sweeper={Sw} PFL={Pfl} Cartwall={Cw} Input={In}",
            s.DevicePlayerId, s.DeviceSweeperId, s.DevicePflId, s.DeviceCartwallId, s.DeviceInputId);
        // Output Device
        OutputMode              = s.OutputMode.ToString();
        SelectedPlayerDevice    = FindDevice(s.DevicePlayerId);
        SelectedSweeperDevice   = FindDevice(s.DeviceSweeperId);
        SelectedPflDevice       = FindDevice(s.DevicePflId);
        SelectedCartwallDevice  = FindDevice(s.DeviceCartwallId);
        SelectedAux1Device      = FindDevice(s.DeviceAux1Id);
        SelectedAux2Device      = FindDevice(s.DeviceAux2Id);
        SelectedAux3Device      = FindDevice(s.DeviceAux3Id);
        SelectedAux4Device      = FindDevice(s.DeviceAux4Id);
        SelectedInputDevice     = FindInputDevice(s.DeviceInputId);

        // Audio
        SampleRate          = s.SampleRate.ToString();
        BufferSize          = s.BufferSize.ToString();
        CrossfadeEnabled    = s.CrossfadeEnabled;
        CrossfadeDurationMs = s.CrossfadeDurationMs;
        DuckingLevelDb      = s.DuckingLevelDb;
        DuckingAttackMs     = s.DuckingAttackMs;
        DuckingReleaseMs    = s.DuckingReleaseMs;

        // Auto DJ
        StopFadeoutMs            = s.StopFadeoutMs;
        AuxFadeoutMs             = s.AuxFadeoutMs;
        AutoFadeOutDurationS     = s.AutoFadeOutDurationS;
        SilenceRemoverEnabled    = s.SilenceRemoverEnabled;
        SilenceStartThresholdDb  = s.SilenceStartThresholdDb;
        SilenceMixThresholdDb    = s.SilenceMixThresholdDb;
        SilenceEndThresholdDb    = s.SilenceEndThresholdDb;
        SweeperEnabled           = s.SweeperEnabled;
        SweeperFormatId          = s.SweeperFormatId;
        MusicFormatId            = s.MusicFormatId;
        SweeperMinIntroMs        = s.SweeperMinIntroMs;
        SweeperDuckingDb         = s.SweeperDuckingDb;

        // Playlist
        DefaultMode            = s.DefaultMode.ToString();
        CountdownRedEnabled    = s.CountdownRedEnabled;
        CountdownRedThresholdS = s.CountdownRedThresholdS;
        CountdownGreenEnabled  = s.CountdownGreenEnabled;
        DeadAirEnabled         = s.DeadAirEnabled;
        DeadAirThresholdS      = s.DeadAirThresholdS;
        SelectedEmergencyPlaylist =
            EmergencyPlaylists.FirstOrDefault(p => p.PlaylistId == s.EmergencyPlaylistId)
            ?? EmergencyPlaylists.FirstOrDefault();

        // Cartwall
        CartwallSlotsPerPage   = s.CartwallSlotsPerPage.ToString();
        CartwallFadeoutMs      = s.CartwallFadeoutMs;
        CartwallSeparateWindow = s.CartwallSeparateWindow;

        // API
        ApiEnabled        = s.ApiEnabled;
        ApiPort           = s.ApiPort;
        ApiAuthEnabled    = s.ApiAuthEnabled;
        ApiUsername       = s.ApiUsername ?? "";
        ApiNewPassword    = "";
        ApiAnonymousLocal = s.ApiAnonymousLocal;

        // Appearance
        Theme = s.Theme.ToString();

        // Backup
        BackupPath      = s.BackupPath ?? "";
        BackupIntervalH = s.BackupIntervalH.ToString();
        BackupKeepCount = s.BackupKeepCount;
        BackupOnClose   = s.BackupOnClose;
        BackupLastAtText = s.BackupLastAt.HasValue
            ? s.BackupLastAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "—";
    }

    private void MapFromConfig(JsonNode cfg)
    {
        var gen = cfg["general"];
        if (gen is not null)
        {
            LogoPath         = gen["logo_path"]?.GetValue<string>()          ?? "";
            ShowClock        = gen["show_clock"]?.GetValue<bool>()            ?? true;
            ShowDate         = gen["show_date"]?.GetValue<bool>()             ?? true;
            Language         = gen["language"]?.GetValue<string>()            ?? "pl";
            DateFormat       = gen["date_format"]?.GetValue<string>()         ?? "dddd, d MMMM yyyy";
            ProcessPriority  = gen["process_priority"]?.GetValue<string>()    ?? "Normal";
            ResultsToShow    = gen["results_to_show"]?.GetValue<int>()        ?? 100;
            LibraryPageSize  = gen["library_page_size"]?.GetValue<int>()      ?? 50;
            AllowOneInstance = gen["allow_one_instance"]?.GetValue<bool>()    ?? true;
            MinimizeToTray   = gen["minimize_to_tray"]?.GetValue<bool>()      ?? false;
            AlwaysOnTop      = gen["always_on_top"]?.GetValue<bool>()         ?? false;
            EnableErrorLog   = gen["enable_error_log"]?.GetValue<bool>()      ?? true;
        }

        var audio = cfg["audio"];
        if (audio is not null)
        {
            PlaybackBufferMs = audio["playback_buffer_ms"]?.GetValue<int>() ?? 450;
            MixerBuffer      = audio["mixer_buffer"]?.GetValue<int>()       ?? 3;
            InputBufferMs    = audio["input_buffer_ms"]?.GetValue<int>()    ?? 2500;
            PreloadBeforeS   = audio["preload_before_s"]?.GetValue<int>()   ?? 10;
            FadeInManualMs   = audio["fadein_manual_ms"]?.GetValue<int>()   ?? 900;
        }

        var st = cfg["stream_titles"];
        if (st is not null)
        {
            StreamTitlesEnabled        = st["enabled"]?.GetValue<bool>()            ?? false;
            StreamTitlesPath           = st["output_file_path"]?.GetValue<string>() ?? "";
            StreamTitlesFormat         = st["format"]?.GetValue<string>()           ?? "$artist$ - $title$";
            StreamTitlesEncoding       = st["encoding"]?.GetValue<string>()         ?? "UTF-8";
            StreamTitlesUpdateOn       = st["update_on"]?.GetValue<string>()        ?? "TRACK_STARTED";
            StreamTitlesFallbackArtist = st["fallback_artist"]?.GetValue<string>()  ?? "Radio";
            StreamTitlesFallbackTitle  = st["fallback_title"]?.GetValue<string>()   ?? "";
            var allowedIds = (st["allowed_format_ids"] as JsonArray)
                ?.Select(n => n?.GetValue<string>())
                .OfType<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
            foreach (var item in StreamTitleFormats)
                item.IsChecked = allowedIds.Contains(item.FormatId);
        }

        // Streaming module — only the on/off switch lives in the config; the profiles themselves
        // are per-studio rows in the database, edited through the child view model.
        StreamingModuleEnabled = cfg["streaming"]?["enabled"]?.GetValue<bool>() ?? false;

        var rec = cfg["recording"];
        RecordingModuleEnabled = rec?["enabled"]?.GetValue<bool>() ?? false;
        RecordingDirectory     = Blank(rec?["output_directory"]?.GetValue<string>()) ?? DefaultRecordingDirectory;
        RecordingFormat        = Blank(rec?["format"]?.GetValue<string>())           ?? "MP3";
        RecordingBitrateKbps   = rec?["bitrate_kbps"]?.GetValue<int>()               ?? 192;
        RecordingNamePrefix    = Blank(rec?["name_prefix"]?.GetValue<string>())      ?? "rec";

        // API port from base_url
        var apiUrl = cfg["api"]?["base_url"]?.GetValue<string>();
        if (apiUrl is not null && Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri))
            ApiPort = uri.Port;
    }

    private AudioSettings BuildAudioSettings() => new()
    {
        SettingsId          = _current.SettingsId,
        StudioId            = _current.StudioId,

        OutputMode          = Enum.Parse<DriverType>(OutputMode),
        DevicePlayerId      = SelectedPlayerDevice?.DeviceId,
        DeviceSweeperId     = SelectedSweeperDevice?.DeviceId,
        DevicePflId         = SelectedPflDevice?.DeviceId,
        DeviceCartwallId    = SelectedCartwallDevice?.DeviceId,
        DeviceAux1Id        = SelectedAux1Device?.DeviceId,
        DeviceAux2Id        = SelectedAux2Device?.DeviceId,
        DeviceAux3Id        = SelectedAux3Device?.DeviceId,
        DeviceAux4Id        = SelectedAux4Device?.DeviceId,
        DeviceInputId       = SelectedInputDevice?.DeviceId,

        SampleRate          = Enum.Parse<AudioSampleRate>(SampleRate),
        BufferSize          = Enum.Parse<AudioBufferSize>(BufferSize),
        CrossfadeEnabled    = CrossfadeEnabled,
        CrossfadeDurationMs = CrossfadeDurationMs,
        DuckingLevelDb      = DuckingLevelDb,
        DuckingAttackMs     = DuckingAttackMs,
        DuckingReleaseMs    = DuckingReleaseMs,

        StopFadeoutMs           = StopFadeoutMs,
        AuxFadeoutMs            = AuxFadeoutMs,
        AutoFadeOutDurationS    = AutoFadeOutDurationS,
        SilenceRemoverEnabled   = SilenceRemoverEnabled,
        SilenceStartThresholdDb = SilenceStartThresholdDb,
        SilenceMixThresholdDb   = SilenceMixThresholdDb,
        SilenceEndThresholdDb   = SilenceEndThresholdDb,
        LoudnessTargetLufs      = _current.LoudnessTargetLufs,
        LoudnessNormalization   = _current.LoudnessNormalization,
        SweeperEnabled          = SweeperEnabled,
        SweeperFormatId         = SweeperFormatId,
        MusicFormatId           = MusicFormatId,
        SweeperMinIntroMs       = SweeperMinIntroMs,
        SweeperDuckingDb        = Math.Clamp(SweeperDuckingDb, 0f, 12f),

        DefaultMode            = Enum.Parse<PlaylistMode>(DefaultMode),
        CountdownRedEnabled    = CountdownRedEnabled,
        CountdownRedThresholdS = CountdownRedThresholdS,
        CountdownGreenEnabled  = CountdownGreenEnabled,
        DeadAirEnabled         = DeadAirEnabled,
        DeadAirThresholdS      = DeadAirThresholdS,
        EmergencyPlaylistId    = SelectedEmergencyPlaylist?.PlaylistId,

        CartwallSlotsPerPage   = byte.TryParse(CartwallSlotsPerPage, out var slots) ? slots : (byte)16,
        CartwallFadeoutMs      = CartwallFadeoutMs,
        CartwallSeparateWindow = CartwallSeparateWindow,

        BackupPath      = string.IsNullOrWhiteSpace(BackupPath) ? null : BackupPath,
        BackupIntervalH = byte.TryParse(BackupIntervalH, out var bih) ? bih : (byte)24,
        BackupKeepCount = (byte)Math.Clamp(BackupKeepCount, 1, 30),
        BackupOnClose   = BackupOnClose,
        BackupLastAt    = _current.BackupLastAt,

        ApiEnabled        = ApiEnabled,
        ApiPort           = (ushort)Math.Clamp(ApiPort, 1024, 65535),
        ApiAuthEnabled    = ApiAuthEnabled,
        ApiUsername       = string.IsNullOrWhiteSpace(ApiUsername) ? null : ApiUsername.Trim(),
        ApiPasswordHash   = string.IsNullOrWhiteSpace(ApiNewPassword)
                            ? _current.ApiPasswordHash
                            : BCrypt.Net.BCrypt.HashPassword(ApiNewPassword, workFactor: 12),
        ApiAnonymousLocal = ApiAnonymousLocal,

        Theme     = Enum.TryParse<AppTheme>(Theme, out var theme) ? theme : AppTheme.Dark,
        UpdatedAt = DateTime.UtcNow
    };

    // Mirrors the "stream_titles" section written in ApplyToConfig and the startup DI factory
    // (App.axaml.cs) so StreamTitlesService.UpdateSettings gets exactly what will be persisted.
    private RDM.Core.Models.StreamTitlesSettings BuildStreamTitlesSettings() => new()
    {
        Enabled        = StreamTitlesEnabled,
        OutputFilePath = StreamTitlesPath ?? string.Empty,
        Format         = string.IsNullOrEmpty(StreamTitlesFormat) ? "$artist$ - $title$" : StreamTitlesFormat,
        Encoding       = string.IsNullOrEmpty(StreamTitlesEncoding) ? "UTF-8" : StreamTitlesEncoding,
        FallbackArtist = StreamTitlesFallbackArtist ?? "Radio",
        FallbackTitle  = StreamTitlesFallbackTitle ?? string.Empty,
        AllowedFormatIds = new HashSet<string>(
            StreamTitleFormats.Where(f => f.IsChecked).Select(f => f.FormatId),
            StringComparer.OrdinalIgnoreCase),
    };

    private void ApplyToConfig(JsonNode cfg)
    {
        // General
        if (cfg["general"] is not JsonObject gen)
            cfg["general"] = gen = new JsonObject();
        gen["logo_path"]          = LogoPath;
        gen["show_clock"]         = ShowClock;
        gen["show_date"]          = ShowDate;
        gen["language"]           = Language;
        gen["date_format"]        = DateFormat;
        gen["process_priority"]   = ProcessPriority;
        gen["results_to_show"]    = ResultsToShow;
        gen["library_page_size"]  = LibraryPageSize;
        gen["allow_one_instance"] = AllowOneInstance;
        gen["minimize_to_tray"]   = MinimizeToTray;
        gen["always_on_top"]      = AlwaysOnTop;
        gen["enable_error_log"]   = EnableErrorLog;

        // Audio buffers
        if (cfg["audio"] is not JsonObject aud)
            cfg["audio"] = aud = new JsonObject();
        aud["playback_buffer_ms"] = PlaybackBufferMs;
        aud["mixer_buffer"]       = MixerBuffer;
        aud["input_buffer_ms"]    = InputBufferMs;
        aud["preload_before_s"]   = PreloadBeforeS;
        aud["fadein_manual_ms"]   = FadeInManualMs;

        // Stream Titles
        if (cfg["stream_titles"] is not JsonObject st)
            cfg["stream_titles"] = st = new JsonObject();
        st["enabled"]          = StreamTitlesEnabled;
        st["output_file_path"] = StreamTitlesPath;
        st["format"]           = StreamTitlesFormat;
        st["encoding"]         = StreamTitlesEncoding;
        st["update_on"]        = StreamTitlesUpdateOn;
        st["fallback_artist"]  = StreamTitlesFallbackArtist;
        st["fallback_title"]   = StreamTitlesFallbackTitle;
        var allowedArr = new JsonArray();
        foreach (var item in StreamTitleFormats.Where(f => f.IsChecked))
            allowedArr.Add(item.FormatId);
        st["allowed_format_ids"] = allowedArr;

        // Streaming / recording modules
        if (cfg["streaming"] is not JsonObject streamNode)
            cfg["streaming"] = streamNode = new JsonObject();
        streamNode["enabled"] = StreamingModuleEnabled;

        if (cfg["recording"] is not JsonObject recNode)
            cfg["recording"] = recNode = new JsonObject();
        recNode["enabled"]          = RecordingModuleEnabled;
        recNode["output_directory"] = RecordingDirectory;
        recNode["format"]           = RecordingFormat;
        recNode["bitrate_kbps"]     = RecordingBitrateKbps;
        recNode["name_prefix"]      = RecordingNamePrefix;

        // API port → base_url
        if (cfg["api"] is not JsonObject apiNode)
            cfg["api"] = apiNode = new JsonObject();
        var currentUrl = apiNode["base_url"]?.GetValue<string>() ?? "http://localhost:9300";
        if (Uri.TryCreate(currentUrl, UriKind.Absolute, out var uri))
            apiNode["base_url"] = $"{uri.Scheme}://{uri.Host}:{ApiPort}";
    }
}
