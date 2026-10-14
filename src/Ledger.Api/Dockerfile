# Multi-stage build: SDK image compiles and publishes, runtime image only carries the output.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# .editorconfig travels with the build: it marks EF migrations as generated code for the analyzers.
COPY Directory.Build.props Directory.Packages.props global.json .editorconfig ./
COPY src/Ledger.Domain/Ledger.Domain.csproj src/Ledger.Domain/
COPY src/Ledger.Application/Ledger.Application.csproj src/Ledger.Application/
COPY src/Ledger.Infrastructure/Ledger.Infrastructure.csproj src/Ledger.Infrastructure/
COPY src/Ledger.Api/Ledger.Api.csproj src/Ledger.Api/
RUN dotnet restore src/Ledger.Api/Ledger.Api.csproj
COPY src/ src/
RUN dotnet publish src/Ledger.Api/Ledger.Api.csproj -c Release -o /app --no-restore

# Runtime: the chiseled Ubuntu image carries only the runtime and its direct dependencies, no shell,
# no package manager, no perl; it removed every HIGH/CRITICAL finding trivy reported on the Debian
# base. It runs as the non-root "app" user (uid 1654). Liveness and readiness are probed by the
# orchestrator on /health/live and /health/ready (see deploy/helm), not by a Docker HEALTHCHECK.
FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app .
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ledger.Api.dll"]
