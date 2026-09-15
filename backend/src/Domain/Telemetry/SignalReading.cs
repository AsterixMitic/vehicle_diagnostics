using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Domain.Telemetry;

/// <summary>One decoded physical value sampled on the device at <see cref="SampledAt"/>.</summary>
public sealed record SignalReading
{
    private const int MaxUnitLength = 32;

    private SignalReading(SignalName name, double value, string unit, DateTimeOffset sampledAt)
    {
        Name = name;
        Value = value;
        Unit = unit;
        SampledAt = sampledAt;
    }

    public SignalName Name { get; }

    public double Value { get; }

    /// <summary>Physical unit symbol; empty for dimensionless signals.</summary>
    public string Unit { get; }

    /// <summary>Device-side sample timestamp carried by the message envelope.</summary>
    public DateTimeOffset SampledAt { get; }

    public static Result<SignalReading, ValidationError> Create(
        SignalName name, double value, string? unit, DateTimeOffset sampledAt)
    {
        if (!double.IsFinite(value))
        {
            return ValidationError.Invalid("value", "must be a finite number");
        }

        string normalizedUnit = unit?.Trim() ?? string.Empty;
        if (normalizedUnit.Length > MaxUnitLength)
        {
            return ValidationError.OutOfRange("unit", $"longer than {MaxUnitLength} characters");
        }

        return new SignalReading(name, value, normalizedUnit, sampledAt);
    }
}
