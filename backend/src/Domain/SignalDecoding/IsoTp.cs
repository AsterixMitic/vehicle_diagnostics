namespace VehicleDiagnostics.Domain.SignalDecoding;

/// <summary>ISO 15765-2 protocol control information, from the upper nibble of the first data byte.</summary>
public enum IsoTpFrameKind
{
    /// <summary>Whole message fits in this frame; length in the low nibble (1–7).</summary>
    SingleFrame = 0,

    /// <summary>Start of a multi-frame message.</summary>
    FirstFrame = 1,

    /// <summary>Continuation of a multi-frame message.</summary>
    ConsecutiveFrame = 2,

    /// <summary>Receiver's flow-control frame.</summary>
    FlowControl = 3,

    /// <summary>Empty frame or a length that does not fit the frame.</summary>
    Invalid = -1,
}

/// <summary>
/// Single-frame parsing only. Multi-frame reassembly (needed for VIN and DTC lists) is a later
/// increment; here those frames are classified so the decoder can count them as unsupported.
/// </summary>
public static class IsoTp
{
    public static IsoTpFrameKind Classify(CanFrame frame, out int singleFrameLength)
    {
        ArgumentNullException.ThrowIfNull(frame);
        singleFrameLength = 0;
        if (frame.Data.Length == 0)
        {
            return IsoTpFrameKind.Invalid;
        }

        int kind = frame.Data[0] >> 4;
        if (kind != (int)IsoTpFrameKind.SingleFrame)
        {
            return kind is >= 1 and <= 3 ? (IsoTpFrameKind)kind : IsoTpFrameKind.Invalid;
        }

        int length = frame.Data[0] & 0x0F;
        if (length is < 1 or > 7 || frame.Data.Length < 1 + length)
        {
            return IsoTpFrameKind.Invalid;
        }

        singleFrameLength = length;
        return IsoTpFrameKind.SingleFrame;
    }

    /// <summary>The application payload of a single frame, without PCI byte or padding.</summary>
    public static ReadOnlySpan<byte> SingleFramePayload(CanFrame frame, int length) =>
        frame.Data.AsSpan().Slice(1, length);
}
