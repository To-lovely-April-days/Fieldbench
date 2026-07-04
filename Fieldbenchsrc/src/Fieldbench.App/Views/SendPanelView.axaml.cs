using Avalonia.Controls;
using Avalonia.Input;
using Fieldbench.App.ViewModels;

namespace Fieldbench.App.Views;

public partial class SendPanelView : UserControl
{
    public SendPanelView()
    {
        InitializeComponent();
    }

    private void OnPayloadKeyDown(object? sender, KeyEventArgs e)
    {
        // Enter sends (PRD §6.3 shortcut).
        if (e.Key == Key.Enter && DataContext is SendPanelViewModel vm)
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
