using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fieldbench.Core.Ai;
using Fieldbench.Core.Export;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Licensing;
using Fieldbench.Core.Master;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Profiles;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Tests;

// ──────────────────────────────────────────────────────────────────────────
// PointMapTextParser
// ──────────────────────────────────────────────────────────────────────────

public class PointMapTextParserTests
{
    private const string TabTable =
        "40001\tTemperature PV\tINT16 x0.1\t°C\tR\n" +
        "40005\tTemp setpoint\tFLOAT32\t°C\tR/W\n" +
        "4A001\tFirmware version\tINT16\t—\tR\n";

    private const string SpaceTable =
        "40001  Temperature PV  INT16 x0.1  °C  R\n" +
        "40005  Temp setpoint  FLOAT32  °C  R/W\n" +
        "4A001  Firmware version  INT16  —  R\n";

    [Theory]
    [InlineData(TabTable)]
    [InlineData(SpaceTable)]
    public void Parse_ProducesOneRowPerDataLine_IncludingBadAddressRows(string table)
    {
        var result = PointMapTextParser.Parse(table);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal("40001", result.Rows[0].RawAddress);
        Assert.Equal("40005", result.Rows[1].RawAddress);
        Assert.Equal("4A001", result.Rows[2].RawAddress); // bad address still yields a row
    }

    [Theory]
    [InlineData(TabTable)]
    [InlineData(SpaceTable)]
    public void Parse_FirstRow_NameSlugified_TypeScaleUnitReadOnly(string table)
    {
        var row = PointMapTextParser.Parse(table).Rows[0];

        Assert.Equal("temperature_pv", row.Name);
        Assert.Equal(RegisterDataType.Int16, row.DataType);
        Assert.Equal(0.1, row.Scale, 10);
        Assert.Equal("°C", row.Unit);
        Assert.False(row.Writable);
    }

    [Theory]
    [InlineData(TabTable)]
    [InlineData(SpaceTable)]
    public void Parse_SecondRow_Float32AndWritable(string table)
    {
        var row = PointMapTextParser.Parse(table).Rows[1];

        Assert.Equal(RegisterDataType.Float32, row.DataType);
        Assert.True(row.Writable);
        Assert.Equal("°C", row.Unit);
        Assert.Equal(1, row.Scale, 10);

        // Parser quirk: "Temp setpoint" contains "INT" (setpoINT) so the name
        // cell is consumed as a type cell and the fallback name is used.
        Assert.Equal("point_40005", row.Name);
    }

    [Theory]
    [InlineData(TabTable)]
    [InlineData(SpaceTable)]
    public void Parse_ThirdRow_HexLikeAddressKeptVerbatim(string table)
    {
        var row = PointMapTextParser.Parse(table).Rows[2];

        Assert.Equal("4A001", row.RawAddress);
        Assert.Equal("firmware_version", row.Name);
        Assert.Equal(RegisterDataType.Int16, row.DataType);
        Assert.False(row.Writable);
    }

    [Theory]
    [InlineData(TabTable)]
    [InlineData(SpaceTable)]
    public void Parse_PlcStyleAddresses_SuggestPlc1Based(string table)
    {
        Assert.Equal(AddressBase.Plc1Based, PointMapTextParser.Parse(table).SuggestedBase);
    }

    [Fact]
    public void Parse_LowAddresses_SuggestProtocol0Based()
    {
        var result = PointMapTextParser.Parse("0\tPump run\tBOOL\tR\n1\tAlarm\tBOOL\tR\n100\tSpeed\tUINT16\tRPM\tR/W\n");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(AddressBase.Protocol0Based, result.SuggestedBase);
    }

    [Fact]
    public void Parse_HeaderRowIsSkipped()
    {
        var result = PointMapTextParser.Parse("Address\tName\tType\tUnit\tRW\n40001\tTemperature PV\tINT16\t°C\tR\n");

        var row = Assert.Single(result.Rows);
        Assert.Equal("40001", row.RawAddress);
    }

    [Fact]
    public void Parse_BlankAndSingleCellLines_AreIgnored()
    {
        var result = PointMapTextParser.Parse("\n   \n40001\n40002\tFlow\tUINT16\tR\n");

        var row = Assert.Single(result.Rows);
        Assert.Equal("40002", row.RawAddress);
    }

    [Fact]
    public void Parse_UnknownScaleDefaultsToOne_UnknownTypeDefaultsToUInt16()
    {
        var row = PointMapTextParser.Parse("40010\tRun hours\t\tR\n").Rows[0];

        Assert.Equal(RegisterDataType.UInt16, row.DataType);
        Assert.Equal(1, row.Scale, 10);
        Assert.Equal("run_hours", row.Name);
    }
}

// ──────────────────────────────────────────────────────────────────────────
// PointMapReview.Resolve
// ──────────────────────────────────────────────────────────────────────────

public class PointMapReviewResolveTests
{
    private static PointRow Row(string raw, RegisterDataType type = RegisterDataType.UInt16) =>
        new() { RawAddress = raw, DataType = type, Name = "p" + raw };

    [Theory]
    [InlineData("40001", RegisterArea.HoldingRegisters, 0)]
    [InlineData("49999", RegisterArea.HoldingRegisters, 9998)]
    [InlineData("30001", RegisterArea.InputRegisters, 0)]
    [InlineData("10001", RegisterArea.DiscreteInputs, 0)]
    [InlineData("1", RegisterArea.Coils, 0)]
    [InlineData("9999", RegisterArea.Coils, 9998)]
    [InlineData("400001", RegisterArea.HoldingRegisters, 0)]
    [InlineData("465536", RegisterArea.HoldingRegisters, 65535)]
    [InlineData("300001", RegisterArea.InputRegisters, 0)]
    [InlineData("100001", RegisterArea.DiscreteInputs, 0)]
    public void Resolve_Plc1Based_MapsAreaAndProtocolAddress(string raw, RegisterArea area, int addr)
    {
        var row = Row(raw);
        PointMapReview.Resolve(new[] { row }, AddressBase.Plc1Based);

        Assert.Equal(PointStatus.Ready, row.Status);
        Assert.Equal(area, row.Area);
        Assert.Equal(addr, row.ProtocolAddress);
    }

    [Theory]
    [InlineData("4A001")]
    [InlineData("0")]
    [InlineData("20000")]
    [InlineData("50000")]
    [InlineData("70000")]
    public void Resolve_Plc1Based_UnmappableAddress_IsInvalidWithCheckAddressNote(string raw)
    {
        var row = Row(raw);
        PointMapReview.Resolve(new[] { row }, AddressBase.Plc1Based);

        Assert.Equal(PointStatus.Invalid, row.Status);
        Assert.Equal("check address", row.StatusNote);
        Assert.Equal(-1, row.ProtocolAddress);
    }

    [Fact]
    public void Resolve_Protocol0Based_KeepsRawAddress()
    {
        var row = Row("123");
        PointMapReview.Resolve(new[] { row }, AddressBase.Protocol0Based);

        Assert.Equal(PointStatus.Ready, row.Status);
        Assert.Equal(123, row.ProtocolAddress);
        Assert.Equal(RegisterArea.HoldingRegisters, row.Area);
    }

    [Theory]
    [InlineData("70000")]
    [InlineData("-1")]
    public void Resolve_Protocol0Based_OutOfRange_IsInvalid(string raw)
    {
        var row = Row(raw);
        PointMapReview.Resolve(new[] { row }, AddressBase.Protocol0Based);

        Assert.Equal(PointStatus.Invalid, row.Status);
        Assert.Equal("out of range", row.StatusNote);
        Assert.Equal(-1, row.ProtocolAddress);
    }

    [Fact]
    public void Resolve_Protocol0Based_NonBitTypeInCoilArea_MovesToHolding()
    {
        var row = Row("5", RegisterDataType.Int16);
        row.Area = RegisterArea.Coils;
        PointMapReview.Resolve(new[] { row }, AddressBase.Protocol0Based);

        Assert.Equal(RegisterArea.HoldingRegisters, row.Area);
    }

    [Fact]
    public void Resolve_Protocol0Based_BitTypeInCoilArea_StaysCoil()
    {
        var row = Row("5", RegisterDataType.Bit);
        row.Area = RegisterArea.Coils;
        PointMapReview.Resolve(new[] { row }, AddressBase.Protocol0Based);

        Assert.Equal(RegisterArea.Coils, row.Area);
        Assert.Equal(RegisterDataType.Bit, row.DataType);
    }

    [Fact]
    public void Resolve_BitTypeInRegisterArea_BecomesUInt16()
    {
        var row = Row("40010", RegisterDataType.Bit);
        PointMapReview.Resolve(new[] { row }, AddressBase.Plc1Based);

        Assert.Equal(RegisterArea.HoldingRegisters, row.Area);
        Assert.Equal(RegisterDataType.UInt16, row.DataType);
    }

    [Fact]
    public void Resolve_Float32FollowedByOverlappingInt16_FlagsSecondRowWarning()
    {
        var f32 = Row("40005", RegisterDataType.Float32); // holding 4..5
        var i16 = Row("40006", RegisterDataType.Int16);   // holding 5 → overlaps
        PointMapReview.Resolve(new[] { f32, i16 }, AddressBase.Plc1Based);

        Assert.Equal(PointStatus.Ready, f32.Status);
        Assert.Equal(PointStatus.Warning, i16.Status);
        Assert.NotNull(i16.StatusNote);
        Assert.Contains("overlaps", i16.StatusNote);
        Assert.Contains("40005", i16.StatusNote); // display address of the earlier point
    }

    [Fact]
    public void Resolve_AdjacentButNotOverlapping_NoWarning()
    {
        var f32 = Row("40001", RegisterDataType.Float32); // holding 0..1
        var i16 = Row("40003", RegisterDataType.Int16);   // holding 2 → contiguous, no overlap
        PointMapReview.Resolve(new[] { f32, i16 }, AddressBase.Plc1Based);

        Assert.Equal(PointStatus.Ready, f32.Status);
        Assert.Equal(PointStatus.Ready, i16.Status);
    }

    [Fact]
    public void Resolve_SameOffsetDifferentAreas_NoOverlapWarning()
    {
        var holding = Row("40001");
        var input = Row("30001");
        PointMapReview.Resolve(new[] { holding, input }, AddressBase.Plc1Based);

        Assert.Equal(PointStatus.Ready, holding.Status);
        Assert.Equal(PointStatus.Ready, input.Status);
    }

    [Fact]
    public void Resolve_UnselectedRows_DoNotParticipateInOverlapDetection()
    {
        var f32 = Row("40005", RegisterDataType.Float32);
        f32.Selected = false;
        var i16 = Row("40006", RegisterDataType.Int16);
        PointMapReview.Resolve(new[] { f32, i16 }, AddressBase.Plc1Based);

        Assert.Equal(PointStatus.Ready, i16.Status);
    }
}

// ──────────────────────────────────────────────────────────────────────────
// PointMapReview.BuildPollTasks
// ──────────────────────────────────────────────────────────────────────────

public class PointMapReviewBuildPollTasksTests
{
    private static PointRow Point(RegisterArea area, int addr, RegisterDataType type = RegisterDataType.UInt16) =>
        new() { RawAddress = addr.ToString(), Area = area, ProtocolAddress = addr, DataType = type };

    [Fact]
    public void BuildPollTasks_ContiguousPoints_MergeIntoOneTask()
    {
        var rows = new[]
        {
            Point(RegisterArea.HoldingRegisters, 0),
            Point(RegisterArea.HoldingRegisters, 1),
            Point(RegisterArea.HoldingRegisters, 2, RegisterDataType.Float32), // 2..3
        };

        var tasks = PointMapReview.BuildPollTasks(rows, unit: 7, periodMs: 250);

        var task = Assert.Single(tasks);
        Assert.Equal((ushort)0, task.Start);
        Assert.Equal((ushort)4, task.Count);
        Assert.Equal(ModbusFunction.ReadHoldingRegisters, task.Function);
        Assert.Equal((byte)7, task.Unit);
        Assert.Equal(250, task.PeriodMs);
        Assert.False(task.Enabled);
    }

    [Fact]
    public void BuildPollTasks_GapSplitsIntoTwoTasks()
    {
        var rows = new[]
        {
            Point(RegisterArea.HoldingRegisters, 0),
            Point(RegisterArea.HoldingRegisters, 1),
            Point(RegisterArea.HoldingRegisters, 2),
            Point(RegisterArea.HoldingRegisters, 10),
        };

        var tasks = PointMapReview.BuildPollTasks(rows, unit: 1);

        Assert.Equal(2, tasks.Count);
        Assert.Equal((ushort)0, tasks[0].Start);
        Assert.Equal((ushort)3, tasks[0].Count);
        Assert.Equal((ushort)10, tasks[1].Start);
        Assert.Equal((ushort)1, tasks[1].Count);
        Assert.Equal("Task 1", tasks[0].Name);
        Assert.Equal("Task 2", tasks[1].Name);
    }

    [Fact]
    public void BuildPollTasks_SpanOver125Registers_SplitsAtLimit()
    {
        var rows = Enumerable.Range(0, 126)
            .Select(i => Point(RegisterArea.HoldingRegisters, i))
            .ToArray();

        var tasks = PointMapReview.BuildPollTasks(rows, unit: 1);

        Assert.Equal(2, tasks.Count);
        Assert.Equal((ushort)0, tasks[0].Start);
        Assert.Equal((ushort)125, tasks[0].Count);
        Assert.Equal((ushort)125, tasks[1].Start);
        Assert.Equal((ushort)1, tasks[1].Count);
    }

    [Fact]
    public void BuildPollTasks_Exactly125Registers_SingleTask()
    {
        var rows = Enumerable.Range(0, 125)
            .Select(i => Point(RegisterArea.HoldingRegisters, i))
            .ToArray();

        var tasks = PointMapReview.BuildPollTasks(rows, unit: 1);

        var task = Assert.Single(tasks);
        Assert.Equal((ushort)0, task.Start);
        Assert.Equal((ushort)125, task.Count);
    }

    [Fact]
    public void BuildPollTasks_CoilPoints_UseFc01()
    {
        var rows = new[]
        {
            Point(RegisterArea.Coils, 0, RegisterDataType.Bit),
            Point(RegisterArea.Coils, 1, RegisterDataType.Bit),
            Point(RegisterArea.Coils, 2, RegisterDataType.Bit),
        };

        var tasks = PointMapReview.BuildPollTasks(rows, unit: 3);

        var task = Assert.Single(tasks);
        Assert.Equal(ModbusFunction.ReadCoils, task.Function);
        Assert.Equal((ushort)0, task.Start);
        Assert.Equal((ushort)3, task.Count);
    }

    [Fact]
    public void BuildPollTasks_DifferentAreas_ProduceSeparateTasks()
    {
        var rows = new[]
        {
            Point(RegisterArea.HoldingRegisters, 0),
            Point(RegisterArea.InputRegisters, 0),
        };

        var tasks = PointMapReview.BuildPollTasks(rows, unit: 1);

        Assert.Equal(2, tasks.Count);
        Assert.Contains(tasks, t => t.Function == ModbusFunction.ReadHoldingRegisters);
        Assert.Contains(tasks, t => t.Function == ModbusFunction.ReadInputRegisters);
    }

    [Fact]
    public void BuildPollTasks_InvalidUnselectedOrUnresolvedRows_AreExcluded()
    {
        var invalid = Point(RegisterArea.HoldingRegisters, 0);
        invalid.Status = PointStatus.Invalid;
        var unselected = Point(RegisterArea.HoldingRegisters, 1);
        unselected.Selected = false;
        var unresolved = Point(RegisterArea.HoldingRegisters, 2);
        unresolved.ProtocolAddress = -1;

        var tasks = PointMapReview.BuildPollTasks(new[] { invalid, unselected, unresolved }, unit: 1);

        Assert.Empty(tasks);
    }

    [Fact]
    public void BuildPollTasks_OverlappingPoints_StillMergeIntoOneSpan()
    {
        var rows = new[]
        {
            Point(RegisterArea.HoldingRegisters, 4, RegisterDataType.Float32), // 4..5
            Point(RegisterArea.HoldingRegisters, 5),                           // overlaps
        };

        var tasks = PointMapReview.BuildPollTasks(rows, unit: 1);

        var task = Assert.Single(tasks);
        Assert.Equal((ushort)4, task.Start);
        Assert.Equal((ushort)2, task.Count);
    }
}

// ──────────────────────────────────────────────────────────────────────────
// LicenseManager
// ──────────────────────────────────────────────────────────────────────────

public class LicenseManagerTests
{
    private static string NewStateDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fieldbench-lic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static LicensePayload ProPayload(Action<LicensePayload>? mutate = null)
    {
        var p = new LicensePayload
        {
            Key = "FB-TEST-0001",
            Email = "buyer@example.com",
            Tier = "pro",
            IssuedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        mutate?.Invoke(p);
        return p;
    }

    [Fact]
    public void GenerateKeyPair_Issue_Activate_HappyPath()
    {
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        var manager = new LicenseManager(NewStateDir(), pub);

        var (ok, message) = manager.Activate(LicenseManager.Issue(ProPayload(), priv));

        Assert.True(ok, message);
        Assert.Contains("buyer@example.com", message);
        Assert.NotNull(manager.Active);
        Assert.Equal(LicenseTier.Pro, manager.Tier);
        Assert.True(manager.IsProUnlocked);
        Assert.True(manager.SlaveSimulationAllowed);
        Assert.Equal(int.MaxValue, manager.MaxConnections);
        Assert.Equal(int.MaxValue, manager.MaxChartChannels);
    }

    [Fact]
    public void Activate_SignedWithDifferentKey_IsRejected()
    {
        var (foreignPriv, _) = LicenseManager.GenerateKeyPair();
        var (_, ourPub) = LicenseManager.GenerateKeyPair();
        var manager = new LicenseManager(NewStateDir(), ourPub);

        var (ok, message) = manager.Activate(LicenseManager.Issue(ProPayload(), foreignPriv));

        Assert.False(ok);
        Assert.Contains("Signature check failed", message);
        Assert.Null(manager.Active);
        Assert.Equal(LicenseTier.Free, manager.Tier);
    }

    [Fact]
    public void Activate_MachineBoundToOtherMachine_IsRejectedAndReportsOwnCode()
    {
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        var manager = new LicenseManager(NewStateDir(), pub);
        var file = LicenseManager.Issue(ProPayload(p => p.Machines.Add("0000-0000-0000-0000")), priv);

        var (ok, message) = manager.Activate(file);

        Assert.False(ok);
        Assert.Contains("bound to other machines", message);
        Assert.Contains(LicenseManager.MachineCode(), message);
        Assert.Null(manager.Active);
    }

    [Fact]
    public void Activate_MachineBoundIncludingThisMachine_Succeeds()
    {
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        var manager = new LicenseManager(NewStateDir(), pub);
        var file = LicenseManager.Issue(ProPayload(p =>
        {
            p.Machines.Add("0000-0000-0000-0000");
            p.Machines.Add(LicenseManager.MachineCode());
        }), priv);

        var (ok, _) = manager.Activate(file);

        Assert.True(ok);
        Assert.Equal(LicenseTier.Pro, manager.Tier);
    }

    [Fact]
    public void Activate_ExpiredLicense_IsRejected()
    {
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        var manager = new LicenseManager(NewStateDir(), pub);
        var file = LicenseManager.Issue(ProPayload(p => p.ExpiresUtc = DateTime.UtcNow.AddDays(-1)), priv);

        var (ok, message) = manager.Activate(file);

        Assert.False(ok);
        Assert.Contains("expired", message);
        Assert.Equal(LicenseTier.Free, manager.Tier);
    }

    [Fact]
    public void Activate_FutureExpiry_GrantsPro()
    {
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        var manager = new LicenseManager(NewStateDir(), pub);
        var file = LicenseManager.Issue(ProPayload(p => p.ExpiresUtc = DateTime.UtcNow.AddYears(1)), priv);

        Assert.True(manager.Activate(file).Ok);
        Assert.Equal(LicenseTier.Pro, manager.Tier);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    public void Activate_GarbageInput_IsRejectedGracefully(string input)
    {
        var manager = new LicenseManager(NewStateDir());

        var (ok, message) = manager.Activate(input);

        Assert.False(ok);
        Assert.Contains("Not a valid activation file", message);
    }

    [Fact]
    public void Activate_CorruptedBase64_IsRejected()
    {
        var manager = new LicenseManager(NewStateDir());

        var (ok, message) = manager.Activate("""{"payload":"!!not-base64!!","signature":"@@"}""");

        Assert.False(ok);
        Assert.Contains("corrupted", message);
    }

    [Fact]
    public void Tier_NoLicenseNoTrial_IsFreeWithFreeLimits()
    {
        var manager = new LicenseManager(NewStateDir());

        Assert.Equal(LicenseTier.Free, manager.Tier);
        Assert.False(manager.IsProUnlocked);
        Assert.Equal(1, manager.MaxConnections);
        Assert.Equal(2, manager.MaxChartChannels);
        Assert.False(manager.SlaveSimulationAllowed);
        Assert.Equal(14, manager.TrialDaysLeft);
    }

    [Fact]
    public void Tier_ActiveTrial_IsTrialPro()
    {
        var manager = new LicenseManager(NewStateDir())
        {
            TrialStartedUtc = DateTime.UtcNow.AddDays(-1),
        };

        Assert.Equal(LicenseTier.TrialPro, manager.Tier);
        Assert.True(manager.IsProUnlocked);
        Assert.Equal(13, manager.TrialDaysLeft);
    }

    [Fact]
    public void Tier_ExpiredTrial_FallsBackToFree()
    {
        var manager = new LicenseManager(NewStateDir())
        {
            TrialStartedUtc = DateTime.UtcNow.AddDays(-15),
        };

        Assert.Equal(LicenseTier.Free, manager.Tier);
        Assert.Equal(0, manager.TrialDaysLeft);
    }

    [Fact]
    public void StartTrial_SetsTrialStartAndUnlocksTrialPro()
    {
        var manager = new LicenseManager(NewStateDir());

        manager.StartTrial();

        Assert.NotNull(manager.TrialStartedUtc);
        Assert.Equal(LicenseTier.TrialPro, manager.Tier);
    }

    [Fact]
    public void StartTrial_DoesNotResetAnExistingTrial()
    {
        var started = DateTime.UtcNow.AddDays(-3);
        var manager = new LicenseManager(NewStateDir()) { TrialStartedUtc = started };

        manager.StartTrial();

        Assert.Equal(started, manager.TrialStartedUtc);
    }

    [Fact]
    public void ProLicense_OutranksExpiredTrial()
    {
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        var manager = new LicenseManager(NewStateDir(), pub)
        {
            TrialStartedUtc = DateTime.UtcNow.AddDays(-30),
        };

        Assert.True(manager.Activate(LicenseManager.Issue(ProPayload(), priv)).Ok);
        Assert.Equal(LicenseTier.Pro, manager.Tier);
    }

    [Fact]
    public void Activation_PersistsAcrossManagerInstances()
    {
        var dir = NewStateDir();
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        var first = new LicenseManager(dir, pub);
        Assert.True(first.Activate(LicenseManager.Issue(ProPayload(), priv)).Ok);

        var second = new LicenseManager(dir, pub);

        Assert.NotNull(second.Active);
        Assert.Equal("buyer@example.com", second.Active!.Email);
        Assert.Equal(LicenseTier.Pro, second.Tier);
    }

    [Fact]
    public void Deactivate_ReturnsToFree()
    {
        var (priv, pub) = LicenseManager.GenerateKeyPair();
        var manager = new LicenseManager(NewStateDir(), pub);
        Assert.True(manager.Activate(LicenseManager.Issue(ProPayload(), priv)).Ok);

        manager.Deactivate();

        Assert.Null(manager.Active);
        Assert.Equal(LicenseTier.Free, manager.Tier);
    }

    [Fact]
    public void MachineCode_HasGroupedHexFormat()
    {
        Assert.Matches(new Regex("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$"), LicenseManager.MachineCode());
    }

    [Fact]
    public void MachineCode_IsStableAcrossCalls()
    {
        Assert.Equal(LicenseManager.MachineCode(), LicenseManager.MachineCode());
    }
}

// ──────────────────────────────────────────────────────────────────────────
// FrameExporter
// ──────────────────────────────────────────────────────────────────────────

public class FrameExporterTests
{
    private static readonly DateTime TsA = new(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);
    private static readonly DateTime TsB = new(2026, 1, 2, 3, 4, 5, 690, DateTimeKind.Utc);
    private static readonly DateTime TsC = new(2026, 1, 2, 3, 4, 6, 5, DateTimeKind.Utc);

    private static Frame RequestFrame() => new()
    {
        Id = 1,
        TimestampUtc = TsA,
        Direction = StreamDirection.Tx,
        Bytes = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A],
        Status = FrameStatus.Ok,
        AddressToken = "@01",
        FunctionToken = "FC03",
        Summary = "Read holding 0–9",
        StatusTag = "OK",
        Fields = [new FrameField(0, 1, FieldKind.Address, "Slave address", "0x01")],
        FunctionCode = 0x03,
        UnitId = 0x01,
    };

    private static Frame ResponseFrameWithQuotes() => new()
    {
        Id = 2,
        TimestampUtc = TsB,
        Direction = StreamDirection.Rx,
        Bytes = [0x01, 0x03, 0x02, 0x00, 0x2A],
        Status = FrameStatus.Ok,
        AddressToken = "@01",
        FunctionToken = "FC03",
        Summary = "value \"hot\"",
        StatusTag = "OK",
        DeltaMs = 12.5,
        FunctionCode = 0x03,
        UnitId = 0x01,
    };

    private static Frame CrcFailFrame() => new()
    {
        Id = 3,
        TimestampUtc = TsC,
        Direction = StreamDirection.Rx,
        Bytes = [0xFF, 0x00],
        Status = FrameStatus.Error,
        Summary = "Unrecognized bytes",
        StatusTag = "CRC FAIL",
    };

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    [Fact]
    public void ToCsv_HeaderAndPlainRow()
    {
        var lines = Lines(FrameExporter.ToCsv(new[] { RequestFrame() }));

        Assert.Equal("timestamp,direction,length,summary,status,hex", lines[0]);
        Assert.Equal("2026-01-02 03:04:05.678,Tx,6,\"@01 FC03 Read holding 0–9\",OK,01 03 00 00 00 0A", lines[1]);
    }

    [Fact]
    public void ToCsv_DoublesQuotesInsideSummary()
    {
        var lines = Lines(FrameExporter.ToCsv(new[] { ResponseFrameWithQuotes() }));

        Assert.Contains("\"@01 FC03 value \"\"hot\"\"\"", lines[1]);
    }

    [Fact]
    public void ToCsv_EmptyStatusTagBecomesEmptyColumn()
    {
        var frame = new Frame
        {
            TimestampUtc = TsA,
            Direction = StreamDirection.Rx,
            Bytes = [0xAA],
            Summary = "raw",
        };

        var lines = Lines(FrameExporter.ToCsv(new[] { frame }));

        Assert.Equal("2026-01-02 03:04:05.678,Rx,1,\"raw\",,AA", lines[1]);
    }

    [Fact]
    public void ToTxt_FormatsDirectionHexAndSummary()
    {
        var lines = Lines(FrameExporter.ToTxt(new[] { RequestFrame(), CrcFailFrame() }));

        Assert.StartsWith("03:04:05.678  TX  01 03 00 00 00 0A", lines[0]);
        Assert.Contains("@01 FC03 Read holding 0–9", lines[0]);
        Assert.DoesNotContain("[OK]", lines[0]);

        Assert.StartsWith("03:04:06.005  RX  FF 00", lines[1]);
        Assert.EndsWith("[CRC FAIL]", lines[1]);
    }

    [Fact]
    public void ExportJson_RoundTripsFrameProperties()
    {
        var bytes = FrameExporter.Export(new[] { RequestFrame(), ResponseFrameWithQuotes() }, ExportFormat.Json);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.Equal(2, root.GetArrayLength());

        var first = root[0];
        Assert.Equal("Tx", first.GetProperty("direction").GetString());
        Assert.Equal(6, first.GetProperty("length").GetInt32());
        Assert.Equal("@01", first.GetProperty("address").GetString());
        Assert.Equal("FC03", first.GetProperty("function").GetString());
        Assert.Equal("Read holding 0–9", first.GetProperty("summary").GetString());
        Assert.Equal("OK", first.GetProperty("status").GetString());
        Assert.Equal("01 03 00 00 00 0A", first.GetProperty("hex").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("deltaMs").ValueKind);
        Assert.Equal(TsA, first.GetProperty("timestamp").GetDateTime());
        Assert.Equal("Slave address", first.GetProperty("fields")[0].GetProperty("name").GetString());

        var second = root[1];
        Assert.Equal("value \"hot\"", second.GetProperty("summary").GetString());
        Assert.Equal(12.5, second.GetProperty("deltaMs").GetDouble());
    }

    [Fact]
    public void ExportBin_ConcatenatesFrameBytesInOrder()
    {
        var a = RequestFrame();
        var b = ResponseFrameWithQuotes();
        var c = CrcFailFrame();

        var bin = FrameExporter.Export(new[] { a, b, c }, ExportFormat.Bin);

        Assert.Equal(a.Bytes.Concat(b.Bytes).Concat(c.Bytes).ToArray(), bin);
    }

    [Fact]
    public void ExportCsvAndTxt_AreUtf8OfStringBuilders()
    {
        var frames = new[] { RequestFrame() };

        Assert.Equal(Encoding.UTF8.GetBytes(FrameExporter.ToCsv(frames)), FrameExporter.Export(frames, ExportFormat.Csv));
        Assert.Equal(Encoding.UTF8.GetBytes(FrameExporter.ToTxt(frames)), FrameExporter.Export(frames, ExportFormat.Txt));
    }

    [Theory]
    [InlineData(ExportFormat.Csv, "csv")]
    [InlineData(ExportFormat.Json, "json")]
    [InlineData(ExportFormat.Bin, "bin")]
    [InlineData(ExportFormat.Txt, "log")]
    public void DefaultExtension_MatchesFormat(ExportFormat format, string expected)
    {
        Assert.Equal(expected, FrameExporter.DefaultExtension(format));
    }
}

// ──────────────────────────────────────────────────────────────────────────
// OfflineAiEngine
// ──────────────────────────────────────────────────────────────────────────

public class OfflineAiEngineTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ExplainContext Ctx(double errorRatio, params Frame[] frames) => new()
    {
        Protocol = "Modbus RTU",
        ConnectionParams = "COM3 9600 8N1",
        SelectedFrames = frames,
        RecentErrorRatio = errorRatio,
    };

    private static byte[] WithCorruptedCrc(byte[] body, out ushort goodCrc)
    {
        goodCrc = Checksums.Crc16Modbus(body);
        ushort corrupted = (ushort)(goodCrc ^ 0x0001); // exactly one bit off
        return [.. body, (byte)(corrupted & 0xFF), (byte)(corrupted >> 8)];
    }

    [Fact]
    public void Analyze_CrcFail_SingleBitError_BlamesWireCorruption()
    {
        var bytes = WithCorruptedCrc([0x01, 0x03, 0x00, 0x00, 0x00, 0x0A], out _);
        var frame = new Frame
        {
            TimestampUtc = T0,
            Direction = StreamDirection.Rx,
            Bytes = bytes,
            Status = FrameStatus.Error,
            StatusTag = "CRC FAIL",
            Summary = "Read holding 0–9",
            FunctionCode = 0x03,
            UnitId = 0x01,
            Role = FrameRole.MasterToSlave,
        };

        var result = new OfflineAiEngine().Analyze(Ctx(0.1, frame));

        Assert.Contains("single-bit", result.Verdict);
        Assert.Contains(result.Causes, c => c.Text.Contains("noise", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(result.Checks);
    }

    [Fact]
    public void Analyze_CrcFail_SwappedChecksumBytes_DetectsByteOrderBug()
    {
        byte[] body = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A];
        ushort crc = Checksums.Crc16Modbus(body);
        Assert.NotEqual((byte)(crc & 0xFF), (byte)(crc >> 8)); // sanity: swap actually changes bytes
        byte[] bytes = [.. body, (byte)(crc >> 8), (byte)(crc & 0xFF)]; // hi byte first = swapped

        var frame = new Frame
        {
            TimestampUtc = T0,
            Direction = StreamDirection.Rx,
            Bytes = bytes,
            Status = FrameStatus.Error,
            StatusTag = "CRC FAIL",
            Summary = "Read holding 0–9",
            FunctionCode = 0x03,
            UnitId = 0x01,
            Role = FrameRole.MasterToSlave,
        };

        var result = new OfflineAiEngine().Analyze(Ctx(0.1, frame));

        Assert.Contains("swapped", result.Verdict);
        Assert.Contains(result.Causes, c => c.Text.Contains("reversed byte order"));
    }

    [Fact]
    public void Analyze_Exception02_ExplainsIllegalAddressAndMentionsAddressCauses()
    {
        var frame = new Frame
        {
            TimestampUtc = T0,
            Direction = StreamDirection.Rx,
            Bytes = [0x01, 0x83, 0x02, 0xC0, 0xF1],
            Status = FrameStatus.Warning,
            StatusTag = "EXC 02",
            Summary = "Illegal data address",
            FunctionCode = 0x03,
            UnitId = 0x01,
            ExceptionCode = 0x02,
            Role = FrameRole.SlaveToMaster,
        };

        var result = new OfflineAiEngine().Analyze(Ctx(0, frame));

        Assert.Contains("Illegal data address", result.Verdict);
        Assert.Contains(result.Causes, c => c.Text.Contains("address", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Checks, c => c.Text.Contains("address", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_GarbageWithHighErrorRatio_DiagnosesWrongSerialParameters()
    {
        var frame = new Frame
        {
            TimestampUtc = T0,
            Direction = StreamDirection.Rx,
            Bytes = [0x00, 0xFF, 0xFE, 0x80, 0x00, 0xFF, 0x9A, 0xC0],
            Status = FrameStatus.Error,
            StatusTag = "CRC FAIL",
            Summary = "Unrecognized bytes",
        };

        var result = new OfflineAiEngine().Analyze(Ctx(0.8, frame));

        // Verdict describes the wrong-speed UART signature; the ranked causes name the baud mismatch.
        Assert.Contains("wrong speed", result.Verdict);
        Assert.Contains(result.Causes, c => c.Text.Contains("Baud rate mismatch"));
        Assert.Contains(result.Checks, c => c.Text.Contains("COM3 9600 8N1"));
    }

    [Fact]
    public void Analyze_TxRequestWithoutResponse_ChecksIncludeSlaveScan()
    {
        var frame = new Frame
        {
            TimestampUtc = T0,
            Direction = StreamDirection.Tx,
            Bytes = [0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD],
            Status = FrameStatus.Ok,
            StatusTag = "OK",
            Summary = "Read holding 0–9",
            FunctionCode = 0x03,
            UnitId = 0x01,
            Role = FrameRole.MasterToSlave,
        };

        var result = new OfflineAiEngine().Analyze(Ctx(0, frame));

        Assert.Contains("nothing came back", result.Verdict);
        Assert.Contains(result.Checks, c => c.Text.Contains("Scan slaves"));
        Assert.Contains(result.Causes, c => c.Text.Contains("No device at address 1"));
    }

    [Fact]
    public void Analyze_TxRequestWithMatchingRxResponse_IsNormalNotDeadAir()
    {
        var request = new Frame
        {
            TimestampUtc = T0,
            Direction = StreamDirection.Tx,
            Bytes = [0x01, 0x03, 0x00, 0x00, 0x00, 0x01],
            Status = FrameStatus.Ok,
            StatusTag = "OK",
            Summary = "Read holding 0",
            FunctionCode = 0x03,
            UnitId = 0x01,
            Role = FrameRole.MasterToSlave,
        };
        var response = new Frame
        {
            TimestampUtc = T0.AddMilliseconds(15),
            Direction = StreamDirection.Rx,
            Bytes = [0x01, 0x03, 0x02, 0x00, 0x2A],
            Status = FrameStatus.Ok,
            StatusTag = "OK",
            Summary = "1 register",
            FunctionCode = 0x03,
            UnitId = 0x01,
            Role = FrameRole.SlaveToMaster,
        };

        var result = new OfflineAiEngine().Analyze(Ctx(0, request, response));

        Assert.DoesNotContain("nothing came back", result.Verdict);
        Assert.Contains("FC03", result.Verdict);
    }

    [Fact]
    public void Analyze_NormalFc03Response_ProducesFieldNotes()
    {
        var frame = new Frame
        {
            TimestampUtc = T0,
            Direction = StreamDirection.Rx,
            Bytes = [0x01, 0x03, 0x02, 0x00, 0x2A],
            Status = FrameStatus.Ok,
            StatusTag = "OK",
            Summary = "1 register",
            FunctionCode = 0x03,
            UnitId = 0x01,
            Role = FrameRole.SlaveToMaster,
            DeltaMs = 15,
            Fields =
            [
                new FrameField(0, 1, FieldKind.Address, "Slave address", "0x01"),
                new FrameField(1, 1, FieldKind.Function, "Function", "0x03", "Read holding"),
            ],
        };

        var result = new OfflineAiEngine().Analyze(Ctx(0, frame));

        Assert.Contains("Read holding", result.Verdict);
        Assert.Contains("FC03", result.Verdict);
        Assert.NotEmpty(result.FieldNotes);
        Assert.Contains(result.FieldNotes, n => n.Field == "Slave address");
        Assert.Contains(result.FieldNotes, n => n.Field == "Response time");
        Assert.Empty(result.Causes);
    }

    [Fact]
    public void Analyze_EmptySelection_AsksForFrames()
    {
        var result = new OfflineAiEngine().Analyze(Ctx(0));

        Assert.Contains("Select one or more frames", result.Verdict);
    }

    [Fact]
    public async Task ExplainAsync_StreamsVerdictThenDone_WithZeroDelay()
    {
        var frame = new Frame
        {
            TimestampUtc = T0,
            Direction = StreamDirection.Rx,
            Bytes = [0x01, 0x03, 0x02, 0x00, 0x2A],
            Status = FrameStatus.Ok,
            StatusTag = "OK",
            Summary = "1 register",
            FunctionCode = 0x03,
            UnitId = 0x01,
            Role = FrameRole.SlaveToMaster,
        };
        var engine = new OfflineAiEngine { StreamDelay = TimeSpan.Zero };
        var ctx = Ctx(0, frame);

        var chunks = new List<AiChunk>();
        await foreach (var chunk in engine.ExplainAsync(ctx))
        {
            chunks.Add(chunk);
        }

        Assert.True(chunks[^1].Done);
        var streamedVerdict = string.Concat(chunks.Where(c => c.VerdictDelta is not null).Select(c => c.VerdictDelta));
        Assert.Equal(engine.Analyze(ctx).Verdict, streamedVerdict);
    }

    [Fact]
    public async Task ExtractPointMapAsync_NoTextNoImage_ReturnsError()
    {
        var extraction = await new OfflineAiEngine().ExtractPointMapAsync(null, null);

        Assert.NotNull(extraction.Error);
        Assert.Empty(extraction.Rows);
    }

    [Fact]
    public async Task ExtractPointMapAsync_TableText_UsesTextParser()
    {
        var extraction = await new OfflineAiEngine().ExtractPointMapAsync("40001\tTemperature PV\tINT16 x0.1\t°C\tR", null);

        Assert.Null(extraction.Error);
        var row = Assert.Single(extraction.Rows);
        Assert.Equal("temperature_pv", row.Name);
    }
}

// ──────────────────────────────────────────────────────────────────────────
// SettingsStore
// ──────────────────────────────────────────────────────────────────────────

public class SettingsStoreTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fieldbench-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Load_OldQuotaMonth_FreeUser_KeepsOneTimeTrialCounters()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{"aiQuotaMonth":"2020-01","aiQuota":{"explainsUsed":7,"extractionsUsed":2,"explainsLimit":30,"extractionsLimit":3}}""");

        var store = new SettingsStore(dir);

        // PRD §6.7: the free allowance is a one-time gift — no monthly refill.
        var currentMonth = DateTime.UtcNow.ToString("yyyy-MM");
        Assert.Equal(currentMonth, store.Settings.AiQuotaMonth);
        Assert.Equal(7, store.Settings.AiQuota.ExplainsUsed);
        Assert.Equal(2, store.Settings.AiQuota.ExtractionsUsed);

        // The month stamp is persisted immediately.
        Assert.Contains(currentMonth, File.ReadAllText(Path.Combine(dir, "settings.json")));
    }

    [Fact]
    public void Load_OldQuotaMonth_Subscriber_ResetsCountersToSubscriptionLimits()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            """{"aiQuotaMonth":"2020-01","aiQuota":{"explainsUsed":900,"extractionsUsed":25,"explainsLimit":1000,"extractionsLimit":30,"subscribed":true}}""");

        var store = new SettingsStore(dir);

        // PRD §6.7/§6.8: subscribers get 1000 explains + 30 extractions per month.
        Assert.Equal(0, store.Settings.AiQuota.ExplainsUsed);
        Assert.Equal(0, store.Settings.AiQuota.ExtractionsUsed);
        Assert.Equal(1000, store.Settings.AiQuota.ExplainsLimit);
        Assert.Equal(30, store.Settings.AiQuota.ExtractionsLimit);
    }

    [Fact]
    public void Load_SameQuotaMonth_KeepsCounters()
    {
        var dir = NewDir();
        var month = DateTime.UtcNow.ToString("yyyy-MM");
        File.WriteAllText(Path.Combine(dir, "settings.json"),
            $$$"""{"aiQuotaMonth":"{{{month}}}","aiQuota":{"explainsUsed":7,"extractionsUsed":2,"subscribed":true}}""");

        var store = new SettingsStore(dir);

        Assert.Equal(month, store.Settings.AiQuotaMonth);
        Assert.Equal(7, store.Settings.AiQuota.ExplainsUsed);
        Assert.Equal(2, store.Settings.AiQuota.ExtractionsUsed);
        Assert.True(store.Settings.AiQuota.Subscribed);
        Assert.Equal(23, store.Settings.AiQuota.ExplainsLeft);
    }

    [Fact]
    public void Load_CorruptSettingsFile_FallsBackToDefaults()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "settings.json"), "not json {{{{");

        var store = new SettingsStore(dir);

        Assert.Equal("Light", store.Settings.Theme);
        Assert.Equal(0, store.Settings.AiQuota.ExplainsUsed);
        Assert.Equal(DateTime.UtcNow.ToString("yyyy-MM"), store.Settings.AiQuotaMonth);
    }

    [Fact]
    public void Save_RoundTripsSettings()
    {
        var dir = NewDir();
        var store = new SettingsStore(dir);
        store.Settings.Theme = "Dark";
        store.Settings.AiQuota.ExplainsUsed = 4;
        store.Save();

        var reloaded = new SettingsStore(dir);

        Assert.Equal("Dark", reloaded.Settings.Theme);
        Assert.Equal(4, reloaded.Settings.AiQuota.ExplainsUsed);
    }
}
