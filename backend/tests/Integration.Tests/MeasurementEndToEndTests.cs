using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Npgsql;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Infrastructure.Observability;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// Every message received over MQTT ends as exactly one row in the Analytics measurement log,
/// through the real broker, bus and database: P2 and P0 successes, an MQTT redelivery, a raw
/// envelope that decodes to nothing, and an unparseable message.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
public sealed class MeasurementEndToEndTests(StackFixture stack) : IAsyncLifetime
{
    private const string DeviceTraceId = "5b8aa5a2d2c872e8321cf37308d69df2";

    private readonly ConcurrentBag<Activity> _activities = [];
    private ActivityListener _listener = null!;
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _listener = new ActivityListener
        {
            // As on a real host: the receive span must exist to be the parent of the settlement's
            // publish, and "MassTransit" must be listened to for the bus to carry trace context.
            ShouldListenTo = source => source.Name is AnalyticsDiagnostics.ActivitySourceName
                or IngestDiagnostics.ActivitySourceName or "MassTransit",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Add,
        };
        ActivitySource.AddActivityListener(_listener);
        _host = await stack.StartHostAsync("e2e-measure");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
        }

        _listener.Dispose();
    }

    private static byte[] SignalsEnvelope(string deviceId, long sequence) =>
        EnvelopeSerializer.Serialize(new MessageEnvelope<SignalReadingsPayload>
        {
            SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
            DeviceId = deviceId,
            VehicleId = "meas-car",
            Sequence = sequence,
            TimestampDevice = DateTimeOffset.UtcNow,
            TraceId = $"00-{DeviceTraceId}-b7ad6b7169203331-01",
            ProcessingMode = "P2",
            Payload = new SignalReadingsPayload
            {
                Readings =
                [
                    new SignalReadingDto { Name = "engine_rpm", Value = 1726.25, Unit = "rpm" },
                    new SignalReadingDto { Name = "vehicle_speed", Value = 60, Unit = "km_h" },
                ],
            },
        });

    private static byte[] RawEnvelope(string deviceId, long sequence, uint canId, string data) =>
        EnvelopeSerializer.Serialize(new MessageEnvelope<RawFramesPayload>
        {
            SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
            DeviceId = deviceId,
            VehicleId = "meas-car",
            Sequence = sequence,
            TimestampDevice = DateTimeOffset.UtcNow,
            TraceId = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ProcessingMode = "P0",
            Payload = new RawFramesPayload { Frames = [new CanFrameDto { CanId = canId, Data = data }] },
        });

    private sealed record Row(
        string Outcome, string? Reason, int? PayloadBytes, string? Mode, DateTimeOffset ReceivedAt, DateTimeOffset? PersistedAt);

    private async Task<List<Row>> WaitForRowsAsync(string deviceId, int expected)
    {
        List<Row> rows = [];
        for (int attempt = 0; attempt < 100 && rows.Count < expected; attempt++)
        {
            await Task.Delay(200);
            rows = [];
            await using var connection = new NpgsqlConnection(stack.Database.GetConnectionString());
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT outcome, reject_reason, payload_bytes, processing_mode, received_at, persisted_at " +
                "FROM analytics.message_log WHERE device_id = @d ORDER BY received_at",
                connection);
            command.Parameters.AddWithValue("d", deviceId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(new Row(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        return rows;
    }

    [Fact]
    public async Task P2_message_and_its_redelivery_become_persisted_then_duplicate()
    {
        using IMqttClient publisher = await stack.ConnectPublisherAsync("meas-p2-publisher");
        byte[] payload = SignalsEnvelope("meas-p2", 1);
        string topic = TelemetryTopics.Signals("meas-car", "meas-p2");
        DateTimeOffset before = DateTimeOffset.UtcNow;

        await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, payload), CancellationToken.None);
        await WaitForRowsAsync("meas-p2", 1);
        await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, payload), CancellationToken.None);
        List<Row> rows = await WaitForRowsAsync("meas-p2", 2);

        rows.Select(r => r.Outcome).Should().Equal("persisted", "duplicate");
        Row persisted = rows[0];
        persisted.Mode.Should().Be("P2");
        persisted.PayloadBytes.Should().Be(payload.Length);
        persisted.ReceivedAt.Should().BeOnOrAfter(before.AddSeconds(-1));
        persisted.PersistedAt.Should().BeOnOrAfter(persisted.ReceivedAt);
        rows[1].PersistedAt.Should().BeNull();

        _activities.Should().Contain(a => a.OperationName == "analytics.record" && a.TraceId.ToHexString() == DeviceTraceId,
            "recording the settlement joins the device's trace");
    }

    [Fact]
    public async Task P0_message_is_measured_by_the_raw_envelope_it_arrived_as()
    {
        using IMqttClient publisher = await stack.ConnectPublisherAsync("meas-p0-publisher");
        byte[] payload = RawEnvelope("meas-p0", 1, 0x7E8, "04410C1AF8");

        await publisher.PublishAsync(
            MqttIngestEndToEndTests.Message(TelemetryTopics.Raw("meas-car", "meas-p0"), payload), CancellationToken.None);
        List<Row> rows = await WaitForRowsAsync("meas-p0", 1);

        rows.Should().ContainSingle();
        Row row = rows[0];
        row.Outcome.Should().Be("persisted");
        row.Mode.Should().Be("P0");
        row.PayloadBytes.Should().Be(payload.Length, "the size is the raw envelope's, not the decoded message's");
        row.PersistedAt.Should().BeAfter(row.ReceivedAt, "receipt is taken before both bus hops");
    }

    [Fact]
    public async Task Raw_envelope_that_decodes_to_nothing_is_rejected_as_no_readings()
    {
        using IMqttClient publisher = await stack.ConnectPublisherAsync("meas-empty-publisher");
        byte[] payload = RawEnvelope("meas-empty", 1, 0x123, "0102030405");

        await publisher.PublishAsync(
            MqttIngestEndToEndTests.Message(TelemetryTopics.Raw("meas-car", "meas-empty"), payload), CancellationToken.None);
        List<Row> rows = await WaitForRowsAsync("meas-empty", 1);

        rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Outcome = "rejected",
            Reason = "no_readings",
            PayloadBytes = (int?)payload.Length,
            Mode = "P0",
        });
    }

    [Fact]
    public async Task Unparseable_message_is_rejected_as_malformed_and_attributed_by_topic()
    {
        using IMqttClient publisher = await stack.ConnectPublisherAsync("meas-bad-publisher");
        byte[] payload = Encoding.UTF8.GetBytes("this is not json");

        await publisher.PublishAsync(
            MqttIngestEndToEndTests.Message(TelemetryTopics.Signals("meas-car", "meas-bad"), payload), CancellationToken.None);
        List<Row> rows = await WaitForRowsAsync("meas-bad", 1);

        rows.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Outcome = "rejected",
            Reason = "malformed",
            PayloadBytes = (int?)payload.Length,
            Mode = (string?)null,
        });
    }
}
