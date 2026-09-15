namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>
/// Inbound port of the Telemetry context: any envelope source (MQTT today, others later)
/// hands structurally valid telemetry to this use case.
/// </summary>
public interface IIngestTelemetry
{
    Task<IngestOutcome> IngestAsync(RawTelemetry telemetry, CancellationToken cancellationToken);
}
