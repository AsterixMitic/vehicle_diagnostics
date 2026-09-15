using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Infrastructure.Events;
using VehicleDiagnostics.Infrastructure.Health;
using VehicleDiagnostics.Infrastructure.Mqtt;
using VehicleDiagnostics.Infrastructure.Telemetry;

namespace VehicleDiagnostics.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Connection string name injected by the AppHost's database resource.</summary>
    public const string TelemetryDbConnectionName = "telemetrydb";

    public static IServiceCollection AddTelemetryIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = configuration.GetConnectionString(TelemetryDbConnectionName)
            ?? throw new InvalidOperationException(
                $"Connection string '{TelemetryDbConnectionName}' is not configured.");

        services.AddDbContext<TelemetryDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<TelemetryDbInitializer>();
        services.AddScoped<ITelemetryRepository, PostgresTelemetryRepository>();
        services.AddScoped<IIngestTelemetry, IngestTelemetry>();
        services.AddSingleton<IDomainEventPublisher, LoggingDomainEventPublisher>();
        services.TryAddSingleton(TimeProvider.System);

        services.Configure<MqttIngestOptions>(configuration.GetSection("Mqtt"));
        services.AddSingleton<MqttConnectionState>();
        services.AddHostedService<MqttIngestService>();

        services.AddHealthChecks()
            .AddCheck<TelemetryDbHealthCheck>("telemetry-db", tags: ["ready"])
            .AddCheck<MqttConnectionHealthCheck>("mqtt-broker", tags: ["ready"]);

        return services;
    }
}
