using VehicleDiagnostics.Application.Abstractions;

namespace VehicleDiagnostics.Infrastructure.Messaging;

/// <summary>Receipt fields of a bus message, which are both absent in a version-1 contract.</summary>
internal static class BusReceipt
{
    public static ReceiptInfo? From(DateTimeOffset? receivedAt, int? payloadBytes) =>
        receivedAt is { } at && payloadBytes is { } bytes ? new ReceiptInfo(at, bytes) : null;
}
