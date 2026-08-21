using System.Text;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;

namespace RDM.Core.Services;

/// <summary>
/// Subscribes to PlaylistItemStartedEvent and writes the currently playing track
/// to a text file (used by streaming encoders for "Now Playing" overlays).
///
/// Configuration source: rdm.config.json → "stream_titles" section (via StreamTitlesSettings).
///
/// Supported tokens: $artist$, $title$, $duration$
/// DB-backed tokens: $year$, $bpm$ (queried only when present in the format string)
/// Stub token:       $format$ (requires IAssetFormatRepository — logs warning when used)
/// </summary>
public sealed class StreamTitlesService : IAsyncDisposable
{
    private readonly IEventBus                    _eventBus;
    // Hot-swappable: replaced wholesale by UpdateSettings when the user saves Settings.
    // volatile guarantees the fire-and-forget handler tasks observe the new reference.
    private volatile StreamTitlesSettings         _settings;
    private readonly IAssetRepository             _assetRepo;
    private readonly IAssetFormatRepository       _formatRepo;
    private readonly ILogger<StreamTitlesService> _logger;
    private readonly CancellationTokenSource      _cts = new();

    private readonly Action<PlaylistItemStartedEvent> _handler;

    private static readonly string[] DbTokens = ["$year$", "$bpm$"];

    public StreamTitlesService(
        IEventBus                    eventBus,
        StreamTitlesSettings         settings,
        IAssetRepository             assetRepo,
        IAssetFormatRepository       formatRepo,
        ILogger<StreamTitlesService> logger)
    {
        _eventBus   = eventBus;
        _settings   = settings;
        _assetRepo  = assetRepo;
        _formatRepo = formatRepo;
        _logger     = logger;

        _handler = evt => _ = FireAndForget(HandleAsync(evt, _cts.Token));
        _eventBus.Subscribe(_handler);
    }

    /// <summary>
    /// Hot-swaps the active configuration after the user saves Settings, so changes to the
    /// output path, format, encoding, fallbacks and allowed formats take effect immediately
    /// without restarting the application. The next "Now Playing" write uses the new values.
    /// </summary>
    public void UpdateSettings(StreamTitlesSettings settings)
    {
        _settings = settings;
        _logger.LogDebug(
            "StreamTitlesService: settings hot-updated (enabled={Enabled}, path='{Path}')",
            settings.Enabled, settings.OutputFilePath);
    }

    public async ValueTask DisposeAsync()
    {
        _eventBus.Unsubscribe(_handler);
        await _cts.CancelAsync();
        _cts.Dispose();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task HandleAsync(PlaylistItemStartedEvent evt, CancellationToken ct)
    {
        if (!_settings.Enabled) return;
        if (string.IsNullOrEmpty(_settings.OutputFilePath)) return;

        var text = await ComposeTitleAsync(evt, ct);
        if (text is null) return;

        await WriteAsync(text, ct);
    }

    /// <summary>
    /// Builds the "now playing" line from the configured format, or null when this track is
    /// filtered out by category (jingles and adverts should not be announced as titles).
    ///
    /// Public because the streaming encoder needs exactly the same string: one definition of what
    /// a title looks like, whether it goes to a file for an external encoder or straight to the
    /// cast server. Deliberately independent of <c>Enabled</c> and <c>OutputFilePath</c> — those
    /// govern the file output only, and a station that streams without writing the file still
    /// wants its titles composed.
    /// </summary>
    public async Task<string?> ComposeTitleAsync(PlaylistItemStartedEvent evt, CancellationToken ct = default)
    {
        if (_settings.AllowedFormatIds.Count > 0 &&
            !_settings.AllowedFormatIds.Contains(evt.FormatId ?? string.Empty)) return null;

        string artist   = string.IsNullOrEmpty(evt.Artist)  ? _settings.FallbackArtist : evt.Artist;
        string title    = string.IsNullOrEmpty(evt.Title)   ? _settings.FallbackTitle  : evt.Title;
        string duration = FormatDuration(evt.DurationMs);

        string text = _settings.Format
            .Replace("$artist$",   artist)
            .Replace("$title$",    title)
            .Replace("$duration$", duration);

        // $format$ — resolve the format name from the event's format id (queried only when the token is present)
        if (text.Contains("$format$"))
        {
            var format = evt.FormatId is not null
                ? await _formatRepo.GetByIdAsync(evt.FormatId, ct)
                : null;
            text = text.Replace("$format$", format?.Name ?? string.Empty);
        }

        // DB-backed tokens — only query when format actually contains them
        if (DbTokens.Any(text.Contains))
        {
            var asset = evt.AssetId is not null ? await _assetRepo.GetByIdAsync(evt.AssetId, ct) : null;
            text = text
                .Replace("$year$", asset?.Year?.ToString() ?? string.Empty)
                .Replace("$bpm$",  asset?.Bpm?.ToString("F1") ?? string.Empty);
        }

        return text;
    }

    private async Task WriteAsync(string text, CancellationToken ct)
    {
        var encoding = string.Equals(
            _settings.Encoding, "ANSI", StringComparison.OrdinalIgnoreCase)
            ? Encoding.Default
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            await File.WriteAllTextAsync(_settings.OutputFilePath, text, encoding, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "StreamTitlesService: failed to write to '{Path}'", _settings.OutputFilePath);
        }
    }

    private static string FormatDuration(uint ms)
    {
        var ts = TimeSpan.FromMilliseconds(ms);
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }

    private async Task FireAndForget(Task task)
    {
        try   { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in StreamTitlesService");
        }
    }
}
