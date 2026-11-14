# Phase 141: Prima.cpp at a pinned commit, CPU only — the one candidate engine that reads the lab's GGUF as it is.
# A lab fixture for compare-engines.sh; nothing InferHub ships or links (MIT; see docs/research/distributed-inference-engines.md).
#
#   docker build -f candidates/prima.Dockerfile -t inferhub-lab-prima:288df6a candidates
#
# Its Makefile compiles with -march=native, the lab's llama.cpp does not (GGML_NATIVE=OFF): compare-engines.sh therefore
# reads each engine against ITS OWN whole-model run, never one engine's absolute tok/s against the other's.
FROM debian:bookworm-slim AS build
ARG PRIMA_REF=288df6a2cc2120387e12d6f40798f30cc525c9ea
ARG HIGHS_REF=v1.9.0
RUN apt-get update && apt-get install -y --no-install-recommends build-essential cmake git ca-certificates \
      libzmq3-dev cppzmq-dev libcurl4-openssl-dev \
 && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch ${HIGHS_REF} https://github.com/ERGO-Code/HiGHS /highs \
 && cmake -S /highs -B /highs/build -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF -DBUILD_EXAMPLES=OFF \
 && cmake --build /highs/build -j && cmake --install /highs/build && ldconfig
RUN git init -q /src && cd /src && git remote add origin https://github.com/OpenCPIL/prima.cpp \
 && git fetch -q --depth 1 origin ${PRIMA_REF} && git checkout -q FETCH_HEAD
WORKDIR /src
RUN make USE_HIGHS=1 -j"$(nproc)" llama-cli llama-server
RUN mkdir -p /out && cp llama-cli llama-server /out/ && cp -a /usr/local/lib/libhighs.so* /out/

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends iproute2 iputils-ping curl libgomp1 libzmq5 fio ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out /app
ENV LD_LIBRARY_PATH=/app PATH=/app:$PATH
WORKDIR /app
