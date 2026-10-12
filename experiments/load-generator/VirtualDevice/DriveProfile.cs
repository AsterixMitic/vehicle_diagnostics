using System.Globalization;
using VehicleDiagnostics.Contracts;
using VehicleDiagnostics.Domain.SignalDecoding;

namespace VehicleDiagnostics.VirtualDevice;

/// <summary>
/// A drive recorded on a real car, as the Mode 01 data bytes its engine ECU sent: one row per
/// second, one column per PID. Written by <c>experiments/runner/drive_profile.py</c>; the
/// Arduino simulator replays the same bytes from flash.
/// </summary>
/// <remarks>
/// A row becomes exactly the frames the simulated ECU puts on the bus (ISO-TP single frame,
/// positive response 0x41, padded with 0x55 to eight bytes), which is what a real gateway
/// forwards in P0. Its P2 readings are those frames decoded by the backend's own decoder, so
/// P0 and P2 carry the same values by construction.
/// </remarks>
public sealed class DriveProfile
{
    private const uint ObdResponseId = 0x7E8;
    private const byte Padding = 0x55;

    private readonly ObdMode01Encoding[] _rows;

    private DriveProfile(string name, ObdMode01Encoding[] rows)
    {
        Name = name;
        _rows = rows;
    }

    public string Name { get; }

    public int Seconds => _rows.Length;

    /// <summary>Row <paramref name="index"/>, wrapping at the end of the drive.</summary>
    public ObdMode01Encoding Row(long index) => _rows[(int)(((index % _rows.Length) + _rows.Length) % _rows.Length)];

    public static DriveProfile Load(string path)
    {
        string[] lines = File.ReadAllLines(path);
        if (lines.Length < 2)
        {
            throw new InvalidDataException($"{path}: a profile needs a header and at least one row");
        }

        byte[] pids = [.. lines[0].Split(',').Skip(1).Select(column =>
            byte.Parse(column.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
        ObdMode01Encoding[] rows = [.. lines.Skip(1).Where(l => l.Length > 0).Select(line =>
        {
            string[] cells = line.Split(',');
            if (cells.Length != pids.Length + 1)
            {
                throw new InvalidDataException($"{path}: row '{line}' does not have {pids.Length} PIDs");
            }

            return Encode(pids, [.. cells.Skip(1).Select(Convert.FromHexString)]);
        })];
        return new DriveProfile(Path.GetFileNameWithoutExtension(path), rows);
    }

    /// <summary>The frames of one second, and the readings the backend decoder gets from them.</summary>
    public static ObdMode01Encoding Encode(IReadOnlyList<byte> pids, IReadOnlyList<byte[]> data)
    {
        var frames = new List<string>(pids.Count);
        var readings = new List<SignalReadingDto>(pids.Count);
        CanId responseId = CanId.Create(ObdResponseId).Value;
        for (int i = 0; i < pids.Count; i++)
        {
            byte[] bytes = new byte[8];
            Array.Fill(bytes, Padding);
            bytes[0] = (byte)(2 + data[i].Length);
            bytes[1] = 0x41;
            bytes[2] = pids[i];
            data[i].CopyTo(bytes, 3);
            frames.Add(Convert.ToHexString(bytes));

            CanFrame frame = CanFrame.Create(responseId, bytes).Value;
            if (ObdMode01Decoder.Decode(frame) is not FrameDecodeOutcome.Decoded decoded)
            {
                throw new InvalidDataException($"PID 0x{pids[i]:X2} with data {Convert.ToHexString(data[i])} does not decode");
            }

            readings.Add(new SignalReadingDto
            {
                Name = decoded.Signal.Name.Value,
                Value = decoded.Signal.Value,
                Unit = decoded.Signal.Unit,
            });
        }

        return new ObdMode01Encoding(frames, readings);
    }
}
