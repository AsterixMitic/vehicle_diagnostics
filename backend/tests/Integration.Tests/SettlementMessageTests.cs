using FluentAssertions;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Contracts.Internal;
using VehicleDiagnostics.Infrastructure.Messaging;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>Pure mapping from a settlement to its bus message; needs no containers.</summary>
public sealed class SettlementMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Known_receipt_is_carried_over()
    {
        var settlement = new MessageSettlement(
            SettlementOutcome.Persisted, null, "esp32-01", "car-01", 3, "P0", Now.AddMilliseconds(-50),
            new ReceiptInfo(Now.AddMilliseconds(-20), 301), Now.AddMilliseconds(-5), 3);
        Guid id = Guid.NewGuid();

        TelemetryMessageSettled message = MassTransitSettlementReporter.ToMessage(settlement, id, Now);

        message.SettlementId.Should().Be(id);
        message.Outcome.Should().Be(SettlementOutcomes.Persisted);
        message.ReceivedAt.Should().Be(Now.AddMilliseconds(-20));
        message.PayloadBytes.Should().Be(301);
        message.PersistedAt.Should().Be(Now.AddMilliseconds(-5));
        message.ReadingCount.Should().Be(3);
    }

    [Fact]
    public void Unknown_receipt_falls_back_to_settlement_time_and_unknown_size()
    {
        var settlement = new MessageSettlement(
            SettlementOutcome.Duplicate, null, "esp32-01", "car-01", 3, "P0", Now, null, null, 2);

        TelemetryMessageSettled message = MassTransitSettlementReporter.ToMessage(settlement, Guid.NewGuid(), Now);

        message.ReceivedAt.Should().Be(Now);
        message.PayloadBytes.Should().BeNull();
        message.Outcome.Should().Be(SettlementOutcomes.Duplicate);
    }

    [Fact]
    public void Anonymous_rejection_keeps_its_reason()
    {
        MessageSettlement settlement = MessageSettlement.RejectedAnonymous("unknown_topic", new ReceiptInfo(Now, 12), null, null);

        TelemetryMessageSettled message = MassTransitSettlementReporter.ToMessage(settlement, Guid.NewGuid(), Now);

        message.Outcome.Should().Be(SettlementOutcomes.Rejected);
        message.RejectReason.Should().Be("unknown_topic");
        message.DeviceId.Should().BeNull();
        message.Sequence.Should().BeNull();
    }
}
