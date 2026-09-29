using Microsoft.EntityFrameworkCore;

namespace VehicleDiagnostics.Infrastructure.Analytics;

/// <summary>
/// Idempotent schema setup for the Analytics context, executed at host startup, in the same
/// style as the Telemetry context: plain DDL, replayable against a fresh or existing database.
/// </summary>
public sealed class AnalyticsDbInitializer(AnalyticsDbContext context)
{
    private const string Ddl = """
        CREATE SCHEMA IF NOT EXISTS analytics;
        CREATE TABLE IF NOT EXISTS analytics.message_log (
            settlement_id    uuid         NOT NULL,
            received_at      timestamptz  NOT NULL,
            outcome          varchar(16)  NOT NULL,
            reject_reason    varchar(64)  NULL,
            device_id        varchar(64)  NULL,
            vehicle_id       varchar(64)  NULL,
            sequence         bigint       NULL,
            processing_mode  varchar(2)   NULL,
            timestamp_device timestamptz  NULL,
            persisted_at     timestamptz  NULL,
            payload_bytes    integer      NULL,
            reading_count    integer      NOT NULL,
            recorded_at      timestamptz  NOT NULL,
            PRIMARY KEY (settlement_id, received_at)
        );
        SELECT create_hypertable('analytics.message_log', 'received_at', if_not_exists => TRUE);
        CREATE INDEX IF NOT EXISTS message_log_mode_received
            ON analytics.message_log (processing_mode, received_at DESC);
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(Ddl, cancellationToken).ConfigureAwait(false);
    }
}
