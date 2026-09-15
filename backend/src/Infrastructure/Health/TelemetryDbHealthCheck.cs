using Microsoft.Extensions.Diagnostics.HealthChecks;
using VehicleDiagnostics.Infrastructure.Telemetry;

namespace VehicleDiagnostics.Infrastructure.Health;

/// <summary>Readiness: the Telemetry store must be reachable before the host claims to be operational.</summary>
public sealed class TelemetryDbHealthCheck(TelemetryDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        bool canConnect = await dbContext.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false);
        return canConnect
            ? HealthCheckResult.Healthy("Telemetry database reachable.")
            : HealthCheckResult.Unhealthy("Telemetry database unreachable.");
    }
}
