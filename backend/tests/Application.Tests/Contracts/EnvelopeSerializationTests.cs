using System.Text;
using FluentAssertions;
using VehicleDiagnostics.Contracts;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.Contracts;

public sealed class EnvelopeSerializationTests
{
    internal static MessageEnvelope<SignalReadingsPayload> SampleEnvelope(
        int schemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
        string deviceId = "esp32-01",
        string vehicleId = "car-01",
        long sequence = 42,
        string traceId = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        string processingMode = "P2") =>
        new()
        {
            SchemaVersion = schemaVersion,
            DeviceId = deviceId,
            VehicleId = vehicleId,
            Sequence = sequence,
            TimestampDevice = new DateTimeOffset(2026, 9, 15, 12, 0, 0, 500, TimeSpan.Zero),
            TraceId = traceId,
            ProcessingMode = processingMode,
            Payload = new SignalReadingsPayload
            {
                Readings =
                [
                    new SignalReadingDto { Name = "engine_rpm", Value = 2450.5, Unit = "rpm" },
                    new SignalReadingDto { Name = "coolant_temp", Value = 88.0, Unit = "degC" },
                ],
            },
        };

    [Fact]
    public void Round_trip_preserves_every_field()
    {
        MessageEnvelope<SignalReadingsPayload> original = SampleEnvelope();

        byte[] bytes = EnvelopeSerializer.Serialize(original);
        bool parsed = EnvelopeSerializer.TryDeserialize(bytes, out var restored, out string? error);

        parsed.Should().BeTrue(error);
        restored.Should().BeEquivalentTo(original);
    }

    [Fact]
    public void Field_names_are_stable_camelCase()
    {
        string json = Encoding.UTF8.GetString(EnvelopeSerializer.Serialize(SampleEnvelope()));

        json.Should().ContainAll(
            "\"schemaVersion\"", "\"deviceId\"", "\"vehicleId\"", "\"sequence\"",
            "\"timestampDevice\"", "\"traceId\"", "\"processingMode\"", "\"payload\"", "\"readings\"");
    }

    [Fact]
    public void Unknown_extra_fields_are_tolerated()
    {
        string json = Encoding.UTF8.GetString(EnvelopeSerializer.Serialize(SampleEnvelope()));
        string withExtras = json.Replace(
            "\"schemaVersion\"", "\"futureField\":\"ignored\",\"schemaVersion\"", StringComparison.Ordinal);

        bool parsed = EnvelopeSerializer.TryDeserialize(
            Encoding.UTF8.GetBytes(withExtras), out var restored, out string? error);

        parsed.Should().BeTrue(error);
        restored!.Sequence.Should().Be(42);
    }

    [Fact]
    public void Missing_required_field_fails_parsing()
    {
        string json = Encoding.UTF8.GetString(EnvelopeSerializer.Serialize(SampleEnvelope()));
        int start = json.IndexOf("\"sequence\"", StringComparison.Ordinal);
        int end = json.IndexOf(',', start) + 1;
        string withoutSequence = json.Remove(start, end - start);

        bool parsed = EnvelopeSerializer.TryDeserialize(
            Encoding.UTF8.GetBytes(withoutSequence), out _, out string? error);

        parsed.Should().BeFalse();
        error.Should().Contain("sequence");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"half\":")]
    [InlineData("null")]
    public void Malformed_content_fails_parsing_without_throwing(string content)
    {
        bool parsed = EnvelopeSerializer.TryDeserialize(
            Encoding.UTF8.GetBytes(content), out _, out string? error);

        parsed.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }
}
