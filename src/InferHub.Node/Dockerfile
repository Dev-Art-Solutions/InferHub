# Build context is the repo root — the node needs InferHub.Shared and the root
# Directory.Build.props:
#
#   docker build -f src/InferHub.Node/Dockerfile -t inferhub-node .
#
# No EXPOSE and no healthcheck port: the node never accepts a connection. It dials out to
# the coordinator over SignalR and out to a local Ollama, which is the whole point — a GPU
# box behind NAT needs no inbound rule.
#
# Reaching Ollama on the *host* from inside this container:
#   Docker Desktop (Windows/macOS):  Ollama__Endpoint=http://host.docker.internal:11434/
#   Plain Linux:                     Ollama__Endpoint=http://172.17.0.1:11434/
#                                    (the docker0 bridge gateway; `ip addr show docker0` to confirm)
#
# In practice most GPU nodes are better off *not* containerized — see deploy/docker/README.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY Directory.Build.props nuget.config ./
COPY src/InferHub.Shared/InferHub.Shared.csproj  src/InferHub.Shared/
COPY src/InferHub.Node/InferHub.Node.csproj      src/InferHub.Node/
RUN dotnet restore src/InferHub.Node/InferHub.Node.csproj -a "$TARGETARCH"

COPY src/InferHub.Shared/ src/InferHub.Shared/
COPY src/InferHub.Node/   src/InferHub.Node/
RUN dotnet publish src/InferHub.Node/InferHub.Node.csproj \
        -c Release -a "$TARGETARCH" --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Same trap as the coordinator's (see its Dockerfile for the long version): `USER app` cannot
# write /app, so the default ReplicaDirectory of ./data/vector-replicas resolves to /app/data and
# the node fails the moment the coordinator assigns it a vector replica. The mkdir+chown is what
# lets a named volume mounted here be writable too — Docker seeds a fresh volume's ownership from
# the image's mount point, and a mount point that does not exist is created root-owned.
RUN mkdir -p /data && chown app:app /data
ENV Vector__ReplicaDirectory=/data/vector-replicas

# The *other* half of the same bug, and the one v2.5.1 missed: `FileNodeIdentity` persists the
# stable node id to `Node:DataDirectory`, which defaults to the content root — /app, which `app`
# cannot write. Unlike the replica directory this is not conditional on a feature being on, so
# the node image has been **dead at startup** since v2.3.0: it threw
# `UnauthorizedAccessException: /app/.inferhub-node-id` before it ever reached the coordinator.
# Found the only way this class of bug is ever found — by running the container (D7).
ENV Node__DataDirectory=/data

USER app

ENTRYPOINT ["dotnet", "InferHub.Node.dll"]
