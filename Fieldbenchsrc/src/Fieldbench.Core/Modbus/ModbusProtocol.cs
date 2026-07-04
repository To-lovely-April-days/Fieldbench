namespace Fieldbench.Core.Modbus;

public static class ModbusFunction
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte WriteMultipleCoils = 0x0F;
    public const byte WriteMultipleRegisters = 0x10;

    public static readonly byte[] All =
    [
        ReadCoils, ReadDiscreteInputs, ReadHoldingRegisters, ReadInputRegisters,
        WriteSingleCoil, WriteSingleRegister, WriteMultipleCoils, WriteMultipleRegisters,
    ];

    public static bool IsSupported(byte fc) => Array.IndexOf(All, fc) >= 0;

    public static string Name(byte fc) => fc switch
    {
        ReadCoils => "Read coils",
        ReadDiscreteInputs => "Read discrete",
        ReadHoldingRegisters => "Read holding",
        ReadInputRegisters => "Read input",
        WriteSingleCoil => "Write coil",
        WriteSingleRegister => "Write single",
        WriteMultipleCoils => "Write coils",
        WriteMultipleRegisters => "Write multiple",
        _ => $"FC{fc:X2}",
    };
}

/// <summary>Modbus exception codes with the plain-language troubleshooting line (PRD §6.4).</summary>
public static class ModbusExceptions
{
    public static string Name(byte code) => code switch
    {
        0x01 => "Illegal function",
        0x02 => "Illegal data address",
        0x03 => "Illegal data value",
        0x04 => "Slave device failure",
        0x05 => "Acknowledge",
        0x06 => "Slave device busy",
        0x08 => "Memory parity error",
        0x0A => "Gateway path unavailable",
        0x0B => "Gateway target failed to respond",
        _ => $"Exception 0x{code:X2}",
    };

    /// <summary>Human troubleshooting hint, shown in the timeline summary / AI panel.</summary>
    public static string Hint(byte code) => code switch
    {
        0x01 => "The slave does not support this function code — check the device manual for supported FCs.",
        0x02 => "The slave has no register at this address — verify start address, count and the 0/1-based offset.",
        0x03 => "The value or count is out of range for the slave — check quantity limits and value bounds.",
        0x04 => "The slave hit an internal error while responding — check device diagnostics or power-cycle it.",
        0x05 => "Long-running command accepted; poll again later.",
        0x06 => "The slave is busy — retry after a delay.",
        0x08 => "The slave detected a memory parity error — device memory may be failing.",
        0x0A => "The gateway cannot route to the target — verify gateway configuration.",
        0x0B => "The device behind the gateway did not respond — verify the target address is online.",
        _ => "Unknown exception code — consult the device documentation.",
    };
}

/// <summary>Register area (address prefix convention: 0x/1x/3x/4x).</summary>
public enum RegisterArea
{
    Coils = 0,
    DiscreteInputs = 1,
    InputRegisters = 3,
    HoldingRegisters = 4,
}

public static class RegisterAreaInfo
{
    public static string Label(this RegisterArea area) => area switch
    {
        RegisterArea.Coils => "Coils 0x",
        RegisterArea.DiscreteInputs => "Discrete 1x",
        RegisterArea.InputRegisters => "Input 3x",
        RegisterArea.HoldingRegisters => "Holding 4x",
        _ => "?",
    };

    public static bool IsBitArea(this RegisterArea area) => area is RegisterArea.Coils or RegisterArea.DiscreteInputs;

    public static bool IsWritable(this RegisterArea area) => area is RegisterArea.Coils or RegisterArea.HoldingRegisters;

    /// <summary>PLC 1-based display address ("40001") for a 0-based protocol address.</summary>
    public static string DisplayAddress(this RegisterArea area, int protocolAddress) => area switch
    {
        RegisterArea.Coils => (protocolAddress + 1).ToString("00000"),
        RegisterArea.DiscreteInputs => (10000 + protocolAddress + 1).ToString("00000"),
        RegisterArea.InputRegisters => (30000 + protocolAddress + 1).ToString("00000"),
        RegisterArea.HoldingRegisters => (40000 + protocolAddress + 1).ToString("00000"),
        _ => protocolAddress.ToString(),
    };

    public static RegisterArea AreaForReadFc(byte fc) => fc switch
    {
        ModbusFunction.ReadCoils => RegisterArea.Coils,
        ModbusFunction.ReadDiscreteInputs => RegisterArea.DiscreteInputs,
        ModbusFunction.ReadInputRegisters => RegisterArea.InputRegisters,
        _ => RegisterArea.HoldingRegisters,
    };

    public static byte ReadFcForArea(RegisterArea area) => area switch
    {
        RegisterArea.Coils => ModbusFunction.ReadCoils,
        RegisterArea.DiscreteInputs => ModbusFunction.ReadDiscreteInputs,
        RegisterArea.InputRegisters => ModbusFunction.ReadInputRegisters,
        _ => ModbusFunction.ReadHoldingRegisters,
    };
}
