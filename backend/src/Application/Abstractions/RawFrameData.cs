namespace VehicleDiagnostics.Application.Abstractions;

/// <summary>
/// Transport-agnostic CAN frame as it crosses application boundaries: numeric identifier,
/// extended flag, raw bytes. Shared by the Telemetry (accepts) and Signal Decoding (decodes)
/// contexts, so it lives outside both context namespaces.
/// </summary>
public sealed record RawFrameData(uint CanId, bool IsExtended, ReadOnlyMemory<byte> Data);
