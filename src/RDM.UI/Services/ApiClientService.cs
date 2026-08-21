using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RDM.Shared.DTOs;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.Services;

/// <summary>
/// Single point of communication between the Avalonia UI and the embedded RDM API.
/// Manages HTTP requests and a WebSocket connection for real-time push events.
///
/// Credentials are set after login via SetCredentials() and cleared on 401.
/// WebSocket reconnects automatically with exponential backoff (1–30 s).
/// </summary>
public sealed class ApiClientService : IDisposable
{
    private readonly IHttpClientFactory        _factory;
    private readonly string                    _baseUrl;
    private readonly ILogger<ApiClientService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    // Thread-safe: reference assignment is atomic in .NET.
    private volatile string? _authHeader;

    private CancellationTokenSource? _wsCts;
    private Task?                    _wsTask;

    // ── Public surface ────────────────────────────────────────────────────────

    public bool IsConnected { get; private set; }

    // ── WebSocket events (fired on background thread — callers dispatch to UI) ─

    public event Action<TrackStartedPayload>?       TrackStarted;
    public event Action<TrackEndedPayload>?         TrackEnded;
    public event Action<TrackOutroReachedPayload>?  TrackOutroReached;
    public event Action<PlaylistModeChangedPayload>? PlaylistModeChanged;
    public event Action?                            PlaylistStopped;
    public event Action<CartTriggeredPayload>?      CartTriggered;
    public event Action<CartStoppedPayload>?        CartStopped;
    public event Action<WaveformReadyPayload>?      WaveformReady;
    public event Action?                            PlaylistUpdated;
    public event Action<AssetImportedPayload>?      AssetImported;
    public event Action<LoudnessAnalyzedPayload>?   LoudnessAnalyzed;
    public event Action?                            ScheduleChanged;
    public event Action?                            PflEnded;
    public event Action<StreamMetaChangedPayload>?  StreamMetaChanged;

    public event Action<bool>? ConnectionStateChanged;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ApiClientService(
        IHttpClientFactory        factory,
        IConfiguration            config,
        ILogger<ApiClientService> logger)
    {
        _factory = factory;
        _baseUrl = (config["api:base_url"] ?? "http://localhost:9300").TrimEnd('/');
        _logger  = logger;
    }

    // ── Credentials ───────────────────────────────────────────────────────────

    public void SetCredentials(string username, string password)
    {
        var b64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{username}:{password}"));
        _authHeader = $"Basic {b64}";
    }

    public void ClearCredentials() => _authHeader = null;

    // ── WebSocket lifecycle ───────────────────────────────────────────────────

    public Task StartWebSocketAsync(CancellationToken ct = default)
    {
        _wsCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _wsTask = RunWebSocketLoopAsync(_wsCts.Token);
        return Task.CompletedTask;
    }

    public void StopWebSocket()
    {
        _wsCts?.Cancel();
    }

    public void Dispose()
    {
        StopWebSocket();
        _wsCts?.Dispose();
    }

    // ── Now Playing ───────────────────────────────────────────────────────────

    public Task<NowPlayingDto?> GetNowPlayingAsync(CancellationToken ct = default)
        => GetAsync<NowPlayingDto>("/api/v1/nowplaying", ct);

    public Task<PlayoutHistoryResponseDto?> GetHistoryAsync(int limit = 50, int offset = 0, CancellationToken ct = default)
        => GetAsync<PlayoutHistoryResponseDto>($"/api/v1/nowplaying/history?limit={limit}&offset={offset}", ct);

    // ── Playback controls ─────────────────────────────────────────────────────

    public Task<bool> PlayAsync(CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/play", ct);

    public Task<bool> StopAsync(CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/stop", ct);

    public Task<bool> NextAsync(CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/next", ct);

    public Task<bool> PauseAsync(CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/pause", ct);

    public Task<bool> SetPlayerLoopAsync(bool enabled, CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/loop", new AuxLoopRequestDto(enabled), ct);

    public Task<bool> ResetAsync(CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/reset", ct);

    public async Task<bool> RemoveCurrentItemAsync(CancellationToken ct = default)
    {
        var ok = await SendBoolAsync(HttpMethod.Delete, "/api/v1/playlist/current", ct);
        if (ok) PlaylistUpdated?.Invoke();
        return ok;
    }

    public Task<bool> StartPflAsync(string assetId, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/playlist/pfl/{assetId}", ct);

    public Task<bool> StartPflFromFileAsync(string filePath, CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/pfl/file", new PflFromFileRequestDto(filePath), ct);

    public Task<bool> StopPflAsync(CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/pfl/stop", ct);

    public Task<bool> SeekPflAsync(int offsetMs, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/playlist/pfl/seek?offset_ms={offsetMs}", ct);

    // ── Live playlist management ──────────────────────────────────────────────

    public Task<PlaylistItemsEnvelopeDto?> GetPlaylistItemsAsync(CancellationToken ct = default)
        => GetAsync<PlaylistItemsEnvelopeDto>("/api/v1/playlist/items", ct);

    public async Task<bool> AddPlaylistItemAsync(AddPlaylistItemRequestDto dto, CancellationToken ct = default)
    {
        var ok = await PostBoolAsync("/api/v1/playlist/items", dto, ct);
        if (ok) PlaylistUpdated?.Invoke();
        return ok;
    }

    public async Task<bool> AddExternalPlaylistItemAsync(AddExternalPlaylistItemRequestDto dto, CancellationToken ct = default)
    {
        var ok = await PostBoolAsync("/api/v1/playlist/items/external", dto, ct);
        if (ok) PlaylistUpdated?.Invoke();
        return ok;
    }

    public async Task<bool> RemovePlaylistItemAsync(string itemId, CancellationToken ct = default)
    {
        var ok = await SendBoolAsync(HttpMethod.Delete, $"/api/v1/playlist/items/{itemId}", ct);
        if (ok) PlaylistUpdated?.Invoke();
        return ok;
    }

    public async Task<bool> ClearPlaylistAsync(CancellationToken ct = default)
    {
        var ok = await SendBoolAsync(HttpMethod.Delete, "/api/v1/playlist/items", ct);
        if (ok) PlaylistUpdated?.Invoke();
        return ok;
    }

    public async Task<bool> ReorderPlaylistItemAsync(ReorderPlaylistItemRequestDto dto, CancellationToken ct = default)
    {
        var ok = await SendBoolAsync(HttpMethod.Patch, "/api/v1/playlist/items/reorder", dto, ct);
        if (ok) PlaylistUpdated?.Invoke();
        return ok;
    }

    public async Task<bool> PatchPlaylistItemAsync(string itemId, PatchPlaylistItemDto dto, CancellationToken ct = default)
    {
        var ok = await SendBoolAsync(HttpMethod.Patch, $"/api/v1/playlist/items/{itemId}", dto, ct);
        if (ok) PlaylistUpdated?.Invoke();
        return ok;
    }

    public Task<bool> ChangePlaylistModeAsync(string mode, CancellationToken ct = default)
        => PostBoolAsync("/api/v1/playlist/mode", new ChangePlaylistModeRequestDto(mode), ct);

    // ── Assets / Library ─────────────────────────────────────────────────────

    public Task<AssetSearchEnvelopeDto?> SearchAssetsAsync(
        string? q             = null,
        string? assetType     = null,
        string? formatId      = null,
        string? status        = "ACTIVE",
        string? genre         = null,
        string? subcategoryId = null,
        int     limit         = 50,
        int     offset        = 0,
        string? sortColumn    = null,
        bool    sortAscending = true,
        CancellationToken ct  = default)
    {
        var sb = new StringBuilder($"/api/v1/assets?limit={limit}&offset={offset}");
        if (q             is not null) sb.Append($"&q={Uri.EscapeDataString(q)}");
        if (assetType     is not null) sb.Append($"&asset_type={assetType}");
        if (formatId      is not null) sb.Append($"&format_id={formatId}");
        if (status        is not null) sb.Append($"&status={status}");
        if (genre         is not null) sb.Append($"&genre={Uri.EscapeDataString(genre)}");
        if (subcategoryId is not null) sb.Append($"&subcategory_id={subcategoryId}");
        if (sortColumn    is not null) sb.Append($"&sort={sortColumn}&sort_dir={( sortAscending ? "asc" : "desc")}");
        return GetAsync<AssetSearchEnvelopeDto>(sb.ToString(), ct);
    }

    public Task<AssetDetailDto?> GetAssetDetailAsync(string assetId, CancellationToken ct = default)
        => GetAsync<AssetDetailDto>($"/api/v1/track/{assetId}", ct);

    public Task<AssetDto?> FindAssetByFilePathAsync(string filePath, CancellationToken ct = default)
        => GetAsync<AssetDto>($"/api/v1/assets/by-path?path={Uri.EscapeDataString(filePath)}", ct);

    public Task<bool> UpdateAssetAsync(string assetId, UpdateAssetRequestDto dto, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Put, $"/api/v1/track/{assetId}", dto, ct);

    public Task<AssetFormatsEnvelopeDto?> GetFormatsAsync(CancellationToken ct = default)
        => GetAsync<AssetFormatsEnvelopeDto>("/api/v1/formats", ct);

    public async Task<byte[]?> GetWaveformAsync(string assetId, CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, $"/api/v1/assets/{assetId}/waveform", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GET waveform {AssetId} failed", assetId);
            return null;
        }
    }

    public Task<bool> RequestWaveformAsync(string assetId, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/assets/{assetId}/waveform/request", ct);

    public Task<bool> AnalyzeLoudnessAsync(IReadOnlyList<string> assetIds, CancellationToken ct = default)
        => PostBoolAsync("/api/v1/assets/analyze-loudness",
            new AnalyzeLoudnessRequestDto(assetIds), ct);

    public Task<bool> AnalyzeCueAsync(IReadOnlyList<string> assetIds,
        double startDb, double nextStartDb, double endDb, CancellationToken ct = default)
        => PostBoolAsync("/api/v1/assets/analyze-cue",
            new AnalyzeCueRequestDto(assetIds, startDb, nextStartDb, endDb), ct);

    // ── Import ────────────────────────────────────────────────────────────────

    public Task<ImportResponseDto?> StartImportAsync(ImportRequestDto dto, CancellationToken ct = default)
        => PostAsync<ImportRequestDto, ImportResponseDto>("/api/v1/assets/import", dto, ct);

    public Task<ImportStatusDto?> GetImportStatusAsync(string importId, CancellationToken ct = default)
        => GetAsync<ImportStatusDto>($"/api/v1/assets/import/{importId}", ct);

    // ── Scan (Update Tracks) ──────────────────────────────────────────────────

    public Task<ScanResponseDto?> StartScanAsync(ScanRequestDto dto, CancellationToken ct = default)
        => PostAsync<ScanRequestDto, ScanResponseDto>("/api/v1/assets/scan", dto, ct);

    public Task<ScanStatusDto?> GetScanStatusAsync(string scanId, CancellationToken ct = default)
        => GetAsync<ScanStatusDto>($"/api/v1/assets/scan/{scanId}", ct);

    public Task<IReadOnlyList<NewTrackDto>?> GetScanResultsAsync(string scanId, CancellationToken ct = default)
        => GetAsync<IReadOnlyList<NewTrackDto>>($"/api/v1/assets/scan/{scanId}/results", ct);

    public Task<AddStreamResponseDto?> AddStreamAsync(AddStreamRequestDto dto, CancellationToken ct = default)
        => PostAsync<AddStreamRequestDto, AddStreamResponseDto>("/api/v1/streams", dto, ct);

    public Task<Id3PeekResponseDto?> ReadId3FromFileAsync(string filePath, CancellationToken ct = default)
        => PostAsync<Id3PeekRequestDto, Id3PeekResponseDto>("/api/v1/assets/id3/peek", new Id3PeekRequestDto(filePath), ct);

    public Task<bool> PatchAssetStatusAsync(string assetId, string status, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Patch, $"/api/v1/assets/{assetId}/status",
            new AssetStatusUpdateRequestDto(status), ct);

    public Task<DeleteAssetResponseDto?> DeleteAssetAsync(string assetId, bool deleteFile, CancellationToken ct = default)
        => DeleteAsync<DeleteAssetResponseDto>($"/api/v1/assets/{assetId}?deleteFile={deleteFile}", ct);

    public Task<BatchDeleteAssetsResponseDto?> BatchDeleteAssetsAsync(
        IReadOnlyList<string> assetIds, bool deleteFiles, CancellationToken ct = default)
        => PostAsync<BatchDeleteAssetsRequestDto, BatchDeleteAssetsResponseDto>(
            "/api/v1/assets/batch-delete",
            new BatchDeleteAssetsRequestDto(assetIds, deleteFiles), ct);

    public async Task<PurgeOrphansResponseDto?> PurgeOrphansAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post, "/api/v1/assets/purge-orphans", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<PurgeOrphansResponseDto>(
                await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PurgeOrphans failed");
            return null;
        }
    }

    public async Task<OptimizeDatabaseResponseDto?> OptimizeDatabaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post, "/api/v1/assets/optimize", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<OptimizeDatabaseResponseDto>(
                await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "OptimizeDatabase failed");
            return null;
        }
    }

    // ── Cartwalls ─────────────────────────────────────────────────────────────

    public Task<CartwallsEnvelopeDto?> GetCartwallsAsync(CancellationToken ct = default)
        => GetAsync<CartwallsEnvelopeDto>("/api/v1/cartwalls", ct);

    public Task<bool> TriggerCartAsync(string slotId, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/cartwall/{slotId}/trigger", ct);

    public Task<bool> StopCartAsync(string slotId, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/cartwall/{slotId}/stop", ct);

    public Task<bool> PatchCartSlotAsync(string slotId, PatchCartSlotDto dto, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Patch, $"/api/v1/cartwall/{slotId}", dto, ct);

    public Task<bool> SetCartwallModeAsync(string cartwallId, string mode, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/cartwalls/{cartwallId}/mode",
            new SetCartwallModeRequestDto(mode), ct);

    public Task<CartwallDto?> CreateCartwallAsync(CreateCartwallDto dto, CancellationToken ct = default)
        => PostAsync<CreateCartwallDto, CartwallDto>("/api/v1/cartwalls", dto, ct);

    public Task<bool> DeleteCartwallAsync(string cartwallId, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Delete, $"/api/v1/cartwalls/{cartwallId}", ct);

    public Task<bool> PatchCartwallAsync(string cartwallId, PatchCartwallDto dto, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Patch, $"/api/v1/cartwalls/{cartwallId}", dto, ct);

    // ── Saved playlists ───────────────────────────────────────────────────────

    public Task<SavedPlaylistsEnvelopeDto?> GetSavedPlaylistsAsync(CancellationToken ct = default)
        => GetAsync<SavedPlaylistsEnvelopeDto>("/api/v1/playlists", ct);

    public Task<SavedPlaylistDetailDto?> GetSavedPlaylistDetailAsync(string playlistId, CancellationToken ct = default)
        => GetAsync<SavedPlaylistDetailDto>($"/api/v1/playlists/{playlistId}", ct);

    public Task<SavePlaylistResponseDto?> SavePlaylistAsync(SavePlaylistRequestDto dto, CancellationToken ct = default)
        => PostAsync<SavePlaylistRequestDto, SavePlaylistResponseDto>("/api/v1/playlists", dto, ct);

    public Task<SavePlaylistResponseDto?> UpdateSavedPlaylistAsync(string playlistId, SavePlaylistRequestDto dto, CancellationToken ct = default)
        => PutAsync<SavePlaylistRequestDto, SavePlaylistResponseDto>($"/api/v1/playlists/{playlistId}", dto, ct);

    public Task<bool> DeleteSavedPlaylistAsync(string playlistId, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Delete, $"/api/v1/playlists/{playlistId}", ct);

    // ── Scheduled events ──────────────────────────────────────────────────────

    public Task<ScheduledEventResponseEnvelopeDto?> GetEventsAsync(CancellationToken ct = default)
        => GetAsync<ScheduledEventResponseEnvelopeDto>("/api/v1/events", ct);

    public Task<NextScheduledEventDto?> GetNextEventAsync(CancellationToken ct = default)
        => GetAsync<NextScheduledEventDto>("/api/v1/events/next", ct);

    public Task<ScheduledEventCreatedResponseDto?> CreateEventAsync(
        ScheduledEventCreateDto dto, CancellationToken ct = default)
        => PostAsync<ScheduledEventCreateDto, ScheduledEventCreatedResponseDto>("/api/v1/events", dto, ct);

    public Task<bool> UpdateEventAsync(string eventId, ScheduledEventCreateDto dto, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Put, $"/api/v1/events/{eventId}", dto, ct);

    public Task<bool> PatchEventAsync(string eventId, ScheduledEventPatchDto dto, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Patch, $"/api/v1/events/{eventId}", dto, ct);

    public Task<bool> DeleteEventAsync(string eventId, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Delete, $"/api/v1/events/{eventId}", ct);

    public Task<EventLogEnvelopeDto?> GetEventLogAsync(int limit = 50, CancellationToken ct = default)
        => GetAsync<EventLogEnvelopeDto>($"/api/v1/events/log?limit={limit}", ct);

    // ── Microphone ────────────────────────────────────────────────────────────

    public Task<bool> StartMicAsync(CancellationToken ct = default)
        => PostBoolAsync("/api/v1/mic/start", ct);

    public Task<bool> StopMicAsync(CancellationToken ct = default)
        => PostBoolAsync("/api/v1/mic/stop", ct);

    public Task<MicLevelDto?> GetMicLevelAsync(CancellationToken ct = default)
        => GetAsync<MicLevelDto>("/api/v1/mic/level", ct);

    public Task<MicStatusDto?> GetMicStatusAsync(CancellationToken ct = default)
        => GetAsync<MicStatusDto>("/api/v1/mic/status", ct);

    public Task<MicFxDto[]?> GetMicFxListAsync(CancellationToken ct = default)
        => GetAsync<MicFxDto[]>("/api/v1/mic/fx", ct);

    public Task<MicFxAddedDto?> AddMicFxAsync(string fxType, CancellationToken ct = default)
        => PostAsync<AddMicFxRequestDto, MicFxAddedDto>(
               "/api/v1/mic/fx", new AddMicFxRequestDto(fxType), ct);

    public Task<bool> RemoveMicFxAsync(int fxHandle, CancellationToken ct = default)
        => DeleteBoolAsync($"/api/v1/mic/fx/{fxHandle}", ct);

    public Task<bool> UpdateMicFxAsync(
        int slotId, Dictionary<string, float> parameters, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Put, $"/api/v1/mic/fx/{slotId}",
               new UpdateMicFxRequestDto(parameters), ct);

    public Task<MicVstDto[]?> GetMicVstListAsync(CancellationToken ct = default)
        => GetAsync<MicVstDto[]>("/api/v1/mic/vst", ct);

    public Task<MicVstAddedDto?> AddMicVstAsync(string dllPath, CancellationToken ct = default)
        => PostAsync<AddMicVstRequestDto, MicVstAddedDto>(
               "/api/v1/mic/vst", new AddMicVstRequestDto(dllPath), ct);

    public Task<bool> RemoveMicVstAsync(int vstHandle, CancellationToken ct = default)
        => DeleteBoolAsync($"/api/v1/mic/vst/{vstHandle}", ct);

    // ── AUX players ───────────────────────────────────────────────────────────

    public Task<AuxLoadedDto?> LoadAuxAsync(int index, string filePath, CancellationToken ct = default)
        => PostAsync<AuxLoadRequestDto, AuxLoadedDto>($"/api/v1/aux/{index}/load", new AuxLoadRequestDto(filePath), ct);

    public Task<bool> PlayAuxAsync(int index, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/aux/{index}/play", ct);

    public Task<bool> PauseAuxAsync(int index, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/aux/{index}/pause", ct);

    public Task<bool> StopAuxAsync(int index, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/aux/{index}/stop", ct);

    public Task<bool> EjectAuxAsync(int index, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/aux/{index}/eject", ct);

    public Task<bool> SetAuxLoopAsync(int index, bool enabled, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/aux/{index}/loop", new AuxLoopRequestDto(enabled), ct);

    public Task<bool> SetAuxVolumeAsync(int index, float gain, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/aux/{index}/volume", new AuxVolumeRequestDto(gain), ct);

    public Task<bool> SetAuxRouteAsync(int index, bool on, bool pfl, CancellationToken ct = default)
        => PostBoolAsync($"/api/v1/aux/{index}/route", new AuxRouteRequestDto(on, pfl), ct);

    // ── Categories ────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<CategoryDto>?> GetCategoriesAsync(CancellationToken ct = default)
        => GetAsync<IReadOnlyList<CategoryDto>>("/api/v1/categories", ct);

    public Task<CategoryResponseDto?> CreateCategoryAsync(string name, CancellationToken ct = default)
        => PostAsync<CreateCategoryRequestDto, CategoryResponseDto>(
            "/api/v1/categories", new CreateCategoryRequestDto(name), ct);

    public Task<bool> RenameCategoryAsync(string formatId, string newName, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Put, $"/api/v1/categories/{formatId}",
            new RenameCategoryRequestDto(newName), ct);

    public Task<DeleteCategoryResponseDto?> DeleteCategoryAsync(string formatId, CancellationToken ct = default)
        => DeleteAsync<DeleteCategoryResponseDto>($"/api/v1/categories/{formatId}", ct);

    // ── Subcategories ─────────────────────────────────────────────────────────

    public Task<SubcategoriesEnvelopeDto?> GetSubcategoriesAsync(string formatId, CancellationToken ct = default)
        => GetAsync<SubcategoriesEnvelopeDto>($"/api/v1/categories/{formatId}/subcategories", ct);

    public Task<SubcategoryResponseDto?> CreateSubcategoryAsync(string formatId, string name, CancellationToken ct = default)
        => PostAsync<CreateSubcategoryRequestDto, SubcategoryResponseDto>(
            $"/api/v1/categories/{formatId}/subcategories", new CreateSubcategoryRequestDto(name), ct);

    public Task<bool> RenameSubcategoryAsync(string subcategoryId, string newName, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Put, $"/api/v1/categories/subcategories/{subcategoryId}",
            new RenameSubcategoryRequestDto(newName), ct);

    public Task<DeleteSubcategoryResponseDto?> DeleteSubcategoryAsync(string subcategoryId, CancellationToken ct = default)
        => DeleteAsync<DeleteSubcategoryResponseDto>($"/api/v1/categories/subcategories/{subcategoryId}", ct);

    // ── Genres ────────────────────────────────────────────────────────────────

    public Task<GenresEnvelopeDto?> GetGenresAsync(CancellationToken ct = default)
        => GetAsync<GenresEnvelopeDto>("/api/v1/categories/genres", ct);

    public Task<GenreResponseDto?> CreateGenreAsync(string name, CancellationToken ct = default)
        => PostAsync<CreateGenreRequestDto, GenreResponseDto>(
            "/api/v1/categories/genres", new CreateGenreRequestDto(name), ct);

    public Task<bool> RenameGenreAsync(string genreId, string newName, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Put, $"/api/v1/categories/genres/{genreId}",
            new RenameGenreRequestDto(newName), ct);

    public Task<bool> DeleteGenreAsync(string genreId, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Delete, $"/api/v1/categories/genres/{genreId}", ct);

    // ── Private HTTP helpers ──────────────────────────────────────────────────

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Get, path, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<T>(
                await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "GET {Path} failed", path);
            return null;
        }
    }

    private async Task<TResponse?> PutAsync<TBody, TResponse>(
        string path, TBody body, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Put, path, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<TResponse>(
                await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PUT {Path} failed", path);
            return null;
        }
    }

    private async Task<TResponse?> PostAsync<TBody, TResponse>(
        string path, TBody body, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Post, path, body, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<TResponse>(
                await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "POST {Path} failed", path);
            return null;
        }
    }

    private async Task<T?> DeleteAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var resp = await SendAsync(HttpMethod.Delete, path, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonSerializer.Deserialize<T>(
                await resp.Content.ReadAsStringAsync(ct), JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "DELETE {Path} failed", path);
            return null;
        }
    }

    private Task<bool> PostBoolAsync(string path, CancellationToken ct)
        => SendBoolAsync(HttpMethod.Post, path, ct);

    private Task<bool> PostBoolAsync<TBody>(string path, TBody body, CancellationToken ct)
        => SendBoolAsync(HttpMethod.Post, path, body, ct);

    private Task<bool> DeleteBoolAsync(string path, CancellationToken ct)
        => SendBoolAsync(HttpMethod.Delete, path, ct);

    private async Task<bool> SendBoolAsync(HttpMethod method, string path, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(method, path, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                ClearCredentials();
                _logger.LogWarning("ApiClientService: 401 on {Method} {Path}", method, path);
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Method} {Path} failed", method, path);
            return false;
        }
    }

    private async Task<bool> SendBoolAsync<TBody>(
        HttpMethod method, string path, TBody body, CancellationToken ct)
    {
        try
        {
            using var resp = await SendAsync(method, path, body, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                ClearCredentials();
                _logger.LogWarning("ApiClientService: 401 on {Method} {Path}", method, path);
            }
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Method} {Path} failed", method, path);
            return false;
        }
    }

    // ── Streaming encoders ────────────────────────────────────────────────────

    public Task<EncoderProfileEnvelopeDto?> GetEncoderProfilesAsync(CancellationToken ct = default)
        => GetAsync<EncoderProfileEnvelopeDto>("/api/v1/encoder", ct);

    public Task<EncoderStatusEnvelopeDto?> GetEncoderStatusesAsync(CancellationToken ct = default)
        => GetAsync<EncoderStatusEnvelopeDto>("/api/v1/encoder/status", ct);

    public Task<ApiResult<EncoderProfileCreatedDto>> CreateEncoderProfileAsync(
        EncoderProfileCreateDto dto, CancellationToken ct = default)
        => SendWithErrorAsync<EncoderProfileCreateDto, EncoderProfileCreatedDto>(
            HttpMethod.Post, "/api/v1/encoder", dto, ct);

    public Task<ApiResult<EncoderProfileDto>> UpdateEncoderProfileAsync(
        string profileId, EncoderProfileUpdateDto dto, CancellationToken ct = default)
        => SendWithErrorAsync<EncoderProfileUpdateDto, EncoderProfileDto>(
            HttpMethod.Put, $"/api/v1/encoder/{profileId}", dto, ct);

    public Task<bool> DeleteEncoderProfileAsync(string profileId, CancellationToken ct = default)
        => SendBoolAsync(HttpMethod.Delete, $"/api/v1/encoder/{profileId}", ct);

    public Task<ApiResult<EncoderStatusDto>> StartEncoderAsync(string profileId, CancellationToken ct = default)
        => SendWithErrorAsync<EncoderStatusDto>(HttpMethod.Post, $"/api/v1/encoder/{profileId}/start", ct);

    public Task<ApiResult<EncoderStatusDto>> StopEncoderAsync(string profileId, CancellationToken ct = default)
        => SendWithErrorAsync<EncoderStatusDto>(HttpMethod.Post, $"/api/v1/encoder/{profileId}/stop", ct);

    /// Starts every profile marked ready — what the bottom-bar button calls.
    public Task<ApiResult<EncoderStatusEnvelopeDto>> StartArmedEncodersAsync(CancellationToken ct = default)
        => SendWithErrorAsync<EncoderStatusEnvelopeDto>(HttpMethod.Post, "/api/v1/encoder/start-armed", ct);

    public Task<ApiResult<EncoderStatusEnvelopeDto>> StopAllEncodersAsync(CancellationToken ct = default)
        => SendWithErrorAsync<EncoderStatusEnvelopeDto>(HttpMethod.Post, "/api/v1/encoder/stop-all", ct);

    // ── Recording ─────────────────────────────────────────────────────────────

    public Task<RecordingStatusDto?> GetRecordingStatusAsync(CancellationToken ct = default)
        => GetAsync<RecordingStatusDto>("/api/v1/recording/status", ct);

    public Task<ApiResult<RecordingStatusDto>> StartRecordingAsync(
        RecordingStartRequestDto dto, CancellationToken ct = default)
        => SendWithErrorAsync<RecordingStartRequestDto, RecordingStatusDto>(
            HttpMethod.Post, "/api/v1/recording/start", dto, ct);

    public Task<ApiResult<RecordingStatusDto>> StopRecordingAsync(CancellationToken ct = default)
        => SendWithErrorAsync<RecordingStatusDto>(HttpMethod.Post, "/api/v1/recording/stop", ct);

    // ── Error-preserving send ─────────────────────────────────────────────────

    /// <summary>
    /// Like the bool helpers, but keeps the server's explanation. Streaming and recording answer a
    /// bad request with a 422 that names exactly what is wrong — a missing add-on, an unwritable
    /// folder, a mount point Icecast needs. Collapsing that to false would leave the operator with
    /// a button that does nothing and no way to find out why.
    /// </summary>
    private async Task<ApiResult<TResponse>> SendWithErrorAsync<TResponse>(
        HttpMethod method, string path, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            using var resp = await SendAsync(method, path, ct);
            return await ReadResultAsync<TResponse>(resp, method, path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Method} {Path} failed", method, path);
            return new ApiResult<TResponse>(false, null, ex.Message);
        }
    }

    private async Task<ApiResult<TResponse>> SendWithErrorAsync<TBody, TResponse>(
        HttpMethod method, string path, TBody body, CancellationToken ct)
        where TResponse : class
    {
        try
        {
            using var resp = await SendAsync(method, path, body, ct);
            return await ReadResultAsync<TResponse>(resp, method, path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Method} {Path} failed", method, path);
            return new ApiResult<TResponse>(false, null, ex.Message);
        }
    }

    private async Task<ApiResult<TResponse>> ReadResultAsync<TResponse>(
        HttpResponseMessage resp, HttpMethod method, string path, CancellationToken ct)
        where TResponse : class
    {
        var payload = await resp.Content.ReadAsStringAsync(ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            ClearCredentials();
            _logger.LogWarning("ApiClientService: 401 on {Method} {Path}", method, path);
        }

        if (resp.IsSuccessStatusCode)
        {
            var value = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize<TResponse>(payload, JsonOptions);
            return new ApiResult<TResponse>(true, value, null);
        }

        string? message = null;
        try
        {
            message = JsonSerializer.Deserialize<ErrorResponseDto>(payload, JsonOptions)?.Message;
        }
        catch (JsonException)
        {
            // Not an ErrorResponseDto (a proxy page, an empty 500) — fall back to the status line.
        }

        _logger.LogWarning("{Method} {Path} → {Status}: {Message}",
            method, path, (int)resp.StatusCode, message ?? "(no message)");

        return new ApiResult<TResponse>(false, null, message ?? $"HTTP {(int)resp.StatusCode}");
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, CancellationToken ct)
    {
        var client  = _factory.CreateClient("rdm-api");
        var request = new HttpRequestMessage(method, path);
        if (_authHeader is not null)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_authHeader);
        return await client.SendAsync(request, ct);
    }

    private async Task<HttpResponseMessage> SendAsync<TBody>(
        HttpMethod method, string path, TBody body, CancellationToken ct)
    {
        var client  = _factory.CreateClient("rdm-api");
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8, "application/json")
        };
        if (_authHeader is not null)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_authHeader);
        return await client.SendAsync(request, ct);
    }

    // ── WebSocket loop with exponential backoff ───────────────────────────────

    private static readonly int[] BackoffSeconds = { 1, 2, 4, 8, 16, 30 };

    private async Task RunWebSocketLoopAsync(CancellationToken ct)
    {
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                if (_authHeader is not null)
                    ws.Options.SetRequestHeader("Authorization", _authHeader);

                await ws.ConnectAsync(BuildWsUri(), ct);

                attempt = 0;
                SetConnected(true);

                // Fetch fresh NowPlaying state on every reconnect.
                _ = Task.Run(async () =>
                {
                    var dto = await GetNowPlayingAsync(ct);
                    if (dto?.NowPlaying is not null)
                    {
                        TrackStarted?.Invoke(new TrackStartedPayload(
                            dto.NowPlaying.AssetId,
                            dto.NowPlaying.Title,
                            dto.NowPlaying.Artist,
                            dto.NowPlaying.DurationMs,
                            dto.NowPlaying.StartedAt));
                    }
                }, ct);

                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocket disconnected (attempt {Attempt})", attempt);
            }

            SetConnected(false);

            var delay = TimeSpan.FromSeconds(
                BackoffSeconds[Math.Min(attempt, BackoffSeconds.Length - 1)]);
            attempt++;

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }

        SetConnected(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer  = new byte[8192];
        var message = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;

            message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                ProcessMessage(message.ToString());
                message.Clear();
            }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            var frame = JsonSerializer.Deserialize<WebSocketFrameDto>(json, JsonOptions);
            if (frame is null) return;

            if (frame.Payload is not System.Text.Json.JsonElement el) return;

            switch (frame.EventType)
            {
                case "TRACK_STARTED":
                    var started = Deserialize<TrackStartedPayload>(el);
                    if (started is not null) TrackStarted?.Invoke(started);
                    break;

                case "TRACK_ENDED":
                    var ended = Deserialize<TrackEndedPayload>(el);
                    if (ended is not null) TrackEnded?.Invoke(ended);
                    break;

                case "TRACK_OUTRO_REACHED":
                    var outro = Deserialize<TrackOutroReachedPayload>(el);
                    if (outro is not null) TrackOutroReached?.Invoke(outro);
                    break;

                case "PLAYLIST_MODE_CHANGED":
                    var modeChanged = Deserialize<PlaylistModeChangedPayload>(el);
                    if (modeChanged is not null) PlaylistModeChanged?.Invoke(modeChanged);
                    break;

                case "PLAYLIST_STOPPED":
                    PlaylistStopped?.Invoke();
                    break;

                case "PLAYLIST_UPDATED":
                    PlaylistUpdated?.Invoke();
                    break;

                case "CART_TRIGGERED":
                    var triggered = Deserialize<CartTriggeredPayload>(el);
                    if (triggered is not null) CartTriggered?.Invoke(triggered);
                    break;

                case "CART_STOPPED":
                    var stopped = Deserialize<CartStoppedPayload>(el);
                    if (stopped is not null) CartStopped?.Invoke(stopped);
                    break;

                case "WAVEFORM_READY":
                    var wfReady = Deserialize<WaveformReadyPayload>(el);
                    if (wfReady is not null) WaveformReady?.Invoke(wfReady);
                    break;

                case "ASSET_IMPORTED":
                    var imported = Deserialize<AssetImportedPayload>(el);
                    if (imported is not null) AssetImported?.Invoke(imported);
                    break;

                case "LOUDNESS_ANALYZED":
                    var loudness = Deserialize<LoudnessAnalyzedPayload>(el);
                    if (loudness is not null) LoudnessAnalyzed?.Invoke(loudness);
                    break;

                case "SCHEDULE_CHANGED":
                    ScheduleChanged?.Invoke();
                    break;

                case "PFL_ENDED":
                    PflEnded?.Invoke();
                    break;

                case "STREAM_META_CHANGED":
                    var streamMeta = Deserialize<StreamMetaChangedPayload>(el);
                    if (streamMeta is not null) StreamMetaChanged?.Invoke(streamMeta);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process WebSocket message");
        }
    }

    private T? Deserialize<T>(System.Text.Json.JsonElement el)
    {
        try { return JsonSerializer.Deserialize<T>(el.GetRawText(), JsonOptions); }
        catch { return default; }
    }

    private void SetConnected(bool value)
    {
        if (IsConnected == value) return;
        IsConnected = value;
        ConnectionStateChanged?.Invoke(value);
    }

    private Uri BuildWsUri()
    {
        var http = new Uri(_baseUrl);
        var scheme = http.Scheme == "https" ? "wss" : "ws";
        return new Uri($"{scheme}://{http.Authority}/api/v1/ws");
    }
}

// ── WebSocket payload records ─────────────────────────────────────────────────

public record TrackStartedPayload(
    [property: JsonPropertyName("asset_id")]     string    AssetId,
    [property: JsonPropertyName("title")]        string?   Title,
    [property: JsonPropertyName("artist")]       string?   Artist,
    [property: JsonPropertyName("duration_ms")]  uint      DurationMs,
    [property: JsonPropertyName("scheduled_at")] DateTime? ScheduledAt,
    [property: JsonPropertyName("vu_offset_db")] double    VuOffsetDb = 0.0);

public record TrackEndedPayload(
    [property: JsonPropertyName("asset_id")] string?   AssetId,
    [property: JsonPropertyName("ended_at")] DateTime  EndedAt,
    [property: JsonPropertyName("reason")]   string?   Reason);

public record TrackOutroReachedPayload(
    [property: JsonPropertyName("asset_id")]        string AssetId,
    [property: JsonPropertyName("outro_position_ms")] uint OutroPositionMs);

public record PlaylistModeChangedPayload(
    [property: JsonPropertyName("mode")] string Mode);

public record CartTriggeredPayload(
    [property: JsonPropertyName("slot_id")]     string  SlotId,
    [property: JsonPropertyName("duration_ms")] uint    DurationMs,
    [property: JsonPropertyName("label")]       string? Label);

public record CartStoppedPayload(
    [property: JsonPropertyName("slot_id")] string SlotId);

public record WaveformReadyPayload(
    [property: JsonPropertyName("asset_id")] string AssetId);

public record AssetImportedPayload(
    [property: JsonPropertyName("asset_id")] string  AssetId,
    [property: JsonPropertyName("title")]    string? Title,
    [property: JsonPropertyName("artist")]   string? Artist);

public record LoudnessAnalyzedPayload(
    [property: JsonPropertyName("asset_id")]  string  AssetId,
    [property: JsonPropertyName("lufs")]      decimal Lufs,
    [property: JsonPropertyName("true_peak")] decimal TruePeak);

public record StreamMetaChangedPayload(
    [property: JsonPropertyName("asset_id")]     string AssetId,
    [property: JsonPropertyName("stream_title")] string StreamTitle);

/// <summary>
/// Outcome of a call whose failure message matters to the operator. <see cref="ErrorMessage"/> is
/// the server's own explanation when there was one, so the UI can show what actually went wrong
/// instead of a generic failure.
/// </summary>
public sealed record ApiResult<T>(bool Ok, T? Value, string? ErrorMessage) where T : class;
