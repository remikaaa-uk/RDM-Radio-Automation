using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RDM.Core.Services;
using RDM.Shared.Enums;
using RDM.UI.Localization;
using System.Collections.Generic;

namespace RDM.UI.ViewModels;

/// <summary>Backs <see cref="Views.UserEditDialog"/> — collects the fields for a new account.</summary>
public sealed partial class NewUserDialogViewModel : ObservableObject
{
    public IReadOnlyList<UserRole> Roles { get; } = [UserRole.Operator, UserRole.Admin];

    [ObservableProperty] private string   _username = "";
    [ObservableProperty] private UserRole _selectedRole = UserRole.Operator;
    [ObservableProperty] private string   _password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = "";

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [RelayCommand]
    private void Generate() => Password = PasswordGenerator.Generate(12);

    /// <summary>Validates the form and builds the request, or sets <see cref="ErrorMessage"/> and returns null.</summary>
    public NewUserRequest? TryBuildRequest()
    {
        ErrorMessage = "";

        if (string.IsNullOrWhiteSpace(Username))
        {
            ErrorMessage = Localizer.Instance?["users.err.username_required"] ?? "Username is required.";
            return null;
        }

        if (string.IsNullOrEmpty(Password) || Password.Length < 8)
        {
            ErrorMessage = Localizer.Instance?["chpw.err.too_short"] ?? "Password must be at least 8 characters.";
            return null;
        }

        return new NewUserRequest(Username.Trim(), SelectedRole, Password);
    }
}
