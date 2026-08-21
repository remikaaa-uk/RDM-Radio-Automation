using Avalonia.Controls;
using RDM.UI.Localization;
using RDM.UI.Services;
using RDM.UI.ViewModels;
using System.Threading.Tasks;

namespace RDM.UI.Views;

/// <summary>
/// Wires <see cref="CartwallViewModel"/>'s dialog callbacks to a host window.
/// The cartwall lives either in a MainWindow tab or in a standalone CartwallWindow
/// (<c>AudioSettings.CartwallSeparateWindow</c>); whichever hosts it owns the Window that
/// <c>ShowDialog</c> needs, so both route through here.
/// </summary>
internal static class CartwallDialogHost
{
    public static void Wire(Window host, CartwallViewModel vm, ApiClientService api)
    {
        vm.ShowAssetPickerAsync = () =>
            new CartAssetPickerDialog(api).ShowDialog<AssetPickerRow?>(host);

        vm.ShowRenameLabelAsync = current => Prompt(host,
            Tr("cartwall.rename_label.title",  "Change label"),
            Tr("cartwall.rename_label.prompt", "New slot label:"),
            current);

        vm.ShowChangeColorAsync = current => Prompt(host,
            Tr("cartwall.change_color.title",  "Change color"),
            Tr("cartwall.change_color.prompt", "Hex color (e.g. #FF6600), empty = default:"),
            current ?? "");

        vm.ShowCreateTabAsync = () => Prompt(host,
            Tr("cartwall.new_tab.title",  "New tab"),
            Tr("cartwall.new_tab.prompt", "Tab name:"),
            "");

        vm.ShowRenameTabAsync = current => Prompt(host,
            Tr("cartwall.rename_tab.title",  "Rename tab"),
            Tr("cartwall.rename_tab.prompt", "New name:"),
            current);
    }

    private static Task<string?> Prompt(Window host, string title, string prompt, string initialValue) =>
        new SimpleInputDialog(title, prompt, initialValue).ShowDialog<string?>(host);

    private static string Tr(string key, string fallback) => Localizer.Instance?[key] ?? fallback;
}
