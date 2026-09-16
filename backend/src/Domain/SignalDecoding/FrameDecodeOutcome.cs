using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Domain.SignalDecoding;

/// <summary>Why a frame produced no reading. A closed set: every value is a counter tag.</summary>
public enum FrameIgnoreReason
{
    NotObdResponse,
    NotSingleFrame,
    MultiFrameUnsupported,
    NegativeResponse,
    UnsupportedService,
    UnsupportedPid,
    ShortPayload,
}

public static class FrameIgnoreReasonExtensions
{
    /// <summary>Stable snake_case tag for metrics and logs.</summary>
    public static string ToTag(this FrameIgnoreReason reason) => reason switch
    {
        FrameIgnoreReason.NotObdResponse => "not_obd_response",
        FrameIgnoreReason.NotSingleFrame => "not_single_frame",
        FrameIgnoreReason.MultiFrameUnsupported => "multi_frame_unsupported",
        FrameIgnoreReason.NegativeResponse => "negative_response",
        FrameIgnoreReason.UnsupportedService => "unsupported_service",
        FrameIgnoreReason.UnsupportedPid => "unsupported_pid",
        FrameIgnoreReason.ShortPayload => "short_payload",
        _ => "unknown",
    };
}

/// <summary>A physical value decoded from one frame; this context's own reading shape.</summary>
public sealed record DecodedSignal(SignalName Name, double Value, string Unit);

/// <summary>What decoding one frame produced.</summary>
public abstract record FrameDecodeOutcome
{
    private FrameDecodeOutcome()
    {
    }

    public sealed record Decoded(DecodedSignal Signal) : FrameDecodeOutcome;

    public sealed record Ignored(FrameIgnoreReason Reason) : FrameDecodeOutcome;
}
