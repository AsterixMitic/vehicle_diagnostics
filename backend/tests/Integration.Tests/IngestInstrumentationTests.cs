using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Infrastructure;
using VehicleDiagnostics.Infrastructure.Observability;
using VehicleDiagnostics.Infrastructure.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// The instrumentation IS the thesis's measuring instrument: spans must continue the
/// device's trace, and the outcome counters must add up over a mixed batch.
/// </summary>
public sealed class IngestInstrumentationTests : IAsyncLifetime
{
    private const string DeviceTraceId = "0af7651916cd43dd8448eb211c80319c";

    private readonly TimescaleDbFixture _db = new();

    private readonly IContainer _mosquitto = new ContainerBuilder("eclipse-mosquitto:2")
        .WithResourceMapping(Encoding.UTF8.GetBytes("listener 1883\nallow_anonymous true\n"), "/mosquitto/config/mosquitto.conf")
        .WithPortBinding(1883, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(1883))
        .Build();

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

        await Task.WhenAll(_db.InitializeAsync(), _mosquitto.StartAsync());

        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DependencyInjection.TelemetryDbConnectionName}"] = _db.ConnectionString,
            ["Mqtt:Host"] = _mosquitto.Hostname,
            ["Mqtt:Port"] = _mosquitto.GetMappedPublicPort(1883).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Mqtt:ClientId"] = "instr-ingest",
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

        _meterListener.Dispose();
        _activityListener.Dispose();
        await _mosquitto.DisposeAsync();
        await _db.DisposeAsync();
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
        using IMqttClient publisher = new MqttClientFactory().CreateMqttClient();
        await publisher.ConnectAsync(
            new MqttClientOptionsBuilder()
                .WithTcpServer(_mosquitto.Hostname, _mosquitto.GetMappedPublicPort(1883))
                .WithClientId("instr-publisher")
                .Build(),
            CancellationToken.None);

        MqttApplicationMessage Message(byte[] payload) => new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        // Mixed batch: one valid, one malformed, one redelivery of the valid message.
        await publisher.PublishAsync(Message(Envelope(1)), CancellationToken.None);
        await publisher.PublishAsync(Message(Encoding.UTF8.GetBytes("not an envelope")), CancellationToken.None);
        await publisher.PublishAsync(Message(Envelope(1)), CancellationToken.None);

        // The mixed batch is fully processed once all three received messages are accounted
        // for in outcome counters: 1 persisted + 1 rejected + 1 deduplicated.
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
