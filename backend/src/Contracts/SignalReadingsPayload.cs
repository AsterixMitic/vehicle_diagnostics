namespace VehicleDiagnostics.Contracts;

/// <summary>Payload of the <c>tel/signals</c> topic: decoded physical signals (P2-style telemetry).</summary>
public sealed record SignalReadingsPayload
{
    public required IReadOnlyList<SignalReadingDto> Readings { get; init; }
}

/// <summary>One decoded signal sample. Values are physical quantities, already scaled.</summary>
public sealed record SignalReadingDto
{
    public required string Name { get; init; }

    public required double Value { get; init; }

    /// <summary>Physical unit symbol; empty for dimensionless signals.</summary>
    public string Unit { get; init; } = string.Empty;
}
