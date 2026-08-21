using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.ViewModels;
using System.Threading.Tasks;

namespace RDM.UI.Views;

/// <summary>
/// Shown when DatabaseBootstrapper.RunAsync() fails with a MySqlException.
/// Operator fills in correct connection details, tests them, and (on success)
/// the new config is saved to rdm.config.json.
///
/// WaitForResultAsync() always returns false — both cancel and post-save dismiss
/// signal App to shut down so the operator can restart with the new config (A4 decision).
/// </summary>
public partial class DatabaseSetupWindow : Window
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    // Parameterless ctor required by Avalonia XAML loader.
    public DatabaseSetupWindow() : this(string.Empty) { }

    public DatabaseSetupWindow(string errorMessage)
    {
        InitializeComponent();

        var vm = App.Services.GetRequiredService<DatabaseSetupViewModel>();

        if (!string.IsNullOrEmpty(errorMessage))
            vm.SetConnectionError(errorMessage);

        DataContext = vm;
        vm.Dismissed += OnDismissed;
    }

    public Task<bool> WaitForResultAsync() => _tcs.Task;

    private void OnDismissed()
    {
        _tcs.TrySetResult(false);
        Close();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        // Guard: if closed via OS chrome (Alt+F4) without clicking Zamknij.
        _tcs.TrySetResult(false);
        base.OnClosed(e);
    }
}
