# Build context is the repo root — the coordinator needs InferHub.Shared and the root
# Directory.Build.props (TFM + <Version>):
#
#   docker build -f src/InferHub.Coordinator/Dockerfile -t inferhub-coordinator .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

# Restore against the manifests alone so editing a .cs file doesn't re-download the world.
COPY Directory.Build.props nuget.config ./
COPY src/InferHub.Shared/InferHub.Shared.csproj            src/InferHub.Shared/
COPY src/InferHub.Coordinator/InferHub.Coordinator.csproj  src/InferHub.Coordinator/
RUN dotnet restore src/InferHub.Coordinator/InferHub.Coordinator.csproj -a "$TARGETARCH"

COPY src/InferHub.Shared/      src/InferHub.Shared/
COPY src/InferHub.Coordinator/ src/InferHub.Coordinator/
RUN dotnet publish src/InferHub.Coordinator/InferHub.Coordinator.csproj \
        -c Release -a "$TARGETARCH" --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# curl is here only so HEALTHCHECK has something to call; the runtime image ships without it.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# D6 — load-bearing, and NOT interchangeable with ASPNETCORE_URLS.
#
# appsettings.json pins "Urls": "http://localhost:5080". ASPNETCORE_URLS is loaded by the
# ASPNETCORE_-prefixed provider into *host* configuration, which the appsettings.json layer
# then overrides — so ASPNETCORE_URLS loses and the app binds loopback, where nothing outside
# the container can reach it. Setting the "Urls" key directly (unprefixed env vars are layered
# after appsettings.json) is what actually wins. Verified at runtime, not assumed.
ENV Urls=http://+:8080

EXPOSE 8080

# Non-root. The runtime image ships this user for exactly this purpose.
USER app

HEALTHCHECK --interval=30s --timeout=3s --start-period=10s --retries=3 \
    CMD curl --fail --silent http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "InferHub.Coordinator.dll"]
