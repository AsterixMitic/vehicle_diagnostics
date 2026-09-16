using FluentAssertions;
using VehicleDiagnostics.Domain.SignalDecoding;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.SignalDecoding;

public sealed class CanFrameTests
{
    [Theory]
    [InlineData(0x000u, false, true)]
    [InlineData(0x7FFu, false, true)]
    [InlineData(0x800u, false, false)]
    [InlineData(0x1FFFFFFFu, true, true)]
    [InlineData(0x20000000u, true, false)]
    public void CanId_enforces_the_identifier_range(uint value, bool extended, bool valid)
    {
        CanId.Create(value, extended).IsSuccess.Should().Be(valid);
    }

    [Theory]
    [InlineData(0x7E8u, false, true)]
    [InlineData(0x7EFu, false, true)]
    [InlineData(0x7E0u, false, false)]
    [InlineData(0x7DFu, false, false)]
    [InlineData(0x18DAF110u, true, true)]
    [InlineData(0x18DA10F1u, true, false)]
    public void CanId_recognizes_obd_response_identifiers(uint value, bool extended, bool expected)
    {
        CanId.Create(value, extended).Value.IsObdResponse.Should().Be(expected);
    }

    [Fact]
    public void CanFrame_accepts_up_to_eight_bytes_and_rejects_more()
    {
        CanId id = CanId.Create(0x7E8).Value;

        CanFrame.Create(id, new byte[8]).IsSuccess.Should().BeTrue();
        CanFrame.Create(id, []).IsSuccess.Should().BeTrue();
        CanFrame.Create(id, new byte[9]).IsFailure.Should().BeTrue();
    }

    [Theory]
    [InlineData(new byte[] { 0x03, 0x41, 0x0C, 0x1A, 0xF8 }, IsoTpFrameKind.SingleFrame, 3)]
    [InlineData(new byte[] { 0x10, 0x14, 0x49, 0x02, 0x01, 0x57, 0x56, 0x57 }, IsoTpFrameKind.FirstFrame, 0)]
    [InlineData(new byte[] { 0x21, 0x5A, 0x5A, 0x5A }, IsoTpFrameKind.ConsecutiveFrame, 0)]
    [InlineData(new byte[] { 0x30, 0x00, 0x00 }, IsoTpFrameKind.FlowControl, 0)]
    [InlineData(new byte[] { 0x00 }, IsoTpFrameKind.Invalid, 0)]
    [InlineData(new byte[] { 0x05, 0x41, 0x0C }, IsoTpFrameKind.Invalid, 0)]
    [InlineData(new byte[] { }, IsoTpFrameKind.Invalid, 0)]
    public void IsoTp_classifies_frames_by_pci(byte[] data, IsoTpFrameKind expected, int expectedLength)
    {
        CanFrame frame = CanFrame.Create(CanId.Create(0x7E8).Value, data).Value;

        IsoTp.Classify(frame, out int length).Should().Be(expected);
        length.Should().Be(expectedLength);
    }
}
