using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public sealed class AssetPickerRow
{
    public string  AssetId         { get; init; } = string.Empty;
    public string  Title           { get; init; } = string.Empty;
    public string? Artist          { get; init; }
    public string  FormatDisplay   { get; init; } = string.Empty;
    public string  DurationDisplay { get; init; } = string.Empty;
    public uint    DurationMs      { get; init; }
    public bool    HasArtist       => !string.IsNullOrEmpty(Artist);
}

// Item in the format ComboBox — null FormatId means "all formats"
file sealed class FormatItem
{
    public string? FormatId   { get; init; }
    public string  Name       { get; init; } = string.Empty;
    public override string ToString() => Name;
}

public partial class CartAssetPickerDialog : Window
{
    private readonly ApiClientService? _api;
    private List<AssetPickerRow> _allRows = new();
    private string? _selectedFormatId;

    private string _sortColumn = "Title";
    private bool   _sortAsc    = true;

    public CartAssetPickerDialog() { InitializeComponent(); }

    public CartAssetPickerDialog(ApiClientService api)
    {
        _api = api;
        InitializeComponent();
        Opened += async (_, _) =>
        {
            UpdateSortHeaders();
            await LoadFormatsAsync();
            await SearchAsync();
        };
    }

    // ── Format combo ──────────────────────────────────────────────────────────

    private async Task LoadFormatsAsync()
    {
        if (_api is null) return;
        try
        {
            var envelope = await _api.GetFormatsAsync();
            if (envelope is null) return;

            var items = new List<FormatItem>
            {
                new FormatItem { FormatId = null, Name = Localizer.Instance?["cap.all_formats"] ?? "— all —" }
            };
            items.AddRange(envelope.Items
                .OrderBy(f => f.Name)
                .Select(f => new FormatItem { FormatId = f.FormatId, Name = f.Name }));

            FormatCombo.ItemsSource  = items;
            FormatCombo.SelectedIndex = 0;
        }
        catch { /* non-fatal — search still works without format filter */ }
    }

    private void OnFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FormatCombo.SelectedItem is FormatItem fi)
            _selectedFormatId = fi.FormatId;
        _ = SearchAsync();
    }

    private void OnClearFormat(object? sender, RoutedEventArgs e)
    {
        FormatCombo.SelectedIndex = 0;   // "— wszystkie —"
        _selectedFormatId = null;
        _ = SearchAsync();
    }

    // ── Text search ───────────────────────────────────────────────────────────

    private void OnSearchChanged(object? sender, TextChangedEventArgs e)
        => _ = SearchAsync();

    private void OnSearch(object? sender, RoutedEventArgs e)
        => _ = SearchAsync();

    private async Task SearchAsync()
    {
        StatusText.Text = Localizer.Instance?["cap.searching"] ?? "Searching…";
        AssetList.ItemsSource = null;
        OkBtn.IsEnabled = false;
        if (_api is null) return;

        var q = SearchBox.Text;

        try
        {
            var envelope = await _api.SearchAssetsAsync(
                q:        string.IsNullOrWhiteSpace(q) ? null : q,
                formatId: _selectedFormatId,
                status:   "ACTIVE",
                limit:    200);

            if (envelope is null) { StatusText.Text = Localizer.Instance?["cap.error_fetch"] ?? "Fetch error."; return; }

            _allRows = envelope.Items.Select(a => new AssetPickerRow
            {
                AssetId         = a.AssetId,
                Title           = a.Title,
                Artist          = a.Artist,
                FormatDisplay   = a.FormatName ?? string.Empty,
                DurationDisplay = FormatMs(a.DurationMs),
                DurationMs      = a.DurationMs
            }).ToList();

            ApplySort();
            StatusText.Text = string.Format(Localizer.Instance?["cap.results"] ?? "{0} results", envelope.Total);
        }
        catch (Exception ex)
        {
            StatusText.Text = string.Format(Localizer.Instance?["cap.error"] ?? "Error: {0}", ex.Message);
        }
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    private void OnSortTitle(object? sender, RoutedEventArgs e)  => SetSort("Title");
    private void OnSortFormat(object? sender, RoutedEventArgs e) => SetSort("Format");
    private void OnSortTime(object? sender, RoutedEventArgs e)   => SetSort("Time");

    private void SetSort(string column)
    {
        _sortAsc    = _sortColumn == column ? !_sortAsc : true;
        _sortColumn = column;
        UpdateSortHeaders();
        ApplySort();
    }

    private void ApplySort()
    {
        IEnumerable<AssetPickerRow> sorted = _sortColumn switch
        {
            "Format" => _sortAsc
                ? _allRows.OrderBy(r => r.FormatDisplay).ThenBy(r => r.Title)
                : _allRows.OrderByDescending(r => r.FormatDisplay).ThenBy(r => r.Title),
            "Time"   => _sortAsc
                ? _allRows.OrderBy(r => r.DurationMs)
                : _allRows.OrderByDescending(r => r.DurationMs),
            _        => _sortAsc
                ? _allRows.OrderBy(r => r.Title)
                : _allRows.OrderByDescending(r => r.Title),
        };
        AssetList.ItemsSource = sorted.ToList();
    }

    private void UpdateSortHeaders()
    {
        string arrow(string col) => _sortColumn != col ? "" : _sortAsc ? " ↑" : " ↓";
        BtnTitle.Content  = $"{Localizer.Instance?["cap.col.title_artist"] ?? "TITLE / ARTIST"}{arrow("Title")}";
        BtnFormat.Content = $"{Localizer.Instance?["cap.col.format"] ?? "FORMAT"}{arrow("Format")}";
        BtnTime.Content   = $"{Localizer.Instance?["cap.col.time"] ?? "TIME"}{arrow("Time")}";
    }

    // ── Selection / confirm ───────────────────────────────────────────────────

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
        => OkBtn.IsEnabled = AssetList.SelectedItem is not null;

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (AssetList.SelectedItem is AssetPickerRow) Confirm();
    }

    private void OnOk(object? sender, RoutedEventArgs e)     => Confirm();
    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

    private void Confirm()
    {
        if (AssetList.SelectedItem is AssetPickerRow row) Close(row);
    }

    private static string FormatMs(uint ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalMinutes >= 60 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
