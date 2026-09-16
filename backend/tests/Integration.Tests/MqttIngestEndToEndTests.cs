using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Domain.SharedKernel;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// The walking skeleton end to end: publish over a real Mosquitto, ingest through the hosted
/// service and use case, land in a real TimescaleDB — and keep going after a poison message.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
public sealed class MqttIngestEndToEndTests(StackFixture stack) : IAsyncLifetime
{
    private IHost? _host;

    public async Task InitializeAsync() => _host = await stack.StartHostAsync("e2e-ingest");

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
        }
    }

    internal static MqttApplicationMessage Message(string topic, byte[] payload) =>
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

    internal static async Task<IReadOnlyList<StoredReading>> WaitForReadingsAsync(
        IHost host, string deviceId, int expectedCount, DateTimeOffset around)
    {
        using IServiceScope scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITelemetryRepository>();
        DeviceId id = DeviceId.Create(deviceId).Value;

        IReadOnlyList<StoredReading> stored = [];
        for (int attempt = 0; attempt < 100; attempt++)
        {
            stored = await repository.QueryAsync(id, around.AddMinutes(-5), around.AddMinutes(5), CancellationToken.None);
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
        using IMqttClient publisher = await stack.ConnectPublisherAsync("e2e-publisher");

        await publisher.PublishAsync(Message(topic, Envelope(1, sampledAt)), CancellationToken.None);
        IReadOnlyList<StoredReading> afterFirst = await WaitForReadingsAsync(_host!, "e2e-device", 1, sampledAt);
        afterFirst.Should().ContainSingle(r => r.SignalName == "engine_rpm" && r.Value == 901);

        // Poison: not JSON at all. The pipeline must reject it and keep consuming.
        await publisher.PublishAsync(Message(topic, Encoding.UTF8.GetBytes("this is not an envelope")), CancellationToken.None);

        await publisher.PublishAsync(Message(topic, Envelope(2, sampledAt.AddSeconds(1))), CancellationToken.None);
        IReadOnlyList<StoredReading> afterPoison = await WaitForReadingsAsync(_host!, "e2e-device", 2, sampledAt);
        afterPoison.Should().HaveCount(2, "the message after the poison one must still be ingested");

        // Redelivery of sequence 1 must not create a third row.
        await publisher.PublishAsync(Message(topic, Envelope(1, sampledAt)), CancellationToken.None);
        await Task.Delay(1000);
        IReadOnlyList<StoredReading> afterDuplicate = await WaitForReadingsAsync(_host!, "e2e-device", 2, sampledAt);
        afterDuplicate.Should().HaveCount(2);
    }
}
