using FluentAssertions;
using VehicleDiagnostics.Contracts;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.Contracts;

public sealed class EnvelopeValidatorTests
{
    private static EnvelopeValidationResult Validate(
        MessageEnvelope<SignalReadingsPayload> envelope,
        string? topicVehicleId = null,
        string? topicDeviceId = null) =>
        EnvelopeValidator.Validate(
            envelope, topicVehicleId ?? envelope.VehicleId, topicDeviceId ?? envelope.DeviceId);

    [Fact]
    public void Complete_envelope_is_accepted()
    {
        EnvelopeValidationResult result = Validate(EnvelopeSerializationTests.SampleEnvelope());

        result.IsValid.Should().BeTrue();
        result.TraceContextIsValid.Should().BeTrue();
    }

    public static TheoryData<MessageEnvelope<SignalReadingsPayload>, string> RejectionCases() => new()
    {
        {
            EnvelopeSerializationTests.SampleEnvelope(schemaVersion: 99),
            EnvelopeError.UnsupportedSchemaVersion
        },
        {
            EnvelopeSerializationTests.SampleEnvelope(deviceId: ""),
            EnvelopeError.MissingField
        },
        {
            EnvelopeSerializationTests.SampleEnvelope(vehicleId: "  "),
            EnvelopeError.MissingField
        },
        {
            EnvelopeSerializationTests.SampleEnvelope(sequence: -1),
            EnvelopeError.InvalidField
        },
        {
            EnvelopeSerializationTests.SampleEnvelope(processingMode: "P9"),
            EnvelopeError.InvalidField
        },
        {
            EnvelopeSerializationTests.SampleEnvelope() with
            {
                Payload = new SignalReadingsPayload { Readings = [] },
            },
            EnvelopeError.EmptyPayload
        },
        {
            EnvelopeSerializationTests.SampleEnvelope() with { TimestampDevice = default },
            EnvelopeError.MissingField
        },
    };

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public void Invalid_envelope_is_rejected_with_the_expected_code(
        MessageEnvelope<SignalReadingsPayload> envelope, string expectedCode)
    {
        EnvelopeValidationResult result = Validate(envelope);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == expectedCode);
    }

    [Fact]
    public void Topic_and_envelope_identity_must_agree()
    {
        MessageEnvelope<SignalReadingsPayload> envelope = EnvelopeSerializationTests.SampleEnvelope();

        EnvelopeValidationResult deviceMismatch = Validate(envelope, topicDeviceId: "other-device");
        EnvelopeValidationResult vehicleMismatch = Validate(envelope, topicVehicleId: "other-car");

        deviceMismatch.Errors.Should().ContainSingle(e => e.Code == EnvelopeError.IdentityMismatch);
        vehicleMismatch.Errors.Should().ContainSingle(e => e.Code == EnvelopeError.IdentityMismatch);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("00-00000000000000000000000000000000-b7ad6b7169203331-01")]
    [InlineData("00-0af7651916cd43dd8448eb211c80319c-0000000000000000-01")]
    [InlineData("ff-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01")]
    [InlineData("00-0AF7651916CD43DD8448EB211C80319C-B7AD6B7169203331-01")]
    [InlineData("")]
    public void Malformed_trace_context_does_not_reject_the_message(string traceId)
    {
        EnvelopeValidationResult result = Validate(
            EnvelopeSerializationTests.SampleEnvelope(traceId: traceId));

        result.IsValid.Should().BeTrue("a lost trace is a counted continuity loss, not a rejection");
        result.TraceContextIsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("veh/car-01/dev/esp32-01/tel/signals", true, "car-01", "esp32-01")]
    [InlineData("veh/car-01/dev/esp32-01/tel/raw", false, null, null)]
    [InlineData("veh//dev/esp32-01/tel/signals", false, null, null)]
    [InlineData("something/else", false, null, null)]
    [InlineData("", false, null, null)]
    public void Signals_topic_parsing(string topic, bool expected, string? vehicle, string? device)
    {
        bool parsed = TelemetryTopics.TryParseSignals(topic, out string? vehicleId, out string? deviceId);

        parsed.Should().Be(expected);
        vehicleId.Should().Be(vehicle);
        deviceId.Should().Be(device);
    }

    [Fact]
    public void Topic_format_and_filter_agree()
    {
        string topic = TelemetryTopics.Signals("car-01", "esp32-01");

        topic.Should().Be("veh/car-01/dev/esp32-01/tel/signals");
        TelemetryTopics.TryParseSignals(topic, out _, out _).Should().BeTrue();
    }
}
