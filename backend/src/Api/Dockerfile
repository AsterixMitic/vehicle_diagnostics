# Build context: repository root (needs .editorconfig for analyzers-as-errors and backend/ props).
#   docker build -f backend/src/Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Repo-wide conventions and the solution-wide build/package settings first: they change
# rarely, so restore stays cached across source edits.
COPY .editorconfig ./
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/src/Domain/Domain.csproj                   backend/src/Domain/
COPY backend/src/Contracts/Contracts.csproj             backend/src/Contracts/
COPY backend/src/Application/Application.csproj         backend/src/Application/
COPY backend/src/Infrastructure/Infrastructure.csproj   backend/src/Infrastructure/
COPY backend/src/ServiceDefaults/ServiceDefaults.csproj backend/src/ServiceDefaults/
COPY backend/src/Api/Api.csproj                         backend/src/Api/

# global.json's "$host$" path resolves to this image's SDK.
WORKDIR /src/backend
RUN dotnet restore src/Api/Api.csproj

COPY backend/src/ /src/backend/src/
RUN dotnet publish src/Api/Api.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Npgsql/.NET probe for Kerberos at connection time; without this library the runtime logs a
# spurious "Error: libgssapi_krb5.so.2" line on every start, polluting evaluation logs.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "VehicleDiagnostics.Api.dll"]
