using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.ViewModels;
using System;
using System.Threading.Tasks;

namespace RDM.UI.Views;

/// <summary>
/// Shown after successful database bootstrap. Operator authenticates via AuthService.
///
/// Returns true  → login accepted; SuccessCredentials holds (username, password)
///                 for ApiClientService.SetCredentials() wiring in UI-001E.
/// Returns false → operator cancelled; App initiates shutdown.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    /// <summary>
    /// Set after successful login. Username and plaintext password are kept
    /// in memory so UI-001E can pass them to ApiClientService for Basic Auth.
    /// </summary>
    public (string Username, string Password)? SuccessCredentials { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();

        var vm = App.Services.GetRequiredService<LoginViewModel>();
        DataContext = vm;

        vm.LoginSuccessful += OnLoginSuccessful;
        vm.LoginCancelled  += OnLoginCancelled;
    }

    public Task<bool> WaitForResultAsync() => _tcs.Task;

    private void OnLoginSuccessful()
    {
        var vm = (LoginViewModel)DataContext!;
        SuccessCredentials = (vm.Username, vm.Password);
        _tcs.TrySetResult(true);
        Close();
    }

    private void OnLoginCancelled()
    {
        _tcs.TrySetResult(false);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Guard: operator closed via OS chrome without clicking Anuluj.
        _tcs.TrySetResult(false);
        base.OnClosed(e);
    }
}
