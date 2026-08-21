using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

// ── Item VMs ──────────────────────────────────────────────────────────────────

public sealed class CategoryItemVm
{
    public string FormatId { get; }
    public string Name     { get; }
    public CategoryItemVm(string formatId, string name) { FormatId = formatId; Name = name; }
    public override string ToString() => Name;
}

public sealed class SubcategoryItemVm
{
    public string SubcategoryId { get; }
    public string FormatId      { get; }
    public string Name          { get; }
    public SubcategoryItemVm(string subcategoryId, string formatId, string name)
    {
        SubcategoryId = subcategoryId;
        FormatId      = formatId;
        Name          = name;
    }
    public override string ToString() => Name;
}

public sealed class GenreItemVm
{
    public string GenreId { get; }
    public string Name    { get; }
    public GenreItemVm(string genreId, string name) { GenreId = genreId; Name = name; }
    public override string ToString() => Name;
}

// ── Main ViewModel ────────────────────────────────────────────────────────────

public sealed partial class CategoriesManagerViewModel : ObservableObject
{
    private readonly ApiClientService                    _api;
    private readonly ILogger<CategoriesManagerViewModel> _logger;

    // ── Categories ────────────────────────────────────────────────────────────

    public ObservableCollection<CategoryItemVm> Categories { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCategoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameCategoryCommand))]
    private CategoryItemVm? _selectedCategory;

    [ObservableProperty] private string _newCategoryName    = "";
    [ObservableProperty] private string _renameCategoryText = "";

    // ── Sub Categories ────────────────────────────────────────────────────────

    public ObservableCollection<SubcategoryItemVm> SubCategories { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSubCategoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameSubCategoryCommand))]
    private SubcategoryItemVm? _selectedSubCategory;

    [ObservableProperty] private string _newSubCategoryName    = "";
    [ObservableProperty] private string _renameSubCategoryText = "";

    // ── Genres ────────────────────────────────────────────────────────────────

    public ObservableCollection<GenreItemVm> Genres { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteGenreCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameGenreCommand))]
    private GenreItemVm? _selectedGenre;

    [ObservableProperty] private string _newGenreName    = "";
    [ObservableProperty] private string _renameGenreText = "";

    // ── Status ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    // ── Constructor ───────────────────────────────────────────────────────────

    public CategoriesManagerViewModel(
        ApiClientService                    api,
        ILogger<CategoriesManagerViewModel> logger)
    {
        _api    = api;
        _logger = logger;
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var cats = await _api.GetCategoriesAsync();
            Categories.Clear();
            if (cats is not null)
                foreach (var c in cats)
                    Categories.Add(new CategoryItemVm(c.FormatId, c.Name));

            var genres = await _api.GetGenresAsync();
            Genres.Clear();
            if (genres is not null)
                foreach (var g in genres.Items)
                    Genres.Add(new GenreItemVm(g.GenreId, g.Name));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load categories failed");
            StatusMessage = Localizer.Instance?["cat.msg.load_error"] ?? "Error loading data";
        }
        finally { IsBusy = false; }
    }

    // ── Property change hooks ─────────────────────────────────────────────────

    partial void OnSelectedCategoryChanged(CategoryItemVm? value)
    {
        RenameCategoryText = value?.Name ?? "";
        _ = LoadSubCategoriesAsync(value?.FormatId);
    }

    partial void OnSelectedSubCategoryChanged(SubcategoryItemVm? value)
        => RenameSubCategoryText = value?.Name ?? "";

    partial void OnSelectedGenreChanged(GenreItemVm? value)
        => RenameGenreText = value?.Name ?? "";

    private async Task LoadSubCategoriesAsync(string? formatId)
    {
        SubCategories.Clear();
        SelectedSubCategory = null;
        if (formatId is null) return;

        IsBusy = true;
        try
        {
            var envelope = await _api.GetSubcategoriesAsync(formatId);
            if (envelope is not null)
                foreach (var s in envelope.Items)
                    SubCategories.Add(new SubcategoryItemVm(s.SubcategoryId, s.FormatId, s.Name));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Load subcategories failed"); }
        finally { IsBusy = false; }
    }

    // ── Category commands ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddCategoryAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCategoryName)) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var resp = await _api.CreateCategoryAsync(NewCategoryName.Trim());
            if (resp is not null)
            {
                var vm = new CategoryItemVm(resp.FormatId, resp.Name);
                Categories.Add(vm);
                SelectedCategory = vm;
                NewCategoryName  = "";
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Add category failed"); StatusMessage = Localizer.Instance?["cat.msg.add_category_error"] ?? "Error adding category"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedCategory))]
    private async Task DeleteCategoryAsync()
    {
        if (SelectedCategory is null) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var resp = await _api.DeleteCategoryAsync(SelectedCategory.FormatId);
            if (resp is null) { StatusMessage = Localizer.Instance?["cat.msg.delete_error"] ?? "Delete error"; return; }
            if (!resp.Deleted) { StatusMessage = resp.Reason ?? Localizer.Instance?["cat.msg.category_delete_blocked"] ?? "Cannot delete the category"; return; }
            Categories.Remove(SelectedCategory);
            SelectedCategory = null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Delete category failed"); StatusMessage = Localizer.Instance?["cat.msg.delete_category_error"] ?? "Error deleting category"; }
        finally { IsBusy = false; }
    }

    private bool HasSelectedCategory() => SelectedCategory is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedCategory))]
    private async Task RenameCategoryAsync()
    {
        if (SelectedCategory is null || string.IsNullOrWhiteSpace(RenameCategoryText)) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var newName = RenameCategoryText.Trim();
            var ok = await _api.RenameCategoryAsync(SelectedCategory.FormatId, newName);
            if (ok)
            {
                var idx   = Categories.IndexOf(SelectedCategory);
                var newVm = new CategoryItemVm(SelectedCategory.FormatId, newName);
                Categories[idx]  = newVm;
                SelectedCategory = newVm;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Rename category failed"); StatusMessage = Localizer.Instance?["cat.msg.rename_error"] ?? "Error renaming"; }
        finally { IsBusy = false; }
    }

    // ── SubCategory commands ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddSubCategoryAsync()
    {
        if (SelectedCategory is null || string.IsNullOrWhiteSpace(NewSubCategoryName)) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var resp = await _api.CreateSubcategoryAsync(SelectedCategory.FormatId, NewSubCategoryName.Trim());
            if (resp is not null)
            {
                var vm = new SubcategoryItemVm(resp.SubcategoryId, resp.FormatId, resp.Name);
                SubCategories.Add(vm);
                SelectedSubCategory = vm;
                NewSubCategoryName  = "";
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Add subcategory failed"); StatusMessage = Localizer.Instance?["cat.msg.add_subcategory_error"] ?? "Error adding subcategory"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSubCategory))]
    private async Task DeleteSubCategoryAsync()
    {
        if (SelectedSubCategory is null) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var resp = await _api.DeleteSubcategoryAsync(SelectedSubCategory.SubcategoryId);
            if (resp is null) { StatusMessage = Localizer.Instance?["cat.msg.delete_error"] ?? "Delete error"; return; }
            if (!resp.Deleted) { StatusMessage = resp.Reason ?? Localizer.Instance?["cat.msg.subcategory_delete_blocked"] ?? "Cannot delete the subcategory"; return; }
            SubCategories.Remove(SelectedSubCategory);
            SelectedSubCategory = null;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Delete subcategory failed"); StatusMessage = Localizer.Instance?["cat.msg.delete_subcategory_error"] ?? "Error deleting subcategory"; }
        finally { IsBusy = false; }
    }

    private bool HasSelectedSubCategory() => SelectedSubCategory is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedSubCategory))]
    private async Task RenameSubCategoryAsync()
    {
        if (SelectedSubCategory is null || string.IsNullOrWhiteSpace(RenameSubCategoryText)) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var newName = RenameSubCategoryText.Trim();
            var ok = await _api.RenameSubcategoryAsync(SelectedSubCategory.SubcategoryId, newName);
            if (ok)
            {
                var idx   = SubCategories.IndexOf(SelectedSubCategory);
                var newVm = new SubcategoryItemVm(SelectedSubCategory.SubcategoryId, SelectedSubCategory.FormatId, newName);
                SubCategories[idx]  = newVm;
                SelectedSubCategory = newVm;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Rename subcategory failed"); StatusMessage = Localizer.Instance?["cat.msg.rename_error"] ?? "Error renaming"; }
        finally { IsBusy = false; }
    }

    // ── Genre commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddGenreAsync()
    {
        if (string.IsNullOrWhiteSpace(NewGenreName)) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var resp = await _api.CreateGenreAsync(NewGenreName.Trim());
            if (resp is not null)
            {
                var vm = new GenreItemVm(resp.GenreId, resp.Name);
                Genres.Add(vm);
                SelectedGenre = vm;
                NewGenreName  = "";
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Add genre failed"); StatusMessage = Localizer.Instance?["cat.msg.add_genre_error"] ?? "Error adding genre"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGenre))]
    private async Task DeleteGenreAsync()
    {
        if (SelectedGenre is null) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var ok = await _api.DeleteGenreAsync(SelectedGenre.GenreId);
            if (ok)
            {
                Genres.Remove(SelectedGenre);
                SelectedGenre = null;
            }
            else { StatusMessage = Localizer.Instance?["cat.msg.delete_genre_error"] ?? "Error deleting genre"; }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Delete genre failed"); StatusMessage = Localizer.Instance?["cat.msg.delete_genre_error"] ?? "Error deleting genre"; }
        finally { IsBusy = false; }
    }

    private bool HasSelectedGenre() => SelectedGenre is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedGenre))]
    private async Task RenameGenreAsync()
    {
        if (SelectedGenre is null || string.IsNullOrWhiteSpace(RenameGenreText)) return;
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var newName = RenameGenreText.Trim();
            var ok = await _api.RenameGenreAsync(SelectedGenre.GenreId, newName);
            if (ok)
            {
                var idx   = Genres.IndexOf(SelectedGenre);
                var newVm = new GenreItemVm(SelectedGenre.GenreId, newName);
                Genres[idx]   = newVm;
                SelectedGenre = newVm;
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Rename genre failed"); StatusMessage = Localizer.Instance?["cat.msg.rename_error"] ?? "Error renaming"; }
        finally { IsBusy = false; }
    }
}
