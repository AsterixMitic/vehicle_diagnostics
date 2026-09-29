using FluentAssertions;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.Telemetry;

public sealed class AcceptRawTelemetryTests
{
    private readonly FakePublisher _publisher = new();
    private readonly AcceptRawTelemetry _useCase;

    public AcceptRawTelemetryTests()
    {
        _useCase = new AcceptRawTelemetry(_publisher);
    }

    private static RawTelemetryFrames Valid() => new(
        "esp32-01", "car-01", 9, "P0", new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.Zero),
        [new RawFrameData(0x7E8, false, new byte[] { 0x04, 0x41, 0x0C, 0x1A, 0xF8 })]);

    [Fact]
    public async Task Valid_raw_telemetry_is_forwarded_with_typed_identity()
    {
        AcceptRawOutcome outcome = await _useCase.AcceptAsync(Valid(), CancellationToken.None);

        RawFramesAccepted message = outcome.Should().BeOfType<AcceptRawOutcome.Accepted>().Subject.Message;
        message.DeviceId.Value.Should().Be("esp32-01");
        message.ProcessingMode.Should().Be(ProcessingMode.P0);
        message.Sequence.Should().Be(9);
        _publisher.Published.Should().ContainSingle().Which.Should().BeSameAs(message);
    }

    [Fact]
    public async Task Receipt_of_the_original_envelope_is_forwarded()
    {
        var receipt = new ReceiptInfo(new DateTimeOffset(2026, 9, 16, 10, 0, 0, 25, TimeSpan.Zero), 318);

        AcceptRawOutcome outcome = await _useCase.AcceptAsync(Valid() with { Receipt = receipt }, CancellationToken.None);

        outcome.Should().BeOfType<AcceptRawOutcome.Accepted>().Which.Message.Receipt.Should().Be(receipt);
    }

    [Theory]
    [InlineData("", "car-01", "P0", 1)]
    [InlineData("esp32-01", "bad id", "P0", 1)]
    [InlineData("esp32-01", "car-01", "P9", 1)]
    [InlineData("esp32-01", "car-01", "P0", 0)]
    public async Task Invalid_input_is_rejected_and_nothing_published(string device, string vehicle, string mode, int frameCount)
    {
        RawTelemetryFrames input = Valid() with
        {
            DeviceId = device,
            VehicleId = vehicle,
            ProcessingMode = mode,
            Frames = frameCount == 0 ? [] : Valid().Frames,
        };

        AcceptRawOutcome outcome = await _useCase.AcceptAsync(input, CancellationToken.None);

        outcome.Should().BeOfType<AcceptRawOutcome.Rejected>();
        _publisher.Published.Should().BeEmpty();
    }

    private sealed class FakePublisher : IRawFramesPublisher
    {
        public List<RawFramesAccepted> Published { get; } = [];

        public Task PublishAsync(RawFramesAccepted accepted, CancellationToken cancellationToken)
        {
            Published.Add(accepted);
            return Task.CompletedTask;
        }
    }
}
