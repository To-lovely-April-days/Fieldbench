using Avalonia.Controls;
using Avalonia.Interactivity;
using Fieldbench.App.ViewModels;

namespace Fieldbench.App.Views;

public partial class ImportReviewWindow : Window
{
    public ImportReviewWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ImportReviewViewModel vm)
            {
                vm.Completed += _ => Close();
            }
        };
    }

    private async void OnPasteImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImportReviewViewModel vm || Clipboard is null) return;
        try
        {
            var formats = await Clipboard.GetFormatsAsync();
            foreach (var format in new[] { "PNG", "image/png", "image/bmp", "DeviceIndependentBitmap" })
            {
                if (!formats.Contains(format)) continue;
                var data = await Clipboard.GetDataAsync(format);
                if (data is byte[] bytes && bytes.Length > 0)
                {
                    vm.SetImage(bytes);
                    return;
                }
            }
        }
        catch
        {
            // Platform clipboard without image support — the text path still works.
        }
    }
}
