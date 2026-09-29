namespace VehicleDiagnostics.Api.Endpoints;

/// <summary>Every endpoint under <c>/api</c>; shared by the host and the endpoint tests.</summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapVehicleDiagnosticsApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        RouteGroupBuilder api = app.MapGroup("/api");
        api.MapTelemetryEndpoints();
        api.MapEvaluationEndpoints();
        api.MapLiveEndpoints();
        return app;
    }
}
