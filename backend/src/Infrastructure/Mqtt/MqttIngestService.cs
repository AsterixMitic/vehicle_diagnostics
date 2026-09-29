using System.Buffers;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Infrastructure.Observability;

namespace VehicleDiagnostics.Infrastructure.Mqtt;

/// <summary>
/// Subscribes to <c>tel/signals</c> (P2) and <c>tel/raw</c> (P0) at QoS 1 and drives each
/// message through structural validation into the matching Telemetry use case: decoded
/// signals are persisted, raw frames are forwarded to the Signal Decoding context over the
/// bus. An explicit reconnect loop replaces the managed client MQTTnet v5 no longer ships:
/// connectivity is re-checked every second and a reconnect always re-subscribes. A failing
/// message never stops the pipeline.
/// </summary>
public sealed partial class MqttIngestService(
    IServiceScopeFactory scopeFactory,
    MqttConnectionState connectionState,
    IOptions<MqttIngestOptions> options,
    TimeProvider timeProvider,
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
            .WithTopicFilter(f => f
                .WithTopic(TelemetryTopics.RawFilter)
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
        // Measured first, before any parsing: this is the moment the message reached the backend.
        var receipt = new ReceiptInfo(timeProvider.GetUtcNow(), checked((int)args.ApplicationMessage.Payload.Length));
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var message = new InboundMessage(
            args.ApplicationMessage.Topic,
            args.ApplicationMessage.Payload.ToArray(),
            receipt,
            scope.ServiceProvider,
            scope.ServiceProvider.GetRequiredService<IMessageSettlementReporter>());

        try
        {
            await ProcessMessageAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; the unacked message will be redelivered and deduplicated.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            IngestDiagnostics.CountRejected("unhandled_exception");
            LogPoisonMessage(logger, message.Topic, exception);
            // A topic that does not parse leaves both identifiers null, which is what gets settled.
            bool attributed = TelemetryTopics.TryParseTelemetry(message.Topic, out string? vehicleId, out string? deviceId, out _);
            await message.Reporter.ReportAsync(
                MessageSettlement.RejectedAnonymous(
                    "unhandled_exception", receipt, attributed ? deviceId : null, attributed ? vehicleId : null),
                cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task ProcessMessageAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        IngestDiagnostics.MessagesReceived.Add(1);

        if (!TelemetryTopics.TryParseTelemetry(message.Topic, out string? vehicleId, out string? deviceId, out TelemetrySubtype subtype))
        {
            await RejectAsync(message, "unknown_topic", null, null, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (subtype)
        {
            case TelemetrySubtype.Signals:
                await ProcessSignalsAsync(message, vehicleId, deviceId, cancellationToken).ConfigureAwait(false);
                break;
            case TelemetrySubtype.Raw:
                await ProcessRawAsync(message, vehicleId, deviceId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await RejectAsync(message, "unknown_topic", deviceId, vehicleId, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task ProcessSignalsAsync(InboundMessage message, string vehicleId, string deviceId, CancellationToken cancellationToken)
    {
        string topic = message.Topic;
        if (!EnvelopeSerializer.TryDeserialize(message.Payload, out MessageEnvelope<SignalReadingsPayload>? envelope, out string? parseError))
        {
            await RejectAsync(message, "malformed", deviceId, vehicleId, cancellationToken, parseError).ConfigureAwait(false);
            return;
        }

        using Activity? receiveActivity = StartReceive(envelope, topic);

        EnvelopeValidationResult validation = EnvelopeValidator.Validate(envelope, vehicleId, deviceId);
        if (!Validate(topic, validation, receiveActivity))
        {
            await message.Reporter.ReportAsync(RejectedEnvelope(envelope, validation, message.Receipt), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var raw = new RawTelemetry(
            envelope.DeviceId,
            envelope.VehicleId,
            envelope.Sequence,
            envelope.ProcessingMode,
            envelope.TimestampDevice,
            [.. envelope.Payload.Readings.Select(r => new RawSignalReading(r.Name, r.Value, r.Unit))]);

        var useCase = message.Services.GetRequiredService<IIngestTelemetry>();
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

        await message.Reporter.ReportAsync(IngestSettlement.From(raw, outcome, message.Receipt), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ProcessRawAsync(InboundMessage message, string vehicleId, string deviceId, CancellationToken cancellationToken)
    {
        string topic = message.Topic;
        if (!EnvelopeSerializer.TryDeserializeRaw(message.Payload, out MessageEnvelope<RawFramesPayload>? envelope, out string? parseError))
        {
            await RejectAsync(message, "malformed", deviceId, vehicleId, cancellationToken, parseError).ConfigureAwait(false);
            return;
        }

        using Activity? receiveActivity = StartReceive(envelope, topic);

        EnvelopeValidationResult validation = EnvelopeValidator.Validate(envelope, vehicleId, deviceId);
        if (!Validate(topic, validation, receiveActivity))
        {
            await message.Reporter.ReportAsync(RejectedEnvelope(envelope, validation, message.Receipt), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var frames = new RawTelemetryFrames(
            envelope.DeviceId,
            envelope.VehicleId,
            envelope.Sequence,
            envelope.ProcessingMode,
            envelope.TimestampDevice,
            [.. envelope.Payload.Frames.Select(f => new RawFrameData(f.CanId, f.IsExtended, Convert.FromHexString(f.Data)))],
            message.Receipt);

        var useCase = message.Services.GetRequiredService<IAcceptRawTelemetry>();
        AcceptRawOutcome outcome = await useCase.AcceptAsync(frames, cancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case AcceptRawOutcome.Accepted:
                IngestDiagnostics.RawForwarded.Add(1);
                break;
            case AcceptRawOutcome.Rejected rejected:
                foreach (var error in rejected.Errors)
                {
                    IngestDiagnostics.CountRejected(error.Code);
                }

                receiveActivity?.SetStatus(ActivityStatusCode.Error, "raw frames rejected");
                LogRejected(logger, topic, string.Join("; ", rejected.Errors.Select(e => e.Message)));
                await message.Reporter.ReportAsync(IngestSettlement.FromRejectedRaw(frames, rejected), cancellationToken)
                    .ConfigureAwait(false);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// The receive span continues the trace the device started; a malformed traceparent starts
    /// a new root and is counted as lost continuity, never as a rejection.
    /// </summary>
    private static Activity? StartReceive<TPayload>(MessageEnvelope<TPayload> envelope, string topic)
    {
        bool traceRestored = ActivityContext.TryParse(envelope.TraceId, null, isRemote: true, out ActivityContext parentContext);
        Activity? activity = IngestDiagnostics.ActivitySource.StartActivity("ingest.receive", ActivityKind.Consumer, parentContext);
        activity?.SetTag("messaging.destination.name", topic);
        activity?.SetTag("vehiclediagnostics.device_id", envelope.DeviceId);
        activity?.SetTag("vehiclediagnostics.sequence", envelope.Sequence);
        activity?.SetTag("vehiclediagnostics.processing_mode", envelope.ProcessingMode);

        if (!traceRestored)
        {
            IngestDiagnostics.TraceContinuityLost.Add(1);
        }

        return activity;
    }

    private bool Validate(string topic, EnvelopeValidationResult validation, Activity? receiveActivity)
    {
        using (Activity? validateActivity = IngestDiagnostics.ActivitySource.StartActivity("ingest.validate"))
        {
            validateActivity?.SetTag("vehiclediagnostics.valid", validation.IsValid);
        }

        if (validation.IsValid)
        {
            return true;
        }

        foreach (EnvelopeError error in validation.Errors)
        {
            IngestDiagnostics.CountRejected(error.Code);
        }

        receiveActivity?.SetStatus(ActivityStatusCode.Error, "envelope rejected");
        LogRejected(logger, topic, string.Join("; ", validation.Errors.Select(e => e.Message)));
        return false;
    }

    /// <summary>Counts, logs and settles a message rejected before an envelope could be read.</summary>
    private async Task RejectAsync(
        InboundMessage message,
        string reason,
        string? deviceId,
        string? vehicleId,
        CancellationToken cancellationToken,
        string? detail = null)
    {
        IngestDiagnostics.CountRejected(reason);
        LogRejected(logger, message.Topic, detail is null ? reason : $"{reason}: {detail}");
        await message.Reporter.ReportAsync(
            MessageSettlement.RejectedAnonymous(reason, message.Receipt, deviceId, vehicleId), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>An envelope that parsed but failed validation, settled with the fields it carried.</summary>
    private static MessageSettlement RejectedEnvelope<TPayload>(
        MessageEnvelope<TPayload> envelope, EnvelopeValidationResult validation, ReceiptInfo receipt) =>
        new(SettlementOutcome.Rejected,
            SettlementReasons.Join(validation.Errors.Select(e => e.Code)),
            envelope.DeviceId,
            envelope.VehicleId,
            envelope.Sequence,
            envelope.ProcessingMode,
            envelope.TimestampDevice == default ? null : envelope.TimestampDevice,
            receipt,
            null,
            0);

    /// <summary>One broker message and the per-message scope it is processed in.</summary>
    private sealed record InboundMessage(
        string Topic,
        byte[] Payload,
        ReceiptInfo Receipt,
        IServiceProvider Services,
        IMessageSettlementReporter Reporter);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected and subscribed to MQTT broker {Host}:{Port}")]
    private static partial void LogConnected(ILogger logger, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MQTT connection to {Host}:{Port} failed; retrying")]
    private static partial void LogConnectFailed(ILogger logger, string host, int port, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected telemetry message on '{Topic}': {Reason}")]
    private static partial void LogRejected(ILogger logger, string topic, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled failure processing message on '{Topic}'; pipeline continues")]
    private static partial void LogPoisonMessage(ILogger logger, string topic, Exception exception);
}
