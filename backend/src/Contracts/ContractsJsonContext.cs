using System.Text.Json.Serialization;

namespace VehicleDiagnostics.Contracts;

/// <summary>
/// Source-generated serialization keeps reflection off the ingest hot path and
/// preserves the Native AOT option (architecture.md §12.3).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MessageEnvelope<SignalReadingsPayload>))]
[JsonSerializable(typeof(MessageEnvelope<RawFramesPayload>))]
public sealed partial class ContractsJsonContext : JsonSerializerContext;
