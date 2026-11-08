# The shard lab's engine: llama.cpp built with its RPC backend, CPU only.
# The official ghcr.io/ggml-org/llama.cpp images are built without GGML_RPC (checked on b12182), so the lab
# builds its own. One build for every container in the lab: llama.cpp's RPC protocol refuses a peer of another build.
FROM debian:bookworm-slim AS build
ARG LLAMA_REF=b12182
RUN apt-get update && apt-get install -y --no-install-recommends build-essential cmake git ca-certificates libcurl4-openssl-dev \
 && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch ${LLAMA_REF} https://github.com/ggml-org/llama.cpp /src
WORKDIR /src
RUN cmake -B build -DCMAKE_BUILD_TYPE=Release -DGGML_RPC=ON -DGGML_NATIVE=OFF -DLLAMA_BUILD_TESTS=OFF -DLLAMA_CURL=OFF \
 && cmake --build build -j --target llama-server llama-cli rpc-server ggml-rpc-server 2>/dev/null \
 || cmake --build build -j
RUN mkdir -p /out && cp build/bin/* /out/ && find build -name "*.so*" -exec cp {} /out/ \;

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends iproute2 iputils-ping curl libgomp1 ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out /app
ENV LD_LIBRARY_PATH=/app PATH=/app:$PATH
WORKDIR /app
