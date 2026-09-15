using VehicleDiagnostics.Infrastructure;
using VehicleDiagnostics.Infrastructure.Telemetry;
using VehicleDiagnostics.ServiceDefaults;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddTelemetryIngestion(builder.Configuration);

WebApplication app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/", () => "VehicleDiagnostics Api");

// Schema setup must finish before hosted services start consuming telemetry.
using (IServiceScope scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<TelemetryDbInitializer>()
        .InitializeAsync(CancellationToken.None);
}

await app.RunAsync();
