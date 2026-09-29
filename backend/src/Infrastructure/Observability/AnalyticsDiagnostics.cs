using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace VehicleDiagnostics.Infrastructure.Observability;

/// <summary>Spans and counters of the Analytics context's recording stage.</summary>
public static class AnalyticsDiagnostics
{
    public const string ActivitySourceName = "VehicleDiagnostics.Analytics";
    public const string MeterName = "VehicleDiagnostics.Analytics";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> SettlementsRecorded =
        Meter.CreateCounter<long>("analytics.settlements.recorded", description: "Settlements stored in the measurement log, tagged by outcome");

    public static readonly Counter<long> SettlementsRedelivered =
        Meter.CreateCounter<long>("analytics.settlements.redelivered", description: "Bus redeliveries of settlements already in the log");

    public static readonly Counter<long> SettlementsInvalid =
        Meter.CreateCounter<long>("analytics.settlements.invalid", description: "Settlements violating the measurement invariants, tagged by reason");
}
