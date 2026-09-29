using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;
using VehicleDiagnostics.Infrastructure.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

public sealed class TelemetryQueriesTests(TimescaleDbFixture fixture)
    : IClassFixture<TimescaleDbFixture>, IAsyncLifetime
{
    // Minute-aligned, so one-minute buckets start on the minute.
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId DeviceA = DeviceId.Create("dev-q-a").Value;
    private static readonly DeviceId DeviceB = DeviceId.Create("dev-q-b").Value;

    private TelemetryDbContext _context = null!;
    private TimescaleTelemetryQueries _queries = null!;

    public async Task InitializeAsync()
    {
        _context = new TelemetryDbContext(
            new DbContextOptionsBuilder<TelemetryDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        await new TelemetryDbInitializer(_context).InitializeAsync(CancellationToken.None);
        _queries = new TimescaleTelemetryQueries(_context);
        await SeedAsync();
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    /// <summary>
    /// Device a, P2: <c>engine_rpm</c> at 1 Hz for five minutes, value <c>100·k + i</c> in second i of
    /// minute k, one message per minute; one <c>vehicle_speed</c> reading at 2 min. Device b, P0: one
    /// <c>engine_rpm</c> reading at 30 s. The store deduplicates, so seeding again before each test
    /// changes nothing.
    /// </summary>
    private async Task SeedAsync()
    {
        var repository = new PostgresTelemetryRepository(_context);
        for (int minute = 0; minute < 5; minute++)
        {
            SignalReading[] readings = [.. Enumerable.Range(0, 60).Select(second => Reading(
                "engine_rpm", (100 * minute) + second, T0.AddMinutes(minute).AddSeconds(second)))];
            await repository.PersistAsync(Message(DeviceA, minute + 1, ProcessingMode.P2, readings), CancellationToken.None);
        }

        await repository.PersistAsync(
            Message(DeviceA, 100, ProcessingMode.P2, [Reading("vehicle_speed", 62, T0.AddMinutes(2))]), CancellationToken.None);
        await repository.PersistAsync(
            Message(DeviceB, 1, ProcessingMode.P0, [Reading("engine_rpm", 1726.25, T0.AddSeconds(30))]), CancellationToken.None);
    }

    [Fact]
    public async Task Devices_in_range_are_listed_with_their_latest_reading()
    {
        TimeRange range = TimeRange.Create(T0, T0.AddMinutes(10), T0).Value;

        IReadOnlyList<DeviceSeen> devices = await _queries.DevicesAsync(range, CancellationToken.None);

        devices.Should().HaveCount(2);
        DeviceSeen a = devices.Single(d => d.DeviceId == DeviceA);
        a.LastSeen.Should().Be(T0.AddMinutes(4).AddSeconds(59));
        a.ProcessingMode.Should().Be(ProcessingMode.P2);
        devices.Single(d => d.DeviceId == DeviceB).ProcessingMode.Should().Be(ProcessingMode.P0);
    }

    [Fact]
    public async Task Devices_outside_the_range_are_not_listed()
    {
        TimeRange before = TimeRange.Create(T0.AddHours(-1), T0, T0).Value;

        (await _queries.DevicesAsync(before, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Latest_value_per_signal()
    {
        IReadOnlyList<LatestSignal> latest = await _queries.LatestSignalsAsync(DeviceA, CancellationToken.None);

        latest.Select(l => l.Name.Value).Should().Equal("engine_rpm", "vehicle_speed");
        LatestSignal rpm = latest[0];
        rpm.Value.Should().Be(459);
        rpm.SampledAt.Should().Be(T0.AddMinutes(4).AddSeconds(59));
        latest[1].Value.Should().Be(62);
    }

    [Fact]
    public async Task Unknown_device_has_no_latest_values()
    {
        (await _queries.LatestSignalsAsync(DeviceId.Create("dev-nobody").Value, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task History_is_aggregated_per_bucket()
    {
        TimeRange range = TimeRange.Create(T0, T0.AddMinutes(5), T0).Value;
        BucketWidth minute = BucketWidth.Resolve(range, TimeSpan.FromMinutes(1)).Value;

        SignalSeries series = await _queries.SignalSeriesAsync(
            DeviceA, SignalName.Create("engine_rpm").Value, range, minute, CancellationToken.None);

        series.Points.Should().HaveCount(5);
        for (int k = 0; k < 5; k++)
        {
            SignalPoint point = series.Points[k];
            point.BucketStart.Should().Be(T0.AddMinutes(k));
            point.Samples.Should().Be(60);
            point.Minimum.Should().Be(100 * k);
            point.Maximum.Should().Be((100 * k) + 59);
            point.Average.Should().BeApproximately((100 * k) + 29.5, 1e-9);
        }
    }

    [Fact]
    public async Task Unknown_signal_has_an_empty_series()
    {
        TimeRange range = TimeRange.Create(T0, T0.AddMinutes(5), T0).Value;

        SignalSeries series = await _queries.SignalSeriesAsync(
            DeviceA, SignalName.Create("oil_temp").Value, range, BucketWidth.Resolve(range, null).Value, CancellationToken.None);

        series.Points.Should().BeEmpty();
    }

    private static SignalReading Reading(string name, double value, DateTimeOffset at) =>
        SignalReading.Create(SignalName.Create(name).Value, value, "rpm", at).Value;

    private static TelemetryReceived Message(DeviceId device, long sequence, ProcessingMode mode, SignalReading[] readings) =>
        TelemetryReceived.Create(
            device, VehicleId.Create("car-01").Value, sequence, mode, readings, new FakeTimeProvider(T0.AddMinutes(6)));
}
