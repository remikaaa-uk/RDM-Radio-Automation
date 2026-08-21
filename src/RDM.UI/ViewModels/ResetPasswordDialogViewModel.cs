using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDM.Core.Services;
using RDM.UI.Localization;

namespace RDM.UI.ViewModels;

/// <summary>Backs <see cref="Views.ResetPasswordDialog"/> — admin sets a new password for someone else's account, no old password needed.</summary>
public sealed partial class ResetPasswordDialogViewModel : ObservableObject
{
    public string TargetUsername { get; }

    [ObservableProperty] private string _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = "";

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ResetPasswordDialogViewModel(string targetUsername)
    {
        TargetUsername = targetUsername;
    }

    [RelayCommand]
    private void Generate() => Password = PasswordGenerator.Generate(12);

    /// <summary>Validates the form and returns the new password, or sets <see cref="ErrorMessage"/> and returns null.</summary>
    public string? TryGetPassword()
    {
        ErrorMessage = "";

        if (string.IsNullOrEmpty(Password) || Password.Length < 8)
        {
            ErrorMessage = Localizer.Instance?["chpw.err.too_short"] ?? "Password must be at least 8 characters.";
            return null;
        }

        return Password;
    }
}
