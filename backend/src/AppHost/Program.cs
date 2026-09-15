IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<PostgresServerResource> postgres = builder
    .AddPostgres("postgres")
    .WithImage("timescale/timescaledb", "latest-pg17");
IResourceBuilder<PostgresDatabaseResource> telemetryDb = postgres.AddDatabase("telemetrydb");

// Host port 1883 is pinned so the virtual device's defaults work without configuration.
IResourceBuilder<ContainerResource> mosquitto = builder
    .AddContainer("mosquitto", "eclipse-mosquitto", "2")
    .WithBindMount("../../../edge/mosquitto/mosquitto.conf", "/mosquitto/config/mosquitto.conf", isReadOnly: true)
    .WithEndpoint(port: 1883, targetPort: 1883, name: "mqtt");

builder.AddProject<Projects.Api>("api")
    .WithReference(telemetryDb)
    .WaitFor(telemetryDb)
    .WaitFor(mosquitto)
    .WithEnvironment(context =>
    {
        EndpointReference mqtt = mosquitto.GetEndpoint("mqtt");
        context.EnvironmentVariables["Mqtt__Host"] = mqtt.Property(EndpointProperty.Host);
        context.EnvironmentVariables["Mqtt__Port"] = mqtt.Property(EndpointProperty.Port);
    });

builder.Build().Run();
