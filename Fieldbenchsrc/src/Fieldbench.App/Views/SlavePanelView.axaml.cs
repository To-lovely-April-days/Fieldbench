using Avalonia.Controls;
using Avalonia.Input;
using Fieldbench.App.ViewModels;

namespace Fieldbench.App.Views;

public partial class SlavePanelView : UserControl
{
    public SlavePanelView()
    {
        InitializeComponent();
    }

    private void OnEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not SlaveRowViewModel row) return;
        if (e.Key == Key.Enter)
        {
            row.CommitEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.CancelEditCommand.Execute(null);
            e.Handled = true;
        }
    }
}
