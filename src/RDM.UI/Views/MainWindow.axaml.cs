using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class MainWindow : Window
{
    private bool _shutdownStarted;
    private readonly ILogger<MainWindow>       _logger;
    private readonly IKeyboardInputDriver      _keyboardDriver;

    public MainWindow()
    {
        InitializeComponent();

        _logger         = App.Services.GetRequiredService<ILogger<MainWindow>>();
        _keyboardDriver = App.Services.GetRequiredService<IKeyboardInputDriver>();

        var vm = App.Services.GetRequiredService<MainViewModel>();
        DataContext = vm;

        vm.PlayerViewModel.Activate();
        vm.PlaylistViewModel.Activate();
        vm.LibraryViewModel.Activate();
        vm.HistoryViewModel.Activate();
        vm.CartwallViewModel.Activate();
        vm.AuxPlayersViewModel.Activate();

        var settings = App.Services.GetRequiredService<RDM.Core.Entities.AudioSettings>();
        if (settings.CartwallSeparateWindow)
        {
            CartwallTab.IsVisible = false;
            Opened += (_, _) =>
            {
                var win = new CartwallWindow();
                win.Show(this);
            };
        }
        else
        {
            var api = App.Services.GetRequiredService<ApiClientService>();
            CartwallDialogHost.Wire(this, vm.CartwallViewModel, api);
        }

        vm.AuxPlayersViewModel.PickAudioFileAsync = async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title         = Localizer.Instance?["main.picker.audio"] ?? "Select an audio file",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(Localizer.Instance?["common.ft.audio"] ?? "Audio files")
                    {
                        Patterns = ["*.mp3", "*.wav", "*.flac", "*.ogg", "*.m4a", "*.aac", "*.wma"]
                    },
                    new FilePickerFileType(Localizer.Instance?["common.ft.all"] ?? "All files") { Patterns = ["*.*"] }
                ]
            });

            var file = files is { Count: > 0 } ? files[0] : null;
            return file?.TryGetLocalPath();
        };
    }

    // ── Module button clicks ──────────────────────────────────────────────────

    private void OnTracksManagerClicked(object? sender, RoutedEventArgs e)
        => App.Services.GetRequiredService<INavigationService>()
               .OpenOrFocusWindow<TracksManagerWindow>();

    private void OnPlaylistBuilderClicked(object? sender, RoutedEventArgs e)
        => App.Services.GetRequiredService<INavigationService>()
               .OpenOrFocusWindow<PlaylistBuilderWindow>();

    private void OnScheduledEventsClicked(object? sender, RoutedEventArgs e)
        => App.Services.GetRequiredService<INavigationService>()
               .OpenOrFocusWindow<ScheduledEventsWindow>();

    // Refresh the sweeper-subcategory list when opened, so it reflects any change made
    // meanwhile via the CHANGE_SWEEPER_SUBCATEGORY event action or a sweeper-format change.
    private void OnSweeperSubcategoryDropDownOpened(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            _ = vm.SweeperSubcategory.ReloadAsync();
    }

    private void OnHardwareManagerClicked(object? sender, RoutedEventArgs e)
        => App.Services.GetRequiredService<INavigationService>()
               .OpenOrFocusWindow<HardwareManagerWindow>();

    private async void OnChangePasswordClicked(object? sender, RoutedEventArgs e)
        => await new ChangePasswordDialog().ShowDialog<bool>(this);

    /// <summary>
    /// "Switch user" re-authenticates in place via the same LoginWindow used at startup, instead of
    /// tearing down MainWindow — the audio engine, WebSocket and hardware action system are already
    /// running singletons and would otherwise need to be restarted. LoginViewModel already logs the
    /// new user into UserSessionContext itself; only the API credentials need re-wiring here, exactly
    /// mirroring App.axaml.cs' own post-login step.
    /// </summary>
    private async void OnSwitchUserClicked(object? sender, RoutedEventArgs e)
    {
        var loginWindow = new LoginWindow();
        var dialogTask = loginWindow.ShowDialog(this);
        var loggedIn = await loginWindow.WaitForResultAsync();
        await dialogTask;

        if (loggedIn && loginWindow.SuccessCredentials is var (u, p))
        {
            App.Services.GetRequiredService<ApiClientService>().SetCredentials(u, p);
        }
    }


    private async void OnSettingsClicked(object? sender, RoutedEventArgs e)
    {
        await App.Services.GetRequiredService<INavigationService>()
                 .ShowModalAsync<SettingsWindow>();

        // Settings may have toggled "enable automatic sweepers" or changed the sweeper format —
        // refresh the bottom-bar control's visibility and options. The same applies to the
        // streaming/recording module switches, which decide whether their buttons exist at all.
        if (DataContext is MainViewModel vm)
        {
            await vm.SweeperSubcategory.ReloadAsync();
            await vm.RefreshOutputModulesAsync();
        }
    }

    private void OnFullscreenClicked(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;
    }

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Don't intercept when a TextBox has keyboard focus — user is typing.
        if (e.Source is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        var mods = e.KeyModifiers;
        _keyboardDriver.HandleKeyDown(
            e.Key.ToString(),
            altPressed:   mods.HasFlag(KeyModifiers.Alt),
            ctrlPressed:  mods.HasFlag(KeyModifiers.Control),
            shiftPressed: mods.HasFlag(KeyModifiers.Shift));

        base.OnKeyDown(e);
    }

    // ── Closing guard ─────────────────────────────────────────────────────────

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_shutdownStarted)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _ = RunShutdownAsync();
    }

    private async Task RunShutdownAsync()
    {
        var vm = (MainViewModel)DataContext!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            await vm.ShutdownSequenceAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Shutdown timeout (30 s) exceeded — forcing close");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during shutdown sequence");
        }
        finally
        {
            _shutdownStarted = true;
            await Dispatcher.UIThread.InvokeAsync(Close);
        }
    }
}
