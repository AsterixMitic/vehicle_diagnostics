using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;
using VehicleDiagnostics.Infrastructure.Telemetry;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>In-process fan-out behavior; needs no containers.</summary>
public sealed class LiveReadingsHubTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);
    private static readonly DeviceId DeviceA = DeviceId.Create("live-a").Value;
    private static readonly DeviceId DeviceB = DeviceId.Create("live-b").Value;

    private readonly LiveReadingsHub _hub = new();

    private static TelemetryReceived Message(DeviceId device, long sequence) =>
        TelemetryReceived.Create(
            device, VehicleId.Create("car-01").Value, sequence, ProcessingMode.P2,
            [SignalReading.Create(SignalName.Create("engine_rpm").Value, 1726.25, "rpm", Now).Value],
            new FakeTimeProvider(Now));

    private static async Task<List<long>> DrainAsync(ILiveSubscription subscription)
    {
        List<long> sequences = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await foreach (TelemetryReceived message in subscription.ReadAllAsync(timeout.Token))
            {
                sequences.Add(message.Sequence);
            }
        }
        catch (OperationCanceledException)
        {
            // Nothing more buffered.
        }

        return sequences;
    }

    [Fact]
    public async Task Subscriber_for_a_device_sees_only_that_device()
    {
        using ILiveSubscription onlyA = _hub.Subscribe(DeviceA);

        await _hub.PublishAsync(Message(DeviceA, 1), CancellationToken.None);
        await _hub.PublishAsync(Message(DeviceB, 2), CancellationToken.None);
        await _hub.PublishAsync(Message(DeviceA, 3), CancellationToken.None);

        (await DrainAsync(onlyA)).Should().Equal(1, 3);
    }

    [Fact]
    public async Task Every_subscriber_gets_every_matching_message()
    {
        using ILiveSubscription all = _hub.Subscribe(null);
        using ILiveSubscription onlyB = _hub.Subscribe(DeviceB);

        await _hub.PublishAsync(Message(DeviceA, 1), CancellationToken.None);
        await _hub.PublishAsync(Message(DeviceB, 2), CancellationToken.None);

        (await DrainAsync(all)).Should().Equal(1, 2);
        (await DrainAsync(onlyB)).Should().Equal(2);
    }

    [Fact]
    public async Task Slow_subscriber_loses_its_oldest_messages_and_publishing_never_waits()
    {
        using ILiveSubscription slow = _hub.Subscribe(null);
        int total = LiveReadingsHub.SubscriberCapacity + 10;

        for (int sequence = 1; sequence <= total; sequence++)
        {
            ValueTask publish = _hub.PublishAsync(Message(DeviceA, sequence), CancellationToken.None);
            publish.IsCompletedSuccessfully.Should().BeTrue("publishing must never wait for a subscriber");
            await publish;
        }

        List<long> received = await DrainAsync(slow);
        received.Should().HaveCount(LiveReadingsHub.SubscriberCapacity);
        received[0].Should().Be(11);
        received[^1].Should().Be(total);
        slow.Dropped.Should().Be(10);
    }

    [Fact]
    public async Task Disposed_subscription_is_removed_and_its_read_ends()
    {
        ILiveSubscription subscription = _hub.Subscribe(null);
        _hub.SubscriberCount.Should().Be(1);

        subscription.Dispose();
        await _hub.PublishAsync(Message(DeviceA, 1), CancellationToken.None);

        _hub.SubscriberCount.Should().Be(0);
        List<TelemetryReceived> read = [];
        await foreach (TelemetryReceived message in subscription.ReadAllAsync(CancellationToken.None))
        {
            read.Add(message);
        }

        read.Should().BeEmpty();
    }
}
