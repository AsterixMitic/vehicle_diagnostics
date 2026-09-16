namespace VehicleDiagnostics.Contracts;

/// <summary>Payload of the <c>tel/raw</c> topic: CAN frames as captured by the gateway (P0 telemetry).</summary>
public sealed record RawFramesPayload
{
    public required IReadOnlyList<CanFrameDto> Frames { get; init; }
}

/// <summary>One classic CAN 2.0 frame: numeric identifier, extended flag, up to eight data bytes as hex.</summary>
public sealed record CanFrameDto
{
    /// <summary>11-bit (standard) or 29-bit (extended) identifier as a number.</summary>
    public required uint CanId { get; init; }

    public bool IsExtended { get; init; }

    /// <summary>Data bytes as an even-length hex string of at most sixteen characters, e.g. <c>03410C1AF8</c>.</summary>
    public required string Data { get; init; }
}
