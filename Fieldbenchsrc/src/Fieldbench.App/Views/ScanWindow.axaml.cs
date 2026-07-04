using Avalonia.Controls;
using Avalonia.Interactivity;
using Fieldbench.App.ViewModels;

namespace Fieldbench.App.Views;

public partial class ScanWindow : Window
{
    public ScanWindow()
    {
        InitializeComponent();
        Closing += (_, _) => (DataContext as ScanViewModel)?.Stop();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ScanViewModel vm)
            {
                vm.OpenAsMasterRequested += _ => Close();
            }
        };
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
