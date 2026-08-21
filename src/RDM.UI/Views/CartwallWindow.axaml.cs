using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.Services;
using RDM.UI.ViewModels;

namespace RDM.UI.Views;

public partial class CartwallWindow : Window
{
    public CartwallWindow()
    {
        InitializeComponent();

        var vm  = App.Services.GetRequiredService<MainViewModel>();
        var api = App.Services.GetRequiredService<ApiClientService>();
        DataContext = vm.CartwallViewModel;

        CartwallDialogHost.Wire(this, vm.CartwallViewModel, api);

        // Prevent closing — hide instead so state is preserved
        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };
    }
}
