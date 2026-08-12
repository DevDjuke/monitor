# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Monitor.Domain/Monitor.Domain.csproj src/Monitor.Domain/
COPY src/Monitor.Infrastructure/Monitor.Infrastructure.csproj src/Monitor.Infrastructure/
COPY src/Monitor.Web/Monitor.Web.csproj src/Monitor.Web/
RUN dotnet restore src/Monitor.Web/Monitor.Web.csproj

COPY src/ ./src/
RUN dotnet publish src/Monitor.Web/Monitor.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

RUN apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /var/lib/monitor/data-protection-keys \
    && chown -R app:app /var/lib/monitor

COPY --from=build --chown=app:app /app/publish ./

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

USER app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "Monitor.Web.dll"]
