namespace VehicleDiagnostics.Contracts.Internal;

/// <summary>
/// Internal bus message (ADR-0004): the Telemetry context accepted a raw-frames envelope and
/// hands its frames to the Signal Decoding context. Carries the originating envelope's identity
/// so the decoded result can be persisted and deduplicated under the same key.
/// </summary>
public sealed record RawFramesReceived
{
    /// <summary>
    /// Version 2 added <see cref="ReceivedAt"/> and <see cref="PayloadBytes"/>; both are absent
    /// (null) in a version-1 message.
    /// </summary>
    public const int Version = 2;

    public required string DeviceId { get; init; }

    public required string VehicleId { get; init; }

    public required long Sequence { get; init; }

    public required DateTimeOffset TimestampDevice { get; init; }

    public required string ProcessingMode { get; init; }

    public required IReadOnlyList<CanFrameDto> Frames { get; init; }

    /// <summary>When the backend received the original envelope from the broker.</summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>Size in bytes of the original envelope as received.</summary>
    public int? PayloadBytes { get; init; }
}
