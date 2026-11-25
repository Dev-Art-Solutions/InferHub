#!/usr/bin/env bash
# Phase 148: who gave how much to a group. On top of tunnel-lab.sh (head shard-a, members shard-b and shard-c, spare shard-d):
# a few chats through the hub, then shard-c stops and the spare stands in, a few more — and GET /api/admin/shards/contribution
# after each part. shard-c keeps what it gave (connected: false); shard-d starts giving only once it stands in.
#
#   LAB_MODEL_DIR=lab-models ./contribution-lab.sh          # HUB_IMAGE / NODE_IMAGE as tunnel-lab.sh
#   LAB_CHATS=4 …                                           # chats in each part (default 4)
#   LAB_OUT=<dir> …                                         # where the three reads are written (default ./runs/contribution)
set -u
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"
GROUP=${LAB_GROUP:-llama-8b-split}
OUT=${LAB_OUT:-runs/contribution}
H="Authorization: Bearer adm135"
mkdir -p "$OUT"

./tunnel-lab.sh > "$OUT/tunnel-lab.log" 2>&1
read_contribution() { curl -s -H "$H" localhost:15080/api/admin/shards/contribution > "$OUT/$1.json"; cat "$OUT/$1.json"; echo; }
chats() {
  for i in $(seq 1 "${LAB_CHATS:-4}"); do
    curl -s -o /dev/null -w "chat $i: %{http_code} %{time_total}s\n" -H "Authorization: Bearer cli135" -H "Content-Type: application/json" \
      -d "{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"Name one idea in distributed computing.\"}],\"max_tokens\":32,\"temperature\":0}" \
      localhost:15080/v1/chat/completions
  done
}
running() {
  for _ in $(seq 1 $(( ${LAB_WAIT:-300} / 2 ))); do
    curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -q '"state":"running"' && return 0; sleep 2
  done
  return 1
}

echo "== after start"; sleep 12; read_contribution 1-started
echo "== ${LAB_CHATS:-4} chats"; chats; sleep 12; read_contribution 2-chats
echo "== shard-c stops; the spare stands in"; docker stop shard-c >/dev/null
# Phase 149: a planned stop is a clean exit (3.113.0 ended it in an unhandled ObjectDisposedException, exit 139).
echo "shard-c stopped: exit code $(docker inspect -f '{{.State.ExitCode}}' shard-c)"
docker logs shard-c 2>&1 | grep -m1 "Unhandled exception" || true
t0=$(date +%s)
if sleep 5 && running; then echo "group running again after $(( $(date +%s) - t0 )) s"; else
  # 148's first published run saw this once and the lab was gone before anyone looked: keep what the containers said.
  echo "group not running after $(( $(date +%s) - t0 )) s"
  for c in lab-hub shard-a shard-b shard-d; do docker logs "$c" > "$OUT/log-$c.txt" 2>&1; done
fi
curl -s -H "$H" localhost:15080/api/admin/shards/groups > "$OUT/groups-after-stop.json"
chats; sleep 12; read_contribution 3-spare
