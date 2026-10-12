using FluentAssertions;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Domain.SignalDecoding;
using VehicleDiagnostics.VirtualDevice;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.VirtualDevice;

/// <summary>
/// The virtual device replays the recorded Mitsubishi ASX drive from the same profile the
/// Arduino simulator holds in flash; P0 frames and P2 readings must agree exactly.
/// </summary>
public sealed class DriveProfileTests
{
    private static readonly Lazy<DriveProfile> Asx = new(() => DriveProfile.Load(ProfilePath("asx-2012.csv")));

    [Fact]
    public void The_ASX_profile_holds_the_continuous_part_of_the_drive()
    {
        Asx.Value.Seconds.Should().Be(753);
        Asx.Value.Row(0).Readings.Select(r => r.Name).Should().Equal(
            "engine_load", "coolant_temp", "intake_pressure", "engine_rpm", "vehicle_speed",
            "intake_temp", "maf_rate", "throttle_position", "barometric_pressure", "module_voltage");
    }

    [Fact]
    public void A_row_carries_the_recorded_values()
    {
        // experiments/results/20261007-vehicle-drive/readings.csv, second 1
        IReadOnlyList<SignalReadingDto> readings = Asx.Value.Row(1).Readings;

        readings.Should().Contain(r => r.Name == "engine_rpm" && r.Value == 648.5);
        readings.Should().Contain(r => r.Name == "maf_rate" && r.Value == 7.94);
        readings.Should().Contain(r => r.Name == "coolant_temp" && r.Value == 78);
    }

    [Fact]
    public void Frames_are_what_the_simulated_ECU_puts_on_the_bus()
    {
        Asx.Value.Row(0).Frames.Should().AllSatisfy(f => f.Should().HaveLength(16).And.EndWith("55"));
        Asx.Value.Row(0).Frames.Should().Contain("04410C0A29555555", "engine speed 650.25 rpm, padded with 0x55");
    }

    [Fact]
    public void The_replay_wraps_at_the_end_of_the_drive()
    {
        Asx.Value.Row(753).Should().BeSameAs(Asx.Value.Row(0));
        Asx.Value.Row(753 + 1).Should().BeSameAs(Asx.Value.Row(1));
    }

    [Fact]
    public void Every_replayed_row_decodes_in_P0_to_its_P2_readings()
    {
        for (int second = 0; second < Asx.Value.Seconds; second++)
        {
            ObdMode01Encoding row = Asx.Value.Row(second);
            foreach ((string frameHex, SignalReadingDto reading) in row.Frames.Zip(row.Readings))
            {
                CanFrame frame = CanFrame.Create(CanId.Create(0x7E8).Value, Convert.FromHexString(frameHex)).Value;
                DecodedSignal decoded = ObdMode01Decoder.Decode(frame).Should().BeOfType<FrameDecodeOutcome.Decoded>().Subject.Signal;

                decoded.Name.Value.Should().Be(reading.Name);
                decoded.Value.Should().Be(reading.Value, "P0 and P2 must agree exactly at second {0}", second);
            }
        }
    }

    private static string ProfilePath(string name)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "experiments", "profiles", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"experiments/profiles/{name} not found above {AppContext.BaseDirectory}");
    }
}
