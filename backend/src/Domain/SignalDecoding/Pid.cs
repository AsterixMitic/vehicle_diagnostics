namespace VehicleDiagnostics.Domain.SignalDecoding;

/// <summary>
/// OBD-II parameter identifier (SAE J1979). Every byte is a syntactically valid identifier;
/// whether the backend can decode it is the decoder's concern, reported as an ignore reason.
/// Typed so a <c>Pid</c> can never be confused with a UDS <c>Did</c> or a raw byte.
/// </summary>
public readonly record struct Pid(byte Value)
{
    public override string ToString() => $"0x{Value:X2}";
}
