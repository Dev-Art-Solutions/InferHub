#!/usr/bin/env bash
# Phase 141: measure before changing the shard engine. Runs the same prompts on the same GGUF through
#   whole            llama.cpp b12182, one container                     (the text every llama.cpp split must match)
#   rpc-star         llama.cpp RPC, a head and three remote workers      (the engine InferHub's groups run, bare)
#   inferhub-group   a group on the PUBLISHED InferHub images, via the hub (the baseline: tunnels, mTLS, the hub's /v1)
#   prima-whole      Prima.cpp at its pinned commit, one container       (the text a Prima.cpp ring must match)
#   prima-ring       Prima.cpp, four ranks in a ring                     (the candidate: one hop per boundary per token)
#   rpc-draft        rpc-star with a draft model on the head (146)       (LAB_DRAFT_DIR, LAB_DRAFT, LAB_DRAFT_MAX)
#   inferhub-group-draft  inferhub-group with ShardGroups:<n>:Draft (146)  (LAB_DRAFT, a file beside the model)
#   COMPARE_HUB_IMAGE / COMPARE_NODE_IMAGE   other images for the two group configurations (e.g. built from source)
# at each round trip in $COMPARE_RTTS, $COMPARE_REPS times per prompt, timed by one client (compare/ask.mjs), and writes
# results.jsonl, load.jsonl, memory.jsonl, faults.jsonl, skips.jsonl, meta.json and summary.{md,json} to a new directory.
# A candidate that cannot run on these weights is a SKIP with its reason, never a missing row.
#
#   LAB_MODEL_DIR=D:/models ./compare-engines.sh                 # exit 0 only when every required gate held
#   COMPARE_REVISION=<commit>   also require the InferHub images to be built from that commit
#   INFERHUB_TAG=3.100.0        the published release that is the baseline
#   COMPARE_ONLY="whole rpc-star"   run a subset · COMPARE_FAULTS=0 skips the cancel / worker-loss observations
#
# Needs: docker, curl, node; inferhub-lab-llama:b12182 (docker build -t inferhub-lab-llama:b12182 .) and, for the
# candidate, inferhub-lab-prima:288df6a (docker build -f candidates/prima.Dockerfile -t inferhub-lab-prima:288df6a candidates).
# What this cannot show is in docs/research/distributed-inference-decision.md: one host's cores, no GPU, netem delay only.
set -u
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"
: "${LAB_MODEL_DIR:?set LAB_MODEL_DIR to the directory holding the GGUF}"
export LAB_MODEL_DIR
MODEL=${LAB_MODEL:-llama31-8b.gguf}
TAG=${INFERHUB_TAG:-3.100.0}
RTTS=${COMPARE_RTTS:-0 10 30}
REPS=${COMPARE_REPS:-3}
TOKENS=${COMPARE_TOKENS:-48}
ONLY=${COMPARE_ONLY:-whole rpc-star inferhub-group prima-whole prima-ring}
FAULTS=${COMPARE_FAULTS:-1}
LLAMA_IMAGE=inferhub-lab-llama:b12182
PRIMA_IMAGE=${PRIMA_IMAGE:-inferhub-lab-prima:288df6a}
PRIMA_LAYERS=${PRIMA_LAYERS:-8,8,8,8}
REGISTRY=ghcr.io/dev-art-solutions
OUT=${LAB_OUT:-C:/tmp}/compare-$(date -u +%Y%m%dT%H%M%SZ); mkdir -p "$OUT"
echo "results: $OUT"
wanted() { case " $ONLY " in *" $1 "*) return 0 ;; esac; return 1; }
now() { date +%s.%N; }

# The bytes every container sent and the CPU time it used, summed: read before and after a request.
snap() {
  local tx=0 cpu=0 c t u
  for c in "$@"; do
    read -r t u <<<"$(docker exec "$c" sh -c 'cat /sys/class/net/eth0/statistics/tx_bytes; sed -n "s/^usage_usec //p" /sys/fs/cgroup/cpu.stat' 2>/dev/null | tr '\n' ' ')"
    tx=$((tx + ${t:-0})); cpu=$((cpu + ${u:-0}))
  done
  echo "$tx $cpu"
}
memory() { # memory <config> <containers…>: the peak the kernel recorded (page cache included) and what is anonymous now
  local config=$1 c; shift
  for c in "$@"; do
    docker exec "$c" sh -c 'echo "$(cat /sys/fs/cgroup/memory.peak) $(sed -n "s/^anon //p" /sys/fs/cgroup/memory.stat) $(sed -n "s/^file //p" /sys/fs/cgroup/memory.stat)"' 2>/dev/null \
      | { read -r peak anon file; echo "{\"config\":\"$config\",\"container\":\"$c\",\"peak_bytes\":${peak:-null},\"anon_bytes\":${anon:-null},\"file_bytes\":${file:-null}}" >> "$OUT/memory.jsonl"; }
  done
}
shape() { # shape <round trip ms> <containers…>: half of it on every container's way out, delay only
  local rtt=$1 c; shift
  for c in "$@"; do
    docker exec -u root "$c" sh -c "tc qdisc del dev eth0 root 2>/dev/null; [ $rtt = 0 ] || tc qdisc add dev eth0 root netem delay $(awk "BEGIN{print $rtt/2}")ms" || echo "cannot shape $c" >&2
  done
}
healthy() { # healthy <url> <seconds>
  local i; for i in $(seq 1 "$2"); do [ "$(curl -s -o /dev/null -w '%{http_code}' "$1/health")" = 200 ] && return 0; sleep 1; done; return 1
}
loaded() { echo "{\"config\":\"$1\",\"load_s\":$2,\"load_tx_bytes\":$3,\"note\":\"$4\"}" >> "$OUT/load.jsonl"; }
skip() { echo "  SKIP  $1: $2"; echo "{\"config\":\"$1\",\"reason\":\"$2\"}" >> "$OUT/skips.jsonl"; }

# matrix <config> <url> <mode> <model> <bearer> <header> <rtts> <ping from> <ping to> <containers…>
matrix() {
  local config=$1 url=$2 mode=$3 model=$4 bearer=$5 header=$6 rtts=$7 from=$8 to=$9; shift 9
  local rtt prompt rep before after line ping extra=()
  [ -n "$model" ] && extra+=(--model "$model"); [ -n "$bearer" ] && extra+=(--bearer "$bearer"); [ -n "$header" ] && extra+=(--header "$header")
  for rtt in $rtts; do
    shape "$rtt" "$@"
    ping=null
    [ -n "$from" ] && ping=$(docker exec "$from" ping -c 3 -q "$to" 2>/dev/null | sed -n 's|.*= [0-9.]*/\([0-9.]*\)/.*|\1|p'); ping=${ping:-null}
    for prompt in short long; do
      for rep in $(seq 1 "$REPS"); do
        # A hub offers no cache_prompt switch, and llama-server keeps earlier prompts: the long prompt gets this run's own
        # first words, so its first token is never an earlier run's. The short one stays as it is — it is the text check.
        own=(); [ "$mode" = chat ] && [ "$prompt" = long ] && own=(--prefix "[$config $rtt-$rep $RANDOM] ")
        before=$(snap "$@")
        line=$(node compare/ask.mjs --url "$url" --mode "$mode" "${extra[@]}" "${own[@]}" --prompt-file "compare/prompts/$prompt.txt" --n "$TOKENS")
        after=$(snap "$@")
        echo "{\"config\":\"$config\",\"rtt_ms\":$rtt,\"ping_ms\":$ping,\"prompt\":\"$prompt\",\"rep\":$rep,\"net_bytes\":$((${after% *} - ${before% *})),\"cpu_usec\":$((${after#* } - ${before#* })),${line#\{}" >> "$OUT/results.jsonl"
        echo "    $config rtt=$rtt $prompt #$rep $(echo "$line" | sed -n 's/.*"total_ms":\([0-9]*\).*"ttft_ms":\([0-9]*\).*"decode_tps":\([0-9.]*\).*/ttft \2 ms, \3 tok\/s/p')"
      done
    done
  done
  shape 0 "$@"
  memory "$config" "$@"
}

# faults <config> <url> <head> <victim> <containers…>: what the engine does on a cancel and on a lost worker. Observed, not gated.
faults() {
  local config=$1 url=$2 head=$3 victim=$4; shift 4
  local before after busy line running
  node compare/ask.mjs --url "$url" --mode completion --prompt-file compare/prompts/short.txt --n 400 --abort-after 5 >/dev/null
  sleep 2; before=$(snap "$@"); sleep 5; after=$(snap "$@")
  busy=$(awk "BEGIN{printf \"%.2f\", (${after#* } - ${before#* })/5000000}")
  docker kill "$victim" >/dev/null 2>&1; sleep 3
  line=$(node compare/ask.mjs --url "$url" --mode completion --prompt-file compare/prompts/short.txt --n 8 --timeout 60)
  running=$(docker inspect -f '{{.State.Running}}' "$head" 2>/dev/null || echo false)
  echo "{\"config\":\"$config\",\"cpus_busy_5s_after_cancel\":$busy,\"after_worker_loss\":{\"head_running\":$running,\"answer\":$line}}" >> "$OUT/faults.jsonl"
  echo "    $config: $busy CPUs busy after a cancel; a worker killed -> head running=$running, $(echo "$line" | cut -c1-60)"
}

sha=$(sha256sum "$LAB_MODEL_DIR/$MODEL" 2>/dev/null | cut -d' ' -f1)
[ -n "$sha" ] || { echo "no $LAB_MODEL_DIR/$MODEL"; exit 2; }
# One line or "missing": an image that is not there must not put docker's error into meta.json.
image_id() { local id; id=$(docker image inspect -f '{{.Id}}' "$1" 2>/dev/null | head -1 | tr -d '
'); echo "${id:-missing}"; }
label() { docker image inspect -f "{{index .Config.Labels \"org.opencontainers.image.$2\"}}" "$1" 2>/dev/null | head -1 | tr -d '
'; }

if wanted whole; then
  echo "== whole: llama.cpp b12182 in one container"
  docker rm -f cmp-whole >/dev/null 2>&1; t0=$(now)
  docker run -d --name cmp-whole --cap-add NET_ADMIN --cpus 4 -v "$LAB_MODEL_DIR:/models:ro" -p 127.0.0.1:18081:8080 "$LLAMA_IMAGE" \
    llama-server --host 0.0.0.0 --port 8080 -m "/models/$MODEL" -c 4096 -t 4 --no-warmup --alias whole >/dev/null
  if healthy http://127.0.0.1:18081 300; then
    loaded whole "$(awk "BEGIN{print $(now)-$t0}")" 0 "one container, the file from a read-only mount"
    matrix whole http://127.0.0.1:18081 completion "" "" "" 0 "" "" cmp-whole
    matrix whole-chat http://127.0.0.1:18081 chat whole "" "" 0 "" "" cmp-whole
  else skip whole "llama-server did not answer /health in 300 s"; fi
  docker logs cmp-whole > "$OUT/log-whole.txt" 2>&1; docker rm -f cmp-whole >/dev/null 2>&1
fi

if wanted rpc-star; then
  echo "== rpc-star: llama.cpp RPC, a head and three remote workers"
  docker compose down >/dev/null 2>&1; t0=$(now)
  docker compose up -d >/dev/null 2>&1
  P=inferhub-shard-lab; set -- $P-head-1 $P-worker1-1 $P-worker2-1 $P-worker3-1
  if healthy http://127.0.0.1:18080 600; then
    tx=$(snap "$@"); loaded rpc-star "$(awk "BEGIN{print $(now)-$t0}")" "${tx% *}" "the head sends every worker its layers"
    matrix rpc-star http://127.0.0.1:18080 completion "" "" "" "$RTTS" $P-head-1 worker1 "$@"
    [ "$FAULTS" = 1 ] && faults rpc-star http://127.0.0.1:18080 $P-head-1 $P-worker2-1 "$@"
  else skip rpc-star "the head did not answer /health in 600 s"; fi
  docker compose logs > "$OUT/log-rpc-star.txt" 2>&1; docker compose down >/dev/null 2>&1
fi

# Phase 146: the same star with a draft model on the head ($LAB_DRAFT_DIR/$LAB_DRAFT, run on the head's CPU).
if wanted rpc-draft; then
  echo "== rpc-draft: the same star, a draft model (${LAB_DRAFT:-llama32-1b.gguf}) on the head, up to ${LAB_DRAFT_MAX:-8} tokens a pass"
  if [ -z "${LAB_DRAFT_DIR:-}" ] || [ ! -f "${LAB_DRAFT_DIR:-}/${LAB_DRAFT:-llama32-1b.gguf}" ]; then
    skip rpc-draft "no draft model at \$LAB_DRAFT_DIR/\$LAB_DRAFT"
  else
    docker compose down >/dev/null 2>&1; t0=$(now)
    docker compose -f compose.yaml -f compose.draft.yaml up -d >/dev/null 2>&1
    P=inferhub-shard-lab; set -- $P-head-1 $P-worker1-1 $P-worker2-1 $P-worker3-1
    if healthy http://127.0.0.1:18080 600; then
      tx=$(snap "$@"); loaded rpc-draft "$(awk "BEGIN{print $(now)-$t0}")" "${tx% *}" "as rpc-star, plus the draft read from the head's own disk"
      matrix rpc-draft http://127.0.0.1:18080 completion "" "" "" "$RTTS" $P-head-1 worker1 "$@"
      [ "$FAULTS" = 1 ] && faults rpc-draft http://127.0.0.1:18080 $P-head-1 $P-worker2-1 "$@"
    else skip rpc-draft "the head did not answer /health in 600 s"; fi
    docker compose -f compose.yaml -f compose.draft.yaml logs > "$OUT/log-rpc-draft.txt" 2>&1; docker compose -f compose.yaml -f compose.draft.yaml down >/dev/null 2>&1
  fi
fi

HUB_IMAGE=${COMPARE_HUB_IMAGE:-$REGISTRY/inferhub-coordinator:$TAG}; NODE_IMAGE=${COMPARE_NODE_IMAGE:-inferhub-lab-node:$TAG}
# group <config> <draft file or empty>: a group through the hub (135–137); 146's draft is the same group with ShardGroups:<n>:Draft.
group() {
  local config=$1 draft=$2
  echo "== $config: the $TAG images, a group through the hub${draft:+, a draft ($draft) on the head}"
  if [ -n "$draft" ] && [ ! -f "$LAB_MODEL_DIR/$draft" ]; then skip "$config" "no $draft beside the model in $LAB_MODEL_DIR"; return; fi
  docker image inspect "$HUB_IMAGE" >/dev/null 2>&1 || docker pull -q "$HUB_IMAGE" >/dev/null
  docker image inspect "$REGISTRY/inferhub-node:$TAG" >/dev/null 2>&1 || docker pull -q "$REGISTRY/inferhub-node:$TAG" >/dev/null
  docker image inspect "$NODE_IMAGE" >/dev/null 2>&1 || docker build -q -f node.Dockerfile --build-arg "NODE=$TAG" -t "$NODE_IMAGE" . >/dev/null
  t0=$(now)
  HUB_IMAGE=$HUB_IMAGE NODE_IMAGE=$NODE_IMAGE LAB_DRAFT=$draft bash ./tunnel-lab.sh > "$OUT/log-$config.txt" 2>&1
  set -- shard-a shard-b shard-c shard-d
  if grep -q '"state":"running"' "$OUT/log-$config.txt"; then
    tx=$(snap "$@"); loaded "$config" "$(awk "BEGIN{print $(now)-$t0}")" "${tx% *}" "includes the hub start, four enrollments and the lab's two warm-up chats"
    matrix "$config" http://127.0.0.1:15080 chat llama-8b-split cli135 "" "$RTTS" "" "" "$@"
    curl -s -H "Authorization: Bearer adm135" localhost:15080/api/admin/nodes > "$OUT/$config-nodes.json"
    curl -s -H "Authorization: Bearer adm135" localhost:15080/api/admin/shards/groups > "$OUT/$config-groups.json"
  else skip "$config" "the group did not reach running (see log-$config.txt)"; fi
  for c in lab-hub "$@"; do docker logs "$c" > "$OUT/log-$config-$c.txt" 2>&1; done
  docker rm -f lab-hub "$@" >/dev/null 2>&1; docker network rm lab135 >/dev/null 2>&1
}
wanted inferhub-group && group inferhub-group ""
wanted inferhub-group-draft && group inferhub-group-draft "${LAB_DRAFT:-llama32-1b.gguf}"

if wanted prima-whole || wanted prima-ring; then
  if ! docker image inspect "$PRIMA_IMAGE" >/dev/null 2>&1; then
    skip prima-whole "$PRIMA_IMAGE is not built (candidates/prima.Dockerfile)"; skip prima-ring "$PRIMA_IMAGE is not built (candidates/prima.Dockerfile)"
  else
    if wanted prima-whole; then
      echo "== prima-whole: Prima.cpp 288df6a in one container"
      docker rm -f cmp-prima-whole >/dev/null 2>&1; t0=$(now)
      docker run -d --name cmp-prima-whole --cap-add NET_ADMIN --cpus 4 -v "$LAB_MODEL_DIR:/models:ro" -p 127.0.0.1:18082:8080 "$PRIMA_IMAGE" \
        llama-server --host 0.0.0.0 --port 8080 -m "/models/$MODEL" -c 4096 -t 4 >/dev/null
      if healthy http://127.0.0.1:18082 300; then
        loaded prima-whole "$(awk "BEGIN{print $(now)-$t0}")" 0 "one container, the file from a read-only mount"
        matrix prima-whole http://127.0.0.1:18082 completion "" "" "" 0 "" "" cmp-prima-whole
      else skip prima-whole "llama-server did not answer /health in 300 s"; fi
      docker logs cmp-prima-whole > "$OUT/log-prima-whole.txt" 2>&1; docker rm -f cmp-prima-whole >/dev/null 2>&1
    fi
    if wanted prima-ring; then
      echo "== prima-ring: Prima.cpp 288df6a, four ranks, layers $PRIMA_LAYERS"
      docker rm -f prima0 prima1 prima2 prima3 >/dev/null 2>&1; docker network rm cmp-prima >/dev/null 2>&1
      docker network create cmp-prima >/dev/null; t0=$(now)
      common=(--network cmp-prima --cap-add NET_ADMIN --cpus 4 --memory "${LAB_WORKER_MEMORY:-2560m}" -v "$LAB_MODEL_DIR:/models:ro")
      docker run -d --name prima0 "${common[@]}" -p 127.0.0.1:18083:8080 "$PRIMA_IMAGE" llama-server --host 0.0.0.0 --port 8080 \
        -m "/models/$MODEL" -c 4096 -t 4 --world 4 --rank 0 --master prima0 --next prima1 --prefetch -lw "$PRIMA_LAYERS" >/dev/null
      for r in 1 2 3; do
        docker run -d --name "prima$r" "${common[@]}" "$PRIMA_IMAGE" llama-cli -m "/models/$MODEL" -t 4 \
          --world 4 --rank "$r" --master prima0 --next "prima$(( (r + 1) % 4 ))" --prefetch >/dev/null
      done
      set -- prima0 prima1 prima2 prima3
      if healthy http://127.0.0.1:18083 300; then
        tx=$(snap "$@"); loaded prima-ring "$(awk "BEGIN{print $(now)-$t0}")" "${tx% *}" "every rank reads the file itself"
        matrix prima-ring http://127.0.0.1:18083 completion "" "" "" "$RTTS" prima0 prima1 "$@"
        [ "$FAULTS" = 1 ] && faults prima-ring http://127.0.0.1:18083 prima0 prima2 "$@"
      else skip prima-ring "rank 0 did not answer /health in 300 s (see log-prima*.txt)"; fi
      for c in "$@"; do docker logs "$c" > "$OUT/log-$c.txt" 2>&1; done
      docker rm -f "$@" >/dev/null 2>&1; docker network rm cmp-prima >/dev/null 2>&1
    fi
  fi
fi

# The candidates that cannot be put beside the baseline on these weights. The reasons are from their source at the
# pinned commits, argued in docs/research/distributed-inference-engines.md.
skip exo "a60b956: the only engine is MLX and it loads MLX safetensors, not GGUF - no run on the same weights and quantization exists"
skip nakshatra "373a17d: a patched llama.cpp b9992 and per-worker sub-GGUFs cut from the model - GGUF-compatible, not built in this run (the mechanism it shares with Prima.cpp is the one measured)"

cat > "$OUT/meta.json" <<EOF
{"date":"$(date -u +%Y-%m-%dT%H:%M:%SZ)","inferhub_commit":"$(git rev-parse HEAD 2>/dev/null)","inferhub_tag":"$TAG",
 "hub_image":"$HUB_IMAGE","hub_image_id":"$(image_id "$HUB_IMAGE")","hub_revision":"$(label "$HUB_IMAGE" revision)",
 "node_image":"$REGISTRY/inferhub-node:$TAG","node_image_id":"$(image_id "$REGISTRY/inferhub-node:$TAG")","node_revision":"$(label "$REGISTRY/inferhub-node:$TAG" revision)",
 "llama_image":"$LLAMA_IMAGE","llama_image_id":"$(image_id "$LLAMA_IMAGE")","llama_commit":"fd27b2034905f8dadd8bd6d2cc44595f4929d391",
 "prima_image":"$PRIMA_IMAGE","prima_image_id":"$(image_id "$PRIMA_IMAGE")","prima_commit":"288df6a2cc2120387e12d6f40798f30cc525c9ea","prima_layers":"$PRIMA_LAYERS",
 "model":"$MODEL","model_sha256":"$sha","model_bytes":$(stat -c %s "$LAB_MODEL_DIR/$MODEL"),
 "rtts_ms":"$RTTS","reps":$REPS,"tokens":$TOKENS,"cpus_per_container":4,"worker_memory":"${LAB_WORKER_MEMORY:-2560m}","node_memory":"4g",
 "host_cpus":$(docker info -f '{{.NCPU}}'),"host_memory_bytes":$(docker info -f '{{.MemTotal}}'),"docker":"$(docker version -f '{{.Server.Version}}')",
 "expect_revision":"${COMPARE_REVISION:-}","only":"$ONLY"}
EOF
node compare/report.mjs "$OUT"
