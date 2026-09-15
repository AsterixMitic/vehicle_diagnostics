using Microsoft.EntityFrameworkCore;

namespace VehicleDiagnostics.Infrastructure.Telemetry;

/// <summary>
/// EF Core model of the Telemetry context's own schema. No other bounded context may
/// touch the <c>telemetry</c> schema (ADR-0002).
/// </summary>
public sealed class TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : DbContext(options)
{
    public DbSet<SignalReadingRow> SignalReadings => Set<SignalReadingRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var readings = modelBuilder.Entity<SignalReadingRow>();
        readings.ToTable("signal_readings", "telemetry");
        readings.HasKey(r => new { r.DeviceId, r.Sequence, r.SignalName, r.TimestampDevice });
        readings.Property(r => r.DeviceId).HasColumnName("device_id").HasMaxLength(64);
        readings.Property(r => r.VehicleId).HasColumnName("vehicle_id").HasMaxLength(64);
        readings.Property(r => r.Sequence).HasColumnName("sequence");
        readings.Property(r => r.SignalName).HasColumnName("signal_name").HasMaxLength(128);
        readings.Property(r => r.Value).HasColumnName("value");
        readings.Property(r => r.Unit).HasColumnName("unit").HasMaxLength(32);
        readings.Property(r => r.TimestampDevice).HasColumnName("timestamp_device");
        readings.Property(r => r.TimestampIngest).HasColumnName("timestamp_ingest");
        readings.Property(r => r.ProcessingMode).HasColumnName("processing_mode").HasMaxLength(2);
    }
}
