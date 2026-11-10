#!/usr/bin/env bash
# Phases 135–136: three nodes reach each other's shard workers only through their tunnels, and the coordinator runs one
# model across them as a group.
# A coordinator with node certificates on (Identity:Enabled) and one group defined (ShardGroups), three nodes enrolled with
# one-time tokens, each offering 3 GiB; shard-c names no address, so the hub uses the one its connection came from. shard-a
# allows being a head and holds the model. Once every member is ready the hub sends shard-a the group, shard-a runs
# llama-server over its own worker and its tunnels, and the model answers through the hub's /v1/chat/completions.
#
#   LAB_MODEL_DIR=D:/models ./tunnel-lab.sh            # the directory holding $LAB_MODEL (default llama31-8b.gguf)
#   HUB_IMAGE=… NODE_IMAGE=… to use other images (default: built from source, see node.Dockerfile)
#   docker exec -u root shard-c tc qdisc add dev eth0 root netem delay 15ms rate 50mbit   # shape one node's link
#
# Lessons from running it: set Node:Shards:Threads to the container's CPUs — llama.cpp's default is every core on the
# host, which a CPU-limited container turns into a crawl (2.5 tok/s instead of 6.3 here).
set -u
export MSYS_NO_PATHCONV=1
: "${LAB_MODEL_DIR:?set LAB_MODEL_DIR to the directory holding the GGUF}"
MODEL=${LAB_MODEL:-llama31-8b.gguf}
GROUP=${LAB_GROUP:-llama-8b-split}
H="Authorization: Bearer adm135"

docker rm -f lab-hub shard-a shard-b shard-c >/dev/null 2>&1
docker network rm lab135 >/dev/null 2>&1
docker network create lab135 >/dev/null
docker run -d --name lab-hub --network lab135 -p 15080:8080 \
  -e Auth__NodeEnrollmentSecret=lab135 -e Auth__AdminApiKeys__0=adm135 -e Auth__ApiKeys__0=cli135 \
  -e Identity__Enabled=true -e Identity__DataDirectory=/data/identity \
  -e "ShardGroups__${GROUP}__Head=shard-a" -e "ShardGroups__${GROUP}__Members__0=shard-b" -e "ShardGroups__${GROUP}__Members__1=shard-c" \
  -e "ShardGroups__${GROUP}__Model=$MODEL" -e "ShardGroups__${GROUP}__Context=2048" \
  "${HUB_IMAGE:-inferhub-coordinator:dev}" >/dev/null
for _ in $(seq 1 30); do curl -s -o /dev/null -w "%{http_code}" -H "$H" localhost:15080/api/admin/nodes | grep -q 200 && break; sleep 1; done

for n in a b c; do
  token=$(curl -s -X POST -H "$H" -H "Content-Type: application/json" -d "{\"label\":\"shard-$n\"}" \
    localhost:15080/api/admin/identity/tokens | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
  extra=(); [ "$n" = a ] && extra=(-v "$LAB_MODEL_DIR:/models:ro" -e Node__Shards__AllowHead=true -e Node__Shards__ModelsDir=/models)
  adv=(-e "Node__Shards__TunnelAdvertise=shard-$n:50053"); [ "$n" = c ] && adv=()
  docker run -d --name "shard-$n" --network lab135 --cap-add NET_ADMIN --memory 4g --cpus 4 \
    -e Coordinator__Url=http://lab-hub:8080/ -e Coordinator__EnrollmentSecret=lab135 -e "Coordinator__EnrollmentToken=$token" \
    -e "Node__Name=shard-$n" -e Node__Shards__Enabled=true -e Node__Shards__RamGiB=3 -e Node__Shards__Threads=4 \
    "${adv[@]}" "${extra[@]}" "${NODE_IMAGE:-inferhub-lab-node:dev}" >/dev/null
done

# The group runs once its head answers /health; GET /api/admin/shards/groups says why not until then.
for _ in $(seq 1 150); do
  curl -s -H "$H" localhost:15080/api/admin/shards/groups | grep -q '"state":"running"' && break; sleep 2
done
curl -s -H "$H" localhost:15080/api/admin/shards/groups; echo

# And the hub routes the group's name like any other model (a model report follows the group starting).
for _ in $(seq 1 30); do curl -s -H "Authorization: Bearer cli135" localhost:15080/v1/models | grep -q "\"$GROUP\"" && break; sleep 2; done
Q="{\"model\":\"$GROUP\",\"messages\":[{\"role\":\"user\",\"content\":\"Name the three most important ideas in distributed computing.\"}],\"max_tokens\":64,\"temperature\":0,\"seed\":1}"
for _ in 1 2; do
  out=$(curl -s -H "Authorization: Bearer cli135" -H "Content-Type: application/json" localhost:15080/v1/chat/completions -d "$Q")
done
echo "$out"
echo "The console (http://localhost:15080/console.html, key adm135) shows the matrix and the group under Shard tunnels."
