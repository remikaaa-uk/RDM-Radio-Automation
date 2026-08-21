using Dapper;
using Microsoft.AspNetCore.Builder;
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
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Security;
using System;
using System.Collections.Generic;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

// Load custom RDM configuration
builder.Configuration.AddJsonFile("rdm.config.json", optional: false, reloadOnChange: true);

// Add services to the DI container.
builder.Services.AddControllers();

// Register Database dependencies
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddSingleton<DatabaseBootstrapper>();
builder.Services.AddSingleton<StudioContext>();

// Register Repositories
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IAssetRepository, AssetRepository>();
builder.Services.AddScoped<IWaveformRepository, WaveformRepository>();
builder.Services.AddScoped<IScheduledEventRepository, ScheduledEventRepository>();
builder.Services.AddScoped<IPlayoutLogRepository, PlayoutLogRepository>();
builder.Services.AddScoped<IPlaylistRepository, PlaylistRepository>();
builder.Services.AddScoped<ICartwallRepository, CartwallRepository>();
builder.Services.AddScoped<IPlaybackSessionRepository, PlaybackSessionRepository>();
builder.Services.AddScoped<IAudioDeviceRepository, AudioDeviceRepository>();
builder.Services.AddScoped<IAudioSettingsRepository, AudioSettingsRepository>();
builder.Services.AddScoped<IAssetFormatRepository, AssetFormatRepository>();
builder.Services.AddScoped<ISubcategoryRepository, SubcategoryRepository>();
builder.Services.AddScoped<IEncoderProfileRepository, EncoderProfileRepository>();

// Stateless DPAPI wrapper — safe as a singleton in both composition roots.
builder.Services.AddSingleton<ISecretProtector, SecretProtector>();

// Register core infrastructure services
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddSingleton<ILoginThrottle, LoginThrottle>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IAudioEngine, BassAudioEngine>();
builder.Services.AddSingleton<IPlaylistController, PlaylistEngine>();
builder.Services.AddSingleton<IExternalActionRunner, ExternalActionRunner>();
builder.Services.AddScoped<ScheduledActionExecutor>();
builder.Services.AddScoped<EventScheduler>();
// Scoped, following IEncoderProfileRepository above — resolved once from the startup scope.
builder.Services.AddScoped<EncoderAutoStartService>();
builder.Services.AddSingleton<StreamTitlesSettings>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>().GetSection("stream_titles");
    return new StreamTitlesSettings
    {
        Enabled        = cfg.GetValue<bool>("enabled"),
        OutputFilePath = cfg.GetValue<string>("output_file_path") ?? string.Empty,
        Format         = cfg.GetValue<string>("format")           ?? "$artist$ - $title$",
        Encoding       = cfg.GetValue<string>("encoding")         ?? "UTF-8",
        FallbackArtist = cfg.GetValue<string>("fallback_artist")  ?? "Radio",
        FallbackTitle  = cfg.GetValue<string>("fallback_title")   ?? string.Empty,
        AllowedFormatIds = new HashSet<string>(
            cfg.GetSection("allowed_format_ids").Get<string[]>() ?? [],
            StringComparer.OrdinalIgnoreCase)
    };
});
builder.Services.AddSingleton<StreamTitlesService>();
builder.Services.AddSingleton<EncoderTitleService>();

builder.Services.AddSingleton<AudioSettings>(sp =>
{
    using var scope = sp.CreateScope();
    var settingsRepo = scope.ServiceProvider.GetRequiredService<IAudioSettingsRepository>();
    var studioContext = scope.ServiceProvider.GetRequiredService<StudioContext>();
    return settingsRepo.GetByStudioAsync(studioContext.StudioId).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException("AudioSettings not found in database.");
});

// Register Import pipeline services & queues
builder.Services.AddSingleton<WaveformQueue>();
builder.Services.AddSingleton<LoudnessQueue>();
builder.Services.AddSingleton<BpmQueue>();
builder.Services.AddSingleton<CueAnalysisQueue>();
builder.Services.AddSingleton(new ImportPipelineSettings(
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RDM", "image_cache")
));

builder.Services.AddSingleton<IRdmFileWriter, RdmFileWriter>();
builder.Services.AddSingleton<IChecksumService, ChecksumService>();

builder.Services.AddScoped<IMetadataReader, RdmFileReader>();
builder.Services.AddScoped<IMetadataReader, MmdFileReader>();
builder.Services.AddScoped<IMetadataReader, WfrmFileReader>();
builder.Services.AddScoped<IMetadataReader, Id3TagReader>();
builder.Services.AddScoped<IMetadataReader, AutoCueDetector>();

builder.Services.AddScoped<IWaveformGenerator, WaveformGenerator>();
builder.Services.AddScoped<ILoudnessAnalyzer, LoudnessAnalyzer>();
builder.Services.AddScoped<IBpmAnalyzer, BpmAnalyzer>();
builder.Services.AddScoped<IImportPipeline, ImportPipeline>();

// Register Background services
builder.Services.AddHostedService<WaveformQueueService>();
builder.Services.AddHostedService<LoudnessQueueService>();
builder.Services.AddHostedService<BpmQueueService>();
builder.Services.AddHostedService<CueAnalysisQueueService>();
builder.Services.AddHostedService<WaveformRescanService>();

builder.Services.AddSingleton<ImportJobService>();
builder.Services.AddHostedService<ImportJobService>(sp => sp.GetRequiredService<ImportJobService>());

builder.Services.AddSingleton<ScanJobService>();
builder.Services.AddHostedService<ScanJobService>(sp => sp.GetRequiredService<ScanJobService>());

builder.Services.AddSingleton<RdmWebSocketHub>();

var app = builder.Build();

// Run bootstrapper and initialize StudioContext & Engines
using (var scope = app.Services.CreateScope())
{
    var bootstrapper = scope.ServiceProvider.GetRequiredService<DatabaseBootstrapper>();
    await bootstrapper.RunAsync();

    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
    using var conn = dbFactory.CreateConnection();
    var studioId = await conn.QueryFirstOrDefaultAsync<string>("SELECT studio_id FROM studios LIMIT 1");
    if (string.IsNullOrEmpty(studioId))
    {
        throw new InvalidOperationException("No studio found in the database after bootstrapping.");
    }
    
    var studioContext = scope.ServiceProvider.GetRequiredService<StudioContext>();
    studioContext.Initialize(studioId);

    var settingsRepo = scope.ServiceProvider.GetRequiredService<IAudioSettingsRepository>();
    var settings = await settingsRepo.GetByStudioAsync(studioId);
    if (settings != null)
    {
        var audioEngine = scope.ServiceProvider.GetRequiredService<IAudioEngine>();
        try
        {
            await audioEngine.InitializeAsync(settings);

            // Only after a successful init: the service checks IsInitialized itself, but the
            // no-audio path has nothing to stream anyway.
            await scope.ServiceProvider.GetRequiredService<EncoderAutoStartService>()
                       .StartAllAsync();
        }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogWarning(ex, "Failed to initialize AudioEngine at startup. Running in no-audio fallback mode.");
        }
    }

    var playlistController = scope.ServiceProvider.GetRequiredService<IPlaylistController>();
    if (playlistController is PlaylistEngine engine)
    {
        await engine.InitializeAsync();
    }
}

// Force hub instantiation so event subscriptions are active before first client connects
_ = app.Services.GetRequiredService<RdmWebSocketHub>();

// Must be resolved, not merely registered: both subscribe to IEventBus in their constructors.
_ = app.Services.GetRequiredService<StreamTitlesService>();
_ = app.Services.GetRequiredService<EncoderTitleService>();

// Outer middleware first to capture all downstream exceptions
app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<AuthMiddleware>();

app.UseWebSockets();
app.UseRouting();

// Map controllers
app.MapControllers();

// WebSocket endpoint
app.Map("/api/v1/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var ws = await context.WebSockets.AcceptWebSocketAsync();
    var hub = context.RequestServices.GetRequiredService<RdmWebSocketHub>();
    await hub.HandleConnectionAsync(ws, context.RequestAborted);
});

app.Run();

public partial class Program { }
