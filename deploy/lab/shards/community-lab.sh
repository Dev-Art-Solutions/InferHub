#!/usr/bin/env bash
# Phase 138: a community group. shard-a (the head), shard-b and shard-c opted into community groups with the risk accepted;
# shard-d offers the same memory and did not opt in; shard-s is sealed and asks to join anyway. The coordinator must pick b
# and c, never d; shard-s must refuse to start; a client must accept before its prompt is sent.
#
#   LAB_MODEL_DIR=D:/models ./community-lab.sh
set -u
export MSYS_NO_PATHCONV=1
: "${LAB_MODEL_DIR:?set LAB_MODEL_DIR to the directory holding the GGUF}"
MODEL=${LAB_MODEL:-llama31-8b.gguf}
GROUP=llama-8b-community
H="Authorization: Bearer adm135"
C="Authorization: Bearer cli135"
OUT=${LAB_OUT:-C:/tmp}

docker rm -f lab-hub shard-a shard-b shard-c shard-d shard-s >/dev/null 2>&1
docker network rm lab135 >/dev/null 2>&1
docker network create lab135 >/dev/null
docker run -d --name lab-hub --network lab135 -p 15080:8080 \
  -e Auth__NodeEnrollmentSecret=lab135 -e Auth__AdminApiKeys__0=adm135 -e Auth__ApiKeys__0=cli135 \
  -e Identity__Enabled=true -e Identity__DataDirectory=/data/identity \
  -e "ShardGroups__${GROUP}__Mode=community" -e "ShardGroups__${GROUP}__Head=shard-a" -e "ShardGroups__${GROUP}__Workers=2" \
  -e "ShardGroups__${GROUP}__Model=$MODEL" -e "ShardGroups__${GROUP}__Context=2048" \
  "${HUB_IMAGE:-inferhub-coordinator:dev}" >/dev/null
for _ in $(seq 1 30); do curl -s -o /dev/null -w "%{http_code}" -H "$H" localhost:15080/api/admin/nodes | grep -q 200 && break; sleep 1; done

for n in a b c d s; do
  token=$(curl -s -X POST -H "$H" -H "Content-Type: application/json" -d "{\"label\":\"shard-$n\"}" \
    localhost:15080/api/admin/identity/tokens | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
  extra=()
  [ "$n" = a ] && extra+=(-v "$LAB_MODEL_DIR:/models:ro" -e Node__Shards__AllowHead=true -e Node__Shards__ModelsDir=/models)
  case $n in a|b|c|s) extra+=(-e Node__Shards__Community=true -e Node__Shards__CommunityAcceptRisk=true) ;; esac
  [ "$n" = s ] && extra+=(-e Node__Sealed=true)
  docker run -d --name "shard-$n" --network lab135 --memory 4g --cpus 4 \
    -e Coordinator__Url=http://lab-hub:8080/ -e Coordinator__EnrollmentSecret=lab135 -e "Coordinator__EnrollmentToken=$token" \
    -e "Node__Name=shard-$n" -e Node__Shards__Enabled=true -e Node__Shards__RamGiB=3 -e Node__Shards__Threads=4 \
    -e "Node__Shards__TunnelAdvertise=shard-$n:50053" "${extra[@]}" "${NODE_IMAGE:-inferhub-lab-node:dev}" >/dev/null
done

for _ in $(seq 1 150); do
  curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -q '"state":"running"' && break; sleep 2
done
echo "== the group the coordinator picked"
curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -o '"members":\[[^]]*\]\|"ready":[a-z]*\|"community":[a-z]*' | sort -u
echo "== the sealed node that asked to join"
sleep 3; docker ps -a --filter name=shard-s --format '{{.Status}}'; docker logs shard-s 2>&1 | grep -o "Node:Shards:Community[^.]*\." | head -1

for _ in $(seq 1 30); do curl -s -H "$C" localhost:15080/v1/models | grep -q "\"$GROUP\"" && break; sleep 2; done
echo "== /v1/models"; curl -s -H "$C" localhost:15080/v1/models | grep -o "{\"id\":\"$GROUP\"[^}]*}"
Q="{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"Name the three most important ideas in distributed computing.\"}],\"max_tokens\":64,\"temperature\":0,\"seed\":1}"
echo "== without acceptance"
curl -s -o "$OUT/c1.json" -w "HTTP %{http_code} " -H "$C" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$Q"; grep -o '"message":"[^"]*"' "$OUT/c1.json"
echo "== with X-InferHub-Community: accept"
for _ in 1 2; do curl -s -o "$OUT/c2.json" -w "" -H "$C" -H "X-InferHub-Community: accept" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$Q"; done
grep -o '"content":"[^"]\{0,120\}' "$OUT/c2.json"
