using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Domain.SignalDecoding;

/// <summary>A classic CAN identifier: 11-bit standard or 29-bit extended.</summary>
public readonly record struct CanId
{
    public const uint MaxStandard = 0x7FF;
    public const uint MaxExtended = 0x1FFFFFFF;

    private CanId(uint value, bool isExtended)
    {
        Value = value;
        IsExtended = isExtended;
    }

    public uint Value { get; }

    public bool IsExtended { get; }

    /// <summary>
    /// True for OBD-II diagnostic response identifiers: 11-bit <c>0x7E8</c>–<c>0x7EF</c>
    /// (ECU 1–8 replying to the tester) or 29-bit <c>0x18DAF1xx</c> (ISO 15765-4 physical
    /// addressing, target 0xF1 = external tester).
    /// </summary>
    public bool IsObdResponse => IsExtended
        ? (Value & 0xFFFFFF00) == 0x18DAF100
        : Value is >= 0x7E8 and <= 0x7EF;

    public static Result<CanId, ValidationError> Create(uint value, bool isExtended = false)
    {
        uint max = isExtended ? MaxExtended : MaxStandard;
        return value <= max
            ? new CanId(value, isExtended)
            : ValidationError.OutOfRange("canId", $"0x{value:X} exceeds the {(isExtended ? 29 : 11)}-bit range");
    }

    public override string ToString() => IsExtended ? $"0x{Value:X8}" : $"0x{Value:X3}";
}
