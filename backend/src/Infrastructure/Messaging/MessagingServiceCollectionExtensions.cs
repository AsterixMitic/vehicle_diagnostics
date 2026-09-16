using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace VehicleDiagnostics.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Connection string name injected by the AppHost's RabbitMQ resource (an <c>amqp://</c> URL).</summary>
    public const string RabbitMqConnectionName = "rabbitmq";

    /// <summary>Internal bus (ADR-0004): MassTransit over RabbitMQ, consumers, retry, partitioning, health.</summary>
    public static IServiceCollection AddInternalBus(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = configuration.GetConnectionString(RabbitMqConnectionName)
            ?? throw new InvalidOperationException($"Connection string '{RabbitMqConnectionName}' is not configured.");
        var broker = new Uri(connectionString);

        services.Configure<BusOptions>(configuration.GetSection("Bus"));

        // The host is not "started" until every receive endpoint is bound: a message published
        // before its queue exists is unroutable and silently lost. Stop likewise waits for
        // in-flight consumers, so a stopping host never competes with its successor.
        services.AddOptions<MassTransitHostOptions>().Configure(options =>
        {
            options.WaitUntilStarted = true;
            options.StartTimeout = TimeSpan.FromSeconds(30);
            options.StopTimeout = TimeSpan.FromSeconds(30);
        });

        services.AddMassTransit(bus =>
        {
            // Queue names: "decode-raw-frames", "persist-decoded-signals", errors in "<name>_error".
            bus.SetKebabCaseEndpointNameFormatter();
            bus.AddConsumer<DecodeRawFramesConsumer, DecodeRawFramesConsumerDefinition>();
            bus.AddConsumer<PersistDecodedSignalsConsumer, PersistDecodedSignalsConsumerDefinition>();

            bus.UsingRabbitMq((context, cfg) =>
            {
                (string user, string password) = Credentials(broker);
                string virtualHost = broker.AbsolutePath.Length > 1 ? Uri.UnescapeDataString(broker.AbsolutePath[1..]) : "/";
                cfg.Host(broker.Host, (ushort)(broker.IsDefaultPort ? 5672 : broker.Port), virtualHost, host =>
                {
                    host.Username(user);
                    host.Password(password);
                });

                BusOptions options = context.GetRequiredService<IOptions<BusOptions>>().Value;
                cfg.UseMessageRetry(retry => retry.Intervals([.. options.RetryIntervals]));

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    private static (string User, string Password) Credentials(Uri broker)
    {
        if (string.IsNullOrEmpty(broker.UserInfo))
        {
            return ("guest", "guest");
        }

        string[] parts = broker.UserInfo.Split(':', 2);
        return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
    }
}
