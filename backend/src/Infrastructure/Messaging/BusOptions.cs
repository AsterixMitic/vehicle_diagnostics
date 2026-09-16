namespace VehicleDiagnostics.Infrastructure.Messaging;

/// <summary>Internal bus tuning, bound from the <c>Bus</c> configuration section.</summary>
public sealed class BusOptions
{
    /// <summary>Delays between redeliveries of a failed message; exhausted messages go to the error queue.</summary>
    public IReadOnlyList<TimeSpan> RetryIntervals { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];

    /// <summary>Partitions for same-device ordering inside the process (design D4).</summary>
    public int PartitionCount { get; set; } = 8;

    /// <summary>Messages a consumer endpoint may hold in flight.</summary>
    public int PrefetchCount { get; set; } = 16;
}
