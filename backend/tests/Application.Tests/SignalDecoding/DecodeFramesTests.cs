using FluentAssertions;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.SignalDecoding;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.SignalDecoding;

public sealed class DecodeFramesTests
{
    private readonly FakePublisher _publisher = new();
    private readonly DecodeFrames _useCase;

    public DecodeFramesTests()
    {
        _useCase = new DecodeFrames(_publisher);
    }

    private static RawFrameData Frame(uint id, params byte[] data) => new(id, false, data);

    private static FramesToDecode Input(params RawFrameData[] frames) => new(
        "esp32-01", "car-01", 5, "P0", new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero), frames);

    [Fact]
    public async Task Receipt_of_the_original_envelope_travels_with_the_batch()
    {
        var receipt = new ReceiptInfo(DateTimeOffset.UnixEpoch.AddMilliseconds(9), 290);

        await _useCase.DecodeAsync(
            Input(Frame(0x7E8, 0x04, 0x41, 0x0C, 0x1A, 0xF8)) with { Receipt = receipt }, CancellationToken.None);

        _publisher.Published.Should().ContainSingle().Which.Receipt.Should().Be(receipt);
    }

    [Fact]
    public async Task Mixed_envelope_decodes_the_decodable_frames_and_counts_the_rest()
    {
        FramesToDecode input = Input(
            Frame(0x7E8, 0x04, 0x41, 0x0C, 0x1A, 0xF8),      // engine_rpm 1726
            Frame(0x7E8, 0x03, 0x7F, 0x01, 0x12),            // negative response
            Frame(0x123, 0x01, 0x02, 0x03),                  // unrelated identifier
            Frame(0x7E8, 0x03, 0x41, 0x05, 0x5A));           // coolant_temp 50

        DecodeOutcome outcome = await _useCase.DecodeAsync(input, CancellationToken.None);

        var processed = outcome.Should().BeOfType<DecodeOutcome.Processed>().Subject;
        processed.FramesReceived.Should().Be(4);
        processed.Published.Should().BeTrue();
        processed.Signals.Select(s => (s.Name.Value, s.Value)).Should().BeEquivalentTo(
            [("engine_rpm", 1726.0), ("coolant_temp", 50.0)]);
        processed.IgnoredByReason.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["negative_response"] = 1,
            ["not_obd_response"] = 1,
        });

        DecodedSignalsBatch batch = _publisher.Published.Should().ContainSingle().Subject;
        batch.DeviceId.Value.Should().Be("esp32-01");
        batch.Sequence.Should().Be(5);
        batch.ProcessingMode.Should().Be(VehicleDiagnostics.Domain.SharedKernel.ProcessingMode.P0);
        batch.Signals.Should().HaveCount(2);
    }

    [Fact]
    public async Task All_ignored_envelope_publishes_nothing()
    {
        FramesToDecode input = Input(
            Frame(0x7DF, 0x02, 0x01, 0x0C),
            Frame(0x7E8, 0x10, 0x14, 0x49, 0x02, 0x01, 0x57, 0x56, 0x57));

        DecodeOutcome outcome = await _useCase.DecodeAsync(input, CancellationToken.None);

        var processed = outcome.Should().BeOfType<DecodeOutcome.Processed>().Subject;
        processed.Published.Should().BeFalse();
        processed.Signals.Should().BeEmpty();
        processed.IgnoredByReason.Values.Sum().Should().Be(2);
        _publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Unconstructible_frame_is_counted_and_does_not_stop_the_rest()
    {
        FramesToDecode input = Input(
            new RawFrameData(0x800, false, new byte[] { 0x04, 0x41, 0x0C, 0x1A, 0xF8 }),   // 11-bit range exceeded
            Frame(0x7E8, 0x03, 0x41, 0x0D, 0x3C));

        var processed = (DecodeOutcome.Processed)await _useCase.DecodeAsync(input, CancellationToken.None);

        processed.IgnoredByReason.Should().ContainKey(DecodeTags.InvalidFrame).WhoseValue.Should().Be(1);
        processed.Signals.Should().ContainSingle(s => s.Name.Value == "vehicle_speed");
    }

    [Theory]
    [InlineData("", "car-01", "P0")]
    [InlineData("esp32-01", "veh/1", "P0")]
    [InlineData("esp32-01", "car-01", "P7")]
    public async Task Invalid_identity_is_rejected_without_publishing(string device, string vehicle, string mode)
    {
        FramesToDecode input = Input(Frame(0x7E8, 0x04, 0x41, 0x0C, 0x1A, 0xF8)) with
        {
            DeviceId = device,
            VehicleId = vehicle,
            ProcessingMode = mode,
        };

        DecodeOutcome outcome = await _useCase.DecodeAsync(input, CancellationToken.None);

        outcome.Should().BeOfType<DecodeOutcome.Rejected>().Which.Errors.Should().NotBeEmpty();
        _publisher.Published.Should().BeEmpty();
    }

    private sealed class FakePublisher : IDecodedSignalsPublisher
    {
        public List<DecodedSignalsBatch> Published { get; } = [];

        public Task PublishAsync(DecodedSignalsBatch batch, CancellationToken cancellationToken)
        {
            Published.Add(batch);
            return Task.CompletedTask;
        }
    }
}
