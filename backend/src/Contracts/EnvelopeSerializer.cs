using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace VehicleDiagnostics.Contracts;

/// <summary>UTF-8 JSON wire format for <see cref="MessageEnvelope{TPayload}"/> (stable camelCase names).</summary>
public static class EnvelopeSerializer
{
    /// <summary>The schema version this backend produces and accepts.</summary>
    public const int CurrentSchemaVersion = 1;

    public static byte[] Serialize(MessageEnvelope<SignalReadingsPayload> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(
            envelope, ContractsJsonContext.Default.MessageEnvelopeSignalReadingsPayload);
    }

    /// <summary>
    /// Parses an envelope. Unknown extra JSON properties are tolerated (forward
    /// compatibility within a major schema version); missing required fields are not.
    /// </summary>
    public static bool TryDeserialize(
        ReadOnlySpan<byte> utf8Json,
        [NotNullWhen(true)] out MessageEnvelope<SignalReadingsPayload>? envelope,
        [NotNullWhen(false)] out string? parseError)
    {
        try
        {
            envelope = JsonSerializer.Deserialize(
                utf8Json, ContractsJsonContext.Default.MessageEnvelopeSignalReadingsPayload);
        }
        catch (JsonException exception)
        {
            envelope = null;
            parseError = exception.Message;
            return false;
        }

        if (envelope is null)
        {
            parseError = "Message is JSON 'null'.";
            return false;
        }

        parseError = null;
        return true;
    }
}
