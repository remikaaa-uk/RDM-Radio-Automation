# RDM Local Configuration File (`rdm.config.json`)

A guide to RDM's local configuration file — where it lives, how it's loaded, and what every section and key actually does.

> **Not what you're after?** Most day-to-day settings (encoder/streaming profiles, user accounts, trigger mappings, most audio settings like the fadeout time on stop) live in the application's **database**, not in this file — see [§6](#6-what-does-not-live-in-this-file). If you're looking for the HTTP API, see the separate [RDM API Documentation](RDM_API_Documentation.md).

---

## Table of Contents

1. [Overview](#1-overview)
2. [Location](#2-location)
3. [How the File Is Loaded at Startup](#3-how-the-file-is-loaded-at-startup)
4. [Editing the File](#4-editing-the-file)
5. [Full Schema Reference](#5-full-schema-reference)
   - [5.1 `general`](#51-general)
   - [5.2 `audio`](#52-audio)
   - [5.3 `recording`](#53-recording)
   - [5.4 `streaming`](#54-streaming)
   - [5.5 `stream_titles`](#55-stream_titles)
   - [5.6 `api`](#56-api)
   - [5.7 `voicetrack`](#57-voicetrack)
   - [5.8 `scheduler`](#58-scheduler)
   - [5.9 `database`](#59-database)
   - [5.10 `hardware`](#510-hardware)
   - [5.11 `ui_state`](#511-ui_state)
   - [5.12 `mic_dsp`](#512-mic_dsp)
6. [What Does NOT Live in This File](#6-what-does-not-live-in-this-file)
7. [Known Unused/Dead Keys](#7-known-uuseddead-keys)
8. [Full Annotated Example](#8-full-annotated-example)

---

## 1. Overview

`rdm.config.json` is RDM's per-machine configuration file. It holds local, machine-specific settings that need to exist *before* the application can even connect to its shared database (most importantly, the database connection details themselves), plus a handful of other local-only preferences — UI layout memory, buffer sizes, the local API port, and so on.

It is **not** where most of RDM's day-to-day configuration lives. Encoder/streaming profiles, scheduled events, user accounts, trigger mappings, and most audio behaviour (the settings under Settings → Auto DJ, for instance) are stored in the shared **MySQL database** instead, because they need to be the same across every machine in a studio. See [§6](#6-what-does-not-live-in-this-file) for the dividing line.

## 2. Location

**The writable file always lives at:**

```
%ProgramData%\RDM\rdm.config.json
```

— typically `C:\ProgramData\RDM\rdm.config.json`.

The reason it lives there rather than next to the application itself: Program Files (where RDM is installed) isn't writable by a standard user, so any setting the app needs to persist — including the database connection it needs on every startup — has to live somewhere writable from the start.

### First-run seeding

On first launch, if `%ProgramData%\RDM\rdm.config.json` doesn't exist yet, RDM copies a **template** into place from the file the installer placed in the Program Files install folder. After that first copy, the two files are independent — editing the template does nothing once the live file exists.

## 3. How the File Is Loaded at Startup

There's a startup subtlety worth knowing if you're troubleshooting the API port specifically: RDM's HTTP API (see [RDM API Documentation](RDM_API_Documentation.md)) is hosted *inside* the desktop application process, and ASP.NET Core's web server (Kestrel) needs to know what port to bind **before** the rest of the application configuration system has even started up.

To make that work, RDM reads the config file **twice** at startup:

1. A throwaway, minimal read — just to pull `api.base_url` and set an environment variable (`ASPNETCORE_URLS`) before the web server builder is created. This is what actually controls what port Kestrel binds to.
2. The normal, full configuration load that the rest of the application (including RDM.API's controllers) uses for everything else, with live-reload enabled.

**Practical consequence:** changing the port number for `api.base_url` from Settings → API works as expected (it rewrites the file, and a restart re-runs the two-pass startup). Changing the *host* portion of `api.base_url` by hand (e.g. to make the API reachable from other machines on your network) also requires a restart to take effect, and there's no Settings UI for it — see [§5.6](#56-api).

## 4. Editing the File

Most sections in this file have a corresponding control in **Settings** inside the RDM desktop app — that's the safer way to edit them, since the app validates values and writes the file for you. A handful of sections have **no UI at all** and are manual-edit-only; these are called out individually below.

If you do edit the file by hand:

- **Close RDM first**, or expect to restart it afterwards — most sections are only read at startup, not hot-reloaded while running.
- It must remain **valid JSON** — a syntax error will typically cause RDM to fall back to defaults for the broken section (or, for `database`, prevent startup entirely).
- Back up the file before making manual changes; there's no automatic undo for a bad edit.

## 5. Full Schema Reference

### 5.1 `general`

| Key                  | Type   | Default                          | Editable in Settings?              | Purpose                                                               |
| -------------------- | ------ | -------------------------------- | ---------------------------------- | --------------------------------------------------------------------- |
| `language`           | string | `"en"`                           | Yes                                | UI language.                                                          |
| `date_format`        | string | `"dddd, d MMMM yyyy"`            | Yes                                | .NET date format string used for on-screen date displays.             |
| `process_priority`   | string | `"Normal"`                       | Yes                                | OS process priority for the RDM process.                              |
| `results_to_show`    | int    | `100`                            | Yes                                | Max rows shown in various search/results lists.                       |
| `library_page_size`  | int    | `50`                             | Yes                                | Page size for the library/playlist-builder/tracks-manager list views. |
| `allow_one_instance` | bool   | `true`                           | Yes                                | Prevents a second copy of RDM from launching on the same machine.     |
| `minimize_to_tray`   | bool   | `false`                          | Yes                                | Minimise to the system tray instead of the taskbar.                   |
| `always_on_top`      | bool   | `false`                          | Yes                                | Keep the main window always on top.                                   |
| `enable_error_log`   | bool   | `true`                           | Yes                                | Toggles a secondary error log (separate from the main `rdm.log`).     |
| `error_log_path`     | string | `"%AppData%\RDM\logs\error.log"` | Yes (writes, but nothing reads it) | ⚠️ **Not currently used** — see [§7](#7-known-uuseddead-keys).        |
| `logo_path`          | string | `""`                             | Yes                                | Path to a custom logo image shown in the UI.                          |
| `show_clock`         | bool   | `true`                           | Yes                                | Show the clock in the UI.                                             |
| `show_date`          | bool   | `true`                           | Yes                                | Show the date in the UI.                                              |

### 5.2 `audio`

Buffer-related settings for the audio engine. **Changing these requires an application restart** — they're only read at startup.

| Key                   | Type | Default | Editable in Settings?              | Purpose                                                                                                                                                |
| --------------------- | ---- | ------- | ---------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `playback_buffer_ms`  | int  | `450`   | Yes                                | Main playback buffer size (ms).                                                                                                                        |
| `mixer_buffer`        | int  | `3`     | Yes                                | Mixer buffer count.                                                                                                                                    |
| `input_buffer_ms`     | int  | `2500`  | Yes                                | Input (recording/monitoring) buffer size (ms).                                                                                                         |
| `preload_before_s`    | int  | `10`    | Yes                                | How many seconds ahead the next track is preloaded.                                                                                                    |
| `fadein_manual_ms`    | int  | `900`   | Yes                                | Fade-in duration for manually-started playback (ms).                                                                                                   |
| `stop_fadeout_ms`     | int  | `1250`  | Yes (writes, but nothing reads it) | ⚠️ **Not currently used** — the real fade-out-on-stop value is stored in the database instead (Settings → Auto DJ). See [§7](#7-known-uuseddead-keys). |
| `bass_update_period`  | int  | `100`   | No                                 | ⚠️ **Not currently used.**                                                                                                                             |
| `bass_update_threads` | int  | `1`     | No                                 | ⚠️ **Not currently used.**                                                                                                                             |

### 5.3 `recording`

Local recorder defaults (Settings → Streaming/Recording tab).

| Key                | Type   | Default                            | Editable in Settings? | Purpose                                                                                                                       |
| ------------------ | ------ | ---------------------------------- | --------------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `enabled`          | bool   | *(absent = off)*                   | Yes                   | Turns the Recording module on/off (controls whether its icon shows in the UI). Not present until you first touch the setting. |
| `output_directory` | string | `""` (falls back to `~\Music\RDM`) | Yes                   | Default folder for new recordings.                                                                                            |
| `format`           | string | `"MP3"`                            | Yes                   | Default recording format.                                                                                                     |
| `bitrate_kbps`     | int    | `192`                              | Yes                   | Default recording bitrate.                                                                                                    |
| `name_prefix`      | string | `"rec"`                            | Yes                   | Filename prefix for new recordings.                                                                                           |

### 5.4 `streaming`

Not present in any shipped template — added the first time you touch the Streaming toggle in Settings.

| Key       | Type | Default          | Editable in Settings? | Purpose                                                                                                                                                                              |
| --------- | ---- | ---------------- | --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `enabled` | bool | *(absent = off)* | Yes                   | Turns the Streaming module on/off (icon visibility only). **Actual encoder profiles are not stored here** — they live in the database; see [§6](#6-what-does-not-live-in-this-file). |

### 5.5 `stream_titles`

"Now playing" text-file export, for feeding external players/websites (Settings → Stream Titles tab).

| Key                  | Type     | Default                | Editable in Settings?              | Purpose                                                                                                                            |
| -------------------- | -------- | ---------------------- | ---------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| `enabled`            | bool     | `false`                | Yes                                | Turns the feature on/off.                                                                                                          |
| `output_file_path`   | string   | `""`                   | Yes                                | Where the text file is written.                                                                                                    |
| `format`             | string   | `"$artist$ - $title$"` | Yes                                | Template string. Tokens: `$artist$ $title$ $duration$ $format$ $year$ $bpm$`.                                                      |
| `encoding`           | string   | `"UTF-8"`              | Yes                                | Text encoding for the output file (also accepts `"ANSI"`).                                                                         |
| `update_on`          | string   | `"TRACK_STARTED"`      | Yes (writes, but nothing reads it) | ⚠️ **Not currently used** — the service always updates on track-start regardless of this value. See [§7](#7-known-uuseddead-keys). |
| `fallback_artist`    | string   | `"Radio"`              | Yes                                | Used when a track has no artist tag.                                                                                               |
| `fallback_title`     | string   | `"Testowe"`            | Yes                                | Used when a track has no title tag.                                                                                                |
| `allowed_format_ids` | string[] | `[]` (= all formats)   | Yes                                | Restricts the feature to specific asset formats/categories.                                                                        |

### 5.6 `api`

Controls RDM's built-in HTTP API. See the [RDM API Documentation](RDM_API_Documentation.md) for everything the API itself does.

| Key        | Type         | Default                   | Editable in Settings?              | Purpose                            |
| ---------- | ------------ | ------------------------- | ---------------------------------- | ---------------------------------- |
| `base_url` | string (URL) | `"http://localhost:9300"` | **Port only**, via Settings → API. | Base URL the API/Kestrel binds to. |

> ⚠️ **Authentication settings are not here.** `api.base_url` sits in the same Settings tab as the API's login toggle and credentials, but those (`ApiAuthEnabled`, `ApiAnonymousLocal`, `ApiUsername`, `ApiPasswordHash`) are stored in the **database**, not in this file. See [§6](#6-what-does-not-live-in-this-file).
> 
> Changing only the **port** is supported from the UI. If you need the API reachable from other machines on your network, you'd need to change the **host** part of `base_url` by hand and restart — there's no UI control for that, and doing so has security implications (the built-in Basic Auth "trusted localhost" bypass only applies to loopback connections; see the API doc's authentication section).

### 5.7 `voicetrack`

No Settings UI — manual-edit only.

| Key    | Type   | Default         | Purpose                                     |
| ------ | ------ | --------------- | ------------------------------------------- |
| `path` | string | `"VoiceTracks"` | Folder used by the segue/voicetrack editor. |

### 5.8 `scheduler`

No Settings UI — manual-edit only, and **security-relevant**.

| Key                        | Type | Default (installer) | Purpose                                                                                                                                                                                       |
| -------------------------- | ---- | ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `allow_external_execution` | bool | `true`              | Gates whether scheduled events are allowed to run "external program" actions. **A missing or unparsable value is treated as `false`** — the feature fails closed/secure by default, not open. |

### 5.9 `database`

Connection details for RDM's shared MySQL database. Set through a dedicated first-run **Database Setup** window (also reachable later), not through the main Settings dialog. **Changing this requires a restart.**

| Key              | Type   | Default (installer)                  | Purpose                                                             |
| ---------------- | ------ | ------------------------------------ | ------------------------------------------------------------------- |
| `host`           | string | `"localhost"`                        | MySQL host.                                                         |
| `port`           | int    | `3306`                               | MySQL port.                                                         |
| `name`           | string | `"rdm"`                              | Database name.                                                      |
| `username`       | string | `"root"`                             | MySQL username.                                                     |
| `password`       | string | `""`                                 | MySQL password — **stored in plain text** in this file.             |
| `dump_tool_path` | string | `"mysqldump"` (resolved from `PATH`) | Manual-edit only — path to the `mysqldump` binary used for backups. |

### 5.10 `hardware`

Serial (RS-232) device configuration — e.g. driving an external audio matrix. Not present in any shipped template; manual-edit only, no Settings UI.

```json
"hardware": {
  "serial_drivers": [
    { "device_id": "matrix_main", "port": "COM4", "baud_rate": 9600, "terminator": "\r\n" }
  ]
}
```

| Key          | Type   | Default      | Purpose                                                                                                                                                                                                               |
| ------------ | ------ | ------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `device_id`  | string | *(required)* | Identifier referenced elsewhere (e.g. `rdm.sendSerial(deviceId, cmd)` in JavaScript scripts — see the [JavaScript Scripting Guide](JavaScript_Scripting_Guide.md)). An entry with no `device_id` is silently ignored. |
| `port`       | string | *(required)* | COM port name.                                                                                                                                                                                                        |
| `baud_rate`  | int    | `9600`       | Serial baud rate.                                                                                                                                                                                                     |
| `terminator` | string | `"\r\n"`     | Line terminator appended to outgoing commands (accepts `\r`/`\n` escapes).                                                                                                                                            |

### 5.11 `ui_state`

Entirely runtime-managed — RDM writes to it automatically to remember window positions/sizes and which tab was last selected in various panels. Not present on a fresh install; appears after you first move a window or switch a tab. **Not meant to be hand-edited** — delete it if you want to reset the UI layout, don't try to construct it by hand.

```json
"ui_state": {
  "<windowKey>": { "x": 100, "y": 100, "width": 1280, "height": 800 },
  "<panelKey>_tab": 2
}
```

### 5.12 `mic_dsp`

The microphone effects/VST chain — built by the mic FX editor in the UI. Deliberately stored here (machine-local) rather than in the shared database, because VST slots reference absolute DLL paths that are only valid on the machine that loaded them. Not present until you add an effect or VST to the mic path.

```json
"mic_dsp": {
  "fx":  [ { "type": "<MicFxType>", "params": { "<paramKey>": 0.0 } } ],
  "vst": [ { "dll_path": "C:\\VST\\SomePlugin.dll", "name": "Some Plugin", "state_chunk": "<base64, optional>" } ]
}
```

## 6. What Does NOT Live in This File

To avoid the most common confusion: everything in the table below is configured through the RDM UI but is **stored in the shared MySQL database**, not in `rdm.config.json`. That's deliberate — these need to be the same across every machine in a studio, or they change too frequently for a per-machine file to make sense.

| Lives in the database instead                                                 | Where to edit it         |
| ----------------------------------------------------------------------------- | ------------------------ |
| Encoder/streaming server profiles (host, port, credentials, etc.)             | Settings → Streaming     |
| API authentication (`ApiAuthEnabled`, `ApiAnonymousLocal`, username/password) | Settings → API           |
| Most Auto DJ behaviour (crossfade, stop fade-out, ducking parameters)         | Settings → Auto DJ       |
| User accounts (Admin/Operator)                                                | Settings → Users         |
| Trigger mappings (MIDI/hardware)                                              | Hardware Manager         |
| Scheduled events, cartwall contents, saved playlists, the library itself      | Their respective screens |

## 7. Known Unused/Dead Keys

A handful of keys are written by the Settings UI (or shipped in the template files) but have **no code that actually reads them back** at the time of writing. They won't break anything if you set them, but don't expect them to have any effect:

- `general.error_log_path`
- `audio.stop_fadeout_ms` *(the real value is the database-backed one under Settings → Auto DJ)*
- `audio.bass_update_period`, `audio.bass_update_threads`
- `stream_titles.update_on` *(the service always updates on track-start regardless)*

If you're troubleshooting a setting that doesn't seem to "stick", check this list first — before assuming the config file is corrupt.

## 8. Full Annotated Example

Everything below reflects real, functional keys with their defaults (comments added for this document only — real JSON doesn't support `//` comments, so strip them if you paste this in). Runtime-only sections (`ui_state`, `mic_dsp`) are omitted since they shouldn't be hand-authored.

```jsonc
{
  "rdm_config_version": "1.0",

  "general": {
    "language":           "en",
    "date_format":        "dddd, d MMMM yyyy",
    "process_priority":   "Normal",
    "results_to_show":    100,
    "library_page_size":  50,
    "allow_one_instance": true,
    "minimize_to_tray":   false,
    "always_on_top":      false,
    "enable_error_log":   true,
    "logo_path":          "",
    "show_clock":         true,
    "show_date":          true
  },

  "audio": {
    "playback_buffer_ms": 450,
    "mixer_buffer":       3,
    "input_buffer_ms":    2500,
    "preload_before_s":   10,
    "fadein_manual_ms":   900
  },

  "recording": {
    "enabled":          false,
    "output_directory": "",
    "format":           "MP3",
    "bitrate_kbps":      192,
    "name_prefix":       "rec"
  },

  "streaming": {
    "enabled": false
  },

  "stream_titles": {
    "enabled":            false,
    "output_file_path":   "",
    "format":             "$artist$ - $title$",
    "encoding":           "UTF-8",
    "fallback_artist":    "Radio",
    "fallback_title":     "Testowe",
    "allowed_format_ids": []
  },

  "api": {
    "base_url": "http://localhost:9300"
  },

  "voicetrack": {
    "path": "VoiceTracks"
  },

  "scheduler": {
    "allow_external_execution": true
  },

  "database": {
    "host":     "localhost",
    "port":     3306,
    "name":     "rdm",
    "username": "root",
    "password": ""
  },

  "hardware": {
    "serial_drivers": [
      { "device_id": "matrix_main", "port": "COM4", "baud_rate": 9600, "terminator": "\r\n" }
    ]
  }
}
```

---

**Last updated**: 2026-07-18
