using Microsoft.EntityFrameworkCore;

namespace VehicleDiagnostics.Infrastructure.Analytics;

/// <summary>
/// Connection to the Analytics context's own schema, <c>analytics</c>. The measurement log is
/// written and read with plain SQL (inserts with conflict handling, percentile and bucket
/// queries), so the model maps no entities. No other bounded context may touch this schema.
/// </summary>
public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options);
