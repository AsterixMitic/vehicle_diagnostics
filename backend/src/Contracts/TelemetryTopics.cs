using System.Diagnostics.CodeAnalysis;

namespace VehicleDiagnostics.Contracts;

/// <summary>Which telemetry stream a <c>tel/*</c> topic carries.</summary>
public enum TelemetrySubtype
{
    /// <summary>Raw CAN frames (processing mode P0).</summary>
    Raw,

    /// <summary>Decoded physical signals (processing mode P2).</summary>
    Signals,
}

/// <summary>
/// The standard MQTT topic hierarchy <c>veh/{vehicleId}/dev/{deviceId}/&lt;class&gt;/&lt;subtype&gt;</c>
/// (architecture.md §13). The structure is identical across all processing modes; only
/// which topic is used varies.
/// </summary>
public static class TelemetryTopics
{
    /// <summary>Subscription filter covering the signals telemetry of every vehicle and device.</summary>
    public const string SignalsFilter = "veh/+/dev/+/tel/signals";

    /// <summary>Subscription filter covering the raw-frame telemetry of every vehicle and device.</summary>
    public const string RawFilter = "veh/+/dev/+/tel/raw";

    public static string Signals(string vehicleId, string deviceId) =>
        $"veh/{vehicleId}/dev/{deviceId}/tel/signals";

    public static string Raw(string vehicleId, string deviceId) =>
        $"veh/{vehicleId}/dev/{deviceId}/tel/raw";

    /// <summary>
    /// Extracts the identity segments and stream subtype from a concrete <c>tel/*</c> topic.
    /// Returns false for any other shape.
    /// </summary>
    public static bool TryParseTelemetry(
        string topic,
        [NotNullWhen(true)] out string? vehicleId,
        [NotNullWhen(true)] out string? deviceId,
        out TelemetrySubtype subtype)
    {
        vehicleId = null;
        deviceId = null;
        subtype = default;
        if (string.IsNullOrEmpty(topic))
        {
            return false;
        }

        string[] segments = topic.Split('/');
        if (segments is not ["veh", { Length: > 0 } veh, "dev", { Length: > 0 } dev, "tel", var kind])
        {
            return false;
        }

        switch (kind)
        {
            case "signals":
                subtype = TelemetrySubtype.Signals;
                break;
            case "raw":
                subtype = TelemetrySubtype.Raw;
                break;
            default:
                return false;
        }

        vehicleId = veh;
        deviceId = dev;
        return true;
    }

    /// <summary>Parses a concrete <c>tel/signals</c> topic only.</summary>
    public static bool TryParseSignals(
        string topic,
        [NotNullWhen(true)] out string? vehicleId,
        [NotNullWhen(true)] out string? deviceId)
    {
        bool parsed = TryParseTelemetry(topic, out vehicleId, out deviceId, out TelemetrySubtype subtype)
            && subtype == TelemetrySubtype.Signals;
        if (!parsed)
        {
            vehicleId = null;
            deviceId = null;
        }

        return parsed;
    }
}
