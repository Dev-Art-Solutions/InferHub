# Build context is the repo root — the node needs InferHub.Shared and the root
# Directory.Build.props:
#
#   docker build -f src/InferHub.Node/Dockerfile -t inferhub-node .
#
# The *mesh* protocol is outbound-only and always will be: the coordinator never dials a node,
# so a GPU box behind NAT needs no inbound rule for the fleet to work. It dials out to the
# coordinator over SignalR and out to a local Ollama.
#
# Since v3.5 the node can also serve its own clients — solo mode, off by default. That is a
# client-facing surface, not a mesh one (phase-37 D1), and it exists so a deployment with no
# coordinator at all is possible:
#
#   docker run -e LocalApi__Enabled=true -e Coordinator__Enabled=false \
#              -e LocalApi__ApiKeys__0=your-key \
#              -e Ollama__Endpoint=http://host.docker.internal:11434/ \
#              -p 5081:8080 ghcr.io/dev-art-solutions/inferhub-node
#
# then point any OpenAI client at http://localhost:5081/v1.
#
# The key is not optional here and that is deliberate. In a container the API binds a wildcard
# (see LocalApi__Urls below), so it is reachable from outside the box; phase-37 D4 refuses to
# start a keyless non-loopback listener rather than hand arbitrary GPU time to whoever finds the
# port. `LocalApi__AllowAnonymous=true` is the explicit override for a trusted network.
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

# Solo mode binds loopback by default, which inside a container means nothing reaches it through
# `-p`. Set the address here rather than relying on ASPNETCORE_URLS: phase-21 D6 is the third
# feature to walk into that trap — an appsettings.json value overrides the ASPNETCORE_-prefixed
# provider, so a container honouring only ASPNETCORE_URLS binds loopback and answers nobody.
# NodeHostFactory applies LocalApi:Urls to the web host directly, so this wins.
#
# Inert unless LocalApi__Enabled=true: with solo off there is no web host and nothing listens.
ENV LocalApi__Urls=http://+:8080

# Phase 38 (v3.6+): solo retrieval writes its corpus to disk, and the default `./data/retrieval`
# resolves to /app/data — which `app` cannot write, exactly as the vector store (phase-21 D7), the
# node id (found five releases later) and the affinity store (phase-30 D3) each discovered in turn.
# This is the fourth path in that family, so it is set here rather than being rediscovered.
#
# Mount a volume at /data to keep the corpus across `docker run`s; without one it is ephemeral, and
# ephemeral is the honest outcome for a container nobody gave storage to.
#
# Inert unless LocalApi__Retrieval__Enabled=true, which also requires Coordinator__Enabled=false.
ENV LocalApi__Retrieval__DataDirectory=/data/retrieval

# Phase 41 (v3.9+): the tool runtime writes a per-request scratch directory, and the default
# `data/tools/scratch` resolves to /app/data — the FIFTH member of the family above (phase-21 D7,
# the node id, phase-30 D3, phase-38 D4). Set here so it is not rediscovered a sixth time.
#
# Inert unless Tools__Enabled=true, which is off by default. The directory is created per request
# and deleted in a finally, so nothing accumulates and a volume is not required for it — mount one
# only if a worker keeps model weights under /data.
ENV Tools__ScratchDirectory=/data/tools/scratch

USER app

ENTRYPOINT ["dotnet", "InferHub.Node.dll"]
