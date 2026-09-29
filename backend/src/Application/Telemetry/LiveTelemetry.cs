using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;

namespace VehicleDiagnostics.Application.Telemetry;

/// <summary>
/// Live feed of telemetry as it is persisted. Only newly inserted messages appear on it, so a
/// deduplicated redelivery is never streamed.
/// </summary>
public interface ILiveTelemetryFeed
{
    /// <summary>Messages persisted from now on, for one device or, when null, for all devices.</summary>
    ILiveSubscription Subscribe(DeviceId? deviceId);
}

/// <summary>
/// One subscriber's bounded buffer. When the subscriber falls behind, its oldest undelivered
/// messages are dropped and counted; ingestion and other subscribers are never slowed.
/// Disposing unsubscribes and ends the read.
/// </summary>
public interface ILiveSubscription : IDisposable
{
    IAsyncEnumerable<TelemetryReceived> ReadAllAsync(CancellationToken cancellationToken);

    long Dropped { get; }
}
