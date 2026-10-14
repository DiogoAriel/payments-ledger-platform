# Multi-stage build: SDK image compiles and publishes, runtime image only carries the output.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
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

# Runtime: the non-root "app" user that ships with the .NET 8 images (uid 1654), no SDK, no shell
# tools. Liveness and readiness are probed by the orchestrator on /health/live and /health/ready
# (see deploy/helm), not by a Docker HEALTHCHECK.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ledger.Api.dll"]
