using Avalonia.Controls;
using Avalonia.Threading;
using Fieldbench.App.ViewModels;

namespace Fieldbench.App.Views;

public partial class MainWindow : Window, IDialogHost
{
    private bool _firstRunShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.DialogHost = this;
            }
        };
        Opened += (_, _) =>
        {
            // First run: no empty dashboard — go straight to New connection (PRD S0, TTFF < 60s).
            if (!_firstRunShown && DataContext is MainWindowViewModel { Workbench.IsFirstRun: true } vm)
            {
                _firstRunShown = true;
                Dispatcher.UIThread.Post(async () => await ShowNewConnectionAsync(vm), DispatcherPriority.Background);
            }
        };
    }

    public async Task ShowNewConnectionAsync(MainWindowViewModel main)
    {
        var dialog = new NewConnectionWindow { DataContext = new NewConnectionViewModel(main) };
        await dialog.ShowDialog(this);
    }

    public async Task ShowSettingsAsync(MainWindowViewModel main)
    {
        var dialog = new SettingsWindow { DataContext = new SettingsViewModel(main) };
        await dialog.ShowDialog(this);
    }

    public async Task ShowScanAsync(SessionViewModel session)
    {
        var dialog = new ScanWindow { DataContext = new ScanViewModel(session) };
        await dialog.ShowDialog(this);
    }

    public async Task ShowImportAsync(SessionViewModel session)
    {
        var dialog = new ImportReviewWindow { DataContext = new ImportReviewViewModel(session) };
        await dialog.ShowDialog(this);
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new MessageWindow(title, message);
        await dialog.ShowDialog(this);
    }

    public async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        var dialog = new ConfirmWindow(title, message, accept, cancel);
        await dialog.ShowDialog(this);
        return dialog.Accepted;
    }
}
