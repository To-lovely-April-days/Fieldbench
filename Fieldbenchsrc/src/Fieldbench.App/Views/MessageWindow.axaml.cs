using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Fieldbench.App.Views;

public partial class MessageWindow : Window
{
    public MessageWindow()
    {
        InitializeComponent();
    }

    public MessageWindow(string title, string message) : this()
    {
        Title = title;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close();
}
