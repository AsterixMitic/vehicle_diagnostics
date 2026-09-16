using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;
using VehicleDiagnostics.Infrastructure;
using VehicleDiagnostics.Infrastructure.Messaging;
using VehicleDiagnostics.Infrastructure.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

[CollectionDefinition(Name)]
public sealed class StackCollectionDefinition : ICollectionFixture<StackFixture>
{
    public const string Name = "stack";
}

/// <summary>
/// The real local stack, once per test run: Mosquitto, TimescaleDB, RabbitMQ. Each test class
/// starts its own host against it. Tests use distinct device identifiers to stay independent.
/// </summary>
public sealed class StackFixture : IAsyncLifetime
{
    private const string MosquittoConfig = "listener 1883\nallow_anonymous true\n";

    public PostgreSqlContainer Database { get; } =
        new PostgreSqlBuilder("timescale/timescaledb:latest-pg17").Build();

    public IContainer Mqtt { get; } = new ContainerBuilder("eclipse-mosquitto:2")
        .WithResourceMapping(Encoding.UTF8.GetBytes(MosquittoConfig), "/mosquitto/config/mosquitto.conf")
        .WithPortBinding(1883, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1883))
        .Build();

    public RabbitMqContainer Bus { get; } = new RabbitMqBuilder("rabbitmq:4-management-alpine")
        .WithUsername("guest")
        .WithPassword("guest")
        .WithPortBinding(15672, assignRandomHostPort: true)
        .Build();

    public string MqttHost => Mqtt.Hostname;

    public int MqttPort => Mqtt.GetMappedPublicPort(1883);

    public Uri ManagementApi => new($"http://{Bus.Hostname}:{Bus.GetMappedPublicPort(15672)}/");

    public Task InitializeAsync() => Task.WhenAll(Database.StartAsync(), Mqtt.StartAsync(), Bus.StartAsync());

    public async Task DisposeAsync()
    {
        await Bus.DisposeAsync();
        await Mqtt.DisposeAsync();
        await Database.DisposeAsync();
    }

    /// <summary>Builds, schema-initializes, and starts a host wired exactly like the Api.</summary>
    public async Task<IHost> StartHostAsync(string clientId, IDictionary<string, string?>? extraConfiguration = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        var configuration = new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DependencyInjection.TelemetryDbConnectionName}"] = Database.GetConnectionString(),
            [$"ConnectionStrings:{MessagingServiceCollectionExtensions.RabbitMqConnectionName}"] = Bus.GetConnectionString(),
            ["Mqtt:Host"] = MqttHost,
            ["Mqtt:Port"] = MqttPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mqtt:ClientId"] = clientId,
        };
        if (extraConfiguration is not null)
        {
            foreach ((string key, string? value) in extraConfiguration)
            {
                configuration[key] = value;
            }
        }

        builder.Configuration.AddInMemoryCollection(configuration);
        builder.Services.AddTelemetryIngestion(builder.Configuration);
        IHost host = builder.Build();

        using (IServiceScope scope = host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TelemetryDbInitializer>().InitializeAsync(CancellationToken.None);
        }

        await host.StartAsync();
        return host;
    }

    public async Task<IMqttClient> ConnectPublisherAsync(string clientId)
    {
        IMqttClient publisher = new MqttClientFactory().CreateMqttClient();
        MqttClientOptions options = new MqttClientOptionsBuilder()
            .WithTcpServer(MqttHost, MqttPort)
            .WithClientId(clientId)
            .Build();
        await publisher.ConnectAsync(options, CancellationToken.None);
        return publisher;
    }

    /// <summary>
    /// Simulates a database outage without touching the container (whose random host port a
    /// restart would reassign): refuse all new connections, superuser included, and terminate
    /// the existing ones.
    /// </summary>
    public async Task BlockDatabaseAsync()
    {
        string database = new NpgsqlConnectionStringBuilder(Database.GetConnectionString()).Database!;
        await using NpgsqlConnection control = await OpenControlConnectionAsync();
        await using var block = new NpgsqlCommand(
            $"ALTER DATABASE \"{database}\" ALLOW_CONNECTIONS false; " +
            $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{database}' AND pid <> pg_backend_pid();",
            control);
        await block.ExecuteNonQueryAsync();
    }

    public async Task UnblockDatabaseAsync()
    {
        string database = new NpgsqlConnectionStringBuilder(Database.GetConnectionString()).Database!;
        await using NpgsqlConnection control = await OpenControlConnectionAsync();
        await using var allow = new NpgsqlCommand($"ALTER DATABASE \"{database}\" ALLOW_CONNECTIONS true;", control);
        await allow.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenControlConnectionAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Database.GetConnectionString()) { Database = "template1", Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
