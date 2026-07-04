using Avalonia.Controls;
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
/// Drives the real UI headlessly: run the demo (loopback master ↔ slave),
/// let the poll engine produce traffic, and assert the whole pipeline —
/// timeline, register grid, chart channels, AI panel — carries live data.
/// </summary>
public class DemoEndToEndTests
{
    private static Workbench NewWorkbench()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fb-uitest-" + Guid.NewGuid().ToString("N")[..8]);
        var workbench = new Workbench(new SettingsStore(dir));
        workbench.Settings.AiPrivacyAccepted = true; // consent dialog needs a real window host
        return workbench;
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

    [AvaloniaFact]
    public void Demo_ProducesFramesRegistersAndAiExplanation()
    {
        var workbench = NewWorkbench();
        var vm = new MainWindowViewModel(workbench);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.RunDemoCommand.Execute(null);
        PumpFor(TimeSpan.FromSeconds(4));

        // Two tabs: demo master + demo slave.
        Assert.Equal(2, vm.Tabs.Count);
        var master = vm.Tabs[0];
        Assert.True(master.IsMaster);

        // Timeline carries parsed frames with tokens and Δ pairing.
        Assert.True(master.Frames.Count > 4, $"expected frames, got {master.Frames.Count}");
        Assert.Contains(master.Frames, f => f.FunctionToken == "FC03");
        Assert.Contains(master.Frames, f => f.DeltaMs is > 0);

        // The out-of-map poll produced EXC 02 warnings within the first seconds…
        PumpFor(TimeSpan.FromSeconds(2));
        Assert.Contains(master.Frames, f => f.StatusTag != null && f.StatusTag.StartsWith("EXC"));

        // Register grid updates from poll results (sine boiler temp in 15..35 °C).
        var tempRow = master.MasterPanel!.Registers.FirstOrDefault(r => r.Name == "temp_pv");
        Assert.NotNull(tempRow);
        Assert.NotNull(tempRow!.Tag.ScaledValue);
        Assert.InRange(tempRow.Tag.ScaledValue!.Value, 10, 40);

        // Chart channels exist and carry history.
        Assert.True(master.MasterPanel.Chart.Channels.Count >= 2);
        Assert.True(master.MasterPanel.Chart.Channels[0].Samples(TimeSpan.MaxValue).Count > 2);

        // Byte inspector renders the selected frame with colored fields.
        var frame = master.Frames.First(f => f.FunctionToken == "FC03");
        master.OnSelectionChanged([frame]);
        Assert.True(master.Inspector.Bytes.Count > 0);
        Assert.True(master.Inspector.Fields.Count > 0);

        // Offline AI explain produces a structured verdict for the selection.
        ((Core.Ai.GatewayAiClient)workbench.AiClient).Dispose();
        workbench.AiClient = new Core.Ai.OfflineAiEngine { StreamDelay = TimeSpan.Zero };
        master.Ai.ExplainCommand.Execute(null);
        PumpFor(TimeSpan.FromSeconds(2));
        Assert.False(string.IsNullOrWhiteSpace(master.Ai.Verdict));
        Assert.Equal(29, workbench.Settings.AiQuota.ExplainsLeft);

        window.Close();
    }

    [AvaloniaFact]
    public void LensSwitch_ReparsesFullHistory()
    {
        var workbench = NewWorkbench();
        var vm = new MainWindowViewModel(workbench);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        vm.RunDemoCommand.Execute(null);
        PumpFor(TimeSpan.FromSeconds(3));

        var master = vm.Tabs[0];
        int parsedBefore = master.Frames.Count(f => f.FunctionToken is not null);
        Assert.True(parsedBefore > 2);

        // Switch to Raw: whole history becomes raw blocks…
        master.SwitchToRawLensCommand.Execute(null);
        PumpFor(TimeSpan.FromMilliseconds(400));
        Assert.True(master.IsRawLensActive);
        Assert.DoesNotContain(master.Frames, f => f.FunctionToken is not null);
        Assert.True(master.Frames.Count > 0);

        // …and back to Modbus: history fully re-parses (W1 acceptance).
        master.SwitchToModbusLensCommand.Execute(null);
        PumpFor(TimeSpan.FromMilliseconds(400));
        Assert.True(master.IsModbusLensActive);
        Assert.True(master.Frames.Count(f => f.FunctionToken is not null) >= parsedBefore);

        window.Close();
    }

    [AvaloniaFact]
    public void FreeTier_BlocksSecondConnection_AndSlaveSimulation()
    {
        var workbench = NewWorkbench();
        Assert.Equal(Core.Licensing.LicenseTier.Free, workbench.License.Tier);

        var cfg1 = new Core.Transport.ConnectionConfig { Kind = Core.Transport.ConnectionKind.TcpServer, ListenPort = 15020 };
        var c1 = workbench.AddConnection(cfg1);
        Assert.Throws<ProFeatureException>(() =>
            workbench.AddConnection(new Core.Transport.ConnectionConfig { Kind = Core.Transport.ConnectionKind.TcpServer, ListenPort = 15021 }));

        Assert.Throws<ProFeatureException>(() =>
            workbench.CreateSession(c1, SessionKind.Slave, new Core.Lenses.ModbusTcpLens()));

        // Trial unlocks both.
        workbench.License.StartTrial();
        Assert.Equal(Core.Licensing.LicenseTier.TrialPro, workbench.License.Tier);
        var c2 = workbench.AddConnection(new Core.Transport.ConnectionConfig { Kind = Core.Transport.ConnectionKind.TcpServer, ListenPort = 15022 });
        var s = workbench.CreateSession(c2, SessionKind.Slave, new Core.Lenses.ModbusTcpLens());
        Assert.NotNull(s);
    }
}
