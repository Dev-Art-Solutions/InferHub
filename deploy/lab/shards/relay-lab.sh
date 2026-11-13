#!/usr/bin/env bash
# Phase 139: a community group across NAT. shard-a (the head) and shard-b sit on one network with the coordinator; shard-c
# sits on another — "at home" — that only the coordinator is also attached to. Docker routes nothing between two bridge
# networks, so shard-c can dial out to the coordinator and nobody can dial shard-c: no direct path either way, which is
# what a home router gives. All three opted into community groups and into the relay (Node:Shards:TunnelRelay); the
# coordinator runs one (ShardRelay:Enabled). The group must come up with shard-c in it, a<->b direct and every pair with
# shard-c by way of the relay, and answer.
#
#   LAB_MODEL_DIR=D:/models ./relay-lab.sh
#   HUB_IMAGE=… NODE_IMAGE=… to use other images (default: built from source, see node.Dockerfile)
set -u
export MSYS_NO_PATHCONV=1
: "${LAB_MODEL_DIR:?set LAB_MODEL_DIR to the directory holding the GGUF}"
MODEL=${LAB_MODEL:-llama31-8b.gguf}
GROUP=llama-8b-community
H="Authorization: Bearer adm135"
C="Authorization: Bearer cli135"
OUT=${LAB_OUT:-C:/tmp}; mkdir -p "$OUT" 2>/dev/null

docker rm -f lab-hub shard-a shard-b shard-c shard-d shard-s >/dev/null 2>&1
docker network rm lab135 lab139-home >/dev/null 2>&1
docker network create lab135 >/dev/null
docker network create lab139-home >/dev/null
docker run -d --name lab-hub --network lab135 -p 15080:8080 \
  -e Auth__NodeEnrollmentSecret=lab135 -e Auth__AdminApiKeys__0=adm135 -e Auth__ApiKeys__0=cli135 \
  -e Identity__Enabled=true -e Identity__DataDirectory=/data/identity \
  -e ShardRelay__Enabled=true \
  -e "ShardGroups__${GROUP}__Mode=community" -e "ShardGroups__${GROUP}__Head=shard-a" -e "ShardGroups__${GROUP}__Workers=2" \
  -e "ShardGroups__${GROUP}__Model=$MODEL" -e "ShardGroups__${GROUP}__Context=2048" \
  "${HUB_IMAGE:-inferhub-coordinator:dev}" >/dev/null
docker network connect lab139-home lab-hub
for _ in $(seq 1 30); do curl -s -o /dev/null -w "%{http_code}" -H "$H" localhost:15080/api/admin/nodes | grep -q 200 && break; sleep 1; done

for n in a b c; do
  token=$(curl -s -X POST -H "$H" -H "Content-Type: application/json" -d "{\"label\":\"shard-$n\"}" \
    localhost:15080/api/admin/identity/tokens | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
  extra=(); [ "$n" = a ] && extra=(-v "$LAB_MODEL_DIR:/models:ro" -e Node__Shards__AllowHead=true -e Node__Shards__ModelsDir=/models)
  # shard-c names no address: behind a router it has none to give. The coordinator uses the one its connection came from,
  # which only the coordinator can reach.
  net=lab135; adv=(-e "Node__Shards__TunnelAdvertise=shard-$n:50053"); [ "$n" = c ] && { net=lab139-home; adv=(); }
  docker run -d --name "shard-$n" --network "$net" --memory 4g --cpus 4 \
    -e Coordinator__Url=http://lab-hub:8080/ -e Coordinator__EnrollmentSecret=lab135 -e "Coordinator__EnrollmentToken=$token" \
    -e "Node__Name=shard-$n" -e Node__Shards__Enabled=true -e Node__Shards__RamGiB=3 -e Node__Shards__Threads=4 \
    -e Node__Shards__Community=true -e Node__Shards__CommunityAcceptRisk=true -e Node__Shards__TunnelRelay=true \
    "${adv[@]}" "${extra[@]}" "${NODE_IMAGE:-inferhub-lab-node:dev}" >/dev/null
done

echo "== no direct path: shard-a cannot open shard-c's tunnel port, shard-c cannot resolve shard-a"
c_ip=$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' shard-c)
docker exec shard-a bash -c "timeout 3 bash -c '</dev/tcp/$c_ip/50053' 2>/dev/null && echo 'a -> c: OPEN (the lab is not isolating)' || echo 'a -> c ($c_ip:50053): no path'"
docker exec shard-c bash -c "timeout 3 bash -c '</dev/tcp/shard-a/50053' 2>/dev/null && echo 'c -> a: OPEN (the lab is not isolating)' || echo 'c -> a (shard-a:50053): no path'"

for _ in $(seq 1 150); do
  curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -q '"state":"running"' && break; sleep 2
done
echo "== the group"
curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -o '"members":\[[^]]*\]\|"ready":[a-z]*\|"why":"[^"]*"\|"state":"[a-z]*"' | sort -u
echo "== who reaches whom, and how (node: peer via path, round trip)"
curl -s -H "$H" localhost:15080/api/admin/nodes > "$OUT/relay-nodes.json"
node -e '
const read = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
const nodes = Array.isArray(read) ? read : read.nodes ?? [];
for (const n of nodes) for (const p of n.shards?.peers ?? [])
  console.log(`${n.name} -> ${p.name}: ${p.via ?? "?"} ${p.rttMs ?? p.error} ms`);
for (const n of nodes) if (n.shards?.tunnel) console.log(`${n.name}: relay ${n.shards.tunnel.relay ? (n.shards.tunnel.relayError ?? "waiting") : "not asked"}`);
' "$OUT/relay-nodes.json"

for _ in $(seq 1 30); do curl -s -H "$C" localhost:15080/v1/models | grep -q "\"$GROUP\"" && break; sleep 2; done
Q="{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"Name the three most important ideas in distributed computing.\"}],\"max_tokens\":64,\"temperature\":0,\"seed\":1}"
echo "== the answer, through the relay"
for _ in 1 2; do
  curl -s -o "$OUT/relay-answer.json" -w "HTTP %{http_code} in %{time_total}s\n" -H "$C" -H "X-InferHub-Community: accept" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$Q"
done
grep -o '"content":"[^"]\{0,160\}' "$OUT/relay-answer.json"; grep -o '"completion_tokens":[0-9]*' "$OUT/relay-answer.json"
echo "== the relay"
curl -s -H "$H" localhost:15080/api/admin/shards/relay; echo

echo "== revoke shard-c: its pairs are cut and its place at the relay is gone"
id=$(grep -o "\"nodeId\":\"[^\"]*\",\"name\":\"shard-c\"" "$OUT/relay-nodes.json" | head -1 | sed 's/"nodeId":"\([^"]*\)".*/\1/')
curl -s -o /dev/null -X POST -H "$H" -H "Content-Type: application/json" -d '{"reason":"lab: a member leaves"}' "localhost:15080/api/admin/identity/nodes/$id/revoke"
sleep 4
curl -s -H "$H" localhost:15080/api/admin/shards/relay; echo
curl -s -o "$OUT/relay-after.json" -w "next request: HTTP %{http_code} " -H "$C" -H "X-InferHub-Community: accept" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$Q"
grep -o '"message":"[^"]*"' "$OUT/relay-after.json"
