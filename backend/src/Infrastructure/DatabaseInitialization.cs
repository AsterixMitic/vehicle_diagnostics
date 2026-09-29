using Microsoft.Extensions.DependencyInjection;
using VehicleDiagnostics.Infrastructure.Analytics;
using VehicleDiagnostics.Infrastructure.Telemetry;

namespace VehicleDiagnostics.Infrastructure;

/// <summary>
/// Creates every context's schema before hosted services start consuming. One entry point, so
/// the Api host and the test hosts cannot drift apart.
/// </summary>
public static class DatabaseInitialization
{
    public static async Task InitializeSchemasAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        using IServiceScope scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<TelemetryDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
        await scope.ServiceProvider.GetRequiredService<AnalyticsDbInitializer>().InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}
