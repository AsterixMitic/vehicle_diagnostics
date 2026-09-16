using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Contracts.Internal;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>At-least-once delivery, bounded retry, error-queue quarantine, and per-device ordering.</summary>
[Collection(StackCollectionDefinition.Name)]
public sealed class BusResilienceTests(StackFixture stack) : IAsyncLifetime
{
    private const string PersistErrorQueue = "persist-decoded-signals_error";
    private IHost? _host;

    public async Task InitializeAsync() =>
        _host = await stack.StartHostAsync("bus-resilience", new Dictionary<string, string?>
        {
            // Short retry ladder so the outage scenario completes in seconds.
            ["Bus:RetryIntervals:0"] = "00:00:00.300",
            ["Bus:RetryIntervals:1"] = "00:00:00.600",
            ["Bus:RetryIntervals:2"] = "00:00:01.200",
        });

    public async Task DisposeAsync()
    {
        await stack.UnblockDatabaseAsync();
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
        }
    }

    [Fact]
    public async Task Transient_database_outage_is_retried_and_persists_exactly_once()
    {
        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        string topic = TelemetryTopics.Raw("retry-car", "retry-device");
        using IMqttClient publisher = await stack.ConnectPublisherAsync("retry-publisher");

        await stack.BlockDatabaseAsync();
        try
        {
            byte[] envelope = RawFrameEndToEndTests.RawEnvelope(
                "retry-device", "retry-car", 1, sampledAt, "00-2af7651916cd43dd8448eb211c80319e-b7ad6b7169203331-01", "04410C1AF8");
            await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, envelope), CancellationToken.None);
            await Task.Delay(700);
        }
        finally
        {
            await stack.UnblockDatabaseAsync();
        }

        IReadOnlyList<StoredReading> stored = await MqttIngestEndToEndTests.WaitForReadingsAsync(_host!, "retry-device", 1, sampledAt);
        stored.Should().ContainSingle(r => r.SignalName == "engine_rpm" && r.Value == 1726,
            "the retried delivery must land exactly once after the database returns");
        await Task.Delay(1500);
        (await MqttIngestEndToEndTests.WaitForReadingsAsync(_host!, "retry-device", 1, sampledAt)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Poison_message_is_quarantined_and_later_messages_still_flow()
    {
        // Readings = null cannot come from a valid producer; the consumer throws on it every time.
        var poison = new SignalsDecoded
        {
            DeviceId = "poison-device",
            VehicleId = "poison-car",
            Sequence = 1,
            TimestampDevice = DateTimeOffset.UtcNow,
            ProcessingMode = "P0",
            Readings = null!,   // deliberate: the message is malformed by construction
        };
        await _host!.Services.GetRequiredService<IBus>().Publish(poison);

        long quarantined = 0;
        for (int attempt = 0; attempt < 60 && quarantined == 0; attempt++)
        {
            await Task.Delay(500);
            quarantined = await ErrorQueueDepthAsync();
        }

        quarantined.Should().BeGreaterThanOrEqualTo(1, "after the retry ladder the message must land in the error queue");

        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        using IMqttClient publisher = await stack.ConnectPublisherAsync("poison-publisher");
        byte[] envelope = RawFrameEndToEndTests.RawEnvelope(
            "after-poison", "poison-car", 1, sampledAt, "00-3af7651916cd43dd8448eb211c80319f-b7ad6b7169203331-01", "03410D3C");
        await publisher.PublishAsync(MqttIngestEndToEndTests.Message(TelemetryTopics.Raw("poison-car", "after-poison"), envelope), CancellationToken.None);

        (await MqttIngestEndToEndTests.WaitForReadingsAsync(_host, "after-poison", 1, sampledAt))
            .Should().ContainSingle(r => r.SignalName == "vehicle_speed" && r.Value == 60);
    }

    [Fact]
    public async Task Same_device_messages_are_persisted_in_publication_order()
    {
        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        string topic = TelemetryTopics.Raw("order-car", "order-device");
        using IMqttClient publisher = await stack.ConnectPublisherAsync("order-publisher");

        for (int sequence = 1; sequence <= 5; sequence++)
        {
            int rpmRaw = 1000 * sequence;   // 250, 500, ... rpm after /4
            string frame = $"04410C{rpmRaw >> 8:X2}{rpmRaw & 0xFF:X2}";
            byte[] envelope = RawFrameEndToEndTests.RawEnvelope(
                "order-device", "order-car", sequence, sampledAt.AddMilliseconds(sequence),
                $"00-4af7651916cd43dd8448eb211c8031{sequence:X2}-b7ad6b7169203331-01", frame);
            await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, envelope), CancellationToken.None);
        }

        IReadOnlyList<StoredReading> stored = await MqttIngestEndToEndTests.WaitForReadingsAsync(_host!, "order-device", 5, sampledAt);
        stored.Should().HaveCount(5);

        double[] byIngestOrder = [.. stored.OrderBy(r => r.IngestedAt).Select(r => r.Value)];
        byIngestOrder.Should().BeInAscendingOrder("sequence 1..5 carries rising rpm, so ingest order must follow publication order");
    }

    private async Task<long> ErrorQueueDepthAsync()
    {
        using var http = new HttpClient { BaseAddress = stack.ManagementApi };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("guest:guest")));
        using HttpResponseMessage response = await http.GetAsync(new Uri($"api/queues/%2F/{PersistErrorQueue}", UriKind.Relative));
        if (!response.IsSuccessStatusCode)
        {
            return 0;
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.TryGetProperty("messages", out JsonElement messages) ? messages.GetInt64() : 0;
    }
}
