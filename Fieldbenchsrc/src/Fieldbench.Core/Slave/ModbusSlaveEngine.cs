using System.Buffers.Binary;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Sessions;
using Fieldbench.Core.Streams;
using Fieldbench.Core.Transport;

namespace Fieldbench.Core.Slave;

/// <summary>
/// Modbus slave simulator: serves FC 01–06/0F/10 against the sparse register
/// store over RTU (serial) or TCP (listener, multiple masters). Requests and
/// our responses flow through the shared connection stream, so they land on
/// the same timeline the user is watching. Illegal address/function/value
/// produce automatic exceptions 02/01/03.
/// </summary>
public sealed class ModbusSlaveEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _txSerial = new(1, 1);
    private readonly IProtocolLens _rxLens;
    private readonly Timer _generatorTimer;
    private readonly Timer _flushTimer;
    private readonly List<SlaveTag> _tags = new();
    private DateTime _lastTick = DateTime.UtcNow;
    private bool _disposed;

    public ModbusSlaveEngine(Connection connection, bool tcpFraming)
    {
        Connection = connection;
        TcpFraming = tcpFraming;
        Store = new SparseRegisterStore();

        _rxLens = tcpFraming
            ? new ModbusTcpLens { Perspective = LensPerspective.Slave }
            : new ModbusRtuLens
            {
                Perspective = LensPerspective.Slave,
                CharTimeSecondsProvider = () => connection.Config.CharTimeSeconds(),
            };

        connection.Store.ChunkAppended += OnChunkAppended;
        _flushTimer = new Timer(_ => FlushTick(), null, 20, 20);
        _generatorTimer = new Timer(_ => GeneratorTick(), null, 200, 200);
    }

    public Connection Connection { get; }

    public bool TcpFraming { get; }

    public SparseRegisterStore Store { get; }

    public byte UnitId { get; set; } = 1;

    public long ServedRequests { get; private set; }

    public long ExceptionsSent { get; private set; }

    public event Action? Stats;

    public event Action? TagsChanged;

    public IReadOnlyList<SlaveTag> Tags
    {
        get { lock (_gate) return _tags.ToArray(); }
    }

    public SlaveTag AddTag(SlaveTag tag)
    {
        lock (_gate)
        {
            _tags.Add(tag);
            _tags.Sort((a, b) => a.Tag.Area != b.Tag.Area ? a.Tag.Area.CompareTo(b.Tag.Area) : a.Tag.Address.CompareTo(b.Tag.Address));
        }

        // Define backing storage and seed the initial value.
        if (tag.Tag.Area.IsBitArea())
        {
            Store.DefineBits(tag.Tag.Area, tag.Tag.Address, 1);
        }
        else
        {
            Store.DefineWords(tag.Tag.Area, tag.Tag.Address, tag.Tag.RegisterCount);
            if (tag.Tag.ScaledValue is { } v) SetTagValue(tag, v);
        }

        TagsChanged?.Invoke();
        return tag;
    }

    public void RemoveTag(SlaveTag tag)
    {
        lock (_gate) _tags.Remove(tag);
        TagsChanged?.Invoke();
    }

    /// <summary>Grid edit: write a scaled value into the store and refresh the tag.</summary>
    public void SetTagValue(SlaveTag slaveTag, double scaledValue)
    {
        var tag = slaveTag.Tag;
        if (tag.Area.IsBitArea())
        {
            Store.WriteBits(tag.Area, tag.Address, new[] { scaledValue != 0 }, define: true);
            tag.UpdateFromBit(scaledValue != 0, DateTime.UtcNow);
        }
        else
        {
            var words = tag.EncodeForWrite(scaledValue);
            Store.WriteWords(tag.Area, tag.Address, words, define: true);
            tag.UpdateFromWords(words, DateTime.UtcNow);
        }

        slaveTag.WrittenByClient = false;
    }

    private void GeneratorTick()
    {
        if (_disposed) return;
        var now = DateTime.UtcNow;
        double dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        SlaveTag[] tags;
        lock (_gate) tags = _tags.ToArray();
        foreach (var st in tags)
        {
            var next = st.Generator.Next(now, dt, st.Tag.ScaledValue ?? 0);
            if (next is { } v) SetTagValue(st, v);
        }
    }

    private void OnChunkAppended(StreamChunk chunk)
    {
        if (chunk.Direction != StreamDirection.Rx) return;
        IReadOnlyList<Frame> frames;
        lock (_gate) frames = _rxLens.Feed(chunk);
        foreach (var frame in frames) _ = HandleFrameAsync(frame);
    }

    private void FlushTick()
    {
        if (_disposed) return;
        IReadOnlyList<Frame> frames;
        lock (_gate) frames = _rxLens.FlushPending(DateTime.UtcNow);
        foreach (var frame in frames) _ = HandleFrameAsync(frame);
    }

    private async Task HandleFrameAsync(Frame frame)
    {
        // Break out of the ChunkAppended dispatch: responding synchronously would
        // append our TX bytes re-entrantly, making the response reach UI sessions
        // before the request that caused it (inverted timeline, broken Δ pairing).
        await Task.Yield();

        // RTU: a CRC-failed frame must never be answered. TCP has no CRC — the
        // MBAP frame is intact even when the PDU shape is bad, and the spec
        // expects an exception response rather than silence.
        if (frame.Status == FrameStatus.Error && !TcpFraming) return;

        byte unit;
        ReadOnlyMemory<byte> pduMem;
        ushort txId = 0;
        if (TcpFraming)
        {
            if (frame.Bytes.Length < 8) return;
            txId = BinaryPrimitives.ReadUInt16BigEndian(frame.Bytes);
            unit = frame.Bytes[6];
            pduMem = frame.Bytes.AsMemory(7);
        }
        else
        {
            if (frame.Bytes.Length < 4) return;
            unit = frame.Bytes[0];
            pduMem = frame.Bytes.AsMemory(1, frame.Bytes.Length - 3);
        }

        // RTU: ignore other slaves' traffic on the bus. TCP: the unit id is a
        // routing hint — a direct-connected server answers 0xFF (and everything
        // when it is the sole endpoint is out of scope; we honor our id + 0xFF).
        if (TcpFraming)
        {
            if (unit != UnitId && unit != 0xFF && unit != 0) return;
        }
        else if (unit != UnitId && unit != 0)
        {
            return;
        }

        var request = ModbusCodec.TryParseRequestPdu(unit, pduMem.Span);
        byte fcByte = pduMem.Span.Length > 0 ? pduMem.Span[0] : (byte)0;
        // Unknown function → EXC 01 (illegal function); known FC with a broken
        // shape → EXC 03 (illegal data value), per the Modbus spec.
        byte[] responsePdu = request is null
            ? ModbusCodec.BuildExceptionPdu(fcByte, ModbusFunction.IsSupported((byte)(fcByte & 0x7F)) ? (byte)0x03 : (byte)0x01)
            : Process(request);

        ServedRequests++;
        if ((responsePdu[0] & 0x80) != 0) ExceptionsSent++;
        Stats?.Invoke();

        if (unit == 0 && !TcpFraming) return; // RTU broadcast: execute, never answer

        byte[] adu;
        if (TcpFraming)
        {
            adu = new byte[7 + responsePdu.Length];
            BinaryPrimitives.WriteUInt16BigEndian(adu, txId);
            BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(responsePdu.Length + 1));
            adu[6] = unit;
            responsePdu.CopyTo(adu.AsSpan(7));
        }
        else
        {
            adu = ModbusCodec.WrapRtu(unit, responsePdu);
        }

        // Serialize responses: concurrent request batches must not interleave
        // their reply bytes on a single stream.
        await _txSerial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Connection.Transport is TcpServerTransport srv) srv.BumpRequestCount(frame.ClientId);
            await Connection.SendAsync(adu, frame.ClientId).ConfigureAwait(false);
        }
        catch
        {
            // Transport dropped mid-response; nothing to do.
        }
        finally
        {
            _txSerial.Release();
        }
    }

    private byte[] Process(ModbusMessage req)
    {
        try
        {
            switch (req.Function)
            {
                case ModbusFunction.ReadCoils:
                case ModbusFunction.ReadDiscreteInputs:
                {
                    var area = req.Function == ModbusFunction.ReadCoils ? RegisterArea.Coils : RegisterArea.DiscreteInputs;
                    if (req.Quantity is 0 or > ModbusCodec.MaxReadCoils) return ModbusCodec.BuildExceptionPdu(req.Function, 0x03);
                    if (req.Address + req.Quantity > 0x10000) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    if (!Store.AllowUndefined && !Store.IsDefined(area, req.Address, req.Quantity)) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    var bits = Store.ReadBits(area, req.Address, req.Quantity);
                    var data = new byte[(bits.Length + 7) / 8];
                    for (int i = 0; i < bits.Length; i++)
                    {
                        if (bits[i]) data[i / 8] |= (byte)(1 << (i % 8));
                    }

                    return ModbusCodec.BuildReadResponsePdu(req.Function, data);
                }

                case ModbusFunction.ReadHoldingRegisters:
                case ModbusFunction.ReadInputRegisters:
                {
                    var area = req.Function == ModbusFunction.ReadHoldingRegisters ? RegisterArea.HoldingRegisters : RegisterArea.InputRegisters;
                    if (req.Quantity is 0 or > ModbusCodec.MaxReadRegisters) return ModbusCodec.BuildExceptionPdu(req.Function, 0x03);
                    if (req.Address + req.Quantity > 0x10000) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    if (!Store.AllowUndefined && !Store.IsDefined(area, req.Address, req.Quantity)) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    var words = Store.ReadWords(area, req.Address, req.Quantity);
                    var data = new byte[words.Length * 2];
                    for (int i = 0; i < words.Length; i++)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * 2), words[i]);
                    }

                    return ModbusCodec.BuildReadResponsePdu(req.Function, data);
                }

                case ModbusFunction.WriteSingleCoil:
                {
                    if (req.Quantity is not (0x0000 or 0xFF00)) return ModbusCodec.BuildExceptionPdu(req.Function, 0x03);
                    if (!Store.AllowUndefined && !Store.IsDefined(RegisterArea.Coils, req.Address, 1)) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    Store.WriteBits(RegisterArea.Coils, req.Address, new[] { req.Quantity == 0xFF00 });
                    MarkClientWrite(RegisterArea.Coils, req.Address, 1);
                    return ModbusCodec.BuildEchoResponsePdu(req.Function, req.Address, req.Quantity);
                }

                case ModbusFunction.WriteSingleRegister:
                {
                    if (!Store.AllowUndefined && !Store.IsDefined(RegisterArea.HoldingRegisters, req.Address, 1)) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    Store.WriteWords(RegisterArea.HoldingRegisters, req.Address, new[] { req.Quantity });
                    MarkClientWrite(RegisterArea.HoldingRegisters, req.Address, 1);
                    return ModbusCodec.BuildEchoResponsePdu(req.Function, req.Address, req.Quantity);
                }

                case ModbusFunction.WriteMultipleCoils:
                {
                    if (req.Quantity is 0 or > 1968) return ModbusCodec.BuildExceptionPdu(req.Function, 0x03);
                    if (req.Data.Length != (req.Quantity + 7) / 8) return ModbusCodec.BuildExceptionPdu(req.Function, 0x03);
                    if (req.Address + req.Quantity > 0x10000) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    if (!Store.AllowUndefined && !Store.IsDefined(RegisterArea.Coils, req.Address, req.Quantity)) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    var bits = new bool[req.Quantity];
                    for (int i = 0; i < bits.Length; i++)
                    {
                        bits[i] = (req.Data[i / 8] & (1 << (i % 8))) != 0;
                    }

                    Store.WriteBits(RegisterArea.Coils, req.Address, bits);
                    MarkClientWrite(RegisterArea.Coils, req.Address, req.Quantity);
                    return ModbusCodec.BuildEchoResponsePdu(req.Function, req.Address, req.Quantity);
                }

                case ModbusFunction.WriteMultipleRegisters:
                {
                    if (req.Quantity is 0 or > ModbusCodec.MaxWriteRegisters || req.Data.Length != req.Quantity * 2)
                        return ModbusCodec.BuildExceptionPdu(req.Function, 0x03);
                    if (req.Address + req.Quantity > 0x10000) return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    if (!Store.AllowUndefined && !Store.IsDefined(RegisterArea.HoldingRegisters, req.Address, req.Quantity))
                        return ModbusCodec.BuildExceptionPdu(req.Function, 0x02);
                    var words = new ushort[req.Quantity];
                    for (int i = 0; i < words.Length; i++)
                    {
                        words[i] = BinaryPrimitives.ReadUInt16BigEndian(req.Data.AsSpan(i * 2));
                    }

                    Store.WriteWords(RegisterArea.HoldingRegisters, req.Address, words);
                    MarkClientWrite(RegisterArea.HoldingRegisters, req.Address, req.Quantity);
                    return ModbusCodec.BuildEchoResponsePdu(req.Function, req.Address, req.Quantity);
                }

                default:
                    return ModbusCodec.BuildExceptionPdu(req.Function, 0x01);
            }
        }
        catch
        {
            return ModbusCodec.BuildExceptionPdu(req.Function, 0x04);
        }
    }

    /// <summary>Refresh tags covering a client-written range and flag them in the grid.</summary>
    private void MarkClientWrite(RegisterArea area, ushort start, int count)
    {
        SlaveTag[] tags;
        lock (_gate) tags = _tags.ToArray();
        var now = DateTime.UtcNow;
        foreach (var st in tags)
        {
            var t = st.Tag;
            if (t.Area != area || t.Address >= start + count || t.Address + t.RegisterCount <= start) continue;
            st.WrittenByClient = true;
            st.Generator.Kind = st.Generator.Kind == GeneratorKind.Static ? GeneratorKind.Static : st.Generator.Kind;
            if (area.IsBitArea())
            {
                t.UpdateFromBit(Store.ReadBits(area, t.Address, 1)[0], now);
            }
            else
            {
                t.UpdateFromWords(Store.ReadWords(area, t.Address, t.RegisterCount), now);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _generatorTimer.Dispose();
        _flushTimer.Dispose();
        Connection.Store.ChunkAppended -= OnChunkAppended;
    }
}
