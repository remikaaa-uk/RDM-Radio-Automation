using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public sealed class HistoryItemViewModel
{
    public string  PlayedAt          { get; }
    public string  Artist            { get; }
    public string  Title             { get; }
    public string  DurationFormatted { get; }
    public string  SourceType        { get; }
    public bool    HasAsset          { get; }
    public string? AssetId           { get; }

    public HistoryItemViewModel(PlayoutLogDto dto)
    {
        AssetId  = dto.AssetId;
        HasAsset = dto.AssetId is not null;

        var local = dto.StartedAt.ToLocalTime();
        PlayedAt  = local.ToString("dd.MM.yyyy  HH:mm:ss");
        Artist    = dto.Artist ?? "";
        Title     = dto.Title  ?? Localizer.Instance?["common.no_title"] ?? "(no title)";

        var durationMs = dto.EndedAt.HasValue
            ? (uint)(dto.EndedAt.Value - dto.StartedAt).TotalMilliseconds
            : 0;
        DurationFormatted = durationMs > 0 ? FormatMs(durationMs) : "";

        SourceType = dto.SourceType.ToUpperInvariant() switch
        {
            "PLAYLIST" => Localizer.Instance?["history.src.playlist"] ?? "Playlist",
            "CART"     => Localizer.Instance?["history.src.cart"]     ?? "Cart",
            "MANUAL"   => Localizer.Instance?["history.src.manual"]   ?? "Manual",
            _          => dto.SourceType
        };
    }

    private static string FormatMs(uint ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalMinutes >= 60 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}

public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 50;

    private readonly ApiClientService           _api;
    private readonly ILogger<HistoryViewModel>  _logger;

    private int _totalCount;
    private int _pageOffset;

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _paginationText = "0 / 0";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToPlaylistCommand))]
    private HistoryItemViewModel? _selectedItem;

    public bool HasPreviousPage => _pageOffset > 0;
    public bool HasNextPage     => _pageOffset + Items.Count < _totalCount;

    public HistoryViewModel(ApiClientService api, ILogger<HistoryViewModel> logger)
    {
        _api    = api;
        _logger = logger;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Activate()
    {
        _api.TrackStarted    += OnTrackStarted;
        _api.TrackEnded      += OnTrackEnded;
        _api.StreamMetaChanged += OnStreamMetaChanged;
        _ = RefreshCommand.ExecuteAsync(null);
    }

    public void Dispose()
    {
        _api.TrackStarted    -= OnTrackStarted;
        _api.TrackEnded      -= OnTrackEnded;
        _api.StreamMetaChanged -= OnStreamMetaChanged;
    }

    // ── Commands — Refresh ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var response = await _api.GetHistoryAsync(limit: PageSize, offset: _pageOffset);
            if (response is null) return;

            _totalCount = response.Total;

            Items.Clear();
            foreach (var entry in response.History)
                Items.Add(new HistoryItemViewModel(entry));

            UpdatePaginationState();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "History refresh failed"); }
        finally { IsBusy = false; }
    }

    // ── Commands — Pagination ─────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private async Task FirstPageAsync()
    {
        _pageOffset = 0;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasPreviousPage))]
    private async Task PreviousPageAsync()
    {
        _pageOffset = Math.Max(0, _pageOffset - PageSize);
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private async Task NextPageAsync()
    {
        _pageOffset += PageSize;
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private async Task LastPageAsync()
    {
        if (_totalCount == 0) return;
        _pageOffset = ((_totalCount - 1) / PageSize) * PageSize;
        await RefreshAsync();
    }

    // ── Commands — Playlist ───────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanAddToPlaylist))]
    private async Task AddToPlaylistAsync(CancellationToken ct)
    {
        if (SelectedItem?.AssetId is null) return;
        try
        {
            await _api.AddPlaylistItemAsync(
                new AddPlaylistItemRequestDto(SelectedItem.AssetId, null, int.MaxValue, "ASSET"), ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Add to playlist from history failed"); }
    }

    private bool CanAddToPlaylist() => SelectedItem?.HasAsset is true;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdatePaginationState()
    {
        OnPropertyChanged(nameof(HasPreviousPage));
        OnPropertyChanged(nameof(HasNextPage));
        FirstPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        LastPageCommand.NotifyCanExecuteChanged();

        if (_totalCount == 0 || Items.Count == 0)
        {
            PaginationText = "0 / 0";
        }
        else
        {
            var from = _pageOffset + 1;
            var to   = _pageOffset + Items.Count;
            PaginationText = $"{from}–{to} / {_totalCount}";
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnTrackStarted(TrackStartedPayload payload)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _pageOffset = 0;
            _ = RefreshCommand.ExecuteAsync(null);
        });
    }

    private void OnTrackEnded(TrackEndedPayload payload)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(500);
            _ = RefreshCommand.ExecuteAsync(null);
        });
    }

    private void OnStreamMetaChanged(StreamMetaChangedPayload payload)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            await Task.Delay(200);
            _pageOffset = 0;
            _ = RefreshCommand.ExecuteAsync(null);
        });
    }
}
