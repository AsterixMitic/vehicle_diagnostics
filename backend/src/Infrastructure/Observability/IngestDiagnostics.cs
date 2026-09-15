using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace VehicleDiagnostics.Infrastructure.Observability;

/// <summary>
/// The measuring instrument of the ingest path (architecture.md §15): one span per stage,
/// one counter per outcome. Instrumentation is not optional polish — no ingest code path
/// ships without touching this class.
/// </summary>
public static class IngestDiagnostics
{
    public const string ActivitySourceName = "VehicleDiagnostics.Ingest";
    public const string MeterName = "VehicleDiagnostics.Ingest";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> MessagesReceived =
        Meter.CreateCounter<long>("ingest.messages.received", description: "Telemetry messages received from the broker");

    public static readonly Counter<long> MessagesRejected =
        Meter.CreateCounter<long>("ingest.messages.rejected", description: "Messages rejected, tagged by reason");

    public static readonly Counter<long> MessagesDeduplicated =
        Meter.CreateCounter<long>("ingest.messages.deduplicated", description: "Redeliveries collapsed by the store");

    public static readonly Counter<long> MessagesPersisted =
        Meter.CreateCounter<long>("ingest.messages.persisted", description: "Messages whose readings were durably stored");

    public static readonly Counter<long> TraceContinuityLost =
        Meter.CreateCounter<long>("ingest.trace_continuity_lost", description: "Envelopes whose traceparent was malformed");

    public static void CountRejected(string reason) =>
        MessagesRejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
