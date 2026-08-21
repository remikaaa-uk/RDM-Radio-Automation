using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

/// <summary>An entry in the sweeper-subcategory picker. <see cref="Id"/> null = whole category.</summary>
public sealed record SubcategoryOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Bottom-bar quick control for the active sweeper subcategory (pool filter). Lists the
/// subcategories of the currently active sweeper format plus "Wszystkie" (= null, randomize
/// across the whole category), and persists the choice to <c>audio_settings.sweeper_subcategory_id</c>.
///
/// The same field is also written by the CHANGE_SWEEPER_SUBCATEGORY scheduled-event action,
/// so <see cref="ReloadAsync"/> should be called when the picker is opened to reflect changes
/// made in the meantime.
/// </summary>
public sealed partial class SweeperSubcategoryViewModel : ObservableObject
{
    private const string AllLabel = "Wszystkie";

    private readonly IAudioSettingsRepository _settingsRepo;
    private readonly ISubcategoryRepository   _subcategoryRepo;
    private readonly StudioContext            _studio;
    private readonly ILogger<SweeperSubcategoryViewModel> _logger;

    // Guards the selection setter from persisting while ReloadAsync repopulates the list.
    private bool _suppressSave;

    public ObservableCollection<SubcategoryOption> Options { get; } = new();

    [ObservableProperty] private SubcategoryOption? _selected;

    // False when no sweeper format is configured — the picker is then disabled.
    [ObservableProperty] private bool _isAvailable;

    // Mirrors AudioSettings.SweeperEnabled (Auto DJ "enable automatic sweepers"): the whole
    // bottom-bar control is hidden when automatic sweepers are turned off.
    [ObservableProperty] private bool _isSweeperEnabled;

    public SweeperSubcategoryViewModel(
        IAudioSettingsRepository settingsRepo,
        ISubcategoryRepository   subcategoryRepo,
        StudioContext            studio,
        ILogger<SweeperSubcategoryViewModel> logger)
    {
        _settingsRepo    = settingsRepo;
        _subcategoryRepo = subcategoryRepo;
        _studio          = studio;
        _logger          = logger;
    }

    /// <summary>(Re)loads the options and current selection from the active sweeper format.</summary>
    public async Task ReloadAsync()
    {
        try
        {
            var settings = await _settingsRepo.GetByStudioAsync(_studio.StudioId);

            IsSweeperEnabled = settings?.SweeperEnabled ?? false;

            _suppressSave = true;
            Options.Clear();
            Options.Add(new SubcategoryOption(null, AllLabel));

            if (settings?.SweeperFormatId is not null)
            {
                var subs = await _subcategoryRepo.GetByFormatIdAsync(settings.SweeperFormatId);
                foreach (var s in subs.OrderBy(s => s.SortOrder).ThenBy(s => s.Name))
                    Options.Add(new SubcategoryOption(s.SubcategoryId, s.Name));
                IsAvailable = true;
            }
            else
            {
                IsAvailable = false;
            }

            Selected = Options.FirstOrDefault(o => o.Id == settings?.SweeperSubcategoryId)
                       ?? Options[0];
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Sweeper subcategory reload failed"); }
        finally { _suppressSave = false; }
    }

    partial void OnSelectedChanged(SubcategoryOption? value)
    {
        if (_suppressSave || value is null) return;
        _ = SaveAsync(value.Id);
    }

    private async Task SaveAsync(string? subcategoryId)
    {
        try
        {
            var settings = await _settingsRepo.GetByStudioAsync(_studio.StudioId);
            if (settings is null) return;
            await _settingsRepo.UpdateAsync(settings with { SweeperSubcategoryId = subcategoryId });
            _logger.LogInformation("Sweeper subcategory set to {SubId}", subcategoryId ?? "(all)");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Sweeper subcategory save failed"); }
    }
}
