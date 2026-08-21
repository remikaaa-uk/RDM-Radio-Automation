using Microsoft.Extensions.Logging;
using RDM.Audio;
using RDM.Audio.Engine;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Shared.Enums;

// Native BASS libraries live in BassLib\, not next to the executable — without this the very
// first P/Invoke fails. RDM.UI does the same thing in its Main; this tool was missing it.
BassLibInitializer.RegisterResolver(Path.Combine(AppContext.BaseDirectory, "BassLib"));

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("RDM.Audio.TestApp — BASS.NET manual test");
Console.WriteLine(new string('-', 48));

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: RDM.Audio.TestApp <path-to-audio-file>");
    Console.Error.WriteLine("Example: RDM.Audio.TestApp C:\\music\\track.mp3");
    return 1;
}

string filePath = args[0];

var eventBus   = new InMemoryEventBus();
var deviceRepo = new NullAudioDeviceRepository();

eventBus.Subscribe<TrackStartedEvent>   (e => Console.WriteLine($"\n[EVENT] TrackStarted   — {e.AssetId}"));
eventBus.Subscribe<TrackEndedEvent>     (e => Console.WriteLine($"\n[EVENT] TrackEnded     — {e.AssetId} ({e.EndReason})"));
eventBus.Subscribe<TrackOutroReachedEvent>(e => Console.WriteLine($"\n[EVENT] OutroReached   — {e.RemainingMs} ms remaining"));
eventBus.Subscribe<BufferUnderrunEvent> (e => Console.WriteLine($"\n[EVENT] BufferUnderrun — {e.SourceId}"));
eventBus.Subscribe<RecordingStatusChangedEvent>(e =>
{
    var s = e.Status;
    Console.WriteLine($"\n[REC] {s.State}" + (s.FilePath is null ? "" : $" — {s.FilePath}"));
    if (s.Error is not null) Console.WriteLine($"      {s.Error}");
});
eventBus.Subscribe<EncoderStatusChangedEvent>(e =>
{
    var s = e.Status;
    var detail = s.State switch
    {
        EncoderSessionState.RetryWaiting => $" — próba {s.RetryAttempt}, następna o {s.NextRetryAt:HH:mm:ss}",
        EncoderSessionState.Streaming    => $" — od {s.ConnectedAt:HH:mm:ss}",
        _                                => string.Empty
    };
    Console.WriteLine($"\n[ENCODER] {s.ProfileName}: {s.State}{detail}");
    if (s.Error is not null) Console.WriteLine($"          {s.Error}");
});

await using var engine = new BassAudioEngine(eventBus, deviceRepo, new ConsoleLogger<BassAudioEngine>());

try
{
    Console.WriteLine("Initializing BASS...");
    await engine.InitializeAsync(BuildTestSettings());
    Console.WriteLine("BASS initialized OK.");

    Console.WriteLine($"Loading: {filePath}");
    await engine.LoadTrackAsync("test-asset-id", filePath, []);
    Console.WriteLine("Track loaded OK.");

    await engine.PlayAsync();
    Console.WriteLine("Playing.");
    PrintHelp();

    float volume = 1.0f;
    bool  ducked = false;
    EncoderProfile? encoderProfile = null;

    while (true)
    {
        var key = Console.ReadKey(intercept: true);

        if (key.Key is ConsoleKey.Q or ConsoleKey.Escape)
            break;

        if (key.Key is ConsoleKey.S or ConsoleKey.Enter)
        {
            Console.WriteLine("\nStopping...");
            await engine.StopAsync();
            Console.WriteLine("Stopped.");
            break;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            volume = Math.Min(volume + 0.1f, 1.0f);
            await engine.SetVolumeAsync("player", volume);
            Console.WriteLine($"\nMaster volume: {volume:P0}");
        }

        if (key.Key == ConsoleKey.DownArrow)
        {
            volume = Math.Max(volume - 0.1f, 0.0f);
            await engine.SetVolumeAsync("player", volume);
            Console.WriteLine($"\nMaster volume: {volume:P0}");
        }

        if (key.Key == ConsoleKey.D)
        {
            // Ducking was removed; AUX decks now load/play independent files
            // (see IAudioEngine.LoadAuxAsync / PlayAuxAsync) instead of ducking the playlist.
            ducked = !ducked;
            Console.WriteLine($"\nDucking demo disabled (toggle = {ducked}).");
        }

        if (key.Key == ConsoleKey.E)
        {
            if (encoderProfile is not null)
            {
                Console.WriteLine("\nEnkoder już uruchomiony — najpierw X, żeby zatrzymać.");
                continue;
            }

            var (profile, password) = PromptForEncoderProfile();
            if (profile is null) continue;

            encoderProfile = profile;
            await engine.StartEncoderAsync(profile, password);
        }

        if (key.Key == ConsoleKey.X && encoderProfile is not null)
        {
            Console.WriteLine("\nZatrzymywanie enkodera...");
            await engine.StopEncoderAsync(encoderProfile.ProfileId);
            encoderProfile = null;
        }

        if (key.Key == ConsoleKey.R)
        {
            Console.Write("\n  Folder nagrań [%TEMP%\\rdm-rec]: ");
            var dir = Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Path.GetTempPath(), "rdm-rec");

            var recFormat = AskEncoderFormat();
            var status = await engine.StartRecordingAsync(
                new RecordingRequest(dir, recFormat, NamePrefix: "testapp"));
            Console.WriteLine(status.State == RecordingState.Recording
                ? $"  Nagrywam → {status.FilePath}"
                : $"  Nie wystartowało: {status.Error}");
        }

        if (key.Key == ConsoleKey.T)
        {
            var status = await engine.StopRecordingAsync();
            Console.WriteLine($"\n  Nagrywanie: {status.State}" +
                              (status.FilePath is null ? "" : $" → {status.FilePath}"));
        }

        if (key.Key == ConsoleKey.I)
        {
            var all = engine.GetAllEncoderStatuses();
            if (all.Count == 0) Console.WriteLine("\nBrak sesji enkodera.");
            foreach (var s in all)
                Console.WriteLine($"\n[STATUS] {s.ProfileName}: {s.State}" +
                                  (s.Error is null ? "" : $" — {s.Error}"));

            var rec = engine.GetRecordingStatus();
            Console.WriteLine($"[STATUS] Nagrywanie: {rec.State}" +
                              (rec.State == RecordingState.Recording
                                  ? $" — {rec.BytesWritten / 1024} kB, od {rec.StartedAt:HH:mm:ss}"
                                  : rec.Error is null ? "" : $" — {rec.Error}"));
        }
    }
}
catch (FileNotFoundException ex)
{
    Console.Error.WriteLine($"\nERROR — File not found: {ex.FileName}");
    return 2;
}
catch (AudioEngineException ex)
{
    Console.Error.WriteLine($"\nERROR — Audio engine: {ex.Message}");
    return 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nERROR — Unexpected: {ex.Message}");
    return 99;
}

Console.WriteLine("Done.");
return 0;

static void PrintHelp()
{
    Console.WriteLine("  ↑/↓  volume  |  D  toggle AUX duck  |  S/Enter  stop  |  Q/Esc  quit");
    Console.WriteLine("  E  start streaming  |  X  stop streaming  |  I  status");
    Console.WriteLine("  R  start recording  |  T  stop recording");
}

/// <summary>
/// Builds a throwaway profile from console input. The password is never stored or echoed —
/// it lives only in this process, exactly as it will at runtime once the UI passes a decrypted
/// value straight into StartEncoderAsync.
/// </summary>
static (EncoderProfile? Profile, string? Password) PromptForEncoderProfile()
{
    Console.WriteLine("\n── Konfiguracja streamu ─────────────────────────────");

    var host = Ask("Host (np. stream.example.org)");
    if (string.IsNullOrWhiteSpace(host)) { Console.WriteLine("Anulowano."); return (null, null); }

    if (!int.TryParse(Ask("Port (np. 8000)"), out int port) || port is < 1 or > 65535)
    {
        Console.WriteLine("Nieprawidłowy port. Anulowano.");
        return (null, null);
    }

    var mount   = Ask("Mount (np. /live)");
    var format  = AskEncoderFormat();
    var bitrate = int.TryParse(Ask("Bitrate kbps [128]"), out int b) ? b : 128;
    var password = AskSecret("Hasło źródła");

    var profile = new EncoderProfile
    {
        ProfileId   = Guid.NewGuid().ToString(),
        StudioId    = Guid.NewGuid().ToString(),
        Name        = $"TestApp {format}",
        Format      = format,
        BitrateKbps = bitrate,
        Channels    = 2,
        ServerType  = CastServerType.Icecast,
        Host        = host,
        Port        = port,
        Mount       = string.IsNullOrWhiteSpace(mount) ? "/stream" : mount,
        StreamName  = "RDM TestApp",
        Enabled     = true,
        CreatedAt   = DateTime.Now
    };

    Console.WriteLine($"Łączenie z {host}:{port}{profile.Mount} @ {bitrate} kbps...");
    return (profile, password);

    static string Ask(string label)
    {
        Console.Write($"  {label}: ");
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }


    static string AskSecret(string label)
    {
        Console.Write($"  {label}: ");
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0) { buffer.Length--; Console.Write("\b \b"); }
                continue;
            }
            if (char.IsControl(k.KeyChar)) continue;
            buffer.Append(k.KeyChar);
            Console.Write('*');
        }
        Console.WriteLine();
        return buffer.ToString();
    }
}

/// <summary>
/// Format picker shared by the streaming and recording prompts. Defaults to MP3 — the one format
/// already confirmed on air, so pressing Enter reproduces the known-good path.
/// </summary>
static EncoderFormat AskEncoderFormat()
{
    Console.Write("  Format: [1] MP3  [2] OGG  [3] OPUS  (Enter = MP3): ");
    return Console.ReadLine()?.Trim() switch
    {
        "2" => EncoderFormat.Ogg,
        "3" => EncoderFormat.Opus,
        _   => EncoderFormat.Mp3
    };
}

static AudioSettings BuildTestSettings() => new()
{
    SettingsId              = Guid.NewGuid().ToString(),
    StudioId                = Guid.NewGuid().ToString(),
    SampleRate              = AudioSampleRate.Hz48000,
    BufferSize              = AudioBufferSize.Samples512,
    OutputMode              = DriverType.WasapiShared,
    DefaultMode             = PlaylistMode.LiveAssist,
    Theme                   = AppTheme.Dark,
    LoudnessTargetLufs      = -23.0m,
    DuckingLevelDb          = -12.0m,
    DuckingAttackMs         = 400,
    DuckingReleaseMs        = 800,
    SilenceStartThresholdDb = -25.0m,
    SilenceMixThresholdDb   = -15.0m,
    SilenceEndThresholdDb   = -28.0m,
    UpdatedAt               = DateTime.UtcNow
};

// ── Local helpers — TestApp only ─────────────────────────────────────────────

/// <summary>
/// Minimal console logger. The engine's own warnings are the most useful diagnostic when a cast
/// connection misbehaves, and NullLogger would throw them away — which is the opposite of what a
/// manual test harness is for.
/// </summary>
sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        var tag = logLevel switch
        {
            LogLevel.Error or LogLevel.Critical => "ERROR",
            LogLevel.Warning                    => "WARN ",
            _                                   => "INFO "
        };
        Console.WriteLine($"[{tag}] {formatter(state, exception)}");
        if (exception is not null) Console.WriteLine($"        {exception.Message}");
    }
}

sealed class NullAudioDeviceRepository : RDM.Core.Interfaces.IAudioDeviceRepository
{
    public Task<AudioDevice?> GetByIdAsync(string deviceId, CancellationToken ct = default)
        => Task.FromResult<AudioDevice?>(null);

    public Task<IReadOnlyList<AudioDevice>> GetByStudioAsync(string studioId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AudioDevice>>([]);

    public Task<IReadOnlyList<AudioDevice>> GetAvailableAsync(string studioId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AudioDevice>>([]);

    public Task UpsertAsync(AudioDevice device, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdateAvailabilityAsync(string deviceId, bool isAvailable, DateTime lastSeenAt, CancellationToken ct = default)
        => Task.CompletedTask;
}
