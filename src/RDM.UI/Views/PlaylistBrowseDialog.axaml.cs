using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public record BrowsePlaylistResult(bool IsFile, string? PlaylistId, string? Name, string? FilePath);

public partial class PlaylistBrowseDialog : Window
{
    private readonly ApiClientService _api = null!;
    private string? _pickedFilePath;

    public PlaylistBrowseDialog() => InitializeComponent();

    public PlaylistBrowseDialog(ApiClientService api, string title, string actionLabel, bool showBrowseFile = true)
    {
        _api = api;
        InitializeComponent();
        Title               = title;
        ActionBtn.Content   = actionLabel;
        BrowseFileBtn.IsVisible = showBrowseFile;
        Opened += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var envelope = await _api.GetSavedPlaylistsAsync();
        if (envelope is null) return;

        DbList.ItemsSource = envelope.Items
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new SavedPlaylistRowViewModel(p))
            .ToList();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DbList.SelectedItem is not null)
            _pickedFilePath = null;
        UpdateAction();
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        var opts = new FilePickerOpenOptions
        {
            Title         = Localizer.Instance?["pbd.picker_title"] ?? "Open a playlist file",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new FilePickerFileType(Localizer.Instance?["pbd.ft_rdpl"] ?? "RDM playlist (*.rdpl)") { Patterns = new[] { "*.rdpl" } },
                new FilePickerFileType(Localizer.Instance?["pbd.ft_m3u"]  ?? "M3U playlist (*.m3u)")  { Patterns = new[] { "*.m3u"  } },
                new FilePickerFileType(Localizer.Instance?["pbd.ft_m3u8"] ?? "M3U8 playlist (*.m3u8)"){ Patterns = new[] { "*.m3u8" } },
            }
        };
        var files = await StorageProvider.OpenFilePickerAsync(opts);
        if (files.Count == 0) return;

        _pickedFilePath      = files[0].Path.LocalPath;
        DbList.SelectedItem  = null;
        UpdateAction();
    }

    private void UpdateAction()
    {
        ActionBtn.IsEnabled = DbList.SelectedItem is not null || _pickedFilePath is not null;
    }

    private void OnAction(object? sender, RoutedEventArgs e)
    {
        if (_pickedFilePath is not null)
        {
            Close(new BrowsePlaylistResult(IsFile: true, null, null, _pickedFilePath));
            return;
        }
        if (DbList.SelectedItem is SavedPlaylistRowViewModel row)
            Close(new BrowsePlaylistResult(IsFile: false, row.PlaylistId, row.Name, null));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
