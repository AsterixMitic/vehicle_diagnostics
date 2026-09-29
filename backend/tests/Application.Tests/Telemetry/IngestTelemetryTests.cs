using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.Telemetry;

public sealed class IngestTelemetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeRepository _repository = new();
    private readonly FakePublisher _publisher = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly IngestTelemetry _useCase;

    public IngestTelemetryTests()
    {
        _useCase = new IngestTelemetry(_repository, _publisher, _time);
    }

    private static RawTelemetry ValidTelemetry(long sequence = 1) => new(
        "esp32-01", "car-01", sequence, "P2", Now.AddMilliseconds(-40),
        [new RawSignalReading("engine_rpm", 2450.5, "rpm"), new RawSignalReading("vehicle_speed", 62.0, "km_h")]);

    [Fact]
    public async Task Valid_telemetry_is_persisted_and_the_event_published()
    {
        IngestOutcome outcome = await _useCase.IngestAsync(ValidTelemetry(sequence: 7), CancellationToken.None);

        var accepted = outcome.Should().BeOfType<IngestOutcome.Accepted>().Subject;
        accepted.Event.DeviceId.Value.Should().Be("esp32-01");
        accepted.Event.Sequence.Should().Be(7);
        accepted.Event.OccurredAt.Should().Be(Now);
        accepted.Event.Readings.Should().HaveCount(2);

        _repository.Persisted.Should().ContainSingle().Which.Should().BeSameAs(accepted.Event);
        _publisher.Published.Should().ContainSingle().Which.Should().BeSameAs(accepted.Event);
    }

    [Fact]
    public async Task Persist_time_is_read_after_the_insert_returns()
    {
        _repository.OnPersist = () => _time.Advance(TimeSpan.FromMilliseconds(6));

        IngestOutcome outcome = await _useCase.IngestAsync(ValidTelemetry(), CancellationToken.None);

        var accepted = outcome.Should().BeOfType<IngestOutcome.Accepted>().Subject;
        accepted.Event.OccurredAt.Should().Be(Now);
        accepted.PersistedAt.Should().Be(Now.AddMilliseconds(6));
    }

    [Theory]
    [InlineData("", "car-01", "P2")]
    [InlineData("esp32-01", "veh/1", "P2")]
    [InlineData("esp32-01", "car-01", "P9")]
    public async Task Invalid_identity_or_mode_is_rejected_without_touching_ports(
        string deviceId, string vehicleId, string mode)
    {
        RawTelemetry telemetry = ValidTelemetry() with
        {
            DeviceId = deviceId,
            VehicleId = vehicleId,
            ProcessingMode = mode,
        };

        IngestOutcome outcome = await _useCase.IngestAsync(telemetry, CancellationToken.None);

        outcome.Should().BeOfType<IngestOutcome.Rejected>()
            .Which.Errors.Should().NotBeEmpty();
        _repository.Persisted.Should().BeEmpty();
        _publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Non_finite_reading_rejects_the_message()
    {
        RawTelemetry telemetry = ValidTelemetry() with
        {
            Readings = [new RawSignalReading("engine_rpm", double.NaN, "rpm")],
        };

        IngestOutcome outcome = await _useCase.IngestAsync(telemetry, CancellationToken.None);

        outcome.Should().BeOfType<IngestOutcome.Rejected>()
            .Which.Errors.Should().Contain(e => e.Code == "invalid");
        _repository.Persisted.Should().BeEmpty();
    }

    [Fact]
    public async Task Blank_signal_name_rejects_the_message()
    {
        RawTelemetry telemetry = ValidTelemetry() with
        {
            Readings = [new RawSignalReading("  ", 1.0, null)],
        };

        IngestOutcome outcome = await _useCase.IngestAsync(telemetry, CancellationToken.None);

        outcome.Should().BeOfType<IngestOutcome.Rejected>();
    }

    [Fact]
    public async Task Duplicate_persistence_yields_duplicate_outcome_and_no_event()
    {
        _repository.NextResult = PersistResult.Duplicate;

        IngestOutcome outcome = await _useCase.IngestAsync(ValidTelemetry(), CancellationToken.None);

        outcome.Should().BeOfType<IngestOutcome.Duplicate>();
        _publisher.Published.Should().BeEmpty();
    }

    private sealed class FakeRepository : ITelemetryRepository
    {
        public List<TelemetryReceived> Persisted { get; } = [];

        public PersistResult NextResult { get; set; } = PersistResult.Inserted;

        public Action? OnPersist { get; set; }

        public Task<PersistResult> PersistAsync(TelemetryReceived telemetry, CancellationToken cancellationToken)
        {
            OnPersist?.Invoke();
            if (NextResult == PersistResult.Inserted)
            {
                Persisted.Add(telemetry);
            }

            return Task.FromResult(NextResult);
        }

        public Task<IReadOnlyList<StoredReading>> QueryAsync(
            DeviceId deviceId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredReading>>([]);
    }

    private sealed class FakePublisher : IDomainEventPublisher
    {
        public List<IDomainEvent> Published { get; } = [];

        public ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            Published.Add(domainEvent);
            return ValueTask.CompletedTask;
        }
    }
}
