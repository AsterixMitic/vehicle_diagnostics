using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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

    public static byte[] Serialize(MessageEnvelope<RawFramesPayload> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return JsonSerializer.SerializeToUtf8Bytes(
            envelope, ContractsJsonContext.Default.MessageEnvelopeRawFramesPayload);
    }

    /// <summary>
    /// Parses a decoded-signals envelope. Unknown extra JSON properties are tolerated (forward
    /// compatibility within a major schema version); missing required fields are not.
    /// </summary>
    public static bool TryDeserialize(
        ReadOnlySpan<byte> utf8Json,
        [NotNullWhen(true)] out MessageEnvelope<SignalReadingsPayload>? envelope,
        [NotNullWhen(false)] out string? parseError) =>
        TryDeserialize(utf8Json, ContractsJsonContext.Default.MessageEnvelopeSignalReadingsPayload, out envelope, out parseError);

    /// <summary>Parses a raw-frames envelope with the same tolerance rules as the signals variant.</summary>
    public static bool TryDeserializeRaw(
        ReadOnlySpan<byte> utf8Json,
        [NotNullWhen(true)] out MessageEnvelope<RawFramesPayload>? envelope,
        [NotNullWhen(false)] out string? parseError) =>
        TryDeserialize(utf8Json, ContractsJsonContext.Default.MessageEnvelopeRawFramesPayload, out envelope, out parseError);

    private static bool TryDeserialize<TPayload>(
        ReadOnlySpan<byte> utf8Json,
        JsonTypeInfo<MessageEnvelope<TPayload>> typeInfo,
        [NotNullWhen(true)] out MessageEnvelope<TPayload>? envelope,
        [NotNullWhen(false)] out string? parseError)
    {
        try
        {
            envelope = JsonSerializer.Deserialize(utf8Json, typeInfo);
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
