using System.Globalization;
using System.Text;

namespace VehicleDiagnostics.Application.Analytics;

/// <summary>
/// The evaluation summary as CSV for the thesis tables: fixed column order, invariant culture,
/// empty cells for absent values, one row per mode.
/// </summary>
public static class EvaluationCsv
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "mode", "received", "persisted", "duplicates", "rejected", "lost",
        "messages_per_second", "bytes_per_second", "mean_payload_bytes",
        "e2e_p50_ms", "e2e_p95_ms", "e2e_p99_ms",
        "transport_p50_ms", "transport_p95_ms", "transport_p99_ms",
        "backend_p50_ms", "backend_p95_ms", "backend_p99_ms",
    ];

    public static string Write(EvaluationSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var csv = new StringBuilder();
        csv.AppendJoin(',', Columns).Append('\n');
        foreach (ModeSummary m in summary.Modes)
        {
            csv.AppendJoin(',',
            [
                m.Mode,
                Integer(m.Received), Integer(m.Persisted), Integer(m.Duplicates), Integer(m.Rejected), Integer(m.Lost),
                Number(m.MessagesPerSecond), Number(m.BytesPerSecond), Number(m.MeanPayloadBytes),
                Number(m.EndToEnd?.P50), Number(m.EndToEnd?.P95), Number(m.EndToEnd?.P99),
                Number(m.Transport?.P50), Number(m.Transport?.P95), Number(m.Transport?.P99),
                Number(m.Backend?.P50), Number(m.Backend?.P95), Number(m.Backend?.P99),
            ]).Append('\n');
        }

        return csv.ToString();
    }

    /// <summary>Names the range, so exports of different runs do not overwrite each other.</summary>
    public static string FileName(DateTimeOffset from, DateTimeOffset to) =>
        string.Create(CultureInfo.InvariantCulture,
            $"evaluation_{from.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}_{to.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.csv");

    private static string Integer(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double? value) =>
        value is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
}
