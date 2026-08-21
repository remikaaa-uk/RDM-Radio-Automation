using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.API.Middleware;
using RDM.API.Services;
using RDM.API.WebSocket;
using RDM.Audio.Engine;
using RDM.Audio.Processing;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;
using RDM.Core.Services;
using RDM.Infrastructure;
using RDM.Infrastructure.Database;
using RDM.Infrastructure.FileSystem;
using RDM.Core.Hardware;
using RDM.Infrastructure.Hardware;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Scripting;
using RDM.Infrastructure.Security;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using RDM.UI.Views;
using System;
using RDM.Shared.Enums;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI;

public partial class App : Application
{
    private IHost? _host;

    /// <summary>
    /// Exposes the DI container after initialization. Used by windows to resolve ViewModels.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Keep app alive while startup sequence runs (no MainWindow yet).
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Fire-and-forget: Avalonia is single-threaded; the awaits inside release
            // the UI thread so Avalonia can dispatch events (button clicks, etc.).
            _ = RunStartupAsync(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // ── Startup sequence ──────────────────────────────────────────────────────

    // Next to the executable when writable (dev), %ProgramData%\RDM otherwise — see ConfigPaths.
    private static readonly string _logPath = ConfigPaths.LogPath;

    private static void Log(string step)
    {
        try { File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff}  {step}\n"); }
        catch { /* ignore log errors */ }
    }

    /// <summary>Reads general.language from the writable config; null/"system" ⇒ follow the OS.</summary>
    private static string? ReadConfiguredLanguage()
    {
        try
        {
            var path = ConfigPaths.FilePath;
            if (!File.Exists(path)) return null;
            var root = JsonNode.Parse(File.ReadAllText(path));
            return root?["general"]?["language"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private async Task RunStartupAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            // ── Step 1: Build WebApplication (Kestrel + all services) ──────────
            // Pipeline configured inside BuildAndConfigureWebApp().
            // NOT started yet — DB bootstrap must succeed first (Bug 1 fix).
            Log("Step 1: BuildAndConfigureWebApp");
            var webApp = BuildAndConfigureWebApp();
            _host    = webApp;
            Services = webApp.Services;

            _ = Services.GetRequiredService<RdmWebSocketHub>();
            Log("Step 1 done — WebSocketHub ready");

            await Services.GetRequiredService<UiStateService>().LoadThemeAsync();
            Log("Step 1 done — theme loaded");

            // Localization: must be initialized before any localized window is shown.
            var localizer = Services.GetRequiredService<Localizer>();
            localizer.Load(ReadConfiguredLanguage());
            Log($"Step 1 done — localization loaded ({localizer.CurrentLanguage})");

            // ── Step 2: Bootstrap DB ──────────────────────────────────────────
            Log("Step 2: DB bootstrap");
            var bootstrapper = Services.GetRequiredService<DatabaseBootstrapper>();
            BootstrapResult result = await RunBootstrapWithUiAsync(desktop, bootstrapper);
            Log($"Step 2 done — IsFirstRun={result.IsFirstRun}");

            // ── Step 3: Fresh-install ─────────────────────────────────────────
            if (result.IsFirstRun && result.AdminPassword is not null)
            {
                Log("Step 3: fresh install dialog");
                await ShowFreshInstallDialogAsync(result.AdminPassword);
            }

            // ── Step 4: StudioContext ─────────────────────────────────────────
            Log("Step 4: StudioContext");
            var studioContext = Services.GetRequiredService<StudioContext>();
            await InitializeStudioContextAsync(studioContext);
            Log($"Step 4 done — StudioId={studioContext.StudioId}");

            // ── Step 5: Start host ────────────────────────────────────────────
            Log("Step 5: host.StartAsync");
            await _host.StartAsync();
            Log("Step 5 done");

            // ── Step 6: Audio engine ──────────────────────────────────────────
            Log("Step 6: AudioEngine");
            var settings = Services.GetRequiredService<AudioSettings>();
            Log("Step 6: AudioSettings resolved");
            var audioEngine = Services.GetRequiredService<IAudioEngine>();
            bool audioReady = false;
            try
            {
                await audioEngine.InitializeAsync(settings);
                audioReady = true;
                Log("Step 6 done — audio OK");
            }
            catch (Exception ex)
            {
                Log($"Step 6 warn — no-audio mode: {ex.Message}");
                var logger = Services.GetRequiredService<ILogger<App>>();
                logger.LogWarning(ex, "AudioEngine failed to initialize — running in no-audio mode");
            }

            // ── Step 6.1: Auto-start streaming profiles ───────────────────────
            // Before the login window on purpose: an unattended studio that reboots must come
            // back on air without waiting for someone to type a password. The service never
            // throws, so a misconfigured profile cannot block startup here.
            if (audioReady)
            {
                var started = await Services.GetRequiredService<EncoderAutoStartService>()
                                            .StartAllAsync();
                Log($"Step 6.1 done — auto-started {started} encoder profile(s)");
            }

            // ── Step 6.2: Restore the mic DSP chain ───────────────────────────
            // Slots only: the plugins themselves load when the mic is switched on, so this is
            // safe even in no-audio mode and cannot delay startup on a missing VST file.
            await Services.GetRequiredService<MicDspChainStore>().RestoreAsync();
            Log("Step 6.2 done — mic DSP chain restored");

            // ── Step 7: Login ─────────────────────────────────────────────────
            bool loggedIn = false;
            if (!settings.ApiAuthEnabled || settings.ApiAnonymousLocal)
            {
                Log("Step 7: Login bypassed based on API settings");
                loggedIn = true;

                var role = settings.ApiAuthEnabled ? UserRole.Operator : UserRole.Admin;
                var sessionContext = Services.GetRequiredService<UserSessionContext>();
                sessionContext.Login(new User
                {
                    UserId = "local-bypass",
                    StudioId = studioContext.StudioId,
                    Username = "BypassLocal",
                    Role = role,
                    Enabled = true
                });
            }
            else
            {
                // Try auto-login from saved credentials before showing the window.
                var configSvc = Services.GetRequiredService<SettingsConfigService>();
                var cfg       = await configSvc.LoadAsync();
                var autoLogin = cfg["auto_login"];
                var autoUser  = autoLogin?["username"]?.GetValue<string>();
                var autoPass  = autoLogin?["password"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(autoUser) && autoPass is not null)
                {
                    Log("Step 7: attempting auto-login");
                    var authSvc = Services.GetRequiredService<IAuthService>();
                    var autoUser2 = await authSvc.AuthenticateAsync(
                        studioContext.StudioId, autoUser, autoPass);
                    if (autoUser2 is not null)
                    {
                        Log($"Step 7 done — auto-login OK as '{autoUser}'");
                        loggedIn = true;
                        Services.GetRequiredService<UserSessionContext>().Login(autoUser2);
                        Services.GetRequiredService<ApiClientService>().SetCredentials(autoUser, autoPass);
                    }
                    else
                    {
                        Log("Step 7: auto-login failed — showing LoginWindow");
                    }
                }

                if (!loggedIn)
                {
                    Log("Step 7: LoginWindow");
                    var loginWindow = new LoginWindow();
                    loginWindow.Show();
                    Log("Step 7: waiting for login");
                    loggedIn = await loginWindow.WaitForResultAsync();
                    Log($"Step 7 done — loggedIn={loggedIn}");

                    if (loggedIn && loginWindow.SuccessCredentials is var (u, p))
                        Services.GetRequiredService<ApiClientService>().SetCredentials(u, p);
                }
            }

            if (!loggedIn)
            {
                await ShutdownGracefullyAsync(desktop);
                return;
            }

            // ── Step 7.5: Initialize PlaylistEngine with saved default mode ──────
            var playlistEngine = Services.GetRequiredService<PlaylistEngine>();
            await playlistEngine.InitializeAsync();
            Log("Step 7.5 done — PlaylistEngine initialized");

            // ── Step 7.5: Start PlayoutLogService ─────────────────────────────
            // Must be resolved (not just registered) so it subscribes to IEventBus
            // and starts writing played tracks to playout_log (History tab).
            _ = Services.GetRequiredService<PlayoutLogService>();
            _ = Services.GetRequiredService<StreamTitlesService>();
            _ = Services.GetRequiredService<EncoderTitleService>();
            _ = Services.GetRequiredService<SweeperEngine>();

            // ── Step 7.6: Hardware Action System ──────────────────────────────
            // Initialize both mapping caches, then start Action + Feedback routers.
            // Action delegates are registered by MainViewModel (Step 8).
            var triggerCache   = Services.GetRequiredService<ITriggerMappingCache>();
            var feedbackCache  = Services.GetRequiredService<IFeedbackMappingCache>();
            await Task.WhenAll(triggerCache.InitializeAsync(), feedbackCache.InitializeAsync());
            _ = Services.GetRequiredService<ActionRouter>();
            _ = Services.GetRequiredService<FeedbackRouter>();
            _ = Services.GetRequiredService<MidiOutputDriver>();
            _ = Services.GetRequiredService<MacroEngine>();
            _ = Services.GetRequiredService<HttpDriver>();
            _ = Services.GetRequiredService<ScriptRunner>();
            Log("Step 7.6 done — HardwareActionSystem ready (Etap 6)");

            // ── Step 8: Open MainWindow ────────────────────────────────────────
            // MainWindow constructor calls PlayerViewModel.Activate() which
            // triggers ApiClientService.StartWebSocketAsync().
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            try { await ShowFatalErrorAsync(ex); } catch (Exception uiEx) { Log($"ShowFatalError also failed: {uiEx.Message}"); }
            await ShutdownGracefullyAsync(desktop);
        }
    }

    // ── Bootstrap with retry loop (DatabaseSetupWindow on connection error) ──

    private async Task<BootstrapResult> RunBootstrapWithUiAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        DatabaseBootstrapper bootstrapper)
    {
        while (true)
        {
            try
            {
                return await bootstrapper.RunAsync();
            }
            catch (MySqlConnector.MySqlException ex)
            {
                var setupWindow = new DatabaseSetupWindow(ex.Message);
                setupWindow.Show();
                bool retry = await setupWindow.WaitForResultAsync();

                if (!retry)
                {
                    // Operator closed without fixing — terminate.
                    await ShutdownGracefullyAsync(desktop);
                    // Unreachable after shutdown, but compiler needs a return.
                    throw new OperationCanceledException("Operator cancelled DB setup.");
                }
                // Loop: try bootstrap again with the new config saved by DatabaseSetupWindow.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ShowFatalErrorAsync(ex);
                await ShutdownGracefullyAsync(desktop);
                throw;
            }
        }
    }

    // ── StudioContext initialization ──────────────────────────────────────────

    private async Task InitializeStudioContextAsync(StudioContext studioContext)
    {
        var dbFactory = Services.GetRequiredService<IDbConnectionFactory>();
        using var conn = dbFactory.CreateConnection();
        var studioId = await conn.QueryFirstOrDefaultAsync<string>(
            "SELECT studio_id FROM studios LIMIT 1");

        if (string.IsNullOrEmpty(studioId))
            throw new InvalidOperationException(
                "No studio found in the database after bootstrapping.");

        studioContext.Initialize(studioId);
    }

    // ── Fresh-install dialog ──────────────────────────────────────────────────

    private static Task ShowFreshInstallDialogAsync(string adminPassword)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title          = Localizer.Instance?["app.first_run.title"] ?? "RDM — First run",
            Width          = 420,
            Height         = 200,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CanResize      = false,
            Content        = new Avalonia.Controls.StackPanel
            {
                Margin   = new Avalonia.Thickness(20),
                Spacing  = 12,
                Children =
                {
                    new Avalonia.Controls.TextBlock
                    {
                        Text        = Localizer.Instance?["app.first_run.db_installed"] ?? "Database installed.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new Avalonia.Controls.TextBlock
                    {
                        Text        = Localizer.Instance?["app.first_run.admin_password"] ?? "Administrator password (shown only once):",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new Avalonia.Controls.TextBox
                    {
                        Text       = adminPassword,
                        IsReadOnly = true,
                        FontSize   = 16
                    },
                    new Avalonia.Controls.Button
                    {
                        Content             = Localizer.Instance?["common.close"] ?? "Close",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
                    }
                }
            }
        };

        // Wire up the Close button (last child).
        var panel  = (Avalonia.Controls.StackPanel)dialog.Content!;
        var button = (Avalonia.Controls.Button)panel.Children[^1];
        button.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult(true);
        dialog.Show();

        return tcs.Task;
    }

    // ── Fatal error dialog ────────────────────────────────────────────────────

    private static Task ShowFatalErrorAsync(Exception ex)
    {
        var tcs = new TaskCompletionSource<bool>();
        var dialog = new Window
        {
            Title         = Localizer.Instance?["app.fatal.title"] ?? "RDM — Critical error",
            Width         = 480,
            Height        = 240,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content       = new Avalonia.Controls.StackPanel
            {
                Margin  = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new Avalonia.Controls.TextBlock
                    {
                        Text        = Localizer.Instance?["app.fatal.message"]
                                      ?? "The application encountered a critical error and will close.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new Avalonia.Controls.TextBox
                    {
                        Text       = ex.ToString(),
                        IsReadOnly = true,
                        MaxHeight  = 120
                    },
                    new Avalonia.Controls.Button
                    {
                        Content             = Localizer.Instance?["common.close"] ?? "Close",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
                    }
                }
            }
        };

        var panel  = (Avalonia.Controls.StackPanel)dialog.Content!;
        var button = (Avalonia.Controls.Button)panel.Children[^1];
        button.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult(true);
        dialog.Show();

        return tcs.Task;
    }

    // ── Graceful shutdown ─────────────────────────────────────────────────────

    private async Task ShutdownGracefullyAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_host is not null)
        {
            try { await _host.StopAsync(CancellationToken.None); }
            catch { /* best-effort */ }
        }
        desktop.Shutdown(0);
    }

    // ── WebApplication builder ────────────────────────────────────────────────

    private static WebApplication BuildAndConfigureWebApp()
    {
        // Resolve (and on first run seed) the writable config in %ProgramData%\RDM.
        // Must happen before any config read so the DB connection is found there.
        var configPath = ConfigPaths.EnsureInitialized();

        // WebApplication.CreateBuilder picks up ASPNETCORE_URLS from the environment
        // to configure Kestrel. We pre-read rdm.config.json to set it before the
        // builder runs, so the port from our config is respected automatically.
        var preConfig = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true)
            .Build();
        var apiUrl = preConfig["api:base_url"] ?? "http://localhost:9300";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", apiUrl);

#pragma warning disable CS0436  // Program.Args: local type wins over RDM.API's Program (correct)
        var builder = WebApplication.CreateBuilder(Program.Args);
#pragma warning restore CS0436

        builder.Configuration.AddJsonFile(
            configPath, optional: false, reloadOnChange: true);

        // File logger — captures all ILogger<T> output from every layer (Core, API, UI).
        // Debugging phase: let every level/category through to the provider; the
        // FileLogger.IsEnabled gate decides the floor (RDM.* = Trace, others = Debug).
        builder.Logging.AddProvider(new FileLoggerProvider(_logPath));
        builder.Logging.AddFilter<FileLoggerProvider>(null, LogLevel.Trace);

        // Register all application services.
        RegisterServices(builder.Services, builder.Configuration);

        // Register ASP.NET Core MVC with controllers from RDM.API assembly.
        // Disable implicit [Required] for non-nullable strings — controllers do explicit validation.
        builder.Services.AddControllers(options =>
            options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
            .AddApplicationPart(
                typeof(RDM.API.Controllers.NowPlayingController).Assembly);

        var app = builder.Build();

        // Populate HostAccessor so MainViewModel can call StopAsync during shutdown.
        app.Services.GetRequiredService<HostAccessor>().Host = app;

        // ── Middleware pipeline (mirrors RDM.API/Program.cs) ──────────────────
        app.UseMiddleware<ErrorHandlingMiddleware>();
        app.UseMiddleware<AuthMiddleware>();
        app.UseWebSockets();
        app.UseRouting();
        app.MapControllers();

        // WebSocket endpoint
        app.Map("/api/v1/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            var ws  = await context.WebSockets.AcceptWebSocketAsync();
            var hub = context.RequestServices.GetRequiredService<RdmWebSocketHub>();
            await hub.HandleConnectionAsync(ws, context.RequestAborted);
        });

        return app;
    }

    private static void RegisterServices(IServiceCollection services, IConfiguration config)
    {

        // ── Database ─────────────────────────────────────────────────────────
        services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<DatabaseBootstrapper>();

        // ── Context ──────────────────────────────────────────────────────────
        services.AddSingleton<StudioContext>();
        services.AddSingleton<UserSessionContext>();

        // ── Repositories (singleton: Dapper repos are stateless — each method
        //    opens and closes its own connection, no per-request isolation needed) ─
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IAssetRepository, AssetRepository>();
        services.AddSingleton<IWaveformRepository, WaveformRepository>();
        services.AddSingleton<IScheduledEventRepository, ScheduledEventRepository>();
        services.AddSingleton<IPlayoutLogRepository, PlayoutLogRepository>();
        services.AddSingleton<IPlaylistRepository, PlaylistRepository>();
        services.AddSingleton<ICartwallRepository, CartwallRepository>();
        services.AddSingleton<IPlaybackSessionRepository, PlaybackSessionRepository>();
        services.AddSingleton<IAudioDeviceRepository, AudioDeviceRepository>();
        services.AddSingleton<IAudioSettingsRepository, AudioSettingsRepository>();
        services.AddSingleton<IEncoderProfileRepository, EncoderProfileRepository>();

        // ── Core services ─────────────────────────────────────────────────────
        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<PlayoutLogService>();
        // Singleton here because every dependency it has is one in this root; in RDM.API the
        // encoder repository is scoped, so it is registered scoped there instead.
        services.AddSingleton<EncoderAutoStartService>();
        services.AddSingleton<StreamTitlesSettings>(sp =>
        {
            var cfg   = sp.GetRequiredService<IConfiguration>();
            var st    = cfg.GetSection("stream_titles");
            var allowedIds = st.GetSection("allowed_format_ids")
                              .Get<string[]>() ?? [];
            return new StreamTitlesSettings
            {
                Enabled          = st.GetValue<bool>("enabled"),
                OutputFilePath   = st.GetValue<string>("output_file_path") ?? string.Empty,
                Format           = st.GetValue<string>("format")           ?? "$artist$ - $title$",
                Encoding         = st.GetValue<string>("encoding")         ?? "UTF-8",
                FallbackArtist   = st.GetValue<string>("fallback_artist")  ?? "Radio",
                FallbackTitle    = st.GetValue<string>("fallback_title")   ?? string.Empty,
                AllowedFormatIds = new HashSet<string>(allowedIds, StringComparer.OrdinalIgnoreCase),
            };
        });
        services.AddSingleton<StreamTitlesService>();
        services.AddSingleton<EncoderTitleService>();
        services.AddSingleton<Localizer>();
        services.AddSingleton<ILocalizer>(sp => sp.GetRequiredService<Localizer>());
        services.AddSingleton<ILoginThrottle, LoginThrottle>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IAudioEngine, BassAudioEngine>();
        services.AddSingleton<IAudioDeviceEnumerator, BassDeviceEnumerator>();
        // Register PlaylistEngine as both its concrete type (for PlaybackSessionSnapshotService
        // which needs GetSnapshot()) and as the interface (for all other consumers).
        // Both resolve to the same singleton instance via the factory overload.
        services.AddSingleton<PlaylistEngine>();
        services.AddSingleton<IPlaylistController>(sp => sp.GetRequiredService<PlaylistEngine>());
        services.AddSingleton<IExternalActionRunner, ExternalActionRunner>();
        services.AddSingleton<ScheduledActionExecutor>();
        services.AddSingleton<EventScheduler>();
        services.AddSingleton<SweeperEngine>();
        services.AddSingleton<IBackupService, BackupService>();

        // AudioSettings resolved lazily after StudioContext is initialized (step 4).
        // Task.Run() is required: the factory is called from the Avalonia UI thread,
        // and .GetAwaiter().GetResult() on the UI thread deadlocks because
        // MySqlConnector tries to marshal continuations back to the same thread.
        // Task.Run() executes the async work on the thread-pool, breaking the deadlock.
        services.AddSingleton<AudioSettings>(sp =>
        {
            var repo    = sp.GetRequiredService<IAudioSettingsRepository>();
            var context = sp.GetRequiredService<StudioContext>();
            return Task.Run(() => repo.GetByStudioAsync(context.StudioId))
                       .GetAwaiter().GetResult()
                ?? throw new InvalidOperationException(
                       "AudioSettings not found in database.");
        });

        // ── Import pipeline ───────────────────────────────────────────────────
        services.AddSingleton<WaveformQueue>();
        services.AddSingleton<LoudnessQueue>();
        services.AddSingleton<BpmQueue>();
        services.AddSingleton<CueAnalysisQueue>();
        services.AddSingleton(new ImportPipelineSettings(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RDM", "image_cache")
        ));

        services.AddSingleton<IRdmFileWriter, RdmFileWriter>();
        services.AddSingleton<IChecksumService, ChecksumService>();
        services.AddSingleton<IMetadataReader, RdmFileReader>();
        services.AddSingleton<IMetadataReader, MmdFileReader>();
        services.AddSingleton<IMetadataReader, WfrmFileReader>();
        services.AddSingleton<IMetadataReader, Id3TagReader>();
        services.AddSingleton<IMetadataReader, AutoCueDetector>();

        services.AddSingleton<IWaveformGenerator, WaveformGenerator>();
        services.AddSingleton<ILoudnessAnalyzer, LoudnessAnalyzer>();
        services.AddSingleton<IBpmAnalyzer, BpmAnalyzer>();
        services.AddSingleton<IImportPipeline, ImportPipeline>();

        // ── Background services ───────────────────────────────────────────────
        // WaveformQueueService, LoudnessQueueService and CueAnalysisQueueService: background-only.
        services.AddHostedService<WaveformQueueService>();
        services.AddHostedService<LoudnessQueueService>();
        services.AddHostedService<BpmQueueService>();
        services.AddHostedService<CueAnalysisQueueService>();

        // ImportJobService (RDM.API): singleton for direct injection + hosted for background drain.
        services.AddSingleton<ImportJobService>();
        services.AddHostedService<ImportJobService>(sp =>
            sp.GetRequiredService<ImportJobService>());

        // ScanJobService (RDM.API): singleton for direct injection + hosted for background drain.
        services.AddSingleton<ScanJobService>();
        services.AddHostedService<ScanJobService>(sp =>
            sp.GetRequiredService<ScanJobService>());

        // RdmWebSocketHub: singleton, subscribes to IEventBus in ctor.
        services.AddSingleton<RdmWebSocketHub>();

        // HostAccessor: populated after Build() so MainViewModel can call
        // IHost.StopAsync during shutdown. IHost is not auto-registered in DI.
        services.AddSingleton<HostAccessor>();

        // ── ApiClientService ──────────────────────────────────────────────────
        services.AddHttpClient("rdm-api", (sp, client) =>
        {
            var cfg = sp.GetRequiredService<IConfiguration>();
            client.BaseAddress = new Uri(cfg["api:base_url"] ?? "http://localhost:9300");
            client.Timeout     = TimeSpan.FromSeconds(10);
        });
        services.AddSingleton<ApiClientService>();

        // ── Hardware Action System ────────────────────────────────────────────
        services.AddSingleton<IActionRegistry, ActionRegistry>();
        services.AddSingleton<IHardwareLearnService, HardwareLearnService>();
        services.AddSingleton<ITriggerMappingCache, DbTriggerMappingCache>();
        services.AddSingleton<IFeedbackMappingCache, DbFeedbackMappingCache>();
        services.AddSingleton<IKeyboardInputDriver, KeyboardInputDriver>();
        services.AddSingleton<ActionRouter>();
        services.AddSingleton<FeedbackRouter>();
        services.AddSingleton<MidiOutputDriver>();
        services.AddSingleton<MidiInputDriver>();
        services.AddSingleton<IMidiLearnScanner>(sp => sp.GetRequiredService<MidiInputDriver>());
        services.AddHostedService(sp => sp.GetRequiredService<MidiInputDriver>());
        services.AddHostedService<DrMixerDriver>();
        services.AddHostedService<GenericSerialDriver>();
        services.AddSingleton<ITriggerMappingRepository, TriggerMappingRepository>();
        services.AddSingleton<IFeedbackMappingRepository, FeedbackMappingRepository>();
        services.AddSingleton<IMacroRepository, MacroRepository>();
        services.AddSingleton<MacroEngine>();
        services.AddSingleton<HttpDriver>();
        services.AddSingleton<IHardwareMetrics>(sp => sp.GetRequiredService<ActionRouter>());
        services.AddSingleton<IScriptRepository, ScriptRepository>();
        services.AddSingleton<IScriptingFacade, ScriptingFacade>();
        services.AddSingleton<IScriptEngine, JintScriptEngine>();
        services.AddSingleton<ScriptRunner>();

        // ── UI-002A: Infrastructure services ─────────────────────────────────
        services.AddSingleton<UiStateService>();
        services.AddSingleton<SettingsConfigService>();
        services.AddSingleton<MicDspChainStore>();
        services.AddSingleton<ILibrarySelectionService, LibrarySelectionService>();
        services.AddSingleton<IInsertCursorService, InsertCursorService>();

        // ── UI-002B: Navigation + format repository ───────────────────────────
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IAssetFormatRepository, AssetFormatRepository>();
        services.AddSingleton<ISubcategoryRepository, SubcategoryRepository>();
        services.AddSingleton<IGenreRepository, GenreRepository>();

        // ── UI-002C: Countdown + playlist ─────────────────────────────────────
        services.AddSingleton<CountdownService>();
        services.AddTransient<PlaylistViewModel>();

        // ── UI-002D: Right panel view models ──────────────────────────────────
        services.AddTransient<LibraryViewModel>();
        services.AddTransient<HistoryViewModel>();
        services.AddTransient<CartwallViewModel>();
        services.AddTransient<AuxPlayersViewModel>();

        // ── UI-002E: Modal editors ─────────────────────────────────────────────
        services.AddTransient<CueEditorViewModel>();
        services.AddTransient<TrackEditorViewModel>();
        services.AddTransient<TrackEditorWindow>();
        services.AddTransient<SegueEditorViewModel>();
        services.AddTransient<SegueEditorWindow>();
        services.AddTransient<TrackImportViewModel>();
        services.AddTransient<TrackImportWindow>();

        services.AddTransient<UpdateTracksViewModel>();
        services.AddTransient<UpdateTracksWindow>();

        // ── UI-002G: Tracks Manager ────────────────────────────────────────────
        services.AddTransient<TracksManagerViewModel>();
        services.AddTransient<TracksManagerWindow>();

        // ── UI-002H: Playlist Builder ──────────────────────────────────────────
        services.AddTransient<PlaylistBuilderViewModel>();
        services.AddTransient<PlaylistBuilderWindow>();

        // ── UI-002I: Scheduled Events ──────────────────────────────────────────
        services.AddTransient<ScheduledEventsViewModel>();
        services.AddTransient<ScheduledEventsWindow>();

        // ── Categories Manager ────────────────────────────────────────────────
        services.AddTransient<CategoriesManagerViewModel>();
        services.AddTransient<CategoriesManagerWindow>();
        services.AddSingleton<HardwareManagerViewModel>();
        services.AddTransient<HardwareManagerWindow>();

        // ── Settings ──────────────────────────────────────────────────────────
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();

        // ── Mic DSP Chain ─────────────────────────────────────────────────────
        services.AddTransient<MicDspChainViewModel>();
        services.AddTransient<MicDspChainWindow>();

        // ── Streaming ─────────────────────────────────────────────────────────
        // Lives on the Settings window's Streaming tab. Transient because it subscribes to the
        // event bus and unsubscribes when that window closes — a singleton would keep a closed
        // window's handlers alive. Recording has no view model: its settings are plain fields on
        // SettingsViewModel and the bottom bar drives it directly.
        services.AddTransient<StreamingViewModel>();

        // ── Users ────────────────────────────────────────────────────────────
        // Same lifetime reasoning as StreamingViewModel: lives on a Settings tab, reloaded fresh
        // each time the window opens rather than kept as app-lifetime state.
        services.AddTransient<UsersViewModel>();

        // ── Change password (self-service, any role) ────────────────────────────
        // Only the view model is resolved through DI — MainWindow constructs the dialog itself
        // with `new ChangePasswordDialog()`, same as every other simple dialog in this app.
        services.AddTransient<ChangePasswordViewModel>();

        // ── ViewModels ────────────────────────────────────────────────────────
        services.AddTransient<DatabaseSetupViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<PlayerViewModel>();
        services.AddSingleton<SweeperSubcategoryViewModel>();
        services.AddSingleton<MainViewModel>();   // singleton: hosts tab VM lifecycles

        // PlaybackSessionSnapshotService: singleton so ShutdownSequence can call
        // TriggerSnapshotAsync() directly without resolving from DI twice.
        services.AddSingleton<PlaybackSessionSnapshotService>();
        services.AddHostedService<PlaybackSessionSnapshotService>(sp =>
            sp.GetRequiredService<PlaybackSessionSnapshotService>());

        // EventSchedulerService and BackupSchedulerService: background-only.
        services.AddHostedService<EventSchedulerService>();
        services.AddHostedService<BackupSchedulerService>();

        // DeadAirMonitorService: polls master output for silence, drives AUTO-mode recovery.
        services.AddHostedService<DeadAirMonitorService>();
    }
}
