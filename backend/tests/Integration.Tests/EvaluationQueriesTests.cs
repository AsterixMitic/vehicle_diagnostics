using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Domain.Analytics;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Infrastructure.Analytics;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// Evaluation arithmetic against real TimescaleDB, over a hand-built log whose expected
/// percentiles, rates and gaps are computed in the comments.
/// </summary>
public sealed class EvaluationQueriesTests(TimescaleDbFixture fixture)
    : IClassFixture<TimescaleDbFixture>, IAsyncLifetime
{
    // Aligned to a 30 s boundary, so time_bucket(30 s) buckets start at T0.
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeRange Range = TimeRange.Create(T0, T0.AddSeconds(100), T0).Value;

    private AnalyticsDbContext _context = null!;
    private TimescaleEvaluationQueries _queries = null!;
    private int _nextId;

    public async Task InitializeAsync()
    {
        _context = new AnalyticsDbContext(
            new DbContextOptionsBuilder<AnalyticsDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        await new AnalyticsDbInitializer(_context).InitializeAsync(CancellationToken.None);
        _queries = new TimescaleEvaluationQueries(_context);
        await SeedAsync();
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    /// <summary>
    /// xUnit runs <see cref="InitializeAsync"/> before every test against the one shared container.
    /// Settlement identifiers are fixed, so every seeding after the first is a set of redeliveries
    /// the store ignores, and each test sees exactly the log described here.
    /// </summary>
    private SettlementId NextId() =>
        SettlementId.Create(new Guid($"00000000-0000-0000-0000-{++_nextId:D12}")).Value;

    /// <summary>
    /// P2, device a: sequences 1, 2, 4, 7 persisted at 0, 10, 20, 30 s with transport 10/20/30/40 ms
    /// and backend 5 ms (end-to-end 15/25/35/45 ms), 200 B each; a duplicate of 7 at 40 s; sequence 8
    /// at exactly 100 s, outside the half-open range.
    /// P0, device b: sequence 1 persisted at 0.5 s (transport 60 ms, backend 40 ms), 300 B; sequence 2
    /// rejected as no_readings at 50 s, 280 B.
    /// Unknown: an anonymous unknown_topic rejection at 60 s, 40 B.
    /// </summary>
    private async Task SeedAsync()
    {
        var repository = new PostgresMessageLogRepository(_context, new FakeTimeProvider(T0));
        (long Seq, double At, double TransportMs)[] p2 = [(1, 0, 10), (2, 10, 20), (4, 20, 30), (7, 30, 40)];
        foreach ((long seq, double at, double transportMs) in p2)
        {
            await repository.RecordAsync(Persisted("dev-a", seq, ProcessingMode.P2, T0.AddSeconds(at), transportMs, 5, 200), CancellationToken.None);
        }

        await repository.RecordAsync(Duplicate("dev-a", 7, ProcessingMode.P2, T0.AddSeconds(40), 200), CancellationToken.None);
        await repository.RecordAsync(Persisted("dev-a", 8, ProcessingMode.P2, T0.AddSeconds(100), 10, 5, 200), CancellationToken.None);

        await repository.RecordAsync(Persisted("dev-b", 1, ProcessingMode.P0, T0.AddSeconds(0.5), 60, 40, 300), CancellationToken.None);
        await repository.RecordAsync(Measurement(
            MeasurementOutcome.Rejected, "no_readings", "dev-b", 2, ProcessingMode.P0, T0.AddSeconds(50), null, 280), CancellationToken.None);

        await repository.RecordAsync(Measurement(
            MeasurementOutcome.Rejected, "unknown_topic", null, null, null, T0.AddSeconds(60), null, 40), CancellationToken.None);
    }

    [Fact]
    public async Task Summary_has_one_row_per_mode_with_exact_counts_rates_and_gaps()
    {
        EvaluationSummary summary = await _queries.SummaryAsync(Range, CancellationToken.None);

        summary.Modes.Select(m => m.Mode).Should().Equal("P0", "P2", ModeSummary.UnknownMode);

        ModeSummary p2 = summary.Modes.Single(m => m.Mode == "P2");
        p2.Received.Should().Be(5);
        p2.Persisted.Should().Be(4);
        p2.Duplicates.Should().Be(1);
        p2.Rejected.Should().Be(0);
        p2.Lost.Should().Be(3);                          // 7 − 1 + 1 − |{1, 2, 4, 7}|
        p2.MessagesPerSecond.Should().Be(5 / 40.0);      // active interval 0 → 40 s
        p2.BytesPerSecond.Should().Be(1000 / 40.0);
        p2.MeanPayloadBytes.Should().Be(200);

        ModeSummary p0 = summary.Modes.Single(m => m.Mode == "P0");
        p0.Received.Should().Be(2);
        p0.Persisted.Should().Be(1);
        p0.Rejected.Should().Be(1);
        p0.Lost.Should().Be(0);
        p0.MessagesPerSecond.Should().BeApproximately(2 / 49.5, 1e-9);
        p0.MeanPayloadBytes.Should().Be(290);

        ModeSummary unknown = summary.Modes.Single(m => m.Mode == ModeSummary.UnknownMode);
        unknown.Rejected.Should().Be(1);
        unknown.MessagesPerSecond.Should().Be(1 / 100.0); // single message: whole range
        unknown.EndToEnd.Should().BeNull();
    }

    [Fact]
    public async Task Latency_percentiles_are_continuous_over_persisted_messages()
    {
        EvaluationSummary summary = await _queries.SummaryAsync(Range, CancellationToken.None);
        ModeSummary p2 = summary.Modes.Single(m => m.Mode == "P2");

        // End-to-end 15, 25, 35, 45: p50 at 1.5 → 30; p95 at 2.85 → 43.5; p99 at 2.97 → 44.7.
        p2.EndToEnd!.P50.Should().BeApproximately(30, 1e-6);
        p2.EndToEnd.P95.Should().BeApproximately(43.5, 1e-6);
        p2.EndToEnd.P99.Should().BeApproximately(44.7, 1e-6);
        p2.Transport!.P50.Should().BeApproximately(25, 1e-6);
        p2.Transport.P95.Should().BeApproximately(38.5, 1e-6);
        p2.Backend!.P99.Should().BeApproximately(5, 1e-6);

        ModeSummary p0 = summary.Modes.Single(m => m.Mode == "P0");
        p0.EndToEnd!.P50.Should().BeApproximately(100, 1e-6);
        p0.Backend!.P50.Should().BeApproximately(40, 1e-6);
    }

    [Fact]
    public async Task Rejections_are_reported_by_mode_and_reason()
    {
        EvaluationSummary summary = await _queries.SummaryAsync(Range, CancellationToken.None);

        summary.Rejections.Should().Equal(
            new RejectionCount("P0", "no_readings", 1),
            new RejectionCount(ModeSummary.UnknownMode, "unknown_topic", 1));
    }

    [Fact]
    public async Task Empty_range_gives_an_empty_summary()
    {
        TimeRange empty = TimeRange.Create(T0.AddHours(-2), T0.AddHours(-1), T0).Value;

        EvaluationSummary summary = await _queries.SummaryAsync(empty, CancellationToken.None);

        summary.Modes.Should().BeEmpty();
        summary.Rejections.Should().BeEmpty();
    }

    [Fact]
    public async Task Series_buckets_rates_and_p95_per_mode()
    {
        BucketWidth thirty = BucketWidth.Resolve(Range, TimeSpan.FromSeconds(30)).Value;

        EvaluationSeries series = await _queries.SeriesAsync(Range, thirty, CancellationToken.None);

        ModeSeriesPoint[] p2 = [.. series.Points.Where(p => p.Mode == "P2")];
        p2.Select(p => p.BucketStart).Should().Equal(T0, T0.AddSeconds(30));
        p2[0].Received.Should().Be(3);
        p2[0].MessagesPerSecond.Should().Be(0.1);
        p2[0].BytesPerSecond.Should().Be(20);
        p2[0].EndToEndP95.Should().BeApproximately(34, 1e-6); // 15, 25, 35: at 1.9 → 34
        p2[1].Received.Should().Be(2);
        p2[1].EndToEndP95.Should().BeApproximately(45, 1e-6); // the duplicate has no latency

        ModeSeriesPoint[] p0 = [.. series.Points.Where(p => p.Mode == "P0")];
        p0.Select(p => p.BucketStart).Should().Equal(T0, T0.AddSeconds(30));
        p0[1].EndToEndP95.Should().BeNull();

        series.Points.Should().ContainSingle(p => p.Mode == ModeSummary.UnknownMode)
            .Which.BucketStart.Should().Be(T0.AddSeconds(60));
    }

    private MessageMeasurement Persisted(
        string device, long sequence, ProcessingMode mode, DateTimeOffset received, double transportMs, double backendMs, int bytes) =>
        MessageMeasurement.Create(
            NextId(), MeasurementOutcome.Persisted, null,
            DeviceId.Create(device).Value, VehicleId.Create("car-01").Value, sequence, mode,
            received.AddMilliseconds(-transportMs), received, received.AddMilliseconds(backendMs), bytes, 3).Value;

    private MessageMeasurement Duplicate(string device, long sequence, ProcessingMode mode, DateTimeOffset received, int bytes) =>
        MessageMeasurement.Create(
            NextId(), MeasurementOutcome.Duplicate, null,
            DeviceId.Create(device).Value, VehicleId.Create("car-01").Value, sequence, mode,
            received.AddMilliseconds(-20), received, null, bytes, 3).Value;

    private MessageMeasurement Measurement(
        MeasurementOutcome outcome, string reason, string? device, long? sequence, ProcessingMode? mode,
        DateTimeOffset received, DateTimeOffset? persisted, int bytes) =>
        MessageMeasurement.Create(
            NextId(), outcome, reason,
            device is null ? null : DeviceId.Create(device).Value, device is null ? null : VehicleId.Create("car-01").Value,
            sequence, mode, device is null ? null : received.AddMilliseconds(-20), received, persisted, bytes, 0).Value;
}
