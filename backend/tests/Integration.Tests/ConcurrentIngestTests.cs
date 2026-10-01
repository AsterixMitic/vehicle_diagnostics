using FluentAssertions;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Npgsql;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// Concurrent ingestion through the real broker, bus and database: devices are handled in
/// parallel, each device keeps its order, and a concurrency of 1 still ingests everything.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
public sealed class ConcurrentIngestTests(StackFixture stack)
{
    private static byte[] Envelope(string deviceId, long sequence, DateTimeOffset sampledAt) =>
        EnvelopeSerializer.Serialize(new MessageEnvelope<SignalReadingsPayload>
        {
            SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
            DeviceId = deviceId,
            VehicleId = "conc-car",
            Sequence = sequence,
            TimestampDevice = sampledAt.AddMilliseconds(sequence),
            TraceId = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ProcessingMode = "P2",
            Payload = new SignalReadingsPayload
            {
                Readings = [new SignalReadingDto { Name = "engine_rpm", Value = 1000 + sequence, Unit = "rpm" }],
            },
        });

    private async Task<IHost> StartHostAsync(string clientId, int concurrency) =>
        await stack.StartHostAsync(clientId, new Dictionary<string, string?>
        {
            ["Mqtt:IngestConcurrency"] = concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });

    private async Task PublishAsync(string deviceId, int count, DateTimeOffset sampledAt)
    {
        using IMqttClient publisher = await stack.ConnectPublisherAsync($"pub-{deviceId}");
        string topic = TelemetryTopics.Signals("conc-car", deviceId);
        for (long sequence = 1; sequence <= count; sequence++)
        {
            await publisher.PublishAsync(
                MqttIngestEndToEndTests.Message(topic, Envelope(deviceId, sequence, sampledAt)), CancellationToken.None);
        }
    }

    private async Task<Dictionary<string, int>> SettlementsAsync(string deviceId, int expected)
    {
        var outcomes = new Dictionary<string, int>();
        for (int attempt = 0; attempt < 100 && outcomes.Values.Sum() < expected; attempt++)
        {
            await Task.Delay(200);
            outcomes.Clear();
            await using var connection = new NpgsqlConnection(stack.Database.GetConnectionString());
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT outcome, count(*)::int FROM analytics.message_log WHERE device_id = @d GROUP BY outcome", connection);
            command.Parameters.AddWithValue("d", deviceId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                outcomes[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return outcomes;
    }

    private static async Task StopAsync(IHost host)
    {
        await host.StopAsync(TimeSpan.FromSeconds(10));
        host.Dispose();
    }

    [Fact]
    public async Task Several_devices_at_once_are_each_persisted_and_settled_exactly_once()
    {
        IHost host = await StartHostAsync("conc-many", concurrency: 4);
        try
        {
            DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
            string[] devices = [.. Enumerable.Range(1, 6).Select(i => $"conc-many-{i}")];

            await Task.WhenAll(devices.Select(d => PublishAsync(d, 15, sampledAt)));

            foreach (string device in devices)
            {
                IReadOnlyList<StoredReading> readings =
                    await MqttIngestEndToEndTests.WaitForReadingsAsync(host, device, 15, sampledAt);
                long[] missing = [.. Enumerable.Range(1, 15).Select(i => (long)i).Except(readings.Select(r => (long)r.Value - 1000))];
                readings.Should().HaveCount(15, $"{device} published 15 envelopes; sequences {string.Join(", ", missing)} were not persisted");
                (await SettlementsAsync(device, 15)).Should().Equal(
                    new Dictionary<string, int> { ["persisted"] = 15 }, $"each of {device}'s envelopes settles once");
            }
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task One_device_is_persisted_in_arrival_order_with_concurrent_workers()
    {
        IHost host = await StartHostAsync("conc-order", concurrency: 4);
        try
        {
            DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
            await PublishAsync("conc-order-1", 40, sampledAt);

            IReadOnlyList<StoredReading> readings =
                await MqttIngestEndToEndTests.WaitForReadingsAsync(host, "conc-order-1", 40, sampledAt);

            long[] missing = [.. Enumerable.Range(1, 40).Select(i => (long)i).Except(readings.Select(r => (long)r.Value - 1000))];
            readings.Should().HaveCount(40, $"sequences {string.Join(", ", missing)} were not persisted");
            // Value = 1000 + sequence: in sequence order, the ingest times never go backwards.
            readings.OrderBy(r => r.Value).Select(r => r.IngestedAt).Should().BeInAscendingOrder();
        }
        finally
        {
            await StopAsync(host);
        }
    }

    [Fact]
    public async Task Concurrency_below_one_stops_startup_with_a_clear_message()
    {
        Func<Task> start = () => StartHostAsync("conc-zero", concurrency: 0);

        (await start.Should().ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>())
            .WithMessage("*Mqtt:IngestConcurrency must be at least 1*");
    }

    [Fact]
    public async Task Concurrency_of_one_handles_messages_sequentially_and_ingests_everything()
    {
        IHost host = await StartHostAsync("conc-one", concurrency: 1);
        try
        {
            DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
            string[] devices = ["conc-one-1", "conc-one-2", "conc-one-3"];

            await Task.WhenAll(devices.Select(d => PublishAsync(d, 10, sampledAt)));

            foreach (string device in devices)
            {
                (await MqttIngestEndToEndTests.WaitForReadingsAsync(host, device, 10, sampledAt)).Should().HaveCount(10);
                (await SettlementsAsync(device, 10)).Should().Equal(new Dictionary<string, int> { ["persisted"] = 10 });
            }
        }
        finally
        {
            await StopAsync(host);
        }
    }
}
