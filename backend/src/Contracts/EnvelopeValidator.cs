namespace VehicleDiagnostics.Contracts;

/// <summary>Outcome of envelope validation. A malformed trace context does not invalidate a message.</summary>
public sealed record EnvelopeValidationResult
{
    public required IReadOnlyList<EnvelopeError> Errors { get; init; }

    /// <summary>False when <c>traceId</c> is not valid W3C <c>traceparent</c>; the message is still processed.</summary>
    public required bool TraceContextIsValid { get; init; }

    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Structural validation of a parsed telemetry envelope against the message-contracts spec:
/// supported schema version, present and well-formed identity fields, topic/envelope identity
/// agreement, known processing mode. Semantic validation of individual readings (finite value,
/// non-blank name) is the ingest use case's job, via the domain factories.
/// </summary>
public static class EnvelopeValidator
{
    private static readonly string[] KnownProcessingModes = ["P0", "P1", "P2", "P3"];

    public static EnvelopeValidationResult Validate(
        MessageEnvelope<SignalReadingsPayload> envelope,
        string topicVehicleId,
        string topicDeviceId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        List<EnvelopeError> errors = [];

        if (envelope.SchemaVersion != EnvelopeSerializer.CurrentSchemaVersion)
        {
            errors.Add(new EnvelopeError(
                EnvelopeError.UnsupportedSchemaVersion,
                $"Schema version {envelope.SchemaVersion} is not supported; this backend accepts {EnvelopeSerializer.CurrentSchemaVersion}."));
        }

        if (string.IsNullOrWhiteSpace(envelope.DeviceId))
        {
            errors.Add(new EnvelopeError(EnvelopeError.MissingField, "'deviceId' is empty."));
        }

        if (string.IsNullOrWhiteSpace(envelope.VehicleId))
        {
            errors.Add(new EnvelopeError(EnvelopeError.MissingField, "'vehicleId' is empty."));
        }

        if (envelope.Sequence < 0)
        {
            errors.Add(new EnvelopeError(EnvelopeError.InvalidField, "'sequence' must be non-negative."));
        }

        if (envelope.TimestampDevice == default)
        {
            errors.Add(new EnvelopeError(EnvelopeError.MissingField, "'timestampDevice' is missing."));
        }

        if (!KnownProcessingModes.Contains(envelope.ProcessingMode, StringComparer.Ordinal))
        {
            errors.Add(new EnvelopeError(
                EnvelopeError.InvalidField, $"'processingMode' must be one of P0–P3, got '{envelope.ProcessingMode}'."));
        }

        if (!string.IsNullOrWhiteSpace(envelope.DeviceId) && !string.Equals(envelope.DeviceId, topicDeviceId, StringComparison.Ordinal))
        {
            errors.Add(new EnvelopeError(
                EnvelopeError.IdentityMismatch,
                $"Topic deviceId '{topicDeviceId}' does not match envelope deviceId '{envelope.DeviceId}'."));
        }

        if (!string.IsNullOrWhiteSpace(envelope.VehicleId) && !string.Equals(envelope.VehicleId, topicVehicleId, StringComparison.Ordinal))
        {
            errors.Add(new EnvelopeError(
                EnvelopeError.IdentityMismatch,
                $"Topic vehicleId '{topicVehicleId}' does not match envelope vehicleId '{envelope.VehicleId}'."));
        }

        // An envelope with nothing to persist would still consume its sequence number for
        // deduplication; rejecting it keeps "persisted" and "accepted" in lockstep.
        if (envelope.Payload.Readings is null or { Count: 0 })
        {
            errors.Add(new EnvelopeError(EnvelopeError.EmptyPayload, "'payload.readings' is empty."));
        }

        return new EnvelopeValidationResult
        {
            Errors = errors,
            TraceContextIsValid = TraceParent.IsValid(envelope.TraceId),
        };
    }
}
