using FluentAssertions;
using VehicleDiagnostics.Application.Analytics;
using VehicleDiagnostics.Domain.Analytics;
using VehicleDiagnostics.Domain.SharedKernel;
using Xunit;

namespace VehicleDiagnostics.Application.Tests.Analytics;

public sealed class RecordSettlementTests
{
    private static readonly DateTimeOffset Received = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeLog _log = new();
    private readonly RecordSettlement _useCase;

    public RecordSettlementTests()
    {
        _useCase = new RecordSettlement(_log);
    }

    private static SettlementToRecord Persisted(Guid? id = null) => new(
        id ?? Guid.NewGuid(), "persisted", null, "esp32-01", "car-01", 12, "P0",
        Received.AddMilliseconds(-50), Received, Received.AddMilliseconds(8), 318, 3);

    [Fact]
    public async Task Valid_settlement_is_recorded_with_typed_identity()
    {
        RecordOutcome outcome = await _useCase.RecordAsync(Persisted(), CancellationToken.None);

        MessageMeasurement m = outcome.Should().BeOfType<RecordOutcome.Recorded>().Subject.Measurement;
        m.DeviceId.Should().Be(DeviceId.Create("esp32-01").Value);
        m.ProcessingMode.Should().Be(ProcessingMode.P0);
        m.Outcome.Should().Be(MeasurementOutcome.Persisted);
        _log.Stored.Should().ContainSingle();
    }

    [Fact]
    public async Task Second_delivery_of_the_same_settlement_is_a_redelivery()
    {
        Guid id = Guid.NewGuid();

        await _useCase.RecordAsync(Persisted(id), CancellationToken.None);
        RecordOutcome second = await _useCase.RecordAsync(Persisted(id), CancellationToken.None);

        second.Should().BeOfType<RecordOutcome.Redelivered>();
        _log.Stored.Should().ContainSingle();
    }

    [Fact]
    public async Task Invalid_identifiers_of_a_rejection_become_unknown()
    {
        var settlement = new SettlementToRecord(
            Guid.NewGuid(), "rejected", "invalid", "esp32/01", "", 3, "P7", null, Received, null, 120, 0);

        RecordOutcome outcome = await _useCase.RecordAsync(settlement, CancellationToken.None);

        MessageMeasurement m = outcome.Should().BeOfType<RecordOutcome.Recorded>().Subject.Measurement;
        m.DeviceId.Should().BeNull();
        m.VehicleId.Should().BeNull();
        m.ProcessingMode.Should().BeNull();
        m.Sequence.Should().Be(3);
    }

    [Fact]
    public async Task Unknown_outcome_is_invalid_and_not_stored()
    {
        RecordOutcome outcome = await _useCase.RecordAsync(Persisted() with { Outcome = "lost" }, CancellationToken.None);

        outcome.Should().BeOfType<RecordOutcome.Invalid>();
        _log.Stored.Should().BeEmpty();
    }

    [Fact]
    public async Task Persisted_settlement_without_persist_time_is_invalid()
    {
        RecordOutcome outcome = await _useCase.RecordAsync(Persisted() with { PersistedAt = null }, CancellationToken.None);

        outcome.Should().BeOfType<RecordOutcome.Invalid>();
    }

    private sealed class FakeLog : IMessageLogRepository
    {
        public List<MessageMeasurement> Stored { get; } = [];

        public Task<RecordResult> RecordAsync(MessageMeasurement measurement, CancellationToken cancellationToken)
        {
            if (Stored.Any(m => m.SettlementId == measurement.SettlementId))
            {
                return Task.FromResult(RecordResult.AlreadyRecorded);
            }

            Stored.Add(measurement);
            return Task.FromResult(RecordResult.Recorded);
        }
    }
}
