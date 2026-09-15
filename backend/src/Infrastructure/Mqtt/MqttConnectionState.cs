namespace VehicleDiagnostics.Infrastructure.Mqtt;

/// <summary>Last observed broker connectivity, fed by the ingest service and read by the readiness check.</summary>
public sealed class MqttConnectionState
{
    private int _connected;

    public bool IsConnected
    {
        get => Volatile.Read(ref _connected) == 1;
        set => Volatile.Write(ref _connected, value ? 1 : 0);
    }
}
