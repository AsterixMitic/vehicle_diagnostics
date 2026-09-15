using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;

namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>
/// The ingest use case: domain validation → idempotent persistence → domain event.
/// Rejections and duplicates are expected outcomes, not exceptions.
/// </summary>
public sealed class IngestTelemetry(
    ITelemetryRepository repository,
    IDomainEventPublisher eventPublisher,
    TimeProvider timeProvider) : IIngestTelemetry
{
    public async Task<IngestOutcome> IngestAsync(RawTelemetry telemetry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        List<ValidationError> errors = [];

        var deviceIdResult = DeviceId.Create(telemetry.DeviceId);
        if (deviceIdResult.IsFailure)
        {
            errors.Add(deviceIdResult.Error);
        }

        var vehicleIdResult = VehicleId.Create(telemetry.VehicleId);
        if (vehicleIdResult.IsFailure)
        {
            errors.Add(vehicleIdResult.Error);
        }

        if (telemetry.Sequence < 0)
        {
            errors.Add(ValidationError.OutOfRange("sequence", "must be non-negative"));
        }

        if (!Enum.TryParse(telemetry.ProcessingMode, ignoreCase: false, out ProcessingMode mode))
        {
            errors.Add(ValidationError.Invalid("processingMode", $"unknown mode '{telemetry.ProcessingMode}'"));
        }

        List<SignalReading> readings = new(telemetry.Readings.Count);
        foreach (RawSignalReading raw in telemetry.Readings)
        {
            var reading = SignalName.Create(raw.Name)
                .Bind(name => SignalReading.Create(name, raw.Value, raw.Unit, telemetry.TimestampDevice));
            if (reading.IsFailure)
            {
                errors.Add(reading.Error);
            }
            else
            {
                readings.Add(reading.Value);
            }
        }

        if (readings.Count == 0)
        {
            errors.Add(ValidationError.Required("readings"));
        }

        if (errors.Count > 0)
        {
            return new IngestOutcome.Rejected(errors);
        }

        TelemetryReceived @event = TelemetryReceived.Create(
            deviceIdResult.Value, vehicleIdResult.Value, telemetry.Sequence, mode, readings, timeProvider);

        PersistResult persisted = await repository.PersistAsync(@event, cancellationToken).ConfigureAwait(false);
        if (persisted == PersistResult.Duplicate)
        {
            return new IngestOutcome.Duplicate();
        }

        await eventPublisher.PublishAsync(@event, cancellationToken).ConfigureAwait(false);
        return new IngestOutcome.Accepted(@event);
    }
}
