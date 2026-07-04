using System.Buffers.Binary;
using System.Diagnostics;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Sessions;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Master;

public sealed class ModbusRequestResult
{
    public bool Success { get; init; }

    public ModbusMessage? Response { get; init; }

    public bool TimedOut { get; init; }

    public byte? ExceptionCode { get; init; }

    public string? Error { get; init; }

    public double ElapsedMs { get; init; }

    public int Attempts { get; init; }

    public string HumanError => TimedOut
        ? "No response — check slave address, wiring (A/B swap), termination and baud rate."
        : ExceptionCode is { } c
            ? $"{ModbusExceptions.Name(c)} — {ModbusExceptions.Hint(c)}"
            : Error ?? "";
}

/// <summary>
/// Modbus master: builds ADUs (RTU CRC / TCP MBAP), sends through the connection
/// (so every byte lands in the shared stream), and matches responses with
/// timeout + retry. All requests on one connection are serialized — multiple
/// poll tasks interleave safely (PRD: parallel tasks, serial scheduling).
/// </summary>
public sealed class ModbusMasterEngine : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _gate = new();
    private readonly IProtocolLens _rxLens;
    private ushort _nextTransaction = 1;
    private PendingRequest? _pending;
    private bool _disposed;

    private sealed class PendingRequest
    {
        public required byte Unit { get; init; }
        public required byte Function { get; init; }
        public ushort? TransactionId { get; init; }
        /// <summary>Expected data byte count for read responses — rejects a late
        /// response from an earlier, differently-sized request (RTU has no txid).</summary>
        public int? ExpectedDataLength { get; init; }
        public required TaskCompletionSource<ModbusMessage> Tcs { get; init; }
    }

    public ModbusMasterEngine(Connection connection, bool tcpFraming)
    {
        Connection = connection;
        TcpFraming = tcpFraming;
        Map = new RegisterMap();

        // Private response parser, independent of whatever lens the UI shows.
        _rxLens = tcpFraming
            ? new ModbusTcpLens { Perspective = LensPerspective.Master }
            : new ModbusRtuLens
            {
                Perspective = LensPerspective.Master,
                CharTimeSecondsProvider = () => connection.Config.CharTimeSeconds(),
            };
        connection.Store.ChunkAppended += OnChunkAppended;
        _flushTimer = new Timer(_ => FlushTick(), null, 20, 20);
    }

    private readonly Timer _flushTimer;

    public Connection Connection { get; }

    public bool TcpFraming { get; }

    public RegisterMap Map { get; }

    public int TimeoutMs { get; set; } = 1000;

    public int Retries { get; set; } = 3;

    public byte DefaultUnit { get; set; } = 1;

    /// <summary>Raised after every completed request (poll bookkeeping, scanner progress).</summary>
    public event Action<ModbusRequestResult>? RequestCompleted;

    private void OnChunkAppended(StreamChunk chunk)
    {
        if (chunk.Direction != StreamDirection.Rx) return;
        IReadOnlyList<Frame> frames;
        lock (_gate) frames = _rxLens.Feed(chunk);
        Complete(frames);
    }

    private void FlushTick()
    {
        if (_disposed) return;
        IReadOnlyList<Frame> frames;
        lock (_gate) frames = _rxLens.FlushPending(DateTime.UtcNow);
        Complete(frames);
    }

    private void Complete(IReadOnlyList<Frame> frames)
    {
        foreach (var frame in frames)
        {
            PendingRequest? pending;
            lock (_gate) pending = _pending;
            if (pending is null) continue;

            if (frame.Status == FrameStatus.Error)
            {
                // CRC-failed response for our outstanding request: fail fast so retry kicks in.
                pending.Tcs.TrySetException(new ModbusCrcException());
                continue;
            }

            if (frame.UnitId != pending.Unit) continue;
            if (frame.FunctionCode != pending.Function) continue;
            if (TcpFraming && pending.TransactionId is { } tx)
            {
                if (frame.Bytes.Length < 2 || BinaryPrimitives.ReadUInt16BigEndian(frame.Bytes) != tx) continue;
            }

            var msg = RebuildMessage(frame);
            if (msg is null) continue;
            if (msg.Kind == ModbusMessageKind.Response
                && pending.ExpectedDataLength is { } expected
                && msg.Data.Length > 0 && msg.Data.Length != expected)
            {
                // Shape mismatch: almost certainly the late answer to a previous
                // request of the same unit+FC. Ignore it; the timeout/retry handles us.
                continue;
            }

            pending.Tcs.TrySetResult(msg);
        }
    }

    private ModbusMessage? RebuildMessage(Frame frame)
    {
        var pduSpan = TcpFraming
            ? frame.Bytes.AsSpan(7)
            : frame.Bytes.AsSpan(1, Math.Max(0, frame.Bytes.Length - 3));
        byte unit = TcpFraming ? frame.Bytes[6] : frame.Bytes[0];
        return ModbusCodec.TryParseResponsePdu(unit, pduSpan);
    }

    /// <summary>Send one PDU with timeout + retry. Serialized per connection.</summary>
    public async Task<ModbusRequestResult> ExecuteAsync(byte unit, byte[] pdu, CancellationToken ct = default, int? timeoutMs = null, int? retries = null)
    {
        try
        {
            await _serial.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return new ModbusRequestResult { Success = false, Error = "engine disposed" };
        }

        try
        {
            int attempts = Math.Max(1, retries ?? Retries);
            int timeout = timeoutMs ?? TimeoutMs;
            var sw = Stopwatch.StartNew();
            string? lastError = null;

            // Broadcast (unit 0): slaves execute but never answer — send once, done.
            if (unit == 0)
            {
                var bAdu = TcpFraming ? BuildTcpAdu(0, pdu, out _) : ModbusCodec.WrapRtu(0, pdu);
                await Connection.SendAsync(bAdu, 0, ct).ConfigureAwait(false);
                var broadcast = new ModbusRequestResult { Success = true, ElapsedMs = sw.Elapsed.TotalMilliseconds, Attempts = 1 };
                RequestCompleted?.Invoke(broadcast);
                return broadcast;
            }

            int? expectedData = ExpectedReadDataLength(pdu);

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var tcs = new TaskCompletionSource<ModbusMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                ushort? txId = null;
                byte[] adu = TcpFraming ? BuildTcpAdu(unit, pdu, out txId) : ModbusCodec.WrapRtu(unit, pdu);

                lock (_gate)
                {
                    _pending = new PendingRequest
                    {
                        Unit = unit,
                        Function = (byte)(pdu[0] & 0x7F),
                        TransactionId = txId,
                        ExpectedDataLength = expectedData,
                        Tcs = tcs,
                    };
                }

                try
                {
                    await Connection.SendAsync(adu, 0, ct).ConfigureAwait(false);
                    var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct)).ConfigureAwait(false);
                    if (done == tcs.Task)
                    {
                        var msg = await tcs.Task.ConfigureAwait(false);
                        var result = new ModbusRequestResult
                        {
                            Success = msg.Kind != ModbusMessageKind.Exception,
                            Response = msg,
                            ExceptionCode = msg.Kind == ModbusMessageKind.Exception ? msg.ExceptionCode : null,
                            ElapsedMs = sw.Elapsed.TotalMilliseconds,
                            Attempts = attempt,
                        };
                        RequestCompleted?.Invoke(result);
                        return result;
                    }

                    lastError = "timeout";
                }
                catch (ModbusCrcException)
                {
                    lastError = "CRC error in response";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
                finally
                {
                    lock (_gate) _pending = null;
                }
            }

            var failed = new ModbusRequestResult
            {
                Success = false,
                TimedOut = lastError == "timeout",
                Error = lastError,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
                Attempts = attempts,
            };
            RequestCompleted?.Invoke(failed);
            return failed;
        }
        finally
        {
            _serial.Release();
        }
    }

    private byte[] BuildTcpAdu(byte unit, byte[] pdu, out ushort? txId)
    {
        ushort tx;
        lock (_gate) tx = _nextTransaction++;
        txId = tx;
        var adu = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(adu, tx);
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(pdu.Length + 1));
        adu[6] = unit;
        pdu.CopyTo(adu.AsSpan(7));
        return adu;
    }

    /// <summary>For read requests, the exact data length a matching response must carry.</summary>
    private static int? ExpectedReadDataLength(byte[] pdu)
    {
        if (pdu.Length < 5) return null;
        int quantity = (pdu[3] << 8) | pdu[4];
        return pdu[0] switch
        {
            ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs => (quantity + 7) / 8,
            ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters => quantity * 2,
            _ => null,
        };
    }

    // ── convenience operations ──

    public async Task<ModbusRequestResult> ReadAsync(RegisterArea area, ushort start, ushort count, byte? unit = null, CancellationToken ct = default)
    {
        byte fc = RegisterAreaInfo.ReadFcForArea(area);
        var result = await ExecuteAsync(unit ?? DefaultUnit, ModbusCodec.BuildReadRequestPdu(fc, start, count), ct).ConfigureAwait(false);
        if (result is { Success: true, Response: { } msg })
        {
            if (area.IsBitArea()) Map.ApplyBitRead(area, start, count, msg.Data, DateTime.UtcNow);
            else Map.ApplyWordRead(area, start, msg.Data, DateTime.UtcNow);
        }

        return result;
    }

    /// <summary>Write a tag from a grid edit — picks FC 05/06/0F/10 automatically (or honor a forced FC).</summary>
    public async Task<ModbusRequestResult> WriteTagAsync(RegisterTag tag, double scaledValue, byte? unit = null, byte? forceFc = null, CancellationToken ct = default)
    {
        byte u = unit ?? DefaultUnit;
        if (tag.Area == RegisterArea.Coils)
        {
            bool on = scaledValue != 0;
            byte fc = forceFc ?? ModbusFunction.WriteSingleCoil;
            var pdu = fc == ModbusFunction.WriteMultipleCoils
                ? ModbusCodec.BuildWriteMultipleCoilsPdu(tag.Address, new[] { on })
                : ModbusCodec.BuildWriteSingleCoilPdu(tag.Address, on);
            return await ExecuteAsync(u, pdu, ct).ConfigureAwait(false);
        }

        var words = tag.EncodeForWrite(scaledValue);
        byte chosen = forceFc ?? (words.Length == 1 ? ModbusFunction.WriteSingleRegister : ModbusFunction.WriteMultipleRegisters);
        var writePdu = chosen == ModbusFunction.WriteSingleRegister && words.Length == 1
            ? ModbusCodec.BuildWriteSingleRegisterPdu(tag.Address, words[0])
            : ModbusCodec.BuildWriteMultipleRegistersPdu(tag.Address, words);
        var result = await ExecuteAsync(u, writePdu, ct).ConfigureAwait(false);

        // Read back so the grid reflects the device truth immediately.
        if (result.Success)
        {
            await ReadAsync(tag.Area, tag.Address, (ushort)tag.RegisterCount, u, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Advanced mode: raw PDU passthrough.</summary>
    public Task<ModbusRequestResult> SendRawPduAsync(byte unit, byte[] pdu, CancellationToken ct = default) =>
        ExecuteAsync(unit, pdu, ct);

    public void Dispose()
    {
        _disposed = true;
        _flushTimer.Dispose();
        Connection.Store.ChunkAppended -= OnChunkAppended;
        _serial.Dispose();
    }
}

internal sealed class ModbusCrcException : Exception;
