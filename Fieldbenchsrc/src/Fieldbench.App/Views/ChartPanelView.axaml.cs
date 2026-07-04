using Avalonia.Controls;
using Fieldbench.App.Controls;
using Fieldbench.App.ViewModels;

namespace Fieldbench.App.Views;

public partial class ChartPanelView : UserControl
{
    public ChartPanelView()
    {
        InitializeComponent();
        var plot = this.FindControl<FieldChart>("Plot");
        if (plot is not null)
        {
            plot.MarkerClicked += frame =>
            {
                if (DataContext is ChartPanelViewModel vm)
                {
                    vm.JumpToFrameCommand.Execute(frame);
                }
            };
        }
    }
}
