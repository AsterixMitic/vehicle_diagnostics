using VehicleDiagnostics.Domain.SharedKernel;

namespace VehicleDiagnostics.Domain.Telemetry;

/// <summary>Name of a decoded physical signal (e.g. <c>engine_rpm</c>).</summary>
public readonly record struct SignalName
{
    private const int MaxLength = 128;

    private SignalName(string value) => Value = value;

    public string Value { get; }

    public static Result<SignalName, ValidationError> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ValidationError.Required("signalName");
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            return ValidationError.OutOfRange("signalName", $"longer than {MaxLength} characters");
        }

        if (trimmed.Any(char.IsControl))
        {
            return ValidationError.Invalid("signalName", "must not contain control characters");
        }

        return new SignalName(trimmed);
    }

    public override string ToString() => Value;
}
