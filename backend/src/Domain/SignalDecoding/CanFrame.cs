using System.Collections.Immutable;
using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Domain.SignalDecoding;

/// <summary>One classic CAN 2.0 frame: identifier plus zero to eight data bytes.</summary>
public sealed record CanFrame
{
    public const int MaxDataLength = 8;

    private CanFrame(CanId id, ImmutableArray<byte> data)
    {
        Id = id;
        Data = data;
    }

    public CanId Id { get; }

    public ImmutableArray<byte> Data { get; }

    public static Result<CanFrame, ValidationError> Create(CanId id, ReadOnlySpan<byte> data) =>
        data.Length <= MaxDataLength
            ? new CanFrame(id, [.. data])
            : ValidationError.OutOfRange("data", $"{data.Length} bytes exceeds the {MaxDataLength}-byte CAN payload");

    public override string ToString() => $"{Id}#{Convert.ToHexString(Data.AsSpan())}";
}
