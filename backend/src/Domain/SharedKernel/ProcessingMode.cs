namespace VehicleDiagnostics.Domain.SharedKernel;

/// <summary>
/// How much of the processing pipeline (F1 receive → F2 reassemble → F3 decode → F4 analyze)
/// runs on the device. Runtime configuration, never a compile-time branch (architecture.md §17).
/// </summary>
public enum ProcessingMode
{
    /// <summary>Device does F1 only and publishes raw CAN frames.</summary>
    P0 = 0,

    /// <summary>Device does F1–F2 and publishes reassembled PDUs.</summary>
    P1 = 1,

    /// <summary>Device does F1–F3 and publishes decoded signals.</summary>
    P2 = 2,

    /// <summary>Device does F1–F4 and publishes only events and anomalies.</summary>
    P3 = 3,
}
