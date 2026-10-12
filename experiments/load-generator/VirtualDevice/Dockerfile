# Build context: repository root (the device shares the Contracts and Domain projects with the backend).
#   docker build -f experiments/load-generator/VirtualDevice/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY .editorconfig ./
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/src/Contracts/ backend/src/Contracts/
COPY backend/src/Domain/ backend/src/Domain/
COPY experiments/load-generator/VirtualDevice/ experiments/load-generator/VirtualDevice/
RUN dotnet publish experiments/load-generator/VirtualDevice/VirtualDevice.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
# The recorded drive the Arduino simulator replays; DRIVE_PROFILE= (empty) selects the synthetic cycle.
COPY experiments/profiles/ /app/profiles/
ENV DRIVE_PROFILE=/app/profiles/asx-2012.csv
ENTRYPOINT ["dotnet", "VehicleDiagnostics.VirtualDevice.dll"]
