using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RDM.UI.Views;

public partial class MicDspChainWindow : Window
{
    private readonly ILogger<MicDspChainWindow> _logger;

    public MicDspChainWindow()
    {
        InitializeComponent();

        _logger = App.Services.GetRequiredService<ILogger<MicDspChainWindow>>();
        _logger.LogInformation("MicDspChainWindow: constructor — InitializeComponent done. Content is null={ContentNull}, type={ContentType}",
            Content is null, Content?.GetType().Name ?? "<null>");

        var vm = App.Services.GetRequiredService<MicDspChainViewModel>();
        DataContext = vm;
        vm.ShowVstEditorAsync = ShowVstEditorAsync;

        BrowseVstButton.Click += OnBrowseVstClicked;

        Closed += (_, _) =>
        {
            foreach (var editor in _editors.Values.ToList())
                editor.Close();
            vm.Dispose();
        };
    }

    /// One editor window per VST slot: reopening a plugin that already has one just brings it
    /// forward, because BASS_VST refuses to embed an editor that is already open.
    private readonly Dictionary<int, VstEditorWindow> _editors = new();

    private MicDspChainViewModel? ViewModel => DataContext as MicDspChainViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        _logger.LogInformation(
            "MicDspChainWindow.OnOpened: hwnd={Hwnd}, ClientSize={CW}x{CH}, Bounds={BW}x{BH}, " +
            "IsVisible={Vis}, Background={Bg}, Content={Content}, DataContext={Dc}",
            hwnd, ClientSize.Width, ClientSize.Height, Bounds.Width, Bounds.Height,
            IsVisible, Background?.ToString() ?? "<null>",
            Content?.GetType().Name ?? "<null>", DataContext?.GetType().Name ?? "<null>");

        if (ViewModel is { } vm)
        {
            vm.Activate();
        }
        else
        {
            _logger.LogWarning("MicDspChainWindow.OnOpened: ViewModel is null!");
        }
    }

    private Task<string?> ShowVstEditorAsync(MicVstRowViewModel row)
    {
        if (_editors.TryGetValue(row.SlotId, out var existing))
        {
            _logger.LogInformation("ShowVstEditor: slot {SlotId} already has an editor window — activating it", row.SlotId);
            existing.Activate();
            return Task.FromResult<string?>(null);
        }

        var editor = new VstEditorWindow(
            row.SlotId,
            row.PluginName,
            App.Services.GetRequiredService<IAudioEngine>(),
            App.Services.GetRequiredService<ILogger<VstEditorWindow>>());

        _editors[row.SlotId] = editor;
        editor.Closed += (_, _) =>
        {
            _editors.Remove(row.SlotId);
            // Whatever was changed in the plugin's own GUI is only readable while it is loaded,
            // so capture and persist it now rather than at shutdown.
            _ = App.Services.GetRequiredService<MicDspChainStore>().SaveAsync();
        };

        editor.Show(this);
        return editor.EmbedResult;
    }

    private async void OnBrowseVstClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = Localizer.Instance?["md.picker.vst"] ?? "Select a VST 2.x plugin (.dll)",
            AllowMultiple  = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new FilePickerFileType(Localizer.Instance?["md.ft.vst"] ?? "VST 2.x plugin (*.dll)")
                {
                    Patterns = new[] { "*.dll" }
                }
            }
        });

        if (files.Count > 0)
            ViewModel.VstPathInput = files[0].Path.LocalPath;
    }
}
