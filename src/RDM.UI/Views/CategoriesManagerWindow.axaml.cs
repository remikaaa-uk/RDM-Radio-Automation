using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using RDM.UI.ViewModels;

namespace RDM.UI.Views;

public partial class CategoriesManagerWindow : Window
{
    public CategoriesManagerWindow()
    {
        InitializeComponent();
        var vm = App.Services.GetRequiredService<CategoriesManagerViewModel>();
        DataContext = vm;
        Opened += async (_, _) => await vm.LoadAsync();
    }

    private void OnOkClicked(object? sender, RoutedEventArgs e)     => Close();
    private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close();
}
