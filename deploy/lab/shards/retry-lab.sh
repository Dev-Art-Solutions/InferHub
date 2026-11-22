#!/usr/bin/env bash
# Phase 145 on top of tunnel-lab.sh (run that first, with LAB_RETRY=180 and LAB_MEMBER_DISK=1): a request survives a member
# vanishing, and the member takes its place back.
#   1. a long blocking request is running when shard-c is killed → llama.cpp takes the head down; the request waits at the hub
#      while shard-d stands in, is sent again, and answers 200 with the same text as before the kill;
#   2. a request sent while the group reassembles waits too — 200, not 137's 503;
#   3. shard-c starts again → shard-d keeps the slot for SettleSeconds (the status says so), then shard-c takes it back, and a
#      request sent during that reload answers 200 as well.
set -u
export MSYS_NO_PATHCONV=1
GROUP=${LAB_GROUP:-llama-8b-split}
H="Authorization: Bearer adm135"
C="Authorization: Bearer cli135"
# curl.exe from Git Bash cannot write a POSIX path: the scratch files live where both can read them.
OUT=${LAB_OUT:-C:/tmp}; mkdir -p "$OUT" 2>/dev/null
LONG="{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"Write a long essay about distributed computing.\"}],\"max_tokens\":${LAB_MAX_TOKENS:-128},\"temperature\":0,\"seed\":1}"
SHORT="{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"max_tokens\":4}"
t0=$(date +%s)
t() { echo "t+$(( $(date +%s) - t0 ))s"; }
ask() { curl -s -o "$OUT/$1.json" -w "%{http_code} %{time_total}s" -H "$C" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$2"; }
text() { grep -o '"content":"[^"]*"' "$OUT/$1.json"; }
members() { curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -o '"members":\[[^]]*\]' | head -1; }
subs() { curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -o '"substitutions":\[[^]]*\]'; }

echo "== the group: $(members)"
echo "$(t) baseline: $(ask before "$LONG")"

echo "== 1. shard-c is killed while a long request runs"
( echo "$(t) the long request: HTTP $(ask during "$LONG")" ) &
long=$!
sleep "${LAB_KILL_AFTER:-6}"
docker kill shard-c >/dev/null && echo "$(t) killed shard-c"

echo "== 2. a request sent while the group reassembles"
sleep 5
echo "$(t) $(subs)"
echo "$(t) a short request sent in the gap: HTTP $(ask gap "$SHORT") $(grep -o '"message":"[^"]*"' $OUT/gap.json)"
wait $long
[ "$(text before)" = "$(text during)" ] && echo "SAME TEXT as before the kill" || { echo "DIFFERENT TEXT"; text before; text during; }
echo "$(t) the group: $(members)"
docker logs lab-hub 2>&1 | grep -E "failed before the caller received anything|Sending job .* again|waits up to" | tail -5

echo "== 3. shard-c comes back"
docker start shard-c >/dev/null && echo "$(t) started shard-c"
for _ in $(seq 1 90); do subs | grep -q "which is back" && { echo "$(t) $(subs)"; break; }; sleep 2; done
for _ in $(seq 1 120); do members | grep -q '"shard-c"' && { echo "$(t) shard-c has its place back: $(members)"; break; }; sleep 1; done
echo "$(t) a short request during the reload: HTTP $(ask reload "$SHORT") $(grep -o '"message":"[^"]*"' $OUT/reload.json)"
echo "$(t) after it: $(ask back "$LONG")"
[ "$(text before)" = "$(text back)" ] && echo "SAME TEXT with shard-c back" || { echo "DIFFERENT TEXT"; text before; text back; }
curl -s -H "$H" localhost:15080/api/admin/nodes | grep -o '"layers":\[[^]]*\]' | head -1
