using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RDM.Shared.DTOs;
using RDM.UI.Localization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace RDM.UI.Views;

public record SavePlaylistOptions(string Name, bool ToDatabase, string? FilePath, string? OverwriteId = null);

public partial class SavePlaylistDialog : Window
{
    /// One row of the "overwrite an existing playlist" combo. ToString drives the display text.
    private sealed record ExistingPlaylist(string PlaylistId, string Name, string Display)
    {
        public override string ToString() => Display;
    }

    private readonly string? _loadedPlaylistId;
    private readonly string  _initialName = string.Empty;
    private readonly bool    _databaseOnly;

    public SavePlaylistDialog() => InitializeComponent();

    /// <param name="loadedPlaylistId">Playlist this content came from — enables "overwrite the loaded one".</param>
    /// <param name="loadedPlaylistName">Shown in the overwrite label so the target is unambiguous.</param>
    /// <param name="existingPlaylists">Everything in the DB — enables "overwrite a playlist picked from the list".</param>
    /// <param name="databaseOnly">Hides the destination picker (playout exports M3U from its own menu).</param>
    /// <param name="externalItemCount">Items that cannot be stored in the DB; warns instead of dropping them silently.</param>
    public SavePlaylistDialog(
        string                                  initialName,
        string?                                 loadedPlaylistId   = null,
        string?                                 loadedPlaylistName = null,
        IReadOnlyList<SavedPlaylistSummaryDto>? existingPlaylists  = null,
        bool                                    databaseOnly       = false,
        int                                     externalItemCount  = 0)
    {
        _loadedPlaylistId = loadedPlaylistId;
        _initialName      = initialName;
        _databaseOnly     = databaseOnly;

        InitializeComponent();
        NameBox.Text = initialName;

        if (loadedPlaylistName is not null)
            OverwriteRadio.Content = Localizer.Instance?.Format("sp.overwrite_loaded", loadedPlaylistName)
                                     ?? $"Overwrite the loaded playlist „{loadedPlaylistName}”";

        if (databaseOnly)
        {
            DestRow.IsVisible      = false;
            FilePathRow.IsVisible  = false;
        }

        if (existingPlaylists is { Count: > 0 })
        {
            var suffix = Localizer.Instance?["playlist.load_menu.item_suffix"] ?? "tr.";
            foreach (var p in existingPlaylists.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                ExistingCombo.Items.Add(new ExistingPlaylist(
                    p.PlaylistId, p.Name,
                    $"{p.Name}   ({p.ItemCount} {suffix} · {p.CreatedAt.ToLocalTime():dd.MM.yyyy})"));

            OverwriteSelectedRadio.IsVisible = true;
            ExistingCombo.IsVisible          = true;

            // Preselect the loaded playlist when it is one of them, otherwise the first row.
            var match = ExistingCombo.Items
                .OfType<ExistingPlaylist>()
                .FirstOrDefault(x => x.PlaylistId == loadedPlaylistId);
            ExistingCombo.SelectedItem = match ?? ExistingCombo.Items.OfType<ExistingPlaylist>().First();
        }

        OverwriteRadio.IsVisible = loadedPlaylistId is not null;

        // Nothing loaded → never default to a destructive action.
        if (loadedPlaylistId is null)
            NewCopyRadio.IsChecked = true;

        if (externalItemCount > 0)
        {
            ExternalWarning.Text = Localizer.Instance?.Format("sp.external_warning", externalItemCount)
                                   ?? $"{externalItemCount} external item(s) will not be saved to the database.";
            ExternalWarning.IsVisible = true;
        }

        UpdateOverwriteSectionVisibility();
    }

    private bool HasOverwriteTargets => _loadedPlaylistId is not null || ExistingCombo.ItemCount > 0;

    private void UpdateOverwriteSectionVisibility()
    {
        if (OverwriteSection is null) return;
        OverwriteSection.IsVisible = HasOverwriteTargets && (_databaseOnly || DestCombo.SelectedIndex == 0);
    }

    private void OnDestChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FilePathRow is null) return;
        FilePathRow.IsVisible = !_databaseOnly && DestCombo.SelectedIndex >= 1;
        UpdateOverwriteSectionVisibility();
    }

    private void OnModeChanged(object? sender, RoutedEventArgs e)
    {
        if (ExistingCombo is null || NameBox is null) return;

        bool overwriteSelected  = OverwriteSelectedRadio?.IsChecked == true;
        ExistingCombo.IsEnabled = overwriteSelected;

        // The name field follows the target: an overwrite renames the row it writes to,
        // so showing anything but that playlist's own name would be misleading.
        if (overwriteSelected)
            SyncNameFromSelection();
        else
            NameBox.Text = _initialName;
    }

    private void OnExistingSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OverwriteSelectedRadio?.IsChecked == true)
            SyncNameFromSelection();
    }

    private void SyncNameFromSelection()
    {
        if (ExistingCombo.SelectedItem is ExistingPlaylist p)
            NameBox.Text = p.Name;
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        bool isM3u = DestCombo.SelectedIndex == 2;

        var opts = new FilePickerSaveOptions
        {
            Title             = Localizer.Instance?["sp.picker_title"] ?? "Save playlist to a file",
            SuggestedFileName = NameBox.Text?.Trim() ?? "playlista",
            FileTypeChoices   = isM3u
                ? new List<FilePickerFileType>
                  {
                      new FilePickerFileType(Localizer.Instance?["playlist.ft_m3u"] ?? "M3U playlist (*.m3u)") { Patterns = new[] { "*.m3u" } },
                  }
                : new List<FilePickerFileType>
                  {
                      new FilePickerFileType(Localizer.Instance?["pbd.ft_rdpl"] ?? "RDM playlist (*.rdpl)") { Patterns = new[] { "*.rdpl" } },
                  }
        };
        var file = await StorageProvider.SaveFilePickerAsync(opts);
        if (file is not null)
            FilePathBox.Text = file.Path.LocalPath;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name)) return;

        bool toDatabase = _databaseOnly || DestCombo.SelectedIndex == 0;
        var filePath    = toDatabase ? null : FilePathBox.Text?.Trim();
        if (!toDatabase && string.IsNullOrEmpty(filePath)) return;

        string? overwriteId = null;
        if (toDatabase && HasOverwriteTargets)
        {
            if (OverwriteRadio.IsChecked == true && _loadedPlaylistId is not null)
            {
                overwriteId = _loadedPlaylistId;
            }
            else if (OverwriteSelectedRadio.IsChecked == true)
            {
                if (ExistingCombo.SelectedItem is not ExistingPlaylist target) return;  // nothing picked yet
                overwriteId = target.PlaylistId;
            }
        }

        Close(new SavePlaylistOptions(name, toDatabase, filePath, overwriteId));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
