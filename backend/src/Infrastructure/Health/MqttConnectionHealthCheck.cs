using Microsoft.Extensions.Diagnostics.HealthChecks;
using VehicleDiagnostics.Infrastructure.Mqtt;

namespace VehicleDiagnostics.Infrastructure.Health;

/// <summary>Readiness: without a broker connection the host is not ingesting and must not claim to be.</summary>
public sealed class MqttConnectionHealthCheck(MqttConnectionState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.IsConnected
            ? HealthCheckResult.Healthy("MQTT broker connected.")
            : HealthCheckResult.Unhealthy("MQTT broker not connected."));
}
