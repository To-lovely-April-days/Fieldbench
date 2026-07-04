using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Fieldbench.App.ViewModels;
using Fieldbench.App.Views;
using Fieldbench.Core.Profiles;
using Fieldbench.Core.Sessions;
using Xunit;

namespace Fieldbench.App.UITests;

/// <summary>
/// Renders the real main window with live demo data and saves PNGs for
/// visual comparison against the design package (light / dark / zh-Hans).
/// Screenshots land in artifacts/screenshots at the repo root.
/// </summary>
public class ScreenshotTests
{
    private static string OutDir()
    {
        var dir = Environment.GetEnvironmentVariable("FB_SHOT_DIR")
                  ?? Path.Combine(Path.GetTempPath(), "fb-screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void PumpFor(TimeSpan span)
    {
        var deadline = DateTime.UtcNow + span;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindowViewModel Vm, MainWindow Window, Workbench Wb) LaunchWithDemo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-shot-" + Guid.NewGuid().ToString("N")[..8]);
        var workbench = new Workbench(new SettingsStore(dir));
        workbench.Settings.AiPrivacyAccepted = true; // consent dialog needs a real window host
        var vm = new MainWindowViewModel(workbench);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 860 };
        window.Show();
        vm.RunDemoCommand.Execute(null);
        PumpFor(TimeSpan.FromSeconds(5));
        return (vm, window, workbench);
    }

    private static void Snap(MainWindow window, string name)
    {
        PumpFor(TimeSpan.FromMilliseconds(300));
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(OutDir(), name));
    }

    [AvaloniaFact]
    public void MainWindow_Light_Dark_And_Chinese()
    {
        var (vm, window, _) = LaunchWithDemo();

        // Select the CRC-failed frame if present (matches the design's hero state).
        var master = vm.Tabs[0];
        var bad = master.Frames.LastOrDefault(f => f.StatusTag == "CRC FAIL")
                  ?? master.Frames.LastOrDefault(f => f.IsAbnormal)
                  ?? master.Frames.LastOrDefault();
        if (bad is not null) master.OnSelectionChanged([bad]);

        // Offline AI explanation filled in, like the mock's right rail.
        master.Main.Workbench.AiClient = new Core.Ai.OfflineAiEngine { StreamDelay = TimeSpan.Zero };
        if (master.Ai.ExplainCommand.CanExecute(null)) master.Ai.ExplainCommand.Execute(null);
        PumpFor(TimeSpan.FromSeconds(1));

        Snap(window, "master-light.png");

        App.Current!.ApplyTheme("Dark");
        Snap(window, "master-dark.png");

        App.Current!.ApplyLanguage("zh");
        Snap(window, "master-dark-zh.png");

        App.Current!.ApplyTheme("Light");
        App.Current!.ApplyLanguage("en");

        // Poll tasks + chart tabs.
        master.MasterPanel!.ActiveTabIndex = 1;
        Snap(window, "master-polltasks.png");
        master.MasterPanel.ActiveTabIndex = 3;
        Snap(window, "master-chart.png");

        // Slave tab.
        vm.ActiveTab = vm.Tabs[1];
        Snap(window, "slave.png");

        window.Close();
    }

    [AvaloniaFact]
    public void Dialogs_Render()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-shot-" + Guid.NewGuid().ToString("N")[..8]);
        var workbench = new Workbench(new SettingsStore(dir));
        var vm = new MainWindowViewModel(workbench);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 860 };
        window.Show();
        PumpFor(TimeSpan.FromMilliseconds(300));

        var dlg = new NewConnectionWindow { DataContext = new NewConnectionViewModel(vm), Width = 492 };
        dlg.Show();
        PumpFor(TimeSpan.FromMilliseconds(400));
        using (var frame = dlg.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(OutDir(), "dialog-newconnection.png"));
        }

        dlg.Close();

        var settings = new SettingsWindow { DataContext = new SettingsViewModel(vm), Width = 1000, Height = 700 };
        settings.Show();
        PumpFor(TimeSpan.FromMilliseconds(400));
        using (var frame = settings.CaptureRenderedFrame())
        {
            frame!.Save(Path.Combine(OutDir(), "dialog-settings.png"));
        }

        settings.Close();
        window.Close();
    }
}
