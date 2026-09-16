using System.Collections.Frozen;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Domain.SignalDecoding;

/// <summary>Scaling formula over the data bytes that follow the PID in a Mode 01 response.</summary>
public delegate double PidFormula(ReadOnlySpan<byte> data);

/// <summary>One row of the SAE J1979 Mode 01 table.</summary>
public sealed record PidDefinition(Pid Pid, SignalName Name, string Unit, int MinBytes, PidFormula Formula);

/// <summary>
/// Table-driven decoder for OBD-II Mode 01 (current data) positive responses carried in ISO-TP
/// single frames: <c>[PCI] [0x41] [PID] [A] [B] [C] [D]</c>. Signal names and units are the ones
/// a gateway emits in mode P2, so P0 and P2 results compare directly (design D2).
/// </summary>
public static class ObdMode01Decoder
{
    private const byte PositiveResponseService = 0x41;
    private const byte NegativeResponseService = 0x7F;

    private static readonly FrozenDictionary<byte, PidDefinition> Table = BuildTable();

    public static IReadOnlyCollection<PidDefinition> Definitions => Table.Values;

    public static bool Supports(Pid pid) => Table.ContainsKey(pid.Value);

    public static FrameDecodeOutcome Decode(CanFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!frame.Id.IsObdResponse)
        {
            return new FrameDecodeOutcome.Ignored(FrameIgnoreReason.NotObdResponse);
        }

        IsoTpFrameKind kind = IsoTp.Classify(frame, out int length);
        switch (kind)
        {
            case IsoTpFrameKind.SingleFrame:
                break;
            case IsoTpFrameKind.FirstFrame:
            case IsoTpFrameKind.ConsecutiveFrame:
            case IsoTpFrameKind.FlowControl:
                return new FrameDecodeOutcome.Ignored(FrameIgnoreReason.MultiFrameUnsupported);
            default:
                return new FrameDecodeOutcome.Ignored(FrameIgnoreReason.NotSingleFrame);
        }

        ReadOnlySpan<byte> payload = IsoTp.SingleFramePayload(frame, length);
        if (payload[0] == NegativeResponseService)
        {
            return new FrameDecodeOutcome.Ignored(FrameIgnoreReason.NegativeResponse);
        }

        if (payload[0] != PositiveResponseService)
        {
            return new FrameDecodeOutcome.Ignored(FrameIgnoreReason.UnsupportedService);
        }

        if (payload.Length < 2)
        {
            return new FrameDecodeOutcome.Ignored(FrameIgnoreReason.ShortPayload);
        }

        if (!Table.TryGetValue(payload[1], out PidDefinition? definition))
        {
            return new FrameDecodeOutcome.Ignored(FrameIgnoreReason.UnsupportedPid);
        }

        ReadOnlySpan<byte> data = payload[2..];
        if (data.Length < definition.MinBytes)
        {
            return new FrameDecodeOutcome.Ignored(FrameIgnoreReason.ShortPayload);
        }

        return new FrameDecodeOutcome.Decoded(
            new DecodedSignal(definition.Name, definition.Formula(data), definition.Unit));
    }

    private static FrozenDictionary<byte, PidDefinition> BuildTable()
    {
        // SAE J1979 Mode 01 scaling. A = data[0], B = data[1].
        PidDefinition[] rows =
        [
            Row(0x04, "engine_load", "percent", 1, d => d[0] * 100.0 / 255.0),
            Row(0x05, "coolant_temp", "degC", 1, d => d[0] - 40.0),
            Row(0x0B, "intake_pressure", "kPa", 1, d => d[0]),
            Row(0x0C, "engine_rpm", "rpm", 2, d => ((d[0] << 8) | d[1]) / 4.0),
            Row(0x0D, "vehicle_speed", "km_h", 1, d => d[0]),
            Row(0x0F, "intake_temp", "degC", 1, d => d[0] - 40.0),
            Row(0x10, "maf_rate", "g_s", 2, d => ((d[0] << 8) | d[1]) / 100.0),
            Row(0x11, "throttle_position", "percent", 1, d => d[0] * 100.0 / 255.0),
            Row(0x2F, "fuel_level", "percent", 1, d => d[0] * 100.0 / 255.0),
            Row(0x33, "barometric_pressure", "kPa", 1, d => d[0]),
            Row(0x42, "module_voltage", "V", 2, d => ((d[0] << 8) | d[1]) / 1000.0),
            Row(0x46, "ambient_temp", "degC", 1, d => d[0] - 40.0),
            Row(0x5C, "oil_temp", "degC", 1, d => d[0] - 40.0),
            Row(0x5E, "fuel_rate", "L_h", 2, d => ((d[0] << 8) | d[1]) / 20.0),
        ];

        return rows.ToFrozenDictionary(r => r.Pid.Value);
    }

    private static PidDefinition Row(byte pid, string name, string unit, int minBytes, PidFormula formula) =>
        new(new Pid(pid), SignalName.Create(name).Value, unit, minBytes, formula);
}
