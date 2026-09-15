namespace VehicleDiagnostics.Contracts;

/// <summary>
/// The versioned envelope every device→backend message carries (architecture.md §13).
/// Field names are a published contract: changing the meaning or type of any field
/// requires a new <see cref="SchemaVersion"/> and an ADR.
/// </summary>
public sealed record MessageEnvelope<TPayload>
{
    public required int SchemaVersion { get; init; }

    public required string DeviceId { get; init; }

    public required string VehicleId { get; init; }

    /// <summary>Monotonically increasing per device; the basis for deduplication.</summary>
    public required long Sequence { get; init; }

    /// <summary>Device-side timestamp of the sample batch.</summary>
    public required DateTimeOffset TimestampDevice { get; init; }

    /// <summary>W3C Trace Context <c>traceparent</c> value linking all layers of one trace.</summary>
    public required string TraceId { get; init; }

    /// <summary>Active processing mode, <c>"P0"</c>–<c>"P3"</c>.</summary>
    public required string ProcessingMode { get; init; }

    public required TPayload Payload { get; init; }
}
