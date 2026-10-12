using VehicleDiagnostics.VirtualDevice;

// Virtual device (design D12): publishes a drive as schema-valid envelopes, as raw OBD-II
// frames (P0) or decoded signals (P2). The drive is a recorded one when DRIVE_PROFILE names a
// profile (experiments/profiles), otherwise a synthetic cycle.
//
// Configuration via environment variables:
//   MQTT_HOST (localhost) · MQTT_PORT (1883) · VEHICLE_ID (car-01)
//   DEVICE_ID_PREFIX (esp32) · RATE_HZ (1) · DEVICE_COUNT (1)
//   PROCESSING_MODE (P2) — P0 publishes raw frames on tel/raw, P2 decoded signals on tel/signals
//   HEARTBEAT_FILE (unset) — when set, touched after every publish, for container health checks
//   PHASE_SPREAD (even) — even: device i of n starts (i−1)/n of a period late, like independent
//                         devices; sync: all devices start together and publish in bursts
//   DRIVE_PROFILE (unset) — profile CSV of a recorded drive: one row per message, device i of n
//                         starting (i−1)/n of the way through it; unset or empty: synthetic cycle

string host = Environment.GetEnvironmentVariable("MQTT_HOST") ?? "localhost";
string? heartbeatFile = Environment.GetEnvironmentVariable("HEARTBEAT_FILE");
int port = int.TryParse(Environment.GetEnvironmentVariable("MQTT_PORT"), out int p) ? p : 1883;
string vehicleId = Environment.GetEnvironmentVariable("VEHICLE_ID") ?? "car-01";
string devicePrefix = Environment.GetEnvironmentVariable("DEVICE_ID_PREFIX") ?? "esp32";
double rateHz = double.TryParse(Environment.GetEnvironmentVariable("RATE_HZ"), out double r) && r > 0 ? r : 1.0;
int deviceCount = int.TryParse(Environment.GetEnvironmentVariable("DEVICE_COUNT"), out int d) && d > 0 ? d : 1;
bool synchronized = string.Equals(Environment.GetEnvironmentVariable("PHASE_SPREAD"), "sync", StringComparison.OrdinalIgnoreCase);
SimulatedMode mode = Enum.TryParse(Environment.GetEnvironmentVariable("PROCESSING_MODE"), ignoreCase: true, out SimulatedMode m)
    ? m
    : SimulatedMode.P2;

string? profilePath = Environment.GetEnvironmentVariable("DRIVE_PROFILE");
DriveProfile? profile = string.IsNullOrWhiteSpace(profilePath) ? null : DriveProfile.Load(profilePath);

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine(
    $"Virtual device(s) → mqtt://{host}:{port}  vehicle={vehicleId}  devices={deviceCount}  rate={rateHz} Hz  mode={mode}  phases={(synchronized ? "sync" : "even")}  drive={(profile is null ? "synthetic" : $"{profile.Name}, {profile.Seconds} s")}. Ctrl+C stops.");

Task[] devices = [.. Enumerable.Range(1, deviceCount)
    .Select(i => new DeviceSimulator(host, port, vehicleId, $"{devicePrefix}-{i:D2}", rateHz, mode, heartbeatFile,
            synchronized ? TimeSpan.Zero : TimeSpan.FromSeconds((i - 1) / (double)deviceCount / rateHz),
            profile, profile is null ? 0 : (long)(i - 1) * profile.Seconds / deviceCount)
        .RunAsync(shutdown.Token))];

await Task.WhenAll(devices);
Console.WriteLine("All virtual devices stopped.");
