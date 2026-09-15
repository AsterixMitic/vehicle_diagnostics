namespace VehicleDiagnostics.Infrastructure.Mqtt;

/// <summary>Broker endpoint for the telemetry subscription. Bound from the <c>Mqtt</c> configuration section.</summary>
public sealed class MqttIngestOptions
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 1883;

    public string ClientId { get; set; } = "vehiclediagnostics-ingest";
}
