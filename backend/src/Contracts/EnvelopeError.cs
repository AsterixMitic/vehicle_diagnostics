namespace VehicleDiagnostics.Contracts;

/// <summary>A machine-readable reason why an envelope was rejected. Codes feed the rejection counters.</summary>
public sealed record EnvelopeError(string Code, string Message)
{
    public const string UnsupportedSchemaVersion = "unsupported_schema_version";
    public const string MissingField = "missing_field";
    public const string InvalidField = "invalid_field";
    public const string IdentityMismatch = "identity_mismatch";
    public const string EmptyPayload = "empty_payload";
}
