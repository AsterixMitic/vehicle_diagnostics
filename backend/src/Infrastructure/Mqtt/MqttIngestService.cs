using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Infrastructure.Observability;

namespace VehicleDiagnostics.Infrastructure.Mqtt;

/// <summary>
/// Subscribes to <c>veh/+/dev/+/tel/signals</c> at QoS 1 and drives each message through
/// structural validation into the ingest use case. An explicit reconnect loop replaces the
/// managed client MQTTnet v5 no longer ships: connectivity is re-checked every second and a
/// reconnect always re-subscribes. A failing message never stops the pipeline.
/// </summary>
public sealed partial class MqttIngestService(
    IServiceScopeFactory scopeFactory,
    MqttConnectionState connectionState,
    IOptions<MqttIngestOptions> options,
    ILogger<MqttIngestService> logger) : BackgroundService
{
    private static readonly TimeSpan ReconnectProbeInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectFailureBackoff = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttClientFactory();
        using IMqttClient client = factory.CreateMqttClient();
        client.ApplicationMessageReceivedAsync += args => HandleMessageAsync(args, stoppingToken);

        MqttClientOptions connectOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Value.Host, options.Value.Port)
            .WithClientId(options.Value.ClientId)
            .Build();
        MqttClientSubscribeOptions subscribeOptions = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f
                .WithTopic(TelemetryTopics.SignalsFilter)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce))
            .Build();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    connectionState.IsConnected = false;
                    await client.ConnectAsync(connectOptions, stoppingToken).ConfigureAwait(false);
                    await client.SubscribeAsync(subscribeOptions, stoppingToken).ConfigureAwait(false);
                    connectionState.IsConnected = true;
                    LogConnected(logger, options.Value.Host, options.Value.Port);
                }

                await Task.Delay(ReconnectProbeInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                connectionState.IsConnected = false;
                LogConnectFailed(logger, options.Value.Host, options.Value.Port, exception);
                try
                {
                    await Task.Delay(ReconnectFailureBackoff, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        connectionState.IsConnected = false;
        try
        {
            await client.DisconnectAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Shutdown path: the broker may already be gone.
        }
    }

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessMessageAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; the unacked message will be redelivered and deduplicated.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            IngestDiagnostics.CountRejected("unhandled_exception");
            LogPoisonMessage(logger, args.ApplicationMessage.Topic, exception);
        }
    }

    private async Task ProcessMessageAsync(MqttApplicationMessageReceivedEventArgs args, CancellationToken cancellationToken)
    {
        string topic = args.ApplicationMessage.Topic;
        byte[] payload = args.ApplicationMessage.Payload.ToArray();

        IngestDiagnostics.MessagesReceived.Add(1);

        if (!TelemetryTopics.TryParseSignals(topic, out string? vehicleId, out string? deviceId))
        {
            IngestDiagnostics.CountRejected("unknown_topic");
            LogRejected(logger, topic, "unknown_topic");
            return;
        }

        if (!EnvelopeSerializer.TryDeserialize(payload, out MessageEnvelope<SignalReadingsPayload>? envelope, out string? parseError))
        {
            IngestDiagnostics.CountRejected("malformed");
            LogRejected(logger, topic, $"malformed: {parseError}");
            return;
        }

        // The receive span continues the trace the device started; a malformed traceparent
        // starts a new root and is counted as lost continuity, never as a rejection.
        ActivityContext parentContext = default;
        bool traceRestored = ActivityContext.TryParse(envelope.TraceId, null, isRemote: true, out parentContext);
        using Activity? receiveActivity = IngestDiagnostics.ActivitySource.StartActivity(
            "ingest.receive", ActivityKind.Consumer, parentContext);
        receiveActivity?.SetTag("messaging.destination.name", topic);
        receiveActivity?.SetTag("vehiclediagnostics.device_id", envelope.DeviceId);
        receiveActivity?.SetTag("vehiclediagnostics.sequence", envelope.Sequence);

        if (!traceRestored)
        {
            IngestDiagnostics.TraceContinuityLost.Add(1);
        }

        EnvelopeValidationResult validation;
        using (Activity? validateActivity = IngestDiagnostics.ActivitySource.StartActivity("ingest.validate"))
        {
            validation = EnvelopeValidator.Validate(envelope, vehicleId, deviceId);
            validateActivity?.SetTag("vehiclediagnostics.valid", validation.IsValid);
        }

        if (!validation.IsValid)
        {
            foreach (EnvelopeError error in validation.Errors)
            {
                IngestDiagnostics.CountRejected(error.Code);
            }

            receiveActivity?.SetStatus(ActivityStatusCode.Error, "envelope rejected");
            LogRejected(logger, topic, string.Join("; ", validation.Errors.Select(e => e.Message)));
            return;
        }

        var raw = new RawTelemetry(
            envelope.DeviceId,
            envelope.VehicleId,
            envelope.Sequence,
            envelope.ProcessingMode,
            envelope.TimestampDevice,
            [.. envelope.Payload.Readings.Select(r => new RawSignalReading(r.Name, r.Value, r.Unit))]);

        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var useCase = scope.ServiceProvider.GetRequiredService<IIngestTelemetry>();
        IngestOutcome outcome = await useCase.IngestAsync(raw, cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case IngestOutcome.Accepted:
                IngestDiagnostics.MessagesPersisted.Add(1);
                break;
            case IngestOutcome.Duplicate:
                IngestDiagnostics.MessagesDeduplicated.Add(1);
                break;
            case IngestOutcome.Rejected rejected:
                foreach (var error in rejected.Errors)
                {
                    IngestDiagnostics.CountRejected(error.Code);
                }

                receiveActivity?.SetStatus(ActivityStatusCode.Error, "readings rejected");
                LogRejected(logger, topic, string.Join("; ", rejected.Errors.Select(e => e.Message)));
                break;
            default:
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected and subscribed to MQTT broker {Host}:{Port}")]
    private static partial void LogConnected(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT connection to {Host}:{Port} failed; retrying")]
    private static partial void LogConnectFailed(ILogger logger, string host, int port, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected telemetry message on '{Topic}': {Reason}")]
    private static partial void LogRejected(ILogger logger, string topic, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled failure processing message on '{Topic}'; pipeline continues")]
    private static partial void LogPoisonMessage(ILogger logger, string topic, Exception exception);
}
