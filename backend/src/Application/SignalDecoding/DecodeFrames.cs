using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.SignalDecoding;

namespace VehicleDiagnostics.Application.SignalDecoding;

/// <summary>Raw frames as received from the bus, with the originating envelope's identity.</summary>
public sealed record FramesToDecode(
    string DeviceId,
    string VehicleId,
    long Sequence,
    string ProcessingMode,
    DateTimeOffset TimestampDevice,
    IReadOnlyList<RawFrameData> Frames,
    ReceiptInfo? Receipt = null);

/// <summary>Readings decoded from one envelope, ready to be handed back to the Telemetry context.</summary>
public sealed record DecodedSignalsBatch(
    DeviceId DeviceId,
    VehicleId VehicleId,
    long Sequence,
    ProcessingMode ProcessingMode,
    DateTimeOffset TimestampDevice,
    IReadOnlyList<DecodedSignal> Signals,
    ReceiptInfo? Receipt);

/// <summary>Outbound port: publish decoded readings for the Telemetry context to persist.</summary>
public interface IDecodedSignalsPublisher
{
    Task PublishAsync(DecodedSignalsBatch batch, CancellationToken cancellationToken);
}

/// <summary>Counter tag for frames that could not even be constructed (identifier or length out of range).</summary>
public static class DecodeTags
{
    public const string InvalidFrame = "invalid_frame";
}

public abstract record DecodeOutcome
{
    private DecodeOutcome()
    {
    }

    /// <summary>Frames processed; <see cref="Published"/> is false when nothing decoded.</summary>
    public sealed record Processed(
        int FramesReceived,
        IReadOnlyList<DecodedSignal> Signals,
        IReadOnlyDictionary<string, int> IgnoredByReason,
        bool Published) : DecodeOutcome;

    /// <summary>The envelope identity was invalid; nothing decoded.</summary>
    public sealed record Rejected(IReadOnlyList<ValidationError> Errors) : DecodeOutcome;
}

/// <summary>Inbound port of the Signal Decoding context.</summary>
public interface IDecodeFrames
{
    Task<DecodeOutcome> DecodeAsync(FramesToDecode input, CancellationToken cancellationToken);
}

/// <summary>
/// Decodes every frame of one envelope independently: one undecodable frame never prevents the
/// others from decoding. Decoded readings are published as one batch under the envelope's
/// identity; an envelope that yields nothing publishes nothing.
/// </summary>
public sealed class DecodeFrames(IDecodedSignalsPublisher publisher) : IDecodeFrames
{
    public async Task<DecodeOutcome> DecodeAsync(FramesToDecode input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        List<ValidationError> errors = [];

        var deviceId = DeviceId.Create(input.DeviceId);
        if (deviceId.IsFailure)
        {
            errors.Add(deviceId.Error);
        }

        var vehicleId = VehicleId.Create(input.VehicleId);
        if (vehicleId.IsFailure)
        {
            errors.Add(vehicleId.Error);
        }

        if (!Enum.TryParse(input.ProcessingMode, ignoreCase: false, out ProcessingMode mode))
        {
            errors.Add(ValidationError.Invalid("processingMode", $"unknown mode '{input.ProcessingMode}'"));
        }

        if (errors.Count > 0)
        {
            return new DecodeOutcome.Rejected(errors);
        }

        List<DecodedSignal> signals = [];
        Dictionary<string, int> ignored = [];

        foreach (RawFrameData raw in input.Frames)
        {
            var frame = CanId.Create(raw.CanId, raw.IsExtended)
                .Bind(id => CanFrame.Create(id, raw.Data.Span));
            if (frame.IsFailure)
            {
                Count(ignored, DecodeTags.InvalidFrame);
                continue;
            }

            switch (ObdMode01Decoder.Decode(frame.Value))
            {
                case FrameDecodeOutcome.Decoded decoded:
                    signals.Add(decoded.Signal);
                    break;
                case FrameDecodeOutcome.Ignored skipped:
                    Count(ignored, skipped.Reason.ToTag());
                    break;
                default:
                    break;
            }
        }

        bool published = signals.Count > 0;
        if (published)
        {
            var batch = new DecodedSignalsBatch(
                deviceId.Value, vehicleId.Value, input.Sequence, mode, input.TimestampDevice, signals, input.Receipt);
            await publisher.PublishAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        return new DecodeOutcome.Processed(input.Frames.Count, signals, ignored, published);
    }

    private static void Count(Dictionary<string, int> counts, string tag) =>
        counts[tag] = counts.TryGetValue(tag, out int n) ? n + 1 : 1;
}
