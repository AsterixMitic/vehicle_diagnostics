WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

WebApplication app = builder.Build();

app.MapGet("/", () => "VehicleDiagnostics Api");

app.Run();
