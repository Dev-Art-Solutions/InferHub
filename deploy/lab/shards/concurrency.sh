#!/usr/bin/env bash
# Phase 147: does a split model serve several requests at once, and what does it buy? The bare engine first (llama.cpp RPC,
# a head and three workers — compose.yaml), for each slot count in $PAR_SLOTS, at each round trip in $PAR_RTTS, with K
# requests sent together for each K in $PAR_KS, $PAR_REPS times. Writes results.jsonl, props.jsonl and summary.md.
#
#   LAB_MODEL_DIR=D:/tmp118/gguf/llama31-8b ./concurrency.sh
#   PAR_SLOTS="1 4" PAR_RTTS="0 30 120" PAR_KS="1 2 4" PAR_REPS=2 PAR_TOKENS=64
#
# The question a token answers: one pass of a split model costs a fixed number of round trips (133 D3) whatever the batch,
# so K sequences in one pass should cost about what one does — the round trip is paid once for all of them. A batch that
# changes a request's text at temperature 0 is recorded (same_text), never smoothed over.
set -u
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"
: "${LAB_MODEL_DIR:?set LAB_MODEL_DIR to the directory holding the GGUF}"
export LAB_MODEL_DIR
SLOTS=${PAR_SLOTS:-1 4}
RTTS=${PAR_RTTS:-0 30 120}
KS=${PAR_KS:-1 2 4}
REPS=${PAR_REPS:-2}
TOKENS=${PAR_TOKENS:-64}
OUT=${LAB_OUT:-C:/tmp}/concurrency-$(date -u +%Y%m%dT%H%M%SZ); mkdir -p "$OUT"
echo "results: $OUT"
P=inferhub-shard-lab
set -- $P-head-1 $P-worker1-1 $P-worker2-1 $P-worker3-1

shape() { # shape <round trip ms>: half of it on every container's way out, delay only (141's shaping)
  local c
  for c in $P-head-1 $P-worker1-1 $P-worker2-1 $P-worker3-1; do
    docker exec -u root "$c" sh -c "tc qdisc del dev eth0 root 2>/dev/null; [ $1 = 0 ] || tc qdisc add dev eth0 root netem delay $(awk "BEGIN{print $1/2}")ms" || echo "cannot shape $c" >&2
  done
}
healthy() { local i; for i in $(seq 1 "$2"); do [ "$(curl -s -o /dev/null -w '%{http_code}' "$1/health")" = 200 ] && return 0; sleep 1; done; return 1; }

for slots in $SLOTS; do
  echo "== the star with -np $slots"
  docker compose -f compose.yaml -f compose.parallel.yaml down >/dev/null 2>&1
  LAB_PARALLEL=$slots docker compose -f compose.yaml -f compose.parallel.yaml up -d >/dev/null 2>&1
  if ! healthy http://127.0.0.1:18080 900; then echo "  the head did not answer /health in 900 s"; continue; fi
  props=$(curl -s http://127.0.0.1:18080/props)
  echo "{\"np\":$slots,\"total_slots\":$(echo "$props" | sed -n 's/.*"total_slots":\([0-9]*\).*/\1/p'),\"n_ctx\":$(echo "$props" | sed -n 's/.*"n_ctx":\([0-9]*\).*/\1/p' | head -1)}" | tee -a "$OUT/props.jsonl"
  node compare/burst.mjs --url http://127.0.0.1:18080 --k 1 --n 8 --prompt-file compare/prompts/short.txt >/dev/null # warm
  for rtt in $RTTS; do
    shape "$rtt"
    for k in $KS; do
      for rep in $(seq 1 "$REPS"); do
        line=$(node compare/burst.mjs --url http://127.0.0.1:18080 --k "$k" --n "$TOKENS" --prompt-file compare/prompts/short.txt)
        echo "{\"np\":$slots,\"rtt_ms\":$rtt,\"rep\":$rep,${line#\{}" >> "$OUT/results.jsonl"
        echo "    np=$slots rtt=$rtt k=$k #$rep $(echo "$line" | sed -n 's/.*"wall_ms":\([0-9]*\).*"aggregate_tps":\([0-9.]*\).*"same_text":\([a-z]*\).*/wall \1 ms, \2 tok\/s together, same text \3/p')"
      done
    done
  done
  shape 0
  docker compose -f compose.yaml -f compose.parallel.yaml logs head > "$OUT/log-head-np$slots.txt" 2>&1
done
docker compose -f compose.yaml -f compose.parallel.yaml down >/dev/null 2>&1

node -e '
const fs = require("fs"); const dir = process.argv[1];
const rows = fs.readFileSync(dir + "/results.jsonl", "utf8").trim().split("\n").map(JSON.parse);
const key = r => `${r.np}|${r.rtt_ms}|${r.k}`; const groups = {};
for (const r of rows) (groups[key(r)] ??= []).push(r);
const median = xs => { const s = [...xs].sort((a, b) => a - b); return s[Math.floor((s.length - 1) / 2)]; };
const base = {}; for (const [k, rs] of Object.entries(groups)) { const [np, rtt, kk] = k.split("|"); if (kk === "1") base[`${np}|${rtt}`] = median(rs.map(r => r.aggregate_tps)); }
const single = Object.fromEntries(rows.filter(r => r.k === 1).map(r => [`${r.np}|${r.rtt_ms}`, r.texts[0]]));
let md = "| -np | round trip | K together | tok/s together (median) | × one request | slowest request ends | same text as one |\n|---|---|---|---|---|---|---|\n";
for (const [k, rs] of Object.entries(groups)) {
  const [np, rtt, kk] = k.split("|"); const tps = median(rs.map(r => r.aggregate_tps));
  const same = rs.every(r => r.ok && r.same_text && r.texts[0] === single[`${np}|${rtt}`]);
  md += `| ${np} | ${rtt} ms | ${kk} | ${tps.toFixed(2)} | ${(tps / base[`${np}|${rtt}`]).toFixed(2)}× | ${(median(rs.map(r => r.wall_ms)) / 1000).toFixed(1)} s | ${same ? "yes" : "**no**"} |\n`;
}
fs.writeFileSync(dir + "/summary.md", md); console.log(md);
' "$OUT"
