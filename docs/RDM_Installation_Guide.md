# RDM Installation Guide

How to get RDM running — either by installing the release build, or by building it from source. This is a guide for **end users and self-hosters**; if you're contributing code, see the project's own developer notes as well.

> Looking for something else? [RDM Local Configuration File](RDM_LocalConfig.md) documents `rdm.config.json` in detail, and [RDM API Documentation](RDM_API_Documentation.md) covers the HTTP API.

---

## Table of Contents

1. [Two Ways to Get RDM](#1-two-ways-to-get-rdm)
2. [System Requirements](#2-system-requirements)
3. [Installing from the Release Installer](#3-installing-from-the-release-installer)
4. [After Installation](#4-after-installation)
5. [Uninstalling](#5-uninstalling)
6. [Building from Source](#6-building-from-source)
7. [Troubleshooting](#7-troubleshooting)

---

## 1. Two Ways to Get RDM

| Path | Who it's for |
|---|---|
| **[Installer](#3-installing-from-the-release-installer)** (`RDM-Setup.exe`, from [GitHub Releases](https://github.com/remikaaa-uk/RDM-Radio-Automation/releases)) | Everyone who just wants to run RDM. Includes the audio engine libraries and offers to install everything else it needs. |
| **[Build from source](#6-building-from-source)** | Anyone who wants to modify the code, audit it, or run it on a machine without using the pre-built installer. You'll need to obtain the audio engine libraries yourself — see [§6](#6-building-from-source) for why. |

## 2. System Requirements

Only what's actually enforced or confirmed by the project is listed here — this section deliberately does not guess at minimum RAM/CPU, since that hasn't been benchmarked.

- **Operating system:** Windows, **64-bit only**. The installer explicitly blocks 32-bit systems and installs into the 64-bit Program Files.
- **Administrator rights** are required to run the installer (it installs to Program Files and can install system-wide prerequisites).
- **Disk space:** the installed application itself is roughly **35–50 MB**. If you let the installer set up a **local** database server (MariaDB), budget an additional **~250–400 MB** for that. The downloaded installer file itself is about **12 MB**.
- **Database:** a MariaDB or MySQL server — either installed locally by the installer, or an existing server elsewhere on your network that you already have credentials for.

## 3. Installing from the Release Installer

### 3.1 Download

Get the latest `RDM-Setup.exe` from the project's **[GitHub Releases](https://github.com/remikaaa-uk/RDM-Radio-Automation/releases)** page. Don't build the installer yourself unless you're doing [§6](#6-building-from-source) — the release build is the supported path.

### 3.2 Run the installer

The installer is a standard Windows wizard (Inno Setup) with two extra pages beyond the usual "choose a folder" steps:

**Database page** — asks for the MariaDB/MySQL connection you want RDM to use: host, port, database name, username, password. The suggested defaults (`localhost`, `3306`, `rdm`, `root`) work if you're about to let the installer set up a local database for you. **You don't need to create the database or its tables yourself** — RDM creates its schema and the initial admin account automatically the first time it starts.

**Requirements page** — the installer probes your system and pre-selects only what's actually missing:

| Component | How it's detected | What happens if missing and selected |
|---|---|---|
| **.NET 10 Runtime** (Desktop + ASP.NET Core, both required) | Runs `dotnet --list-runtimes` and checks for both `Microsoft.WindowsDesktop.App 10.x` and `Microsoft.AspNetCore.App 10.x` | Downloaded from Microsoft and installed silently |
| **MariaDB** (database server) | Checks the Windows service registry for a MariaDB/MySQL service | Downloaded and installed silently, with the port and root password you gave on the database page — **only offered if the database host you entered is `localhost`/`127.0.0.1`/empty**; if you pointed at a remote server, this step is skipped automatically |
| **Visual C++ 2015–2022 Redistributable (x64)** | Checks the registry for the VC++ x64 runtime | Downloaded from Microsoft and installed silently — required by the native audio (BASS) libraries |

You can uncheck any box if you'd rather handle that component yourself.

> **The audio engine libraries (BASS) are already bundled in the installer** — unlike the three components above, you don't need to source or install them separately when installing from the release build. See [§6.3](#63-audio-engine-libraries-bass) if you're building from source instead, where that's not the case.

### 3.3 What happens after you click through

1. The selected prerequisites (if any) are downloaded and installed silently.
2. The application files are copied to `Program Files\RDM`.
3. A config template is written to `%ProgramData%\RDM\rdm.config.json` (only if that file doesn't already exist — an existing config from a previous install is left alone) with the `database` section filled in from what you entered on the database page.
4. Desktop/Start Menu shortcuts are created.
5. RDM launches (if you leave "Run RDM after installation" checked). On this very first launch, RDM connects to the database you configured and creates its schema and an initial administrator account — there's no separate manual database-setup step.

## 4. After Installation

- **Configuration file:** `%ProgramData%\RDM\rdm.config.json` — see [RDM Local Configuration File](RDM_LocalConfig.md) for the full schema.
- **Log file:** `%ProgramData%\RDM\rdm.log`.
- **A note on the audio engine's licence:** the bundled BASS libraries (un4seen) are free for **non-commercial** use. If you're running RDM for a **commercial** radio station, that requires a paid BASS licence from un4seen directly, independent of how the files reached your machine — this isn't something the installer or RDM itself can determine on your behalf. See un4seen's own site for current terms.

## 5. Uninstalling

Use **Settings → Apps** (or the Start Menu shortcut) to uninstall RDM as you would any Windows application. Note what the uninstaller does **not** do:

- It does **not** remove `%ProgramData%\RDM\` — your configuration, logs, and (if you used one) the local `mic_dsp`/`ui_state` data stay on disk. This is deliberate: reinstalling RDM later picks the existing config back up rather than starting from scratch.
- It does **not** touch a MariaDB/MySQL server or the `rdm` database, whether the installer set one up locally or you pointed it at an existing one — your library and settings in the database are untouched. Remove those yourself (via the MariaDB/MySQL tooling) if you want a truly clean slate.

## 6. Building from Source

This is for developers, contributors, or anyone who wants to run RDM without the pre-built installer.

### 6.1 Prerequisites

| Requirement | Notes |
|---|---|
| **Windows, 64-bit** | The project targets `net10.0-windows` (Windows-specific APIs) — it isn't currently buildable/runnable on Linux or macOS despite Avalonia's cross-platform UI framework being used. |
| **.NET 10 SDK** | Matches the `net10.0` / `net10.0-windows` target framework used throughout the solution. |
| **Visual Studio 2022** (or later, with the .NET desktop workload) — optional | Convenient for opening `RDM.sln`, but the `dotnet` CLI works too (see [§6.4](#64-build-and-run)). |
| **A MariaDB or MySQL server** | Needed to actually *run* the app (build/compile doesn't need it, but you can't get past first launch without it). |
| **BASS audio libraries** | **Not included in the repository** — see [§6.3](#63-audio-engine-libraries-bass), this is the one prerequisite that needs a manual step. |
| **Inno Setup 6.1+** — optional | Only needed if you also want to build your own `RDM-Setup.exe`; not required to build or run the app itself. |

### 6.2 Get the source

```bash
git clone https://github.com/remikaaa-uk/RDM-Radio-Automation.git
cd RDM-Radio-Automation
```

The repository root contains `RDM.sln` (the solution file — open this in Visual Studio, or use it with the `dotnet` CLI) along with `version.props` and `Directory.Build.props` (shared version numbering for every project).

### 6.3 Audio Engine Libraries (BASS)

RDM's audio engine is built on **BASS** (native libraries) and **Bass.Net** (its managed .NET wrapper), both from [un4seen](https://www.un4seen.com/). These are licensed products and are **deliberately excluded from the source repository** — `libs/*.dll` is in `.gitignore`. Building from source means fetching them yourself.

1. Download the following from un4seen.com (make sure you get the **x64** builds):

   | File | Required? | Enables |
   |---|---|---|
   | `Bass.Net.dll` | **Yes** — the app won't even start without it | The managed BASS wrapper the whole audio engine is built on |
   | `bass.dll` | **Yes** | Core audio engine |
   | `bassmix.dll` | **Yes** | Mixer/routing |
   | `basswasapi.dll` | **Yes** | WASAPI audio output |
   | `bassloud.dll` | **Yes** | Loudness measurement/normalisation |
   | `bass_fx.dll` | Optional | BFX effects on the microphone chain |
   | `bass_vst.dll` | Optional | VST 2.x plugin hosting |
   | `bassasio.dll` | Optional | ASIO output backend |
   | `bassenc.dll` | Optional | Streaming (SHOUTcast/Icecast) — required by every per-format encoder below |
   | `bassenc_mp3.dll`, `bassenc_ogg.dll`, `bassenc_opus.dll` | Optional | One per streaming format; each enables just that format |

2. Place all of them directly in a `libs\` folder at the **repository root** (i.e. `RDM-Radio-Automation\libs\Bass.Net.dll`, `RDM-Radio-Automation\libs\bass.dll`, etc.) — this is the exact path `RDM.Audio.csproj` references via `HintPath`/`<Content Include>`. The optional files are picked up automatically if present (conditional `<Content>` entries) and simply skipped if you don't add them — you'll just lose that specific feature (e.g. no `bassasio.dll` means no ASIO output option), not a broken build.

   > **This is only where you place the files before building — they don't stay there.** The build copies each one to a specific spot next to the compiled executable, and **the two spots are different**:
   > - `Bass.Net.dll` is a plain assembly reference, so it's copied into the **main program folder** (the same folder as `RDM.exe`).
   > - All the native `bass*.dll` files are copied into a **`BassLib\` subfolder** next to `RDM.exe` (this is the exact same layout the release installer produces).
   >
   > Get this wrong — say, by manually copying a `bass*.dll` next to `RDM.exe` instead of into `BassLib\`, or `Bass.Net.dll` into `BassLib\` instead of next to `RDM.exe` — and RDM **will not run**. If you're only ever placing files in `libs\` and letting the build copy them, you don't need to think about this; it only matters if you're troubleshooting a broken build output or copying DLLs in by hand.

3. **Licensing reminder** (not legal advice — confirm current terms at un4seen.com): BASS is free for **non-commercial** use; **commercial** use (e.g. an actual on-air commercial radio station) requires a paid BASS licence, regardless of how the files ended up on your machine. Bass.Net has its own, separate registration/licensing on top of that — see the "Registration slot" comment near `BassNet.Registration(...)` in `BassAudioEngine.cs` if you're setting up a licensed build.

### 6.4 Build and Run

From the repository root:

```bash
# Restore + build the whole solution
dotnet build RDM.sln -c Debug

# Run the desktop app directly
dotnet run --project src\RDM.UI\RDM.UI.csproj
```

Or open `RDM.sln` in Visual Studio and press F5 — same result, with debugging.

On first run in a dev build, RDM copies a config template that sits next to the compiled executable (`src\RDM.UI\bin\Debug\net10.0-windows\rdm.config.json`) into `%ProgramData%\RDM\rdm.config.json` if that file doesn't exist yet — edit the copy in `%ProgramData%`, not the template, once it exists. Fill in the `database` section with credentials for a MariaDB/MySQL server you have running; RDM creates the schema itself on first connect, same as the installed build does. See [RDM Local Configuration File](RDM_LocalConfig.md) for the full config reference.

### 6.5 Running the Tests

```bash
dotnet test RDM.sln
```

### 6.6 Building Your Own Installer (Optional)

Only relevant if you want to produce your own `RDM-Setup.exe` (e.g. for distributing a modified build):

```powershell
powershell -ExecutionPolicy Bypass -File installer\publish.ps1
```

This publishes a framework-dependent `win-x64` build, bumps the build number in `version.props`, and — if Inno Setup 6.1+ is installed — compiles `installer\Output\RDM-Setup.exe` automatically. If Inno Setup isn't found, the script publishes the app anyway and prints the manual `ISCC` command to compile the installer yourself.

## 7. Troubleshooting

- **Nothing happens / a crash on first launch after building from source:** almost always a missing `Bass.Net.dll` — the process references it at startup and will fail before showing any UI or writing a log line. Double-check `libs\Bass.Net.dll` exists at the repo root.
- **Audio doesn't work, but the app otherwise runs fine:** check that the required native BASS DLLs (`bass.dll`, `bassmix.dll`, `basswasapi.dll`, `bassloud.dll`) actually made it into the `BassLib\` folder next to the built executable — if they weren't in `libs\` at build time, they simply won't be copied, silently.
- **Can't connect to the database:** double-check the `database` section of `rdm.config.json` — see [RDM Local Configuration File §5.9](RDM_LocalConfig.md#59-database).
- **General troubleshooting:** check the log file first — `%ProgramData%\RDM\rdm.log` for an installed build, or `src\RDM.UI\bin\Debug\net10.0-windows\rdm.log` for a dev build run from the IDE/CLI.

---

**Last updated**: 2026-08-18
