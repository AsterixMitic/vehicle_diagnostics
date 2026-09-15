namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>
/// Shared validation for identifiers that travel inside MQTT topic segments:
/// non-empty, printable ASCII, no whitespace, and none of the MQTT topic
/// metacharacters (<c>/</c>, <c>+</c>, <c>#</c>).
/// </summary>
internal static class IdentifierRules
{
    internal const int MaxLength = 64;

    internal static Result<string, ValidationError> Validate(string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ValidationError.Required(field);
        }

        if (value.Length > MaxLength)
        {
            return ValidationError.OutOfRange(field, $"longer than {MaxLength} characters");
        }

        foreach (char c in value)
        {
            if (c is <= ' ' or > '~')
            {
                return ValidationError.Invalid(field, "must be printable ASCII without whitespace");
            }

            if (c is '/' or '+' or '#')
            {
                return ValidationError.Invalid(field, "must not contain MQTT topic characters '/', '+', '#'");
            }
        }

        return value;
    }
}
