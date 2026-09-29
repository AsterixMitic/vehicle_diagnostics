using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.SignalDecoding;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.SignalDecoding;
using VehicleDiagnostics.Domain.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.Telemetry;

public sealed class IngestSettlementTests
{
    private static readonly DateTimeOffset Sampled = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly ReceiptInfo Receipt = new(Sampled.AddMilliseconds(30), 287);

    private static RawTelemetry Input() => new(
        "esp32-01", "car-01", 42, "P2", Sampled,
        [new RawSignalReading("engine_rpm", 1726.25, "rpm"), new RawSignalReading("vehicle_speed", 60, "km_h")]);

    [Fact]
    public void Persisted_outcome_carries_persist_time_and_reading_count()
    {
        TelemetryReceived @event = TelemetryReceived.Create(
            DeviceId.Create("esp32-01").Value, VehicleId.Create("car-01").Value, 42, ProcessingMode.P2,
            [SignalReading.Create(SignalName.Create("engine_rpm").Value, 1726.25, "rpm", Sampled).Value],
            new FakeTimeProvider(Sampled.AddMilliseconds(31)));
        DateTimeOffset persistedAt = Sampled.AddMilliseconds(37);

        MessageSettlement settlement = IngestSettlement.From(Input(), new IngestOutcome.Accepted(@event, persistedAt), Receipt);

        settlement.Outcome.Should().Be(SettlementOutcome.Persisted);
        settlement.PersistedAt.Should().Be(persistedAt);
        settlement.ReadingCount.Should().Be(1);
        settlement.Receipt.Should().Be(Receipt);
        settlement.Should().BeEquivalentTo(new
        {
            DeviceId = "esp32-01",
            VehicleId = "car-01",
            Sequence = 42L,
            ProcessingMode = "P2",
            TimestampDevice = Sampled,
            RejectReason = (string?)null,
        });
    }

    [Fact]
    public void Duplicate_outcome_has_no_persist_time()
    {
        MessageSettlement settlement = IngestSettlement.From(Input(), new IngestOutcome.Duplicate(), Receipt);

        settlement.Outcome.Should().Be(SettlementOutcome.Duplicate);
        settlement.PersistedAt.Should().BeNull();
        settlement.ReadingCount.Should().Be(2);
    }

    [Fact]
    public void Rejected_outcome_carries_the_distinct_error_codes()
    {
        var rejected = new IngestOutcome.Rejected(
        [
            ValidationError.Required("deviceId"),
            ValidationError.Invalid("processingMode", "unknown"),
            ValidationError.Required("readings"),
        ]);

        MessageSettlement settlement = IngestSettlement.From(Input(), rejected, Receipt);

        settlement.Outcome.Should().Be(SettlementOutcome.Rejected);
        settlement.RejectReason.Should().Be("required+invalid");
        settlement.ReadingCount.Should().Be(0);
    }

    [Fact]
    public void Rejected_raw_envelope_is_settled_with_its_receipt()
    {
        var frames = new RawTelemetryFrames("esp32-01", "car-01", 5, "P0", Sampled, [], Receipt);

        MessageSettlement settlement = IngestSettlement.FromRejectedRaw(
            frames, new AcceptRawOutcome.Rejected([ValidationError.Required("frames")]));

        settlement.Outcome.Should().Be(SettlementOutcome.Rejected);
        settlement.RejectReason.Should().Be("required");
        settlement.Receipt.Should().Be(Receipt);
        settlement.ProcessingMode.Should().Be("P0");
    }

    [Fact]
    public void Decoding_that_published_readings_leaves_settlement_to_telemetry()
    {
        var input = new FramesToDecode("esp32-01", "car-01", 5, "P0", Sampled, [], Receipt);
        var processed = new DecodeOutcome.Processed(1, [new DecodedSignal(SignalName.Create("engine_rpm").Value, 1726, "rpm")],
            new Dictionary<string, int>(), Published: true);

        DecodeSettlement.From(input, processed).Should().BeNull();
    }

    [Fact]
    public void Decoding_that_yielded_nothing_is_settled_as_no_readings()
    {
        var input = new FramesToDecode("esp32-01", "car-01", 5, "P0", Sampled, [], Receipt);
        var empty = new DecodeOutcome.Processed(2, [], new Dictionary<string, int> { ["not_obd_response"] = 2 }, Published: false);

        MessageSettlement? settlement = DecodeSettlement.From(input, empty);

        settlement.Should().NotBeNull();
        settlement!.Outcome.Should().Be(SettlementOutcome.Rejected);
        settlement.RejectReason.Should().Be(DecodeSettlement.NoReadings);
        settlement.Receipt.Should().Be(Receipt);
    }

    [Fact]
    public void Decoding_rejected_for_identity_is_settled_with_its_codes()
    {
        var input = new FramesToDecode("", "car-01", 5, "P0", Sampled, [], Receipt);

        MessageSettlement? settlement = DecodeSettlement.From(
            input, new DecodeOutcome.Rejected([ValidationError.Required("deviceId")]));

        settlement!.RejectReason.Should().Be("required");
    }

    [Theory]
    [InlineData(new string[0], "unspecified")]
    [InlineData(new[] { "invalid", "invalid" }, "invalid")]
    [InlineData(new[] { "required", "", "out_of_range" }, "required+out_of_range")]
    public void Reasons_are_distinct_and_joined(string[] codes, string expected)
    {
        SettlementReasons.Join(codes).Should().Be(expected);
    }

    [Fact]
    public void Overlong_reason_is_truncated_to_what_the_store_keeps()
    {
        string reason = SettlementReasons.Join(Enumerable.Range(0, 20).Select(i => $"code_{i}"));

        reason.Length.Should().Be(SettlementReasons.MaxLength);
    }
}
