using System.Diagnostics.CodeAnalysis;

namespace VehicleDiagnostics.Contracts;

/// <summary>
/// The standard MQTT topic hierarchy <c>veh/{vehicleId}/dev/{deviceId}/&lt;class&gt;/&lt;subtype&gt;</c>
/// (architecture.md §13). The structure is identical across all processing modes; only
/// which topic is used varies.
/// </summary>
public static class TelemetryTopics
{
    /// <summary>Subscription filter covering the signals telemetry of every vehicle and device.</summary>
    public const string SignalsFilter = "veh/+/dev/+/tel/signals";

    public static string Signals(string vehicleId, string deviceId) =>
        $"veh/{vehicleId}/dev/{deviceId}/tel/signals";

    /// <summary>
    /// Extracts the identity segments from a concrete <c>tel/signals</c> topic.
    /// Returns false for any other shape.
    /// </summary>
    public static bool TryParseSignals(
        string topic,
        [NotNullWhen(true)] out string? vehicleId,
        [NotNullWhen(true)] out string? deviceId)
    {
        vehicleId = null;
        deviceId = null;
        if (string.IsNullOrEmpty(topic))
        {
            return false;
        }

        string[] segments = topic.Split('/');
        if (segments is not ["veh", { Length: > 0 } veh, "dev", { Length: > 0 } dev, "tel", "signals"])
        {
            return false;
        }

        vehicleId = veh;
        deviceId = dev;
        return true;
    }
}
