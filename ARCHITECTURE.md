# RDM Architecture Summary

## Stack

- **.NET 10** — `net10.0` for platform-neutral layers, `net10.0-windows` wherever the code touches Windows-specific APIs
- **Avalonia UI** (desktop, Windows-only today; `RDM.UI` targets `net10.0-windows`, so a port to another platform would require an API review)
- **ASP.NET Core Web API**
- **MariaDB**
- **Dapper** (no EF Core)
- **BASS / Bass.Net** (audio engine: BFX effects, VST hosting, ASIO/WASAPI/DirectSound output)

## Solution Layout (`RDM.sln`)

### RDM.Shared (`net10.0`)
- DTOs — the contract between `RDM.API` and `RDM.UI`
- Shared enums

### RDM.Core (`net10.0`)
- `Entities/` — domain entities (`Asset`, `Playlist`, `Cartwall`, `User`, `ScheduledEvent`, `EncoderProfile`, `Macro`, …)
- `Interfaces/` — contracts implemented in `RDM.Infrastructure` / `RDM.Audio`
- `Services/` — pure domain logic (`PlaylistEngine`, `CuePointBuilder`, `TransitionPlanner`, `SweeperEngine`, `EventScheduler`, `PasswordGenerator`, `UserSessionContext`, …)
- `Commands/` — lightweight marker records for dispatched actions (e.g. `PlayCommand`, `NextTrackCommand`, `LoadPlaylistCommand`), consumed by `IActionRegistry` / `ActionRouter` in `RDM.Infrastructure.Hardware`. This is **not** a full CQRS pipeline with separate command/query handlers — it is a simpler "action as object" pattern for hardware triggers, macros and events, not an ORM-style query layer.
- `Queues/` — background job queue models (BPM, loudness, waveform, cue analysis)
- `Hardware/`, `Events/`, `Exceptions/`, `Scripting/` (script result contracts)

### RDM.Infrastructure (`net10.0-windows`)
- `Repositories/` — Dapper repositories against MariaDB
- `Database/` — `DbConnectionFactory`, `DatabaseBootstrapper`, and `Migrations/` (applied by `MigrationRunner` on API startup, ordered by semantic version rather than filename sequence)
- Background services: `BackupService` / `BackupSchedulerService`, the BPM/loudness/waveform/cue-analysis queue services, `FolderWatchService`, `DeadAirMonitorService`, `PlaybackSessionSnapshotService`, `WaveformRescanService`, `EventSchedulerService`
- `Hardware/` — input/output drivers (MIDI, HTTP, generic serial, keyboard), action/feedback routing, the macro engine, `ActionRegistry`
- `Scripting/` — JavaScript hosting (`JintScriptEngine`, `ScriptingFacade`, `ScriptRunner`)
- `Security/` — `SecretProtector`
- Auth: `AuthService`, `LoginThrottle` (login rate limiting) — backs the multi-user Admin/Operator account model

### RDM.Audio (`net10.0-windows`)
- `Engine/BassAudioEngine.cs` — the audio engine (microphone ducking, equal-power crossfade, four output modes: DirectSound / WASAPI shared / WASAPI exclusive / ASIO, VU metering, sweeper/AUX routing). The largest and most critical file in the solution; it is deliberately untested by unit tests and validated by live listening only.
- `Engine/Output/` — per-backend output implementations (`AsioBackend`, `WasapiBackend`, `DirectSoundBackend`) behind `IOutputBackend`, selected via `OutputBackendFactory`
- `Processing/` — DSP: BFX effect chain, VST hosting for the microphone chain, `BpmAnalyzer`, `LoudnessAnalyzer`, `WaveformGenerator`, `AutoCueDetector`, `CuePointTimer`
- `Sweeper/` — sweeper trigger engine

### RDM.API (`net10.0-windows`)
- REST API + WebSocket (`RdmWebSocketHub` broadcasts live playback state)
- `Controllers/` — Assets, Playback, PlaylistItems, SavedPlaylists, Cartwalls, Aux, Mic, Encoder, Streams, Recording, Import, Scan, Waveform, ScheduledEvents, EventLog, Categories, Formats, NowPlaying
- `Middleware/` — `AuthMiddleware` (Basic Auth, with a loopback-only bypass for anonymous LAN use), `ErrorHandlingMiddleware`
- `Services/` — `ImportJobService`, `ScanJobService`

### RDM.UI (`net10.0-windows`)
- Avalonia, MVVM (`Views/` + `ViewModels/`), DI via `Microsoft.Extensions.DependencyInjection`
- `Localization/` — a bespoke i18n system (`Localizer` + the `{i18n:Tr}` markup extension), language files in `lang/*.json` (currently `en`, `pl`)
- `Controls/` — custom Avalonia controls, including `SegueTimelineControl`, `WaveformControl`, `VuMeterControl`, `CartGridPanel`, `AnalogClockControl`
- `Services/` — `ApiClientService` (HTTP + WebSocket client for `RDM.API`), local configuration and settings (`ConfigPaths`, `SettingsConfigService`), `HotkeyManagerService`, `NavigationService`, `MicDspChainStore`, `WaveformDecoder`, `UndoRedoStack`
- Key windows/dialogs: `MainWindow`, `LibraryView`, `PlaylistView`, `PlaylistBuilderWindow`, `CartwallView`/`CartwallWindow`, `TrackEditorWindow`, `SegueEditorWindow`, `MicDspChainWindow`, `VstEditorWindow` (VST plug-in editor for the mic chain), `HardwareManagerWindow`, `ScheduledEventsWindow`, `SettingsWindow`, `LoginWindow`, `UserEditDialog` (Admin/Operator account management)

### Tests
`RDM.Core.Tests`, `RDM.Infrastructure.Tests`, `RDM.API.Tests` — xUnit + FluentAssertions + Moq.
`RDM.UI` and `RDM.Audio` have **no** test projects: for `RDM.Audio` this is a deliberate decision (the engine is validated by live listening instead); for `RDM.UI` it is open debt — some ViewModel logic is cleanly testable but currently untested.

### tools/RDM.Audio.TestApp
A standalone application for manually exercising the audio engine outside the full UI.

### installer/
Inno Setup (`rdm-setup.iss`) plus `publish.ps1` (publishing, versioning, stripping the dev configuration from the package).

## Conventions

- Dapper, not EF Core.
- Async everywhere I/O is involved.
- Constructor injection; no static state, aside from explicitly documented exceptions (e.g. `Localizer.Instance`, a singleton shared with the markup extension).
- Connection strings and secrets come from configuration (`rdm.config.json` / `%ProgramData%\RDM\`), never hardcoded.
- Every new service registered in `RDM.API` must also be registered in `RDM.UI`'s composition root — the solution has **two independent DI containers** (`RDM.API/Program.cs` and `RDM.UI/App.axaml.cs`); missing one causes a runtime "unable to resolve service" that the test suite will not catch, since tests only exercise `Program.cs`.
- Changes to `RDM.Audio/Engine/BassAudioEngine.cs` (ducking, routing, output modes) require live listening — a successful build is not sufficient verification.

## Database

- MariaDB — local or network instance; host/port/credentials are deployment-specific.
- Connection string comes exclusively from configuration, never hardcoded.
- Migrations are semver-versioned and applied automatically at startup (`DatabaseBootstrapper` → `MigrationRunner`).
