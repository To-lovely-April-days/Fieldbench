using System.Runtime.CompilerServices;
using Fieldbench.Core.Lenses;
using Fieldbench.Core.Modbus;
using Fieldbench.Core.Streams;

namespace Fieldbench.Core.Ai;

/// <summary>
/// Deterministic expert engine that produces the same structured explanation
/// shape as the gateway, from real frame analysis. It powers the demo, tests
/// and offline fallback, and encodes the five benchmark scenarios the PRD
/// requires (§6.7): CRC failure triage, exception codes 01–04, garbage/baud
/// detection, dead-air checklists and normal-frame walkthroughs.
/// </summary>
public sealed class OfflineAiEngine : IAiClient
{
    public bool IsOnline => true;

    /// <summary>Delay between streamed chunks; zero in tests.</summary>
    public TimeSpan StreamDelay { get; set; } = TimeSpan.FromMilliseconds(90);

    public async IAsyncEnumerable<AiChunk> ExplainAsync(ExplainContext context, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var explanation = Analyze(context);

        foreach (var word in ChunkWords(explanation.Verdict))
        {
            ct.ThrowIfCancellationRequested();
            yield return new AiChunk(VerdictDelta: word);
            if (StreamDelay > TimeSpan.Zero) await Task.Delay(StreamDelay / 6, ct).ConfigureAwait(false);
        }

        foreach (var cause in explanation.Causes)
        {
            yield return new AiChunk(Cause: cause);
            if (StreamDelay > TimeSpan.Zero) await Task.Delay(StreamDelay, ct).ConfigureAwait(false);
        }

        foreach (var check in explanation.Checks)
        {
            yield return new AiChunk(Check: check);
            if (StreamDelay > TimeSpan.Zero) await Task.Delay(StreamDelay, ct).ConfigureAwait(false);
        }

        foreach (var note in explanation.FieldNotes)
        {
            yield return new AiChunk(FieldNote: note);
            if (StreamDelay > TimeSpan.Zero) await Task.Delay(StreamDelay / 3, ct).ConfigureAwait(false);
        }

        yield return new AiChunk(Done: true);
    }

    private static IEnumerable<string> ChunkWords(string text)
    {
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i += 4)
        {
            yield return string.Join(' ', words.Skip(i).Take(4)) + (i + 4 < words.Length ? " " : "");
        }
    }

    public AiExplanation Analyze(ExplainContext context)
    {
        var frames = context.SelectedFrames;
        if (frames.Count == 0)
        {
            return new AiExplanation { Verdict = "Select one or more frames to analyze." };
        }

        var primary = frames.FirstOrDefault(f => f.Status == FrameStatus.Error)
                      ?? frames.FirstOrDefault(f => f.Status == FrameStatus.Warning)
                      ?? frames[0];

        // Scenario 3: heavy garbage — likely wrong baud rate.
        if (context.RecentErrorRatio > 0.5 && primary.Status == FrameStatus.Error && LooksLikeBaudGarbage(frames))
        {
            return GarbageScenario(context);
        }

        return primary.Status switch
        {
            FrameStatus.Error when primary.StatusTag == "CRC FAIL" => CrcFailScenario(primary, context),
            FrameStatus.Error => MalformedScenario(primary),
            FrameStatus.Warning when primary.ExceptionCode is { } code => ExceptionScenario(primary, code),
            _ when primary.Direction == StreamDirection.Tx && !HasResponseAfter(primary, frames, context) => NoResponseScenario(primary, context),
            _ => NormalScenario(primary),
        };
    }

    // ── Scenario 1: CRC failure ──

    private static AiExplanation CrcFailScenario(Frame frame, ExplainContext context)
    {
        var e = new AiExplanation();
        var bytes = frame.Bytes;
        ushort received = bytes.Length >= 2 ? (ushort)(bytes[^2] | (bytes[^1] << 8)) : (ushort)0;
        ushort computed = bytes.Length >= 4 ? Checksums.Crc16Modbus(bytes.AsSpan(0, bytes.Length - 2)) : (ushort)0;
        int bitDiff = System.Numerics.BitOperations.PopCount((uint)(received ^ computed));
        bool byteSwapped = bytes.Length >= 4 && (ushort)((received >> 8) | (received << 8)) == computed;

        string frameDesc = frame.UnitId is { } u && frame.FunctionCode is { } fc
            ? $"Valid FC{fc:00} {(frame.Role == FrameRole.MasterToSlave ? "request" : "response")} shape from slave {u} with {Math.Max(0, bytes.Length - 5)} data bytes"
            : "A frame with plausible Modbus structure";

        if (byteSwapped)
        {
            e.Verdict = $"{frameDesc}, but the checksum bytes arrive swapped: received {bytes[^2]:X2} {bytes[^1]:X2}, expected {(byte)(computed & 0xFF):X2} {(byte)(computed >> 8):X2} in reverse order. The sender is emitting CRC high byte first — a byte-order bug in the device firmware, not line noise.";
            e.Causes.Add(new AiCause("Device sends CRC in reversed byte order", 0.85));
            e.Causes.Add(new AiCause("Vendor uses a non-standard CRC convention", 0.35));
            e.Causes.Add(new AiCause("Coincidental double corruption", 0.05));
            e.Checks.Add(new AiCheck("Confirm every frame from this device shows the same swap"));
            e.Checks.Add(new AiCheck("Check the device manual for a CRC order setting"));
            return e;
        }

        if (bitDiff <= 2 && context.RecentErrorRatio < 0.3)
        {
            e.Verdict = $"{frameDesc}, but received checksum {bytes[^2]:X2} {bytes[^1]:X2} ≠ computed {(byte)(computed & 0xFF):X2} {(byte)(computed >> 8):X2} — a {(bitDiff == 1 ? "single-bit" : "two-bit")} error, most likely corruption on the wire rather than framing or baud mismatch.";
            e.Causes.Add(new AiCause("Electrical noise on A/B lines", 0.78));
            e.Causes.Add(new AiCause("Missing 120 Ω termination", 0.44));
            e.Causes.Add(new AiCause("Marginal baud timing at slave", 0.16));
            e.Checks.Add(new AiCheck("Verify bus termination"));
            e.Checks.Add(new AiCheck("Check cable shield and routing"));
            e.Checks.Add(new AiCheck("Retry read", "↻"));
            return e;
        }

        e.Verdict = $"{frameDesc}, but the CRC is wrong by {bitDiff} bits (received {bytes[^2]:X2} {bytes[^1]:X2}, computed {(byte)(computed & 0xFF):X2} {(byte)(computed >> 8):X2}) and {context.RecentErrorRatio:P0} of recent traffic also fails. Multiple corrupted bits across many frames points at a serial parameter mismatch or severe interference, not a single glitch.";
        e.Causes.Add(new AiCause("Baud rate mismatch between master and slave", 0.62));
        e.Causes.Add(new AiCause("Wrong parity / stop bit configuration", 0.48));
        e.Causes.Add(new AiCause("Severe EMI or a failing transceiver", 0.30));
        e.Checks.Add(new AiCheck($"Confirm both ends run {context.ConnectionParams}"));
        e.Checks.Add(new AiCheck("Try the parameter sweep in Scan slaves"));
        e.Checks.Add(new AiCheck("Inspect wiring near motors / VFDs"));
        return e;
    }

    // ── Scenario 2: exception codes ──

    private static AiExplanation ExceptionScenario(Frame frame, byte code)
    {
        var e = new AiExplanation();
        string fcName = frame.FunctionCode is { } fc ? $"FC{fc:00} ({ModbusFunction.Name(fc)})" : "the request";
        e.Verdict = $"Slave {frame.UnitId} answered {fcName} with exception {code:00} — {ModbusExceptions.Name(code)}. {ModbusExceptions.Hint(code)}";

        switch (code)
        {
            case 0x01:
                e.Causes.Add(new AiCause("Device does not implement this function code", 0.80));
                e.Causes.Add(new AiCause("Function disabled by device configuration", 0.35));
                e.Causes.Add(new AiCause("Wrong device model / firmware level", 0.20));
                e.Checks.Add(new AiCheck("List supported FCs from the device manual"));
                e.Checks.Add(new AiCheck("Try FC03 instead of FC04 (or vice versa)"));
                break;
            case 0x02:
                e.Causes.Add(new AiCause("Start address or count beyond the device map", 0.82));
                e.Causes.Add(new AiCause("Off-by-one: 40001 documentation vs 0-based protocol address", 0.60));
                e.Causes.Add(new AiCause("Register exists in a different area (3x vs 4x)", 0.30));
                e.Checks.Add(new AiCheck("Re-check start address and quantity against the point map"));
                e.Checks.Add(new AiCheck("Try reading a single register at the start address"));
                e.Checks.Add(new AiCheck("Confirm the address base (1-based vs 0-based)"));
                break;
            case 0x03:
                e.Causes.Add(new AiCause("Quantity outside the allowed range for this FC", 0.70));
                e.Causes.Add(new AiCause("Written value rejected by the device limits", 0.45));
                e.Causes.Add(new AiCause("Byte count inconsistent with quantity", 0.20));
                e.Checks.Add(new AiCheck("Keep read counts ≤ 125 registers / ≤ 2000 coils"));
                e.Checks.Add(new AiCheck("Check the writable range for the target register"));
                break;
            case 0x04:
                e.Causes.Add(new AiCause("Device internal fault while executing the request", 0.65));
                e.Causes.Add(new AiCause("Subsystem behind the device is offline", 0.40));
                e.Causes.Add(new AiCause("Device overloaded — polling too fast", 0.25));
                e.Checks.Add(new AiCheck("Read the device diagnostic/status registers"));
                e.Checks.Add(new AiCheck("Increase poll interval and retry"));
                e.Checks.Add(new AiCheck("Power-cycle the device if the fault persists"));
                break;
            default:
                e.Causes.Add(new AiCause(ModbusExceptions.Name(code), 0.6));
                e.Checks.Add(new AiCheck("Consult the device documentation"));
                break;
        }

        return e;
    }

    // ── Scenario 3: garbage / baud mismatch ──

    private static bool LooksLikeBaudGarbage(IReadOnlyList<Frame> frames)
    {
        int suspicious = 0, total = 0;
        foreach (var f in frames)
        {
            foreach (var b in f.Bytes)
            {
                total++;
                // Classic wrong-baud symptoms: framing errors collapse bytes toward
                // 0x00/0xFF/0x80/0xFE patterns and high-bit-set values.
                if (b is 0x00 or 0xFF or 0x80 or 0xFE or 0x7F || (b & 0x80) != 0) suspicious++;
            }
        }

        return total > 0 && (double)suspicious / total > 0.55;
    }

    private static AiExplanation GarbageScenario(ExplainContext context)
    {
        var e = new AiExplanation
        {
            Verdict = $"The selected bytes do not form valid frames and {context.RecentErrorRatio:P0} of recent traffic is unreadable, with the byte histogram skewed toward 0x00 / 0xFF / high-bit values — the classic signature of a UART sampling at the wrong speed. The line itself is alive; the parameters are wrong.",
        };
        e.Causes.Add(new AiCause("Baud rate mismatch (most common: 9600 vs 19200)", 0.80));
        e.Causes.Add(new AiCause("Wrong parity or data bits", 0.45));
        e.Causes.Add(new AiCause("RS-485 A/B lines swapped (idle level inverted)", 0.35));
        e.Checks.Add(new AiCheck($"Current setting is {context.ConnectionParams} — try the device's documented default"));
        e.Checks.Add(new AiCheck("Run Scan slaves with the parameter sweep enabled"));
        e.Checks.Add(new AiCheck("Swap A/B wires if the transceiver LEDs look inverted"));
        return e;
    }

    // ── Scenario 4: request without response ──

    private static bool HasResponseAfter(Frame request, IReadOnlyList<Frame> frames, ExplainContext context)
    {
        // A response for our unit in the selection or in the trailing context?
        foreach (var f in frames)
        {
            if (f.Direction == StreamDirection.Rx && f.UnitId == request.UnitId && f.TimestampUtc >= request.TimestampUtc)
                return true;
        }

        return context.ContextSummaries.Any(s => s.Contains("RX", StringComparison.Ordinal));
    }

    private static AiExplanation NoResponseScenario(Frame frame, ExplainContext context)
    {
        var e = new AiExplanation
        {
            Verdict = $"The request to slave {frame.UnitId} ({frame.Summary}) went out {(frame.StatusTag is null ? "well-formed with a valid CRC" : "")} but nothing came back within the timeout. Dead air almost always means the request never reached the device, or the device's answer cannot reach you.",
        };
        e.Causes.Add(new AiCause($"No device at address {frame.UnitId} on this bus", 0.62));
        e.Causes.Add(new AiCause("A/B lines swapped or broken wiring", 0.55));
        e.Causes.Add(new AiCause("Baud/parity mismatch — device can't decode the request", 0.42));
        e.Causes.Add(new AiCause("Missing termination on a long bus", 0.25));
        e.Checks.Add(new AiCheck("Run Scan slaves 1–247 to find who answers", "◎"));
        e.Checks.Add(new AiCheck("Swap A and B wires (the #1 field fix)"));
        e.Checks.Add(new AiCheck($"Verify device parameters match {context.ConnectionParams}"));
        e.Checks.Add(new AiCheck("Check 120 Ω termination at both bus ends"));
        return e;
    }

    // ── Scenario 5: normal frame walkthrough ──

    private static AiExplanation NormalScenario(Frame frame)
    {
        var e = new AiExplanation();
        string role = frame.Role switch
        {
            FrameRole.MasterToSlave => "a request from the master",
            FrameRole.SlaveToMaster => "a response from the slave",
            _ => "a frame",
        };
        e.Verdict = frame.FunctionCode is { } fc
            ? $"This is {role}: {ModbusFunction.Name(fc)} (FC{fc:00}) addressed to unit {frame.UnitId}. {frame.Summary}. Structure and checksum are valid — every field decodes cleanly."
            : $"This is {role}. {frame.Summary}.";

        foreach (var field in frame.Fields)
        {
            e.FieldNotes.Add(($"{field.Name}", $"{field.Value}{(field.Detail is null ? "" : $" — {field.Detail}")}"));
        }

        if (frame.DeltaMs is { } delta)
        {
            e.FieldNotes.Add(("Response time", $"{delta:0} ms after the request"));
        }

        return e;
    }

    private static AiExplanation MalformedScenario(Frame frame)
    {
        var e = new AiExplanation
        {
            Verdict = $"These {frame.Bytes.Length} bytes do not decode as a complete frame. They may be a fragment cut by a pause, or noise between frames.",
        };
        e.Causes.Add(new AiCause("Frame fragment — silence gap split mid-frame", 0.55));
        e.Causes.Add(new AiCause("Line noise between valid frames", 0.40));
        e.Checks.Add(new AiCheck("Increase the split gap in the toolbar"));
        e.Checks.Add(new AiCheck("Select surrounding chunks and re-run the explain"));
        return e;
    }

    // ── point map extraction (offline text parser) ──

    public Task<PointMapExtraction> ExtractPointMapAsync(string? tableText, byte[]? image, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableText))
        {
            return Task.FromResult(new PointMapExtraction
            {
                Error = image is null
                    ? "Paste a register table (text) or an image of the manual page."
                    : "Image extraction requires the AI gateway — offline mode parses pasted table text only.",
            });
        }

        return Task.FromResult(PointMapTextParser.Parse(tableText));
    }
}
