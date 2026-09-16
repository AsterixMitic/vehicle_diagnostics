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
/// agreement, known processing mode, plus payload-shape rules per stream. Semantic validation of
/// individual readings (finite value, non-blank name) is the ingest use case's job, via the
/// domain factories.
/// </summary>
public static class EnvelopeValidator
{
    private const uint MaxStandardId = 0x7FF;
    private const uint MaxExtendedId = 0x1FFFFFFF;
    private const int MaxDataHexLength = 16;

    private static readonly string[] KnownProcessingModes = ["P0", "P1", "P2", "P3"];

    public static EnvelopeValidationResult Validate(
        MessageEnvelope<SignalReadingsPayload> envelope,
        string topicVehicleId,
        string topicDeviceId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        List<EnvelopeError> errors = ValidateCommon(envelope, topicVehicleId, topicDeviceId);

        // An envelope with nothing to persist would still consume its sequence number for
        // deduplication; rejecting it keeps "persisted" and "accepted" in lockstep.
        if (envelope.Payload.Readings is null or { Count: 0 })
        {
            errors.Add(new EnvelopeError(EnvelopeError.EmptyPayload, "'payload.readings' is empty."));
        }

        return Finish(envelope, errors);
    }

    public static EnvelopeValidationResult Validate(
        MessageEnvelope<RawFramesPayload> envelope,
        string topicVehicleId,
        string topicDeviceId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        List<EnvelopeError> errors = ValidateCommon(envelope, topicVehicleId, topicDeviceId);

        if (envelope.Payload.Frames is null or { Count: 0 })
        {
            errors.Add(new EnvelopeError(EnvelopeError.EmptyPayload, "'payload.frames' is empty."));
            return Finish(envelope, errors);
        }

        for (int i = 0; i < envelope.Payload.Frames.Count; i++)
        {
            CanFrameDto frame = envelope.Payload.Frames[i];
            uint maxId = frame.IsExtended ? MaxExtendedId : MaxStandardId;
            if (frame.CanId > maxId)
            {
                errors.Add(new EnvelopeError(
                    EnvelopeError.InvalidField,
                    $"frames[{i}].canId 0x{frame.CanId:X} exceeds the {(frame.IsExtended ? "29" : "11")}-bit range."));
            }

            if (!IsValidDataHex(frame.Data))
            {
                errors.Add(new EnvelopeError(
                    EnvelopeError.InvalidField,
                    $"frames[{i}].data must be an even-length hex string of at most {MaxDataHexLength} characters."));
            }
        }

        return Finish(envelope, errors);
    }

    private static List<EnvelopeError> ValidateCommon<TPayload>(
        MessageEnvelope<TPayload> envelope, string topicVehicleId, string topicDeviceId)
    {
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

        return errors;
    }

    private static EnvelopeValidationResult Finish<TPayload>(MessageEnvelope<TPayload> envelope, List<EnvelopeError> errors) =>
        new()
        {
            Errors = errors,
            TraceContextIsValid = TraceParent.IsValid(envelope.TraceId),
        };

    private static bool IsValidDataHex(string? data)
    {
        if (data is null || data.Length % 2 != 0 || data.Length > MaxDataHexLength)
        {
            return false;
        }

        foreach (char c in data)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
