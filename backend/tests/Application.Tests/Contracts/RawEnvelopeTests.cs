using System.Text;
using System.Text.Json;
using FluentAssertions;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Contracts.Internal;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.Contracts;

public sealed class RawEnvelopeTests
{
    internal static MessageEnvelope<RawFramesPayload> SampleRawEnvelope(params CanFrameDto[] frames) => new()
    {
        SchemaVersion = EnvelopeSerializer.CurrentSchemaVersion,
        DeviceId = "esp32-01",
        VehicleId = "car-01",
        Sequence = 7,
        TimestampDevice = new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero),
        TraceId = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
        ProcessingMode = "P0",
        Payload = new RawFramesPayload
        {
            Frames = frames.Length > 0
                ? frames
                : [new CanFrameDto { CanId = 0x7E8, Data = "04410C1AF8" }],
        },
    };

    [Fact]
    public void Raw_envelope_round_trips()
    {
        MessageEnvelope<RawFramesPayload> original = SampleRawEnvelope(
            new CanFrameDto { CanId = 0x7E8, Data = "04410C1AF8" },
            new CanFrameDto { CanId = 0x18DAF110, IsExtended = true, Data = "03410D3C" });

        byte[] bytes = EnvelopeSerializer.Serialize(original);
        EnvelopeSerializer.TryDeserializeRaw(bytes, out MessageEnvelope<RawFramesPayload>? restored, out string? error)
            .Should().BeTrue(error);

        restored.Should().BeEquivalentTo(original);
        Encoding.UTF8.GetString(bytes).Should().ContainAll("\"frames\"", "\"canId\"", "\"isExtended\"", "\"data\"");
    }

    [Fact]
    public void Valid_raw_envelope_is_accepted()
    {
        EnvelopeValidator.Validate(SampleRawEnvelope(), "car-01", "esp32-01").IsValid.Should().BeTrue();
    }

    public static TheoryData<CanFrameDto, string> InvalidFrames() => new()
    {
        { new CanFrameDto { CanId = 0x800, Data = "0341" }, EnvelopeError.InvalidField },
        { new CanFrameDto { CanId = 0x20000000, IsExtended = true, Data = "0341" }, EnvelopeError.InvalidField },
        { new CanFrameDto { CanId = 0x7E8, Data = "034" }, EnvelopeError.InvalidField },
        { new CanFrameDto { CanId = 0x7E8, Data = "0341ZZ" }, EnvelopeError.InvalidField },
        { new CanFrameDto { CanId = 0x7E8, Data = "000000000000000000" }, EnvelopeError.InvalidField },
    };

    [Theory]
    [MemberData(nameof(InvalidFrames))]
    public void Invalid_frames_are_rejected(CanFrameDto frame, string expectedCode)
    {
        EnvelopeValidationResult result = EnvelopeValidator.Validate(SampleRawEnvelope(frame), "car-01", "esp32-01");

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Code == expectedCode);
    }

    [Fact]
    public void Empty_frame_list_is_rejected()
    {
        MessageEnvelope<RawFramesPayload> envelope = SampleRawEnvelope() with
        {
            Payload = new RawFramesPayload { Frames = [] },
        };

        EnvelopeValidator.Validate(envelope, "car-01", "esp32-01").Errors
            .Should().ContainSingle(e => e.Code == EnvelopeError.EmptyPayload);
    }

    [Fact]
    public void Identity_mismatch_applies_to_raw_envelopes_too()
    {
        EnvelopeValidator.Validate(SampleRawEnvelope(), "car-01", "other").Errors
            .Should().ContainSingle(e => e.Code == EnvelopeError.IdentityMismatch);
    }

    [Theory]
    [InlineData("veh/car-01/dev/esp32-01/tel/raw", true, TelemetrySubtype.Raw)]
    [InlineData("veh/car-01/dev/esp32-01/tel/signals", true, TelemetrySubtype.Signals)]
    [InlineData("veh/car-01/dev/esp32-01/tel/pdu", false, default(TelemetrySubtype))]
    [InlineData("veh/car-01/dev/esp32-01/evt/dtc", false, default(TelemetrySubtype))]
    public void Telemetry_topic_parsing_reports_the_subtype(string topic, bool expected, TelemetrySubtype subtype)
    {
        bool parsed = TelemetryTopics.TryParseTelemetry(topic, out string? vehicleId, out string? deviceId, out TelemetrySubtype actual);

        parsed.Should().Be(expected);
        if (expected)
        {
            vehicleId.Should().Be("car-01");
            deviceId.Should().Be("esp32-01");
            actual.Should().Be(subtype);
        }
    }

    [Fact]
    public void Raw_topic_format_and_filter_agree()
    {
        string topic = TelemetryTopics.Raw("car-01", "esp32-01");

        topic.Should().Be("veh/car-01/dev/esp32-01/tel/raw");
        TelemetryTopics.TryParseSignals(topic, out _, out _).Should().BeFalse("raw is not signals");
    }

    [Fact]
    public void Internal_messages_round_trip_with_plain_json()
    {
        var received = new RawFramesReceived
        {
            DeviceId = "esp32-01",
            VehicleId = "car-01",
            Sequence = 7,
            TimestampDevice = DateTimeOffset.UnixEpoch,
            ProcessingMode = "P0",
            Frames = [new CanFrameDto { CanId = 0x7E8, Data = "04410C1AF8" }],
        };
        var decoded = new SignalsDecoded
        {
            DeviceId = "esp32-01",
            VehicleId = "car-01",
            Sequence = 7,
            TimestampDevice = DateTimeOffset.UnixEpoch,
            ProcessingMode = "P0",
            Readings = [new SignalReadingDto { Name = "engine_rpm", Value = 1726, Unit = "rpm" }],
        };

        JsonSerializer.Deserialize<RawFramesReceived>(JsonSerializer.Serialize(received)).Should().BeEquivalentTo(received);
        JsonSerializer.Deserialize<SignalsDecoded>(JsonSerializer.Serialize(decoded)).Should().BeEquivalentTo(decoded);
    }

    [Fact]
    public void Version_2_receipt_fields_round_trip()
    {
        var received = new RawFramesReceived
        {
            DeviceId = "esp32-01",
            VehicleId = "car-01",
            Sequence = 7,
            TimestampDevice = DateTimeOffset.UnixEpoch,
            ProcessingMode = "P0",
            Frames = [new CanFrameDto { CanId = 0x7E8, Data = "04410C1AF8" }],
            ReceivedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(12),
            PayloadBytes = 312,
        };

        RawFramesReceived? back = JsonSerializer.Deserialize<RawFramesReceived>(JsonSerializer.Serialize(received));

        back!.ReceivedAt.Should().Be(received.ReceivedAt);
        back.PayloadBytes.Should().Be(312);
        RawFramesReceived.Version.Should().Be(2);
        SignalsDecoded.Version.Should().Be(2);
    }

    [Fact]
    public void Version_1_messages_without_receipt_fields_still_deserialize()
    {
        const string v1 = """
            {"DeviceId":"esp32-01","VehicleId":"car-01","Sequence":7,"TimestampDevice":"1970-01-01T00:00:00+00:00",
             "ProcessingMode":"P0","Readings":[{"Name":"engine_rpm","Value":1726,"Unit":"rpm"}]}
            """;

        SignalsDecoded? decoded = JsonSerializer.Deserialize<SignalsDecoded>(v1);

        decoded!.Readings.Should().ContainSingle();
        decoded.ReceivedAt.Should().BeNull();
        decoded.PayloadBytes.Should().BeNull();
    }

    [Fact]
    public void Settlement_round_trips_with_optional_identity()
    {
        var persisted = new TelemetryMessageSettled
        {
            SettlementId = Guid.NewGuid(),
            Outcome = SettlementOutcomes.Persisted,
            DeviceId = "esp32-01",
            VehicleId = "car-01",
            Sequence = 7,
            ProcessingMode = "P2",
            TimestampDevice = DateTimeOffset.UnixEpoch,
            ReceivedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(15),
            PersistedAt = DateTimeOffset.UnixEpoch.AddMilliseconds(21),
            PayloadBytes = 280,
            ReadingCount = 3,
        };
        var anonymous = new TelemetryMessageSettled
        {
            SettlementId = Guid.NewGuid(),
            Outcome = SettlementOutcomes.Rejected,
            RejectReason = "unknown_topic",
            ReceivedAt = DateTimeOffset.UnixEpoch,
            PayloadBytes = 40,
            ReadingCount = 0,
        };

        JsonSerializer.Deserialize<TelemetryMessageSettled>(JsonSerializer.Serialize(persisted)).Should().BeEquivalentTo(persisted);
        JsonSerializer.Deserialize<TelemetryMessageSettled>(JsonSerializer.Serialize(anonymous)).Should().BeEquivalentTo(anonymous);
    }
}
