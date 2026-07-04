using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Fieldbench.App.Views;

public partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    public ConfirmWindow(string title, string message, string accept, string cancel) : this()
    {
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
        this.FindControl<Button>("AcceptBtn")!.Content = accept;
        this.FindControl<Button>("CancelBtn")!.Content = cancel;
    }

    public bool Accepted { get; private set; }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
