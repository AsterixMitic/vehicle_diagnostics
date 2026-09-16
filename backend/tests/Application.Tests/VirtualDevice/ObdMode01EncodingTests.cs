using FluentAssertions;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Domain.SignalDecoding;
using VehicleDiagnostics.VirtualDevice;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.VirtualDevice;

/// <summary>
/// The virtual device emits either raw frames (P0) or readings (P2) from one encoding; the
/// backend decoder must turn those frames into exactly those readings (design D7).
/// </summary>
public sealed class ObdMode01EncodingTests
{
    [Theory]
    [InlineData(1726.3, 60.4, 50.2)]
    [InlineData(800.0, 0.0, 20.0)]
    [InlineData(5999.9, 117.6, 89.7)]
    public void P0_frames_decode_to_the_P2_readings(double rpm, double speed, double coolant)
    {
        ObdMode01Encoding encoding = ObdMode01Encoding.From(rpm, speed, coolant);

        encoding.Frames.Should().HaveCount(encoding.Readings.Count);
        foreach ((string frameHex, SignalReadingDto reading) in encoding.Frames.Zip(encoding.Readings))
        {
            CanFrame frame = CanFrame.Create(CanId.Create(0x7E8).Value, Convert.FromHexString(frameHex)).Value;
            DecodedSignal decoded = ObdMode01Decoder.Decode(frame).Should().BeOfType<FrameDecodeOutcome.Decoded>().Subject.Signal;

            decoded.Name.Value.Should().Be(reading.Name);
            decoded.Unit.Should().Be(reading.Unit);
            decoded.Value.Should().Be(reading.Value, "P0 and P2 must agree exactly, not approximately");
        }
    }

    [Fact]
    public void Readings_are_quantized_by_the_wire_format()
    {
        ObdMode01Encoding encoding = ObdMode01Encoding.From(1726.3, 60.4, 50.2);

        encoding.Readings.Should().Contain(r => r.Name == "engine_rpm" && r.Value == 1726.25);
        encoding.Readings.Should().Contain(r => r.Name == "vehicle_speed" && r.Value == 60);
        encoding.Readings.Should().Contain(r => r.Name == "coolant_temp" && r.Value == 50);
    }
}
