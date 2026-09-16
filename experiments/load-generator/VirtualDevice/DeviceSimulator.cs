using System.Diagnostics;
using MQTTnet;
using MQTTnet.Protocol;
using VehicleDiagnostics.Contracts;

namespace VehicleDiagnostics.VirtualDevice;

/// <summary>Which processing mode the simulated gateway runs in (architecture.md §17).</summary>
public enum SimulatedMode
{
    /// <summary>Publish raw OBD-II response frames on <c>tel/raw</c>; the backend decodes.</summary>
    P0,

    /// <summary>Publish decoded signals on <c>tel/signals</c>; the device decodes.</summary>
    P2,
}

/// <summary>
/// One simulated gateway: a simple vehicle model (rpm follows throttle, speed follows rpm,
/// coolant temperature rises asymptotically — the same shape the future sim-ECU produces),
/// published with a monotonic sequence and a fresh W3C traceparent per message.
/// </summary>
/// <remarks>
/// The drive-cycle state is encoded into OBD-II Mode 01 response bytes first, and both modes
/// derive from those bytes: P0 emits the frames, P2 emits the readings decoded back from them.
/// A real gateway's P2 values come from decoding the same frames, so this quantization is the
/// physical truth, and it makes P0 and P2 results identical by construction (design D7).
/// </remarks>
public sealed class DeviceSimulator(
    string host,
    int port,
    string vehicleId,
    string deviceId,
    double rateHz,
    SimulatedMode mode,
    string? heartbeatFile)
{
    private const uint ObdResponseId = 0x7E8;

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

        string topic = mode == SimulatedMode.P0
            ? TelemetryTopics.Raw(vehicleId, deviceId)
            : TelemetryTopics.Signals(vehicleId, deviceId);
        var interval = TimeSpan.FromSeconds(1.0 / rateHz);
        using var timer = new PeriodicTimer(interval);
        double elapsedSeconds = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Like the real gateway, the simulator reconnects on its own after a broker
                // outage; the sequence keeps counting so redeliveries stay detectable.
                try
                {
                    if (!client.IsConnected)
                    {
                        await client.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"[{deviceId}] connected ({mode}), publishing to '{topic}' every {interval.TotalMilliseconds:F0} ms");
                    }

                    while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                    {
                        elapsedSeconds += interval.TotalSeconds;
                        Advance(elapsedSeconds, interval.TotalSeconds);

                        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                            .WithTopic(topic)
                            .WithPayload(BuildPayload())
                            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                            .Build();
                        await client.PublishAsync(message, cancellationToken).ConfigureAwait(false);
                        Heartbeat();
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Console.WriteLine($"[{deviceId}] connection lost ({exception.GetType().Name}); retrying in 2 s");
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
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

    /// <summary>
    /// Liveness signal for container health checks: the file's modification time is the
    /// moment of the last successful publish. Disabled when no path is configured.
    /// </summary>
    private void Heartbeat()
    {
        if (heartbeatFile is null)
        {
            return;
        }

        File.WriteAllText(heartbeatFile, _sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

    private byte[] BuildPayload()
    {
        long sequence = Interlocked.Increment(ref _sequence);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string traceParent = $"00-{ActivityTraceId.CreateRandom().ToHexString()}-{ActivitySpanId.CreateRandom().ToHexString()}-01";
        ObdMode01Encoding encoded = ObdMode01Encoding.From(_rpm, _speedKmH, _coolantTemp);

        return mode == SimulatedMode.P0
            ? EnvelopeSerializer.Serialize(new MessageEnvelope<RawFramesPayload>
            {
                SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
                DeviceId = deviceId,
                VehicleId = vehicleId,
                Sequence = sequence,
                TimestampDevice = now,
                TraceId = traceParent,
                ProcessingMode = "P0",
                Payload = new RawFramesPayload
                {
                    Frames = [.. encoded.Frames.Select(f => new CanFrameDto { CanId = ObdResponseId, Data = f })],
                },
            })
            : EnvelopeSerializer.Serialize(new MessageEnvelope<SignalReadingsPayload>
            {
                SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
                DeviceId = deviceId,
                VehicleId = vehicleId,
                Sequence = sequence,
                TimestampDevice = now,
                TraceId = traceParent,
                ProcessingMode = "P2",
                Payload = new SignalReadingsPayload { Readings = encoded.Readings },
            });
    }
}

/// <summary>
/// OBD-II Mode 01 wire encoding of the drive-cycle state, and the readings a decoder would
/// get back from those exact bytes. Shared by both emission modes so they agree exactly.
/// </summary>
public sealed record ObdMode01Encoding(IReadOnlyList<string> Frames, IReadOnlyList<SignalReadingDto> Readings)
{
    public static ObdMode01Encoding From(double rpm, double speedKmH, double coolantTemp)
    {
        // SAE J1979: rpm = (256A + B) / 4; speed = A; coolant = A - 40.
        int rpmRaw = Math.Clamp((int)Math.Round(rpm * 4), 0, 0xFFFF);
        int speedRaw = Math.Clamp((int)Math.Round(speedKmH), 0, 0xFF);
        int coolantRaw = Math.Clamp((int)Math.Round(coolantTemp) + 40, 0, 0xFF);

        string[] frames =
        [
            $"04410C{rpmRaw >> 8:X2}{rpmRaw & 0xFF:X2}",
            $"03410D{speedRaw:X2}",
            $"034105{coolantRaw:X2}",
        ];
        SignalReadingDto[] readings =
        [
            new() { Name = "engine_rpm", Value = rpmRaw / 4.0, Unit = "rpm" },
            new() { Name = "vehicle_speed", Value = speedRaw, Unit = "km_h" },
            new() { Name = "coolant_temp", Value = coolantRaw - 40, Unit = "degC" },
        ];
        return new ObdMode01Encoding(frames, readings);
    }
}
