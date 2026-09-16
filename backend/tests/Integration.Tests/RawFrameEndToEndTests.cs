using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Infrastructure.Observability;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// The P0 path: raw OBD-II frames over MQTT → Telemetry accepts → bus → Signal Decoding →
/// bus → Telemetry persists, attributed to P0, deduplicated, on one unbroken trace.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
public sealed class RawFrameEndToEndTests(StackFixture stack) : IAsyncLifetime
{
    private const string DeviceTraceId = "1bf7651916cd43dd8448eb211c80319d";

    private readonly ConcurrentBag<Activity> _activities = [];
    private ActivityListener _listener = null!;
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _listener = new ActivityListener
        {
            // "MassTransit" must be listened to: the bus propagates trace context only when its activity
            // source is observed, which in the real host OpenTelemetry does (ServiceDefaults).
            ShouldListenTo = source => source.Name is IngestDiagnostics.ActivitySourceName or DecodeDiagnostics.ActivitySourceName or "MassTransit",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Add,
        };
        ActivitySource.AddActivityListener(_listener);
        _host = await stack.StartHostAsync("raw-e2e");
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

    internal static byte[] RawEnvelope(string device, string vehicle, long sequence, DateTimeOffset sampledAt, string traceId, params string[] frames) =>
        EnvelopeSerializer.Serialize(new MessageEnvelope<RawFramesPayload>
        {
            SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
            DeviceId = device,
            VehicleId = vehicle,
            Sequence = sequence,
            TimestampDevice = sampledAt,
            TraceId = traceId,
            ProcessingMode = "P0",
            Payload = new RawFramesPayload
            {
                Frames = [.. frames.Select(f => new CanFrameDto { CanId = 0x7E8, Data = f })],
            },
        });

    [Fact]
    public async Task Raw_frames_become_rows_attributed_to_P0_and_are_deduplicated()
    {
        DateTimeOffset sampledAt = DateTimeOffset.UtcNow;
        string topic = TelemetryTopics.Raw("raw-car", "raw-device");
        using IMqttClient publisher = await stack.ConnectPublisherAsync("raw-publisher");

        // engine_rpm 1726, a negative response, and a coolant_temp 50 — the negative one is ignored.
        byte[] envelope = RawEnvelope(
            "raw-device", "raw-car", 1, sampledAt, $"00-{DeviceTraceId}-b7ad6b7169203331-01",
            "04410C1AF8", "037F0112", "0341055A");
        await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, envelope), CancellationToken.None);

        IReadOnlyList<StoredReading> stored = await MqttIngestEndToEndTests.WaitForReadingsAsync(_host!, "raw-device", 2, sampledAt);
        stored.Should().HaveCount(2);
        stored.Should().ContainSingle(r => r.SignalName == "engine_rpm" && r.Value == 1726 && r.Unit == "rpm");
        stored.Should().ContainSingle(r => r.SignalName == "coolant_temp" && r.Value == 50 && r.Unit == "degC");

        using (IServiceScope scope = _host!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VehicleDiagnostics.Infrastructure.Telemetry.TelemetryDbContext>();
            db.SignalReadings.Where(r => r.DeviceId == "raw-device").Select(r => r.ProcessingMode).Distinct()
                .Should().ContainSingle("readings decoded from raw frames keep the envelope's mode").Which.Should().Be("P0");
        }

        // Byte-identical redelivery of the raw envelope: nothing new may be stored.
        await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, envelope), CancellationToken.None);
        await Task.Delay(1500);
        (await MqttIngestEndToEndTests.WaitForReadingsAsync(_host!, "raw-device", 2, sampledAt)).Should().HaveCount(2);

        // One trace from the device through MQTT, the bus, decoding, the bus again, and persistence.
        for (int attempt = 0; attempt < 50 && !_activities.Any(a => a.OperationName == "ingest.persist" && a.TraceId.ToHexString() == DeviceTraceId); attempt++)
        {
            await Task.Delay(100);
        }

        string[] onDeviceTrace = [.. _activities.Where(a => a.TraceId.ToHexString() == DeviceTraceId).Select(a => a.OperationName).Distinct()];
        onDeviceTrace.Should().Contain(["ingest.receive", "ingest.validate", "decode.frames", "ingest.deduplicate", "ingest.persist"]);
    }
}
