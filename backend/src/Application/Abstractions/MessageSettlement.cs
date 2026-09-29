namespace VehicleDiagnostics.Application.Abstractions;

/// <summary>
/// What the backend knew about a message the moment it arrived from the broker. Carried with
/// the message through every hop, so a message decoded on the backend is measured against the
/// time and size at which it arrived, not those of an internal hop.
/// </summary>
public sealed record ReceiptInfo(DateTimeOffset ReceivedAt, int PayloadBytes);

public enum SettlementOutcome
{
    Persisted,
    Duplicate,
    Rejected,
}

/// <summary>
/// The decided fate of one received telemetry message, reported once for measurement. Shared by
/// the Telemetry and Signal Decoding contexts, both of which decide fates, so it lives outside
/// both context namespaces. Identity fields are as received, unvalidated: a rejected message
/// may carry values that failed validation, and they are exactly what should be measured.
/// </summary>
/// <param name="Receipt">Unknown only for a message that crossed the bus in a version-1 contract.</param>
/// <param name="PersistedAt">Present only for <see cref="SettlementOutcome.Persisted"/>.</param>
public sealed record MessageSettlement(
    SettlementOutcome Outcome,
    string? RejectReason,
    string? DeviceId,
    string? VehicleId,
    long? Sequence,
    string? ProcessingMode,
    DateTimeOffset? TimestampDevice,
    ReceiptInfo? Receipt,
    DateTimeOffset? PersistedAt,
    int ReadingCount)
{
    /// <summary>A message rejected before its identity could be read, e.g. on an unknown topic.</summary>
    public static MessageSettlement RejectedAnonymous(string reason, ReceiptInfo receipt, string? deviceId, string? vehicleId) =>
        new(SettlementOutcome.Rejected, reason, deviceId, vehicleId, null, null, null, receipt, null, 0);
}

/// <summary>
/// Outbound port: report a settlement for measurement. Implementations must never throw; a lost
/// measurement is counted, and must not turn into a retried, re-counted ingestion.
/// </summary>
public interface IMessageSettlementReporter
{
    Task ReportAsync(MessageSettlement settlement, CancellationToken cancellationToken);
}

/// <summary>Turns validation codes into the single reason a settlement carries.</summary>
public static class SettlementReasons
{
    /// <summary>Longest reason the measurement store keeps.</summary>
    public const int MaxLength = 64;

    /// <summary>Distinct codes in first-seen order, joined with <c>+</c>, e.g. <c>required+invalid</c>.</summary>
    public static string Join(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        string joined = string.Join('+', codes.Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.Ordinal));
        if (joined.Length == 0)
        {
            return "unspecified";
        }

        return joined.Length <= MaxLength ? joined : joined[..MaxLength];
    }
}
