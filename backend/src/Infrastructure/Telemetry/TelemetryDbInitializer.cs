using Microsoft.EntityFrameworkCore;

namespace VehicleDiagnostics.Infrastructure.Telemetry;

/// <summary>
/// Idempotent schema setup for the Telemetry context, executed at host startup.
/// Plain DDL instead of EF migrations (design D7): <c>create_hypertable</c> is a
/// TimescaleDB function call, and idempotent DDL keeps the whole setup replayable
/// against a fresh or existing database with no migration tooling.
/// </summary>
public sealed class TelemetryDbInitializer(TelemetryDbContext context)
{
    private const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS telemetry;

        CREATE TABLE IF NOT EXISTS telemetry.signal_readings (
            device_id        varchar(64)  NOT NULL,
            vehicle_id       varchar(64)  NOT NULL,
            sequence         bigint       NOT NULL,
            signal_name      varchar(128) NOT NULL,
            value            double precision NOT NULL,
            unit             varchar(32)  NOT NULL,
            timestamp_device timestamptz  NOT NULL,
            timestamp_ingest timestamptz  NOT NULL,
            processing_mode  varchar(2)   NOT NULL,
            PRIMARY KEY (device_id, sequence, signal_name, timestamp_device)
        );

        SELECT create_hypertable('telemetry.signal_readings', 'timestamp_device', if_not_exists => TRUE);
        CREATE INDEX IF NOT EXISTS signal_readings_device_signal_time
            ON telemetry.signal_readings (device_id, signal_name, timestamp_device DESC);
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(Ddl, cancellationToken).ConfigureAwait(false);
    }
}
