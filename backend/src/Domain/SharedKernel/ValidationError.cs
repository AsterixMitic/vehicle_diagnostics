namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>A named, machine-readable reason why a value failed validation.</summary>
public sealed record ValidationError(string Code, string Message)
{
    public static ValidationError Required(string field) =>
        new("required", $"'{field}' is required and was missing or empty.");

    public static ValidationError Invalid(string field, string reason) =>
        new("invalid", $"'{field}' is invalid: {reason}");

    public static ValidationError OutOfRange(string field, string reason) =>
        new("out_of_range", $"'{field}' is out of range: {reason}");
}
