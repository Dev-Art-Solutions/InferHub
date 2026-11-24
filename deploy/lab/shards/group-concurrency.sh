#!/usr/bin/env bash
# Phase 147: several requests at once through a group, via the hub — the product, not the bare engine (concurrency.sh).
# Starts tunnel-lab.sh with the given images (HUB_IMAGE, NODE_IMAGE) and its env (LAB_SLOT_WAIT, LAB_SLOTS, …), then sends K chats
# together for each K in $PAR_KS at each round trip in $PAR_RTTS, and writes results.jsonl, groups.json, models-slots.json and
# status.json beside them. Run once on the release before and once on this one, with the same knobs:
#
#   LAB_MODEL_DIR=… LAB_SLOT_WAIT=20 HUB_IMAGE=ghcr.io/dev-art-solutions/inferhub-coordinator:3.111.0 \
#     NODE_IMAGE=inferhub-lab-node:3.111.0 PAR_LABEL=v3.111.0 ./group-concurrency.sh
set -u
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"
: "${LAB_MODEL_DIR:?set LAB_MODEL_DIR to the directory holding the GGUF}"
RTTS=${PAR_RTTS:-0 120}
KS=${PAR_KS:-1 4}
REPS=${PAR_REPS:-2}
TOKENS=${PAR_TOKENS:-48}
GROUP=${LAB_GROUP:-llama-8b-split}
OUT=${LAB_OUT:-C:/tmp}/group-concurrency-${PAR_LABEL:-run}-$(date -u +%Y%m%dT%H%M%SZ); mkdir -p "$OUT"
echo "results: $OUT"
H="Authorization: Bearer adm135"

LAB_WAIT=${LAB_WAIT:-900} bash ./tunnel-lab.sh > "$OUT/log-lab.txt" 2>&1
if ! grep -q '"state":"running"' "$OUT/log-lab.txt"; then echo "the group did not reach running (see log-lab.txt)"; exit 1; fi
curl -s -H "$H" localhost:15080/api/admin/shards/groups > "$OUT/groups.json"
# What the hub believes the head runs at once for the group's name — the count 108/110 read.
curl -s -H "$H" localhost:15080/api/admin/nodes > "$OUT/nodes.json"
echo "  group: $(sed -n 's/.*"state":"\([a-z]*\)".*/\1/p' "$OUT/groups.json" | head -1), slots reported: $(grep -o '"slots":[0-9]*' "$OUT/groups.json" | head -1)"

for rtt in $RTTS; do
  for c in shard-a shard-b shard-c shard-d; do
    docker exec -u root "$c" sh -c "tc qdisc del dev eth0 root 2>/dev/null; [ $rtt = 0 ] || tc qdisc add dev eth0 root netem delay $(awk "BEGIN{print $rtt/2}")ms"
  done
  for k in $KS; do
    for rep in $(seq 1 "$REPS"); do
      line=$(node compare/burst.mjs --url http://127.0.0.1:15080 --chat --model "$GROUP" --bearer cli135 --k "$k" --n "$TOKENS" --prompt-file compare/prompts/short.txt)
      echo "{\"label\":\"${PAR_LABEL:-run}\",\"rtt_ms\":$rtt,\"rep\":$rep,${line#\{}" >> "$OUT/results.jsonl"
      echo "    rtt=$rtt k=$k #$rep $(echo "$line" | sed -n 's/.*"wall_ms":\([0-9]*\).*"aggregate_tps":\([0-9.]*\).*"ttft_ms":\(\[[^]]*\]\).*"same_text":\([a-z]*\).*/wall \1 ms, \2 tok\/s together, first tokens \3 ms, same text \4/p')"
    done
  done
done
curl -s -H "$H" localhost:15080/api/status > "$OUT/status.json"
echo "  hub slot wait: $(grep -o '"slotWait":{[^}]*}' "$OUT/status.json")"
for c in lab-hub shard-a shard-b shard-c shard-d; do docker logs "$c" > "$OUT/log-$c.txt" 2>&1; done
docker rm -f lab-hub shard-a shard-b shard-c shard-d >/dev/null 2>&1; docker network rm lab135 >/dev/null 2>&1
