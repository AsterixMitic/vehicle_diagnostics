# The official dashboard image is distroless (no shell, no HTTP client), so it cannot run a
# health probe on its own. This derived image adds a single static busybox binary and nothing
# else; the probe itself is declared in docker-compose.yml.
FROM busybox:1.37-musl AS tools

FROM mcr.microsoft.com/dotnet/aspire-dashboard:13.5.2
COPY --from=tools /bin/busybox /bin/busybox
