using System.Text.Json;
using FluentAssertions;
using VehicleDiagnostics.Domain.SignalDecoding;
using Xunit;

namespace VehicleDiagnostics.Domain.Tests.SignalDecoding;

/// <summary>
/// Every <c>*.log</c> in <c>experiments/captures/</c> with a sibling <c>*.expected.json</c> is a
/// decoder regression fixture. Recorded traffic from real hardware drops in without code changes.
/// </summary>
public sealed class CaptureFixtureTests
{
    private static readonly string CapturesDirectory = Path.Combine(AppContext.BaseDirectory, "captures");
    private static readonly JsonSerializerOptions ExpectationJson = new(JsonSerializerDefaults.Web);

    private sealed record Expectation(int Line, string Outcome, string? Name, double? Value, string? Unit, string? Reason);

    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        foreach (string log in Directory.GetFiles(CapturesDirectory, "*.log").Order())
        {
            if (File.Exists(Path.ChangeExtension(log, ".expected.json")))
            {
                data.Add(Path.GetFileName(log));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_decodes_to_its_expectation(string fileName)
    {
        string logPath = Path.Combine(CapturesDirectory, fileName);
        IReadOnlyList<CandumpLog.Entry> entries = CandumpLog.Parse(File.ReadLines(logPath));
        List<Expectation> expectations = JsonSerializer.Deserialize<List<Expectation>>(
            File.ReadAllText(Path.ChangeExtension(logPath, ".expected.json")),
            ExpectationJson)!;

        expectations.Should().HaveCount(entries.Count, "every captured frame needs an expectation");

        foreach ((CandumpLog.Entry entry, Expectation expected) in entries.Zip(expectations))
        {
            entry.Line.Should().Be(expected.Line);
            FrameDecodeOutcome outcome = ObdMode01Decoder.Decode(entry.Frame);
            switch (expected.Outcome)
            {
                case "decoded":
                    DecodedSignal signal = outcome.Should().BeOfType<FrameDecodeOutcome.Decoded>(
                        $"line {entry.Line} {entry.Frame}").Subject.Signal;
                    signal.Name.Value.Should().Be(expected.Name);
                    signal.Value.Should().BeApproximately(expected.Value!.Value, 1e-6);
                    signal.Unit.Should().Be(expected.Unit);
                    break;
                case "ignored":
                    outcome.Should().BeOfType<FrameDecodeOutcome.Ignored>($"line {entry.Line} {entry.Frame}")
                        .Which.Reason.ToTag().Should().Be(expected.Reason);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown outcome '{expected.Outcome}' at line {entry.Line}");
            }
        }
    }

    [Fact]
    public void At_least_one_fixture_exists()
    {
        Fixtures().Should().NotBeEmpty();
    }
}
