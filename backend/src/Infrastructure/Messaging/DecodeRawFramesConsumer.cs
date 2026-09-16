using System.Diagnostics;
using MassTransit;
using Microsoft.Extensions.Logging;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.SignalDecoding;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Contracts.Internal;
using VehicleDiagnostics.Infrastructure.Observability;

namespace VehicleDiagnostics.Infrastructure.Messaging;

/// <summary>Bus → Signal Decoding context: decode one raw envelope's frames.</summary>
public sealed partial class DecodeRawFramesConsumer(
    IDecodeFrames decodeFrames,
    ILogger<DecodeRawFramesConsumer> logger) : IConsumer<RawFramesReceived>
{
    public async Task Consume(ConsumeContext<RawFramesReceived> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RawFramesReceived message = context.Message;

        using Activity? activity = DecodeDiagnostics.ActivitySource.StartActivity("decode.frames");
        activity?.SetTag("vehiclediagnostics.device_id", message.DeviceId);
        activity?.SetTag("vehiclediagnostics.sequence", message.Sequence);
        activity?.SetTag("vehiclediagnostics.frames", message.Frames.Count);

        List<RawFrameData> frames = new(message.Frames.Count);
        int unparseable = 0;
        foreach (CanFrameDto dto in message.Frames)
        {
            try
            {
                frames.Add(new RawFrameData(dto.CanId, dto.IsExtended, Convert.FromHexString(dto.Data)));
            }
            catch (FormatException)
            {
                unparseable++;
            }
        }

        DecodeDiagnostics.FramesReceived.Add(message.Frames.Count);
        if (unparseable > 0)
        {
            DecodeDiagnostics.CountIgnored(DecodeTags.InvalidFrame, unparseable);
        }

        var input = new FramesToDecode(
            message.DeviceId, message.VehicleId, message.Sequence, message.ProcessingMode, message.TimestampDevice, frames);
        DecodeOutcome outcome = await decodeFrames.DecodeAsync(input, context.CancellationToken).ConfigureAwait(false);

        switch (outcome)
        {
            case DecodeOutcome.Processed processed:
                DecodeDiagnostics.FramesDecoded.Add(processed.Signals.Count);
                foreach ((string reason, int count) in processed.IgnoredByReason)
                {
                    DecodeDiagnostics.CountIgnored(reason, count);
                }

                if (!processed.Published)
                {
                    DecodeDiagnostics.EnvelopesEmpty.Add(1);
                }

                activity?.SetTag("vehiclediagnostics.decoded", processed.Signals.Count);
                break;
            case DecodeOutcome.Rejected rejected:
                activity?.SetStatus(ActivityStatusCode.Error, "identity rejected");
                LogRejected(logger, message.DeviceId, message.Sequence, string.Join("; ", rejected.Errors.Select(e => e.Message)));
                break;
            default:
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected raw frames from {DeviceId} sequence {Sequence}: {Reason}")]
    private static partial void LogRejected(ILogger logger, string deviceId, long sequence, string reason);
}
