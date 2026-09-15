namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>Identity of a diagnostic gateway device. Never a raw string across a public boundary.</summary>
public readonly record struct DeviceId
{
    private DeviceId(string value) => Value = value;

    public string Value { get; }

    public static Result<DeviceId, ValidationError> Create(string? value) =>
        IdentifierRules.Validate("deviceId", value).Map(v => new DeviceId(v));

    public override string ToString() => Value;
}
