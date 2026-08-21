using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using RDM.UI.Localization;
using RDM.UI.Services;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.ViewModels;

public partial class DatabaseSetupViewModel : ObservableObject
{
    private readonly ILogger<DatabaseSetupViewModel> _logger;
    private CancellationTokenSource? _testCts;

    // ── Form fields ───────────────────────────────────────────────────────────

    [ObservableProperty] private string _server       = "localhost";
    [ObservableProperty] private string _port         = "3306";
    [ObservableProperty] private string _user         = "";
    [ObservableProperty] private string _password     = "";
    [ObservableProperty] private string _databaseName = "rdm";

    // ── Status ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _statusText  = Localizer.Instance?["db.status.not_connected"] ?? "Not connected";
    [ObservableProperty] private IBrush _statusBrush = Brushes.Gray;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTesting;

    // Locked to true after a successful test + save — prevents re-testing
    // (operator must restart to apply the saved config).
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isConnectionVerified;

    // ── Public event ──────────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the operator is done with this window (cancel or post-save dismiss).
    /// The window subscribes to this and calls Close(); the startup sequence receives false
    /// from WaitForResultAsync() and initiates shutdown (correct for both paths).
    /// </summary>
    public event Action? Dismissed;

    // ── Constructor ───────────────────────────────────────────────────────────

    public DatabaseSetupViewModel(
        IConfiguration                    configuration,
        ILogger<DatabaseSetupViewModel>   logger)
    {
        _logger = logger;

        // Pre-populate from the current (failing) config so operator sees what is wrong.
        _server       = configuration["database:host"]     ?? "localhost";
        _port         = configuration["database:port"]     ?? "3306";
        _user         = configuration["database:username"] ?? "";
        _password     = configuration["database:password"] ?? "";
        _databaseName = configuration["database:name"]     ?? "rdm";
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by DatabaseSetupWindow to surface the original MySqlException message
    /// before the operator has made any changes.
    /// </summary>
    public void SetConnectionError(string error)
    {
        StatusText  = error;
        StatusBrush = Brushes.Salmon;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private bool CanTestConnection() => !IsTesting && !IsConnectionVerified;

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        _testCts    = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        IsTesting   = true;
        StatusText  = Localizer.Instance?["db.status.connecting"] ?? "Connecting…";
        StatusBrush = new SolidColorBrush(Color.Parse("#FF9800")); // orange

        try
        {
            if (!uint.TryParse(Port, out var portNum) || portNum == 0 || portNum > 65535)
            {
                StatusText  = Localizer.Instance?["db.err.invalid_port"] ?? "Error: invalid port number (1–65535).";
                StatusBrush = Brushes.Salmon;
                return;
            }

            var connString = new MySqlConnectionStringBuilder
            {
                Server            = Server,
                Port              = portNum,
                UserID            = User,
                Password          = Password,
                CharacterSet      = "utf8mb4",
                ConnectionTimeout = 8
            }.ConnectionString;

            await using (var conn = new MySqlConnection(connString))
                await conn.OpenAsync(_testCts.Token);

            // Connection OK — persist to rdm.config.json.
            await SaveConfigAsync();

            StatusText           = Localizer.Instance?["db.status.connected_saved"]
                                   ?? "Connected successfully. Configuration saved.\nRestart the application to apply the changes.";
            StatusBrush          = new SolidColorBrush(Color.Parse("#4CAF50")); // green
            IsConnectionVerified = true;
        }
        catch (OperationCanceledException)
        {
            StatusText  = Localizer.Instance?["db.err.timeout"] ?? "Error: connection timed out (8 s).";
            StatusBrush = Brushes.Salmon;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DatabaseSetupViewModel: connection test failed");
            StatusText  = string.Format(Localizer.Instance?["db.err.generic"] ?? "Error: {0}", ex.Message);
            StatusBrush = Brushes.Salmon;
        }
        finally
        {
            IsTesting = false;
            _testCts?.Dispose();
            _testCts = null;
        }
    }

    /// <summary>
    /// Closes the window. Works both as Cancel (no save) and as post-save dismiss.
    /// Always signals App to initiate shutdown — operator must restart to use new config.
    /// </summary>
    [RelayCommand]
    private void Dismiss()
    {
        _testCts?.Cancel();
        Dismissed?.Invoke();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task SaveConfigAsync()
    {
        var configPath = ConfigPaths.FilePath;

        var json = await File.ReadAllTextAsync(configPath);
        var node = JsonNode.Parse(json)
                   ?? throw new InvalidOperationException("rdm.config.json is empty or invalid.");

        node["database"]!["host"]     = Server;
        node["database"]!["port"]     = int.Parse(Port);
        node["database"]!["name"]     = DatabaseName;
        node["database"]!["username"] = User;
        node["database"]!["password"] = Password;

        await File.WriteAllTextAsync(configPath,
            node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        _logger.LogInformation(
            "rdm.config.json updated — database:host={Host} database:name={Db}", Server, DatabaseName);
    }
}
