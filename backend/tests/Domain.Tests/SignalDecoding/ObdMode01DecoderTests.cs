using FluentAssertions;
using VehicleDiagnostics.Domain.SignalDecoding;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.SignalDecoding;

public sealed class ObdMode01DecoderTests
{
    private static CanFrame Frame(uint id, params byte[] data) =>
        CanFrame.Create(CanId.Create(id).Value, data).Value;

    private static CanFrame Response(params byte[] payload)
    {
        byte[] data = new byte[8];
        data[0] = (byte)payload.Length;
        payload.CopyTo(data, 1);
        return Frame(0x7E8, data);
    }

    // Hand-computed from SAE J1979: A = first data byte after the PID, B = second.
    [Theory]
    [InlineData(0x04, new byte[] { 0x80 }, "engine_load", 50.196078431372548, "percent")]
    [InlineData(0x05, new byte[] { 0x5A }, "coolant_temp", 50.0, "degC")]
    [InlineData(0x0B, new byte[] { 0x65 }, "intake_pressure", 101.0, "kPa")]
    [InlineData(0x0C, new byte[] { 0x1A, 0xF8 }, "engine_rpm", 1726.0, "rpm")]
    [InlineData(0x0D, new byte[] { 0x3C }, "vehicle_speed", 60.0, "km_h")]
    [InlineData(0x0F, new byte[] { 0x3E }, "intake_temp", 22.0, "degC")]
    [InlineData(0x10, new byte[] { 0x03, 0xE8 }, "maf_rate", 10.0, "g_s")]
    [InlineData(0x11, new byte[] { 0xFF }, "throttle_position", 100.0, "percent")]
    [InlineData(0x2F, new byte[] { 0x00 }, "fuel_level", 0.0, "percent")]
    [InlineData(0x33, new byte[] { 0x63 }, "barometric_pressure", 99.0, "kPa")]
    [InlineData(0x42, new byte[] { 0x36, 0xB0 }, "module_voltage", 14.0, "V")]
    [InlineData(0x46, new byte[] { 0x28 }, "ambient_temp", 0.0, "degC")]
    [InlineData(0x5C, new byte[] { 0x8C }, "oil_temp", 100.0, "degC")]
    [InlineData(0x5E, new byte[] { 0x00, 0xC8 }, "fuel_rate", 10.0, "L_h")]
    public void Decodes_each_supported_pid_per_sae_j1979(
        byte pid, byte[] bytes, string name, double value, string unit)
    {
        byte[] payload = [0x41, pid, .. bytes];

        FrameDecodeOutcome outcome = ObdMode01Decoder.Decode(Response(payload));

        DecodedSignal signal = outcome.Should().BeOfType<FrameDecodeOutcome.Decoded>().Subject.Signal;
        signal.Name.Value.Should().Be(name);
        signal.Value.Should().BeApproximately(value, 1e-9);
        signal.Unit.Should().Be(unit);
    }

    [Fact]
    public void Spec_example_engine_speed_frame()
    {
        // PCI 0x04: service byte + PID + two data bytes. A PCI of 0x03 here would be a short payload.
        FrameDecodeOutcome outcome = ObdMode01Decoder.Decode(Frame(0x7E8, 0x04, 0x41, 0x0C, 0x1A, 0xF8));

        outcome.Should().BeOfType<FrameDecodeOutcome.Decoded>()
            .Which.Signal.Should().BeEquivalentTo(new { Value = 1726.0, Unit = "rpm" });
    }

    [Fact]
    public void Padding_bytes_beyond_the_pci_length_are_ignored()
    {
        FrameDecodeOutcome outcome = ObdMode01Decoder.Decode(
            Frame(0x7E8, 0x03, 0x41, 0x05, 0x5A, 0xAA, 0xAA, 0xAA, 0xAA));

        outcome.Should().BeOfType<FrameDecodeOutcome.Decoded>().Which.Signal.Value.Should().Be(50.0);
    }

    [Fact]
    public void Extended_addressing_response_is_decoded()
    {
        CanFrame frame = CanFrame.Create(CanId.Create(0x18DAF110, isExtended: true).Value,
            [0x03, 0x41, 0x0D, 0x3C, 0, 0, 0, 0]).Value;

        ObdMode01Decoder.Decode(frame).Should().BeOfType<FrameDecodeOutcome.Decoded>();
    }

    public static TheoryData<CanFrame, FrameIgnoreReason> IgnoredFrames() => new()
    {
        { Frame(0x7DF, 0x02, 0x01, 0x0C), FrameIgnoreReason.NotObdResponse },
        { Frame(0x123, 0x03, 0x41, 0x0C, 0x1A, 0xF8), FrameIgnoreReason.NotObdResponse },
        { Frame(0x7E8, 0x10, 0x14, 0x49, 0x02, 0x01, 0x57, 0x56, 0x57), FrameIgnoreReason.MultiFrameUnsupported },
        { Frame(0x7E8, 0x21, 0x5A, 0x5A), FrameIgnoreReason.MultiFrameUnsupported },
        { Frame(0x7E8, 0x30, 0x00, 0x00), FrameIgnoreReason.MultiFrameUnsupported },
        { Frame(0x7E8), FrameIgnoreReason.NotSingleFrame },
        { Frame(0x7E8, 0x00), FrameIgnoreReason.NotSingleFrame },
        { Frame(0x7E8, 0x03, 0x7F, 0x01, 0x12), FrameIgnoreReason.NegativeResponse },
        { Frame(0x7E8, 0x03, 0x49, 0x02, 0x01), FrameIgnoreReason.UnsupportedService },
        { Frame(0x7E8, 0x01, 0x41), FrameIgnoreReason.ShortPayload },
        { Frame(0x7E8, 0x03, 0x41, 0xFE, 0x00), FrameIgnoreReason.UnsupportedPid },
        { Frame(0x7E8, 0x03, 0x41, 0x0C, 0x1A), FrameIgnoreReason.ShortPayload },
    };

    [Theory]
    [MemberData(nameof(IgnoredFrames))]
    public void Undecodable_frames_are_ignored_with_a_reason(CanFrame frame, FrameIgnoreReason expected)
    {
        ObdMode01Decoder.Decode(frame).Should().BeOfType<FrameDecodeOutcome.Ignored>()
            .Which.Reason.Should().Be(expected);
    }

    [Fact]
    public void Every_ignore_reason_has_a_stable_tag()
    {
        foreach (FrameIgnoreReason reason in Enum.GetValues<FrameIgnoreReason>())
        {
            reason.ToTag().Should().NotBe("unknown").And.MatchRegex("^[a-z_]+$");
        }
    }

    [Fact]
    public void Supported_set_covers_the_spec_minimum()
    {
        byte[] required = [0x04, 0x05, 0x0C, 0x0D, 0x0F, 0x10, 0x11, 0x2F, 0x42, 0x46];

        required.Should().OnlyContain(pid => ObdMode01Decoder.Supports(new Pid(pid)));
    }
}
