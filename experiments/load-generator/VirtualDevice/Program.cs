using VehicleDiagnostics.VirtualDevice;

// Minimal virtual device (design D12): publishes a synthetic drive cycle as schema-valid
// envelopes, as raw OBD-II frames (P0) or decoded signals (P2). The full multi-hundred-device
// load generator is the F6 increment.
//
// Configuration via environment variables:
//   MQTT_HOST (localhost) · MQTT_PORT (1883) · VEHICLE_ID (car-01)
//   DEVICE_ID_PREFIX (esp32) · RATE_HZ (1) · DEVICE_COUNT (1)
//   PROCESSING_MODE (P2) — P0 publishes raw frames on tel/raw, P2 decoded signals on tel/signals
//   HEARTBEAT_FILE (unset) — when set, touched after every publish, for container health checks

string host = Environment.GetEnvironmentVariable("MQTT_HOST") ?? "localhost";
string? heartbeatFile = Environment.GetEnvironmentVariable("HEARTBEAT_FILE");
int port = int.TryParse(Environment.GetEnvironmentVariable("MQTT_PORT"), out int p) ? p : 1883;
string vehicleId = Environment.GetEnvironmentVariable("VEHICLE_ID") ?? "car-01";
string devicePrefix = Environment.GetEnvironmentVariable("DEVICE_ID_PREFIX") ?? "esp32";
double rateHz = double.TryParse(Environment.GetEnvironmentVariable("RATE_HZ"), out double r) && r > 0 ? r : 1.0;
int deviceCount = int.TryParse(Environment.GetEnvironmentVariable("DEVICE_COUNT"), out int d) && d > 0 ? d : 1;
SimulatedMode mode = Enum.TryParse(Environment.GetEnvironmentVariable("PROCESSING_MODE"), ignoreCase: true, out SimulatedMode m)
    ? m
    : SimulatedMode.P2;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    shutdown.Cancel();
};

Console.WriteLine(
    $"Virtual device(s) → mqtt://{host}:{port}  vehicle={vehicleId}  devices={deviceCount}  rate={rateHz} Hz  mode={mode}. Ctrl+C stops.");

Task[] devices = [.. Enumerable.Range(1, deviceCount)
    .Select(i => new DeviceSimulator(host, port, vehicleId, $"{devicePrefix}-{i:D2}", rateHz, mode, heartbeatFile)
        .RunAsync(shutdown.Token))];

await Task.WhenAll(devices);
Console.WriteLine("All virtual devices stopped.");
