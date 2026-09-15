using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Infrastructure;
using VehicleDiagnostics.Infrastructure.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// The walking skeleton end to end: publish over a real Mosquitto, ingest through the
/// hosted service and use case, land in a real TimescaleDB — and keep going after a
/// poison message.
/// </summary>
public sealed class MqttIngestEndToEndTests : IAsyncLifetime
{
    private const string MosquittoConfig = "listener 1883\nallow_anonymous true\n";

    private readonly TimescaleDbFixture _db = new();

    private readonly IContainer _mosquitto = new ContainerBuilder("eclipse-mosquitto:2")
        .WithResourceMapping(Encoding.UTF8.GetBytes(MosquittoConfig), "/mosquitto/config/mosquitto.conf")
        .WithPortBinding(1883, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1883))
        .Build();

    private IHost? _host;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_db.InitializeAsync(), _mosquitto.StartAsync());

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DependencyInjection.TelemetryDbConnectionName}"] = _db.ConnectionString,
            ["Mqtt:Host"] = _mosquitto.Hostname,
            ["Mqtt:Port"] = _mosquitto.GetMappedPublicPort(1883).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mqtt:ClientId"] = "e2e-ingest",
        });
        builder.Services.AddTelemetryIngestion(builder.Configuration);
        _host = builder.Build();

        using (IServiceScope scope = _host.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TelemetryDbInitializer>()
                .InitializeAsync(CancellationToken.None);
        }

        await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
        }

        await _mosquitto.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<IMqttClient> ConnectPublisherAsync()
    {
        IMqttClient publisher = new MqttClientFactory().CreateMqttClient();
        MqttClientOptions options = new MqttClientOptionsBuilder()
            .WithTcpServer(_mosquitto.Hostname, _mosquitto.GetMappedPublicPort(1883))
            .WithClientId("e2e-publisher")
            .Build();
        await publisher.ConnectAsync(options, CancellationToken.None);
        return publisher;
    }

    private static MqttApplicationMessage Message(string topic, byte[] payload) =>
        new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

    private static byte[] Envelope(long sequence, DateTimeOffset sampledAt) =>
        EnvelopeSerializer.Serialize(new MessageEnvelope<SignalReadingsPayload>
        {
            SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
            DeviceId = "e2e-device",
            VehicleId = "e2e-car",
            Sequence = sequence,
            TimestampDevice = sampledAt,
            TraceId = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ProcessingMode = "P2",
            Payload = new SignalReadingsPayload
            {
                Readings = [new SignalReadingDto { Name = "engine_rpm", Value = 900 + sequence, Unit = "rpm" }],
            },
        });

    private async Task<IReadOnlyList<StoredReading>> WaitForReadingsAsync(int expectedCount, DateTimeOffset around)
    {
        using var scope = _host!.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITelemetryRepository>();
        DeviceId deviceId = DeviceId.Create("e2e-device").Value;

        IReadOnlyList<StoredReading> stored = [];
        for (int attempt = 0; attempt < 100; attempt++)
        {
            stored = await repository.QueryAsync(
                deviceId, around.AddMinutes(-5), around.AddMinutes(5), CancellationToken.None);
            if (stored.Count >= expectedCount)
            {
                break;
            }

            await Task.Delay(200);
        }

        return stored;
    }

    [Fact]
    public async Task Publishes_are_ingested_and_a_poison_message_does_not_stop_the_pipeline()
    {
        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        string topic = TelemetryTopics.Signals("e2e-car", "e2e-device");
        IMqttClient publisher = await ConnectPublisherAsync();

        try
        {
            await publisher.PublishAsync(Message(topic, Envelope(1, sampledAt)), CancellationToken.None);
            IReadOnlyList<StoredReading> afterFirst = await WaitForReadingsAsync(1, sampledAt);
            afterFirst.Should().ContainSingle(r => r.SignalName == "engine_rpm" && r.Value == 901);

            // Poison: not JSON at all. The pipeline must reject it and keep consuming.
            await publisher.PublishAsync(
                Message(topic, Encoding.UTF8.GetBytes("this is not an envelope")), CancellationToken.None);

            await publisher.PublishAsync(Message(topic, Envelope(2, sampledAt.AddSeconds(1))), CancellationToken.None);
            IReadOnlyList<StoredReading> afterPoison = await WaitForReadingsAsync(2, sampledAt);
            afterPoison.Should().HaveCount(2, "the message after the poison one must still be ingested");

            // Redelivery of sequence 1 must not create a third row.
            await publisher.PublishAsync(Message(topic, Envelope(1, sampledAt)), CancellationToken.None);
            await Task.Delay(1000);
            IReadOnlyList<StoredReading> afterDuplicate = await WaitForReadingsAsync(2, sampledAt);
            afterDuplicate.Should().HaveCount(2);
        }
        finally
        {
            publisher.Dispose();
        }
    }
}
