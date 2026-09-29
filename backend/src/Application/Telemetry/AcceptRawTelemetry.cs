using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>Structurally valid raw-frames telemetry, before identity validation.</summary>
public sealed record RawTelemetryFrames(
    string DeviceId,
    string VehicleId,
    long Sequence,
    string ProcessingMode,
    DateTimeOffset TimestampDevice,
    IReadOnlyList<RawFrameData> Frames,
    ReceiptInfo? Receipt = null);

/// <summary>A raw-frames message the Telemetry context accepted and hands to decoding.</summary>
public sealed record RawFramesAccepted(
    DeviceId DeviceId,
    VehicleId VehicleId,
    long Sequence,
    ProcessingMode ProcessingMode,
    DateTimeOffset TimestampDevice,
    IReadOnlyList<RawFrameData> Frames,
    ReceiptInfo? Receipt);

/// <summary>Outbound port: hand accepted raw frames to the Signal Decoding context over the bus.</summary>
public interface IRawFramesPublisher
{
    Task PublishAsync(RawFramesAccepted accepted, CancellationToken cancellationToken);
}

public abstract record AcceptRawOutcome
{
    private AcceptRawOutcome()
    {
    }

    public sealed record Accepted(RawFramesAccepted Message) : AcceptRawOutcome;

    public sealed record Rejected(IReadOnlyList<ValidationError> Errors) : AcceptRawOutcome;
}

/// <summary>Inbound port of the Telemetry context for raw-frame telemetry (processing mode P0).</summary>
public interface IAcceptRawTelemetry
{
    Task<AcceptRawOutcome> AcceptAsync(RawTelemetryFrames telemetry, CancellationToken cancellationToken);
}

/// <summary>
/// Raw frames are not persisted by the Telemetry context; they are validated for identity and
/// forwarded to the Signal Decoding context, whose readings come back through
/// <see cref="IIngestTelemetry"/> for persistence.
/// </summary>
public sealed class AcceptRawTelemetry(IRawFramesPublisher publisher) : IAcceptRawTelemetry
{
    public async Task<AcceptRawOutcome> AcceptAsync(RawTelemetryFrames telemetry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        List<ValidationError> errors = [];

        var deviceId = DeviceId.Create(telemetry.DeviceId);
        if (deviceId.IsFailure)
        {
            errors.Add(deviceId.Error);
        }

        var vehicleId = VehicleId.Create(telemetry.VehicleId);
        if (vehicleId.IsFailure)
        {
            errors.Add(vehicleId.Error);
        }

        if (telemetry.Sequence < 0)
        {
            errors.Add(ValidationError.OutOfRange("sequence", "must be non-negative"));
        }

        if (!Enum.TryParse(telemetry.ProcessingMode, ignoreCase: false, out ProcessingMode mode))
        {
            errors.Add(ValidationError.Invalid("processingMode", $"unknown mode '{telemetry.ProcessingMode}'"));
        }

        if (telemetry.Frames.Count == 0)
        {
            errors.Add(ValidationError.Required("frames"));
        }

        if (errors.Count > 0)
        {
            return new AcceptRawOutcome.Rejected(errors);
        }

        var accepted = new RawFramesAccepted(
            deviceId.Value, vehicleId.Value, telemetry.Sequence, mode, telemetry.TimestampDevice, telemetry.Frames, telemetry.Receipt);
        await publisher.PublishAsync(accepted, cancellationToken).ConfigureAwait(false);
        return new AcceptRawOutcome.Accepted(accepted);
    }
}
