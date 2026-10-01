using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Application.SignalDecoding;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Infrastructure.Analytics;
using VehicleDiagnostics.Infrastructure.Events;
using VehicleDiagnostics.Infrastructure.Health;
using VehicleDiagnostics.Infrastructure.Messaging;
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
        services.AddScoped<ITelemetryQueries, TimescaleTelemetryQueries>();
        services.AddScoped<IIngestTelemetry, IngestTelemetry>();
        // Domain events are logged and fanned out to live subscribers (SSE).
        services.AddSingleton<LoggingDomainEventPublisher>();
        services.AddSingleton<LiveReadingsHub>();
        services.AddSingleton<ILiveTelemetryFeed>(sp => sp.GetRequiredService<LiveReadingsHub>());
        services.AddSingleton<IDomainEventPublisher>(sp => new CompositeDomainEventPublisher(
            [sp.GetRequiredService<LoggingDomainEventPublisher>(), sp.GetRequiredService<LiveReadingsHub>()]));
        services.TryAddSingleton(TimeProvider.System);

        // P0 path: Telemetry accepts raw frames → bus → Signal Decoding decodes → bus → Telemetry persists.
        services.AddScoped<IAcceptRawTelemetry, AcceptRawTelemetry>();
        services.AddScoped<IRawFramesPublisher, MassTransitRawFramesPublisher>();
        services.AddScoped<IDecodeFrames, DecodeFrames>();
        services.AddScoped<IDecodedSignalsPublisher, MassTransitDecodedSignalsPublisher>();

        // Measurement: every received message is settled once, for the Analytics context.
        services.AddScoped<IMessageSettlementReporter, MassTransitSettlementReporter>();

        // Analytics context: its own schema on the shared instance (architecture.md §5). Registered
        // with the bus, because the bus hosts its consumer.
        services.AddDbContext<AnalyticsDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<AnalyticsDbInitializer>();
        services.AddScoped<IMessageLogRepository, PostgresMessageLogRepository>();
        services.AddScoped<IRecordSettlement, RecordSettlement>();
        services.AddScoped<IEvaluationQueries, TimescaleEvaluationQueries>();
        services.AddInternalBus(configuration);

        services.AddOptions<MqttIngestOptions>()
            .Bind(configuration.GetSection("Mqtt"))
            .Validate(o => o.IngestConcurrency >= 1, "Mqtt:IngestConcurrency must be at least 1.")
            .ValidateOnStart();
        services.AddSingleton<MqttConnectionState>();
        services.AddHostedService<MqttIngestService>();

        services.AddHealthChecks()
            .AddCheck<TelemetryDbHealthCheck>("telemetry-db", tags: ["ready"])
            .AddCheck<MqttConnectionHealthCheck>("mqtt-broker", tags: ["ready"]);

        return services;
    }
}
