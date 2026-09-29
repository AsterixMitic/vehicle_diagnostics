using System.Globalization;
using FluentAssertions;
using VehicleDiagnostics.Application.Analytics;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.Analytics;

public sealed class EvaluationCsvTests
{
    private static readonly DateTimeOffset From = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private static EvaluationSummary Summary() => new(
        From,
        From.AddMinutes(15),
        [
            new ModeSummary("P0", 900, 895, 3, 2, 1, 1.0, 312.5, 312.456, new(12.25, 30, 41.5), new(8, 20, 25), new(4.1234, 9, 12)),
            new ModeSummary("P2", 900, 900, 0, 0, 0, 1.0, 210.0, 210.0, new(9, 18, 22), new(7, 15, 19), new(2, 3, 4)),
            new ModeSummary(ModeSummary.UnknownMode, 4, 0, 0, 4, 0, 0.004, 0.2, 50, null, null, null),
        ],
        [new RejectionCount("P0", "malformed", 2), new RejectionCount(ModeSummary.UnknownMode, "unknown_topic", 4)]);

    [Fact]
    public void Header_then_one_row_per_mode()
    {
        string[] lines = EvaluationCsv.Write(Summary()).TrimEnd('\n').Split('\n');

        lines.Should().HaveCount(4);
        lines[0].Should().Be(string.Join(',', EvaluationCsv.Columns));
        lines.Skip(1).Select(l => l.Split(',')[0]).Should().Equal("P0", "P2", "unknown");
        lines.Skip(1).Should().OnlyContain(l => l.Split(',').Length == EvaluationCsv.Columns.Count);
    }

    [Fact]
    public void Numbers_use_invariant_culture_whatever_the_current_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sr-Latn-RS");

            string p0 = EvaluationCsv.Write(Summary()).Split('\n')[1];

            p0.Should().Be("P0,900,895,3,2,1,1,312.5,312.456,12.25,30,41.5,8,20,25,4.123,9,12");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Absent_latencies_are_empty_cells()
    {
        string unknown = EvaluationCsv.Write(Summary()).Split('\n')[3];

        unknown.Should().EndWith(",50,,,,,,,,,");
    }

    [Fact]
    public void File_name_contains_the_range_in_utc()
    {
        EvaluationCsv.FileName(From.ToOffset(TimeSpan.FromHours(2)), From.AddMinutes(15))
            .Should().Be("evaluation_20260929T100000Z_20260929T101500Z.csv");
    }
}
