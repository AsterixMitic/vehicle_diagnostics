namespace VehicleDiagnostics.Contracts.Internal;

/// <summary>
/// Internal bus message: the fate of one telemetry message received from the broker has been
/// decided. Published once per received message by the context that decided it (Telemetry, or
/// Signal Decoding for a raw envelope that yields nothing) and consumed by the Analytics
/// context, which keeps it as a measurement. Identity fields are optional because a rejected
/// message may not have parsed far enough to name them.
/// </summary>
public sealed record TelemetryMessageSettled
{
    public const int Version = 1;

    /// <summary>Unique per settlement; Analytics deduplicates bus redeliveries by it.</summary>
    public required Guid SettlementId { get; init; }

    /// <summary>One of <see cref="SettlementOutcomes"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Machine-readable reason, present only for <see cref="SettlementOutcomes.Rejected"/>.</summary>
    public string? RejectReason { get; init; }

    public string? DeviceId { get; init; }

    public string? VehicleId { get; init; }

    public long? Sequence { get; init; }

    public string? ProcessingMode { get; init; }

    public DateTimeOffset? TimestampDevice { get; init; }

    /// <summary>When the backend received the message; the settlement time if that is unknown.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>When the readings were durably stored; present only for persisted messages.</summary>
    public DateTimeOffset? PersistedAt { get; init; }

    /// <summary>Size in bytes of the message as received, when known.</summary>
    public int? PayloadBytes { get; init; }

    public required int ReadingCount { get; init; }
}

/// <summary>Values of <see cref="TelemetryMessageSettled.Outcome"/>.</summary>
public static class SettlementOutcomes
{
    public const string Persisted = "persisted";
    public const string Duplicate = "duplicate";
    public const string Rejected = "rejected";
}
