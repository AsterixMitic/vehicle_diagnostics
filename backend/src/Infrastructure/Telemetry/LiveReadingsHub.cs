using System.Collections.Concurrent;
using System.Threading.Channels;
using VehicleDiagnostics.Application.Abstractions;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;

namespace VehicleDiagnostics.Infrastructure.Telemetry;

/// <summary>
/// In-process fan-out of persisted telemetry to live subscribers (design D9). Plugged in behind
/// the domain-event port, which fires only after an insert, so duplicates never reach it. Every
/// subscriber has its own bounded channel that drops its oldest message when full: publishing
/// never waits. Sees only what this process ingested, which is correct while one host runs.
/// </summary>
public sealed class LiveReadingsHub : ILiveTelemetryFeed, IDomainEventPublisher
{
    /// <summary>Messages buffered per subscriber before the oldest is dropped.</summary>
    public const int SubscriberCapacity = 256;

    private readonly ConcurrentDictionary<Subscription, byte> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public ILiveSubscription Subscribe(DeviceId? deviceId)
    {
        var subscription = new Subscription(this, deviceId);
        _subscribers.TryAdd(subscription, 0);
        return subscription;
    }

    public ValueTask PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent is TelemetryReceived telemetry)
        {
            foreach (Subscription subscriber in _subscribers.Keys)
            {
                subscriber.Offer(telemetry);
            }
        }

        return ValueTask.CompletedTask;
    }

    private sealed class Subscription : ILiveSubscription
    {
        private readonly LiveReadingsHub _hub;
        private readonly DeviceId? _deviceId;
        private readonly Channel<TelemetryReceived> _channel;
        private long _dropped;

        public Subscription(LiveReadingsHub hub, DeviceId? deviceId)
        {
            _hub = hub;
            _deviceId = deviceId;
            _channel = Channel.CreateBounded<TelemetryReceived>(
                new BoundedChannelOptions(SubscriberCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false,
                },
                _ => Interlocked.Increment(ref _dropped));
        }

        public long Dropped => Interlocked.Read(ref _dropped);

        public void Offer(TelemetryReceived telemetry)
        {
            if (_deviceId is null || _deviceId == telemetry.DeviceId)
            {
                _channel.Writer.TryWrite(telemetry);
            }
        }

        public IAsyncEnumerable<TelemetryReceived> ReadAllAsync(CancellationToken cancellationToken) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        public void Dispose()
        {
            _hub._subscribers.TryRemove(this, out _);
            _channel.Writer.TryComplete();
        }
    }
}
