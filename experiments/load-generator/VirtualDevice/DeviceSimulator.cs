using System.Diagnostics;
using MQTTnet;
using MQTTnet.Protocol;
using VehicleDiagnostics.Contracts;

namespace VehicleDiagnostics.VirtualDevice;

/// <summary>
/// One simulated gateway: a simple vehicle model (rpm follows throttle, speed follows rpm,
/// coolant temperature rises asymptotically — the same shape the future sim-ECU produces),
/// published with a monotonic sequence and a fresh W3C traceparent per message.
/// </summary>
public sealed class DeviceSimulator(string host, int port, string vehicleId, string deviceId, double rateHz)
{
    private long _sequence;
    private double _rpm = 800;
    private double _speedKmH;
    private double _coolantTemp = 20;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using IMqttClient client = new MqttClientFactory().CreateMqttClient();
        MqttClientOptions options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"virtual-{deviceId}")
            .Build();

        string topic = TelemetryTopics.Signals(vehicleId, deviceId);
        var interval = TimeSpan.FromSeconds(1.0 / rateHz);
        using var timer = new PeriodicTimer(interval);
        double elapsedSeconds = 0;

        try
        {
            await client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[{deviceId}] connected, publishing to '{topic}' every {interval.TotalMilliseconds:F0} ms");

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                elapsedSeconds += interval.TotalSeconds;
                Advance(elapsedSeconds, interval.TotalSeconds);

                MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(EnvelopeSerializer.Serialize(BuildEnvelope()))
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();
                await client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            Console.WriteLine($"[{deviceId}] stopped after {_sequence} messages");
        }
    }

    private void Advance(double elapsedSeconds, double dtSeconds)
    {
        // Throttle oscillates through a gentle cycle; rpm chases the throttle target with
        // inertia; speed follows rpm; coolant approaches 90 °C asymptotically.
        double throttle = 0.5 + 0.4 * Math.Sin(elapsedSeconds / 20.0);
        double targetRpm = 800 + (throttle * 5200);
        _rpm += (targetRpm - _rpm) * Math.Min(1, dtSeconds * 0.8);
        _speedKmH = Math.Max(0, (_rpm - 800) / 40.0);
        _coolantTemp += (90.0 - _coolantTemp) * Math.Min(1, dtSeconds * 0.01);
    }

    private MessageEnvelope<SignalReadingsPayload> BuildEnvelope() => new()
    {
        SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
        DeviceId = deviceId,
        VehicleId = vehicleId,
        Sequence = Interlocked.Increment(ref _sequence),
        TimestampDevice = DateTimeOffset.UtcNow,
        TraceId = $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01",
        ProcessingMode = "P2",
        Payload = new SignalReadingsPayload
        {
            Readings =
            [
                new SignalReadingDto { Name = "engine_rpm", Value = Math.Round(_rpm, 1), Unit = "rpm" },
                new SignalReadingDto { Name = "vehicle_speed", Value = Math.Round(_speedKmH, 1), Unit = "km_h" },
                new SignalReadingDto { Name = "coolant_temp", Value = Math.Round(_coolantTemp, 1), Unit = "degC" },
            ],
        },
    };
}
