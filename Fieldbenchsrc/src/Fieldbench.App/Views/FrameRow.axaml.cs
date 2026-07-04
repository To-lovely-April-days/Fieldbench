using Avalonia.Controls;
using Avalonia.Interactivity;
using Fieldbench.App.ViewModels;
using Fieldbench.Core.Lenses;

namespace Fieldbench.App.Views;

public partial class FrameRow : UserControl
{
    public FrameRow()
    {
        InitializeComponent();
    }

    private async void OnAiClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not Frame frame) return;
        var center = this.FindAncestorOfType<SessionCenterView>();
        if (center?.DataContext is SessionViewModel vm)
        {
            await vm.Ai.ExplainFrameAsync(frame);
        }
    }
}

internal static class AncestorFinder
{
    public static T? FindAncestorOfType<T>(this Control control) where T : Control
    {
        var current = control.Parent;
        while (current is not null)
        {
            if (current is T match) return match;
            current = current.Parent;
        }

        return null;
    }
}
