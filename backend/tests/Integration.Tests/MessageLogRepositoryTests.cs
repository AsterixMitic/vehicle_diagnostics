using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Domain.Analytics;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Infrastructure.Analytics;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

public sealed class MessageLogRepositoryTests(TimescaleDbFixture fixture)
    : IClassFixture<TimescaleDbFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Received = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private AnalyticsDbContext _context = null!;
    private PostgresMessageLogRepository _repository = null!;

    public async Task InitializeAsync()
    {
        _context = new AnalyticsDbContext(
            new DbContextOptionsBuilder<AnalyticsDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        await new AnalyticsDbInitializer(_context).InitializeAsync(CancellationToken.None);
        _repository = new PostgresMessageLogRepository(_context, new FakeTimeProvider(Received.AddSeconds(1)));
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task Schema_init_creates_the_hypertable_and_is_idempotent()
    {
        await new AnalyticsDbInitializer(_context).InitializeAsync(CancellationToken.None);

        List<string> hypertables = await _context.Database
            .SqlQueryRaw<string>("SELECT hypertable_schema || '.' || hypertable_name AS \"Value\" FROM timescaledb_information.hypertables")
            .ToListAsync();

        hypertables.Should().Contain("analytics.message_log");
    }

    [Fact]
    public async Task Same_settlement_is_stored_once()
    {
        MessageMeasurement measurement = MessageMeasurement.Create(
            SettlementId.Create(Guid.NewGuid()).Value, MeasurementOutcome.Persisted, null,
            DeviceId.Create("esp32-log").Value, VehicleId.Create("car-01").Value, 4, ProcessingMode.P0,
            Received.AddMilliseconds(-50), Received, Received.AddMilliseconds(9), 318, 3).Value;

        RecordResult first = await _repository.RecordAsync(measurement, CancellationToken.None);
        RecordResult second = await _repository.RecordAsync(measurement, CancellationToken.None);

        first.Should().Be(RecordResult.Recorded);
        second.Should().Be(RecordResult.AlreadyRecorded);
        (await CountAsync("esp32-log")).Should().Be(1);
    }

    [Fact]
    public async Task Anonymous_rejection_is_stored_with_nulls()
    {
        MessageMeasurement measurement = MessageMeasurement.Create(
            SettlementId.Create(Guid.NewGuid()).Value, MeasurementOutcome.Rejected, "unknown_topic",
            null, null, null, null, null, Received, null, null, 0).Value;

        (await _repository.RecordAsync(measurement, CancellationToken.None)).Should().Be(RecordResult.Recorded);

        List<string> reasons = await _context.Database
            .SqlQueryRaw<string>(
                "SELECT reject_reason AS \"Value\" FROM analytics.message_log WHERE settlement_id = {0} AND device_id IS NULL AND payload_bytes IS NULL",
                measurement.SettlementId.Value)
            .ToListAsync();
        reasons.Should().Equal("unknown_topic");
    }

    private async Task<int> CountAsync(string deviceId) =>
        (await _context.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM analytics.message_log WHERE device_id = {0}", deviceId)
            .ToListAsync()).Single();
}
