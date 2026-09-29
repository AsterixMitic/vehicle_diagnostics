using MassTransit;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.SignalDecoding;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Contracts.Internal;

namespace VehicleDiagnostics.Infrastructure.Messaging;

/// <summary>Telemetry → bus: accepted raw frames become a <see cref="RawFramesReceived"/> message.</summary>
public sealed class MassTransitRawFramesPublisher(IPublishEndpoint publishEndpoint) : IRawFramesPublisher
{
    public Task PublishAsync(RawFramesAccepted accepted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return publishEndpoint.Publish(
            new RawFramesReceived
            {
                DeviceId = accepted.DeviceId.Value,
                VehicleId = accepted.VehicleId.Value,
                Sequence = accepted.Sequence,
                TimestampDevice = accepted.TimestampDevice,
                ProcessingMode = accepted.ProcessingMode.ToString(),
                Frames = [.. accepted.Frames.Select(ToDto)],
                ReceivedAt = accepted.Receipt?.ReceivedAt,
                PayloadBytes = accepted.Receipt?.PayloadBytes,
            },
            cancellationToken);
    }

    internal static CanFrameDto ToDto(RawFrameData frame) => new()
    {
        CanId = frame.CanId,
        IsExtended = frame.IsExtended,
        Data = Convert.ToHexString(frame.Data.Span),
    };
}

/// <summary>Signal Decoding → bus: decoded readings become a <see cref="SignalsDecoded"/> message.</summary>
public sealed class MassTransitDecodedSignalsPublisher(IPublishEndpoint publishEndpoint) : IDecodedSignalsPublisher
{
    public Task PublishAsync(DecodedSignalsBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return publishEndpoint.Publish(
            new SignalsDecoded
            {
                DeviceId = batch.DeviceId.Value,
                VehicleId = batch.VehicleId.Value,
                Sequence = batch.Sequence,
                TimestampDevice = batch.TimestampDevice,
                ProcessingMode = batch.ProcessingMode.ToString(),
                Readings = [.. batch.Signals.Select(s => new SignalReadingDto
                {
                    Name = s.Name.Value,
                    Value = s.Value,
                    Unit = s.Unit,
                })],
                ReceivedAt = batch.Receipt?.ReceivedAt,
                PayloadBytes = batch.Receipt?.PayloadBytes,
            },
            cancellationToken);
    }
}
