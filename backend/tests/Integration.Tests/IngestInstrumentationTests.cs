using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Infrastructure.Observability;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// The instrumentation IS the thesis's measuring instrument: spans must continue the
/// device's trace, and the outcome counters must add up over a mixed batch.
/// </summary>
[Collection(StackCollectionDefinition.Name)]
public sealed class IngestInstrumentationTests(StackFixture stack) : IAsyncLifetime
{
    private const string DeviceTraceId = "0af7651916cd43dd8448eb211c80319c";

    private readonly ConcurrentBag<Activity> _activities = [];
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private ActivityListener _activityListener = null!;
    private MeterListener _meterListener = null!;
    private IHost? _host;

    public async Task InitializeAsync()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == IngestDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Add,
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == IngestDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            _counters.AddOrUpdate(instrument.Name, measurement, (_, sum) => sum + measurement));
        _meterListener.Start();

        _host = await stack.StartHostAsync("instr-ingest");
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(10));
            _host.Dispose();
        }

        _meterListener.Dispose();
        _activityListener.Dispose();
    }

    private static byte[] Envelope(long sequence) =>
        EnvelopeSerializer.Serialize(new MessageEnvelope<SignalReadingsPayload>
        {
            SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
            DeviceId = "instr-device",
            VehicleId = "instr-car",
            Sequence = sequence,
            TimestampDevice = DateTimeOffset.UtcNow,
            TraceId = $"00-{DeviceTraceId}-b7ad6b7169203331-01",
            ProcessingMode = "P2",
            Payload = new SignalReadingsPayload
            {
                Readings = [new SignalReadingDto { Name = "engine_rpm", Value = 1500, Unit = "rpm" }],
            },
        });

    private long Counter(string name) => _counters.TryGetValue(name, out long value) ? value : 0;

    [Fact]
    public async Task Spans_continue_the_device_trace_and_counters_match_a_mixed_batch()
    {
        string topic = TelemetryTopics.Signals("instr-car", "instr-device");
        using IMqttClient publisher = await stack.ConnectPublisherAsync("instr-publisher");

        // Mixed batch: one valid, one malformed, one redelivery of the valid message.
        // A redelivery is byte-identical — the same envelope, not a rebuilt one.
        byte[] validEnvelope = Envelope(1);
        await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, validEnvelope), CancellationToken.None);
        await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, Encoding.UTF8.GetBytes("not an envelope")), CancellationToken.None);
        await publisher.PublishAsync(MqttIngestEndToEndTests.Message(topic, validEnvelope), CancellationToken.None);

        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (Counter("ingest.messages.persisted") + Counter("ingest.messages.rejected")
                + Counter("ingest.messages.deduplicated") >= 3)
            {
                break;
            }

            await Task.Delay(200);
        }

        Counter("ingest.messages.received").Should().Be(3);
        Counter("ingest.messages.persisted").Should().Be(1);
        Counter("ingest.messages.rejected").Should().Be(1);
        Counter("ingest.messages.deduplicated").Should().Be(1);

        Activity[] traced = [.. _activities.Where(a => a.TraceId.ToHexString() == DeviceTraceId)];
        traced.Select(a => a.OperationName).Should().Contain(
            ["ingest.receive", "ingest.validate", "ingest.deduplicate", "ingest.persist"],
            "every ingest stage must be a span on the trace the device started");
    }
}
