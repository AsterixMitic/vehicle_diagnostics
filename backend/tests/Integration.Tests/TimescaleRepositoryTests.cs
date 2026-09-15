using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;
using VehicleDiagnostics.Infrastructure.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

public sealed class TimescaleRepositoryTests(TimescaleDbFixture fixture)
    : IClassFixture<TimescaleDbFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset IngestTime = new(2026, 9, 15, 12, 0, 1, TimeSpan.Zero);
    private static readonly DateTimeOffset SampleTime = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private TelemetryDbContext _context = null!;
    private PostgresTelemetryRepository _repository = null!;

    public async Task InitializeAsync()
    {
        DbContextOptions<TelemetryDbContext> options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        _context = new TelemetryDbContext(options);
        await new TelemetryDbInitializer(_context).InitializeAsync(CancellationToken.None);
        _repository = new PostgresTelemetryRepository(_context);
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    private static TelemetryReceived Batch(string deviceId, long sequence, DateTimeOffset sampledAt) =>
        TelemetryReceived.Create(
            DeviceId.Create(deviceId).Value,
            VehicleId.Create("car-01").Value,
            sequence,
            ProcessingMode.P2,
            [
                SignalReading.Create(SignalName.Create("engine_rpm").Value, 2450.5, "rpm", sampledAt).Value,
                SignalReading.Create(SignalName.Create("coolant_temp").Value, 88.0, "degC", sampledAt).Value,
            ],
            new FakeTimeProvider(IngestTime));

    [Fact]
    public async Task Schema_init_creates_a_hypertable_and_is_idempotent()
    {
        await new TelemetryDbInitializer(_context).InitializeAsync(CancellationToken.None);

        List<string> hypertables = await _context.Database
            .SqlQueryRaw<string>("SELECT hypertable_name FROM timescaledb_information.hypertables")
            .ToListAsync();

        hypertables.Should().Contain("signal_readings");
    }

    [Fact]
    public async Task Duplicate_insert_stores_readings_exactly_once()
    {
        TelemetryReceived batch = Batch("esp32-dup", 5, SampleTime);

        PersistResult first = await _repository.PersistAsync(batch, CancellationToken.None);
        PersistResult second = await _repository.PersistAsync(batch, CancellationToken.None);

        first.Should().Be(PersistResult.Inserted);
        second.Should().Be(PersistResult.Duplicate);

        IReadOnlyList<StoredReading> stored = await _repository.QueryAsync(
            DeviceId.Create("esp32-dup").Value, SampleTime.AddMinutes(-1), SampleTime.AddMinutes(1), CancellationToken.None);
        stored.Should().HaveCount(2);
        stored.Select(r => r.SignalName).Should().BeEquivalentTo("engine_rpm", "coolant_temp");
    }

    [Fact]
    public async Task Deduplication_survives_a_fresh_context()
    {
        TelemetryReceived batch = Batch("esp32-restart", 9, SampleTime);
        await _repository.PersistAsync(batch, CancellationToken.None);

        DbContextOptions<TelemetryDbContext> options = new DbContextOptionsBuilder<TelemetryDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using var freshContext = new TelemetryDbContext(options);
        var freshRepository = new PostgresTelemetryRepository(freshContext);

        PersistResult redelivery = await freshRepository.PersistAsync(batch, CancellationToken.None);

        redelivery.Should().Be(PersistResult.Duplicate);
    }

    [Fact]
    public async Task Query_returns_exactly_the_device_and_range_with_both_timestamps()
    {
        DateTimeOffset inRange = SampleTime;
        DateTimeOffset outOfRange = SampleTime.AddHours(2);
        await _repository.PersistAsync(Batch("esp32-q", 1, inRange), CancellationToken.None);
        await _repository.PersistAsync(Batch("esp32-q", 2, outOfRange), CancellationToken.None);
        await _repository.PersistAsync(Batch("esp32-other", 1, inRange), CancellationToken.None);

        IReadOnlyList<StoredReading> stored = await _repository.QueryAsync(
            DeviceId.Create("esp32-q").Value, inRange.AddMinutes(-1), inRange.AddMinutes(1), CancellationToken.None);

        stored.Should().HaveCount(2, "only the in-range batch of the queried device qualifies");
        stored.Should().OnlyContain(r => r.SampledAt == inRange && r.IngestedAt == IngestTime);
    }
}
