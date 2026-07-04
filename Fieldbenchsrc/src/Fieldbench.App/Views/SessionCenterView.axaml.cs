using Avalonia.Controls;
using Avalonia.Threading;
using Fieldbench.App.ViewModels;
using Fieldbench.Core.Lenses;

namespace Fieldbench.App.Views;

public partial class SessionCenterView : UserControl
{
    private SessionViewModel? _vm;

    public SessionCenterView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Rebind();
    }

    private void Rebind()
    {
        if (_vm is not null)
        {
            _vm.TailChanged -= OnTailChanged;
            _vm.JumpRequested -= OnJumpRequested;
        }

        _vm = DataContext as SessionViewModel;
        if (_vm is not null)
        {
            _vm.TailChanged += OnTailChanged;
            _vm.JumpRequested += OnJumpRequested;
        }
    }

    private void OnTailChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var list = this.FindControl<ListBox>("TimelineList");
            if (list is null || _vm is null || _vm.Frames.Count == 0) return;
            // Follow the tail unless the user selected something above it.
            if (list.SelectedItems is { Count: > 0 }) return;
            list.ScrollIntoView(_vm.Frames.Count - 1);
        }, DispatcherPriority.Background);
    }

    private void OnJumpRequested(Frame frame)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var list = this.FindControl<ListBox>("TimelineList");
            if (list is null || _vm is null) return;
            int index = _vm.Frames.IndexOf(frame);
            if (index < 0) return;
            list.SelectedIndex = index;
            list.ScrollIntoView(index);
        });
    }

    private void OnTimelineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_vm is null || sender is not ListBox list) return;
        var selected = list.SelectedItems?.OfType<Frame>().ToArray() ?? [];
        _vm.OnSelectionChanged(selected);
    }
}
