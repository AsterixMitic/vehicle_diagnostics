using Testcontainers.PostgreSql;
using Xunit;

namespace VehicleDiagnostics.Integration.Tests;

/// <summary>
/// One TimescaleDB container per test class. Real TimescaleDB, not a mock: hypertable
/// creation and ON CONFLICT semantics are exactly what mocks would lie about (design D11).
/// </summary>
public sealed class TimescaleDbFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } =
        new PostgreSqlBuilder("timescale/timescaledb:latest-pg17").Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
