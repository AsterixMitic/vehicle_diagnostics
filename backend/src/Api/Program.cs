using VehicleDiagnostics.Api.Endpoints;
using VehicleDiagnostics.Infrastructure;
using VehicleDiagnostics.ServiceDefaults;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddTelemetryIngestion(builder.Configuration);
builder.Services.AddProblemDetails();

WebApplication app = builder.Build();

app.MapDefaultEndpoints();
app.MapGet("/", () => "VehicleDiagnostics Api");
app.MapVehicleDiagnosticsApi();

// Schema setup must finish before hosted services start consuming telemetry.
await app.Services.InitializeSchemasAsync(CancellationToken.None);

await app.RunAsync();
