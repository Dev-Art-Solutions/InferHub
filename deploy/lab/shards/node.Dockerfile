# Phase 134: an InferHub node as the published image ships it, plus the lab's llama.cpp beside it — so the node's
# own shard worker (Node:Shards) has a ggml-rpc-server to supervise. The published node images carry no llama.cpp: a
# node uses the llama.cpp its owner installed (the Windows release and winget ship ggml-rpc-server.exe).
#
#   docker build -t inferhub-lab-llama:b12182 deploy/lab/shards                         # once (the lab's engine)
#   docker build -f deploy/lab/shards/node.Dockerfile --build-arg NODE=3.95.0 -t inferhub-lab-node:3.95.0 deploy/lab/shards
#   # or on a node image built from source:  --build-arg BASE=inferhub-node:dev
#
# Phase 135 adds iproute2 so netem.sh can shape a node's link (the container needs --cap-add NET_ADMIN).
# Not an image InferHub publishes: a lab fixture, like the rest of this directory.
ARG NODE=latest
ARG BASE=ghcr.io/dev-art-solutions/inferhub-node:${NODE}
ARG LLAMA=b12182
FROM inferhub-lab-llama:${LLAMA} AS llama

FROM ${BASE}
USER root
RUN apt-get update && apt-get install -y --no-install-recommends libgomp1 iproute2 && rm -rf /var/lib/apt/lists/*
COPY --from=llama /app /opt/llama
ENV LD_LIBRARY_PATH=/opt/llama
# The worker is found beside the node's llama-server (Backend:Engines:<name>:Serve:Executable) or named outright here.
ENV Node__Shards__Executable=/opt/llama/ggml-rpc-server
USER app
