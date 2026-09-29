using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http.HttpResults;
using VehicleDiagnostics.Application.Telemetry;
using VehicleDiagnostics.Domain.SharedKernel;
using VehicleDiagnostics.Domain.Telemetry;

namespace VehicleDiagnostics.Api.Endpoints;

/// <summary>Server-Sent Events of telemetry as it is persisted.</summary>
public static class LiveEndpoints
{
    public const string EventType = "reading";

    public static RouteGroupBuilder MapLiveEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/stream/readings", Stream);
        return api;
    }

    private static Results<ServerSentEventsResult<LiveReadingDto>, ProblemHttpResult> Stream(
        string? deviceId, ILiveTelemetryFeed feed, HttpResponse response, CancellationToken cancellationToken)
    {
        DeviceId? filter = null;
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            Result<DeviceId, ValidationError> device = DeviceId.Create(deviceId);
            if (device.IsFailure)
            {
                return QueryParameters.Problem(device.Error);
            }

            filter = device.Value;
        }

        return TypedResults.ServerSentEvents(ReadAsync(feed, filter, response, cancellationToken), EventType);
    }

    /// <summary>
    /// Subscribes, then sends the response headers at once rather than with the first event: a
    /// client holding the headers is known to be subscribed and misses nothing published after.
    /// Unsubscribes when the client goes away.
    /// </summary>
    private static async IAsyncEnumerable<LiveReadingDto> ReadAsync(
        ILiveTelemetryFeed feed, DeviceId? filter, HttpResponse response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using ILiveSubscription subscription = feed.Subscribe(filter);

        // StartAsync alone only buffers the headers in Kestrel; the flush puts them on the wire.
        await response.StartAsync(cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
        await foreach (TelemetryReceived telemetry in subscription.ReadAllAsync(cancellationToken))
        {
            yield return LiveReadingDto.Of(telemetry);
        }
    }
}
