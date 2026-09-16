using System.Globalization;
using System.Text.RegularExpressions;
using VehicleDiagnostics.Domain.SignalDecoding;

namespace VehicleDiagnostics.Domain.Tests.SignalDecoding;

/// <summary>
/// Parser for the <c>candump -l</c> line format, <c>(seconds.micros) interface ID#HEX</c>, the
/// de-facto capture format `can-utils` and USB-CAN adapters produce. A three-digit identifier
/// is standard (11-bit); an eight-digit one is extended (29-bit).
/// </summary>
internal static partial class CandumpLog
{
    public sealed record Entry(int Line, double Timestamp, string Interface, CanFrame Frame);

    [GeneratedRegex(@"^\((?<ts>\d+\.\d+)\)\s+(?<if>\S+)\s+(?<id>[0-9A-Fa-f]{3}|[0-9A-Fa-f]{8})#(?<data>[0-9A-Fa-f]*)\s*$")]
    private static partial Regex LineRegex();

    public static IReadOnlyList<Entry> Parse(IEnumerable<string> lines)
    {
        List<Entry> entries = [];
        int lineNumber = 0;
        foreach (string line in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            Match match = LineRegex().Match(line);
            if (!match.Success)
            {
                throw new FormatException($"Line {lineNumber} is not in candump format: '{line}'");
            }

            string idText = match.Groups["id"].Value;
            bool extended = idText.Length == 8;
            CanId id = CanId.Create(uint.Parse(idText, NumberStyles.HexNumber, CultureInfo.InvariantCulture), extended).Value;
            CanFrame frame = CanFrame.Create(id, Convert.FromHexString(match.Groups["data"].Value)).Value;
            entries.Add(new Entry(
                lineNumber,
                double.Parse(match.Groups["ts"].Value, CultureInfo.InvariantCulture),
                match.Groups["if"].Value,
                frame));
        }

        return entries;
    }
}
