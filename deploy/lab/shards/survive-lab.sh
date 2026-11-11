#!/usr/bin/env bash
# Phase 137 on top of tunnel-lab.sh (run that first): a group survives a member leaving.
#   1. revoke shard-c → the spare shard-d stands in; every request in the gap gets a 503 with the reason and Retry-After,
#      then the group answers again with the same text;
#   2. shard-b's owner pauses it while a long request is in flight → the head drains: that request completes, then the
#      group stops (no spare left) and the next request gets a 503 that names the pause.
set -u
export MSYS_NO_PATHCONV=1
GROUP=${LAB_GROUP:-llama-8b-split}
H="Authorization: Bearer adm135"
C="Authorization: Bearer cli135"
# curl.exe from Git Bash cannot write a POSIX path: the scratch files live where both can read them.
OUT=${LAB_OUT:-C:/tmp}; mkdir -p "$OUT" 2>/dev/null
Q="{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"Name the three most important ideas in distributed computing.\"}],\"max_tokens\":64,\"temperature\":0,\"seed\":1}"

id_of() { curl -s -H "$H" localhost:15080/api/admin/nodes | grep -o "\"nodeId\":\"[^\"]*\",\"name\":\"$1\"" | head -1 | sed 's/"nodeId":"\([^"]*\)".*/\1/'; }
ask() { curl -s -o $OUT/answer.json -w "%{http_code}" -H "$C" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$1"; }

echo "== the group, and who holds which layers"
curl -s -H "$H" localhost:15080/api/admin/nodes | grep -o '"layers":\[[^]]*\]' | head -1
before=$(curl -s -H "$C" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$Q" | grep -o '"content":"[^"]*"')

echo "== 1. revoke shard-c"
curl -s -o /dev/null -X POST -H "$H" -H "Content-Type: application/json" -d '{"reason":"lab: a member leaves"}' \
  "localhost:15080/api/admin/identity/nodes/$(id_of shard-c)/revoke"
start=$(date +%s)
while true; do
  code=$(curl -s -o $OUT/gap.json -D $OUT/gap.h -w "%{http_code}" -H "$C" -H "Content-Type: application/json" localhost:15080/v1/chat/completions \
    -d "{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":4}")
  echo "t+$(( $(date +%s) - start ))s $code $(grep -io '^retry-after: [0-9]*' $OUT/gap.h | tr -d '\r') $(grep -o '"message":"[^"]*"' $OUT/gap.json)"
  [ "$code" = 200 ] && break
  [ $(( $(date +%s) - start )) -gt 300 ] && { echo "gave up after 300 s"; break; }
  sleep 3
done
after=$(curl -s -H "$C" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$Q" | grep -o '"content":"[^"]*"')
[ "$before" = "$after" ] && echo "SAME TEXT after reassembly" || { echo "DIFFERENT TEXT"; echo "$before"; echo "$after"; }
curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -o '"substitutions":\[[^]]*\]'
curl -s -H "$H" localhost:15080/api/admin/nodes | grep -o '"layers":\[[^]]*\]' | head -1

echo "== 2. shard-b's owner pauses it during a long request"
LONG="{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"Write a long essay about distributed computing.\"}],\"max_tokens\":200,\"temperature\":0}"
( code=$(ask "$LONG"); echo "long request in flight: HTTP $code, $(grep -o '"completion_tokens":[0-9]*' $OUT/answer.json)" ) &
long=$!
sleep 4
token=$(docker exec shard-b cat /data/control.token)
docker run --rm --network container:shard-b curlimages/curl:8.11.1 -s -o /dev/null -w "pause: HTTP %{http_code}\n" -X PUT \
  -H "Authorization: Bearer $token" -H "Content-Type: application/json" localhost:8080/api/control/sharing -d '{"mode":"paused"}'
sleep 2
curl -s -H "$H" localhost:15080/api/admin/nodes | grep -o '"name":"[^"]*","model":"[^"]*","state":"[a-z]*"' | head -2
wait $long
sleep 3
code=$(curl -s -o $OUT/after.json -w "%{http_code}" -H "$C" -H "Content-Type: application/json" localhost:15080/v1/chat/completions \
  -d "{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":4}")
echo "next request: $code $(grep -o '"message":"[^"]*"' $OUT/after.json)"
