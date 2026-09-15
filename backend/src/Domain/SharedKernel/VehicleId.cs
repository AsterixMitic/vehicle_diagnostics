namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>Identity of a vehicle. Never a raw string across a public boundary.</summary>
public readonly record struct VehicleId
{
    private VehicleId(string value) => Value = value;

    public string Value { get; }

    public static Result<VehicleId, ValidationError> Create(string? value) =>
        IdentifierRules.Validate("vehicleId", value).Map(v => new VehicleId(v));

    public override string ToString() => Value;
}
