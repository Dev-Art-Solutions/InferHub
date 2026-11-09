#!/usr/bin/env bash
# Phase 135: three nodes reach each other's shard workers only through their tunnels.
# A coordinator with node certificates on (Identity:Enabled), three nodes enrolled with one-time tokens, each offering
# 3 GiB; shard-c names no address, so the hub uses the one its connection came from. Then a head in shard-a splits the
# model over its own worker and its forwards to the other two.
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
H="Authorization: Bearer adm135"

docker rm -f lab-hub shard-a shard-b shard-c >/dev/null 2>&1
docker network rm lab135 >/dev/null 2>&1
docker network create lab135 >/dev/null
docker run -d --name lab-hub --network lab135 -p 15080:8080 \
  -e Auth__NodeEnrollmentSecret=lab135 -e Auth__AdminApiKeys__0=adm135 \
  -e Identity__Enabled=true -e Identity__DataDirectory=/data/identity "${HUB_IMAGE:-inferhub-coordinator:dev}" >/dev/null
for _ in $(seq 1 30); do curl -s -o /dev/null -w "%{http_code}" -H "$H" localhost:15080/api/admin/nodes | grep -q 200 && break; sleep 1; done

for n in a b c; do
  token=$(curl -s -X POST -H "$H" -H "Content-Type: application/json" -d "{\"label\":\"shard-$n\"}" \
    localhost:15080/api/admin/identity/tokens | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
  extra=(); [ "$n" = a ] && extra=(-v "$LAB_MODEL_DIR:/models:ro")
  adv=(-e "Node__Shards__TunnelAdvertise=shard-$n:50053"); [ "$n" = c ] && adv=()
  docker run -d --name "shard-$n" --network lab135 --cap-add NET_ADMIN --memory 4g --cpus 4 \
    -e Coordinator__Url=http://lab-hub:8080/ -e Coordinator__EnrollmentSecret=lab135 -e "Coordinator__EnrollmentToken=$token" \
    -e "Node__Name=shard-$n" -e Node__Shards__Enabled=true -e Node__Shards__RamGiB=3 -e Node__Shards__Threads=4 \
    "${adv[@]}" "${extra[@]}" "${NODE_IMAGE:-inferhub-lab-node:dev}" >/dev/null
done

# Every node has measured both peers: six round trips in the hub's view.
for _ in $(seq 1 60); do
  [ "$(curl -s -H "$H" localhost:15080/api/admin/nodes | grep -o '"rttMs":[0-9.]*' | wc -l)" -ge 6 ] && break; sleep 2
done
nodes=$(curl -s -H "$H" localhost:15080/api/admin/nodes)
forwards=$(echo "$nodes" | sed 's/"name":"shard-a"/\nA_START/' | sed -n '/A_START/,$p' \
  | grep -o '"local":"127.0.0.1:[0-9]*"' | head -2 | sed 's/"local":"//; s/"//' | paste -sd,)
echo "shard-a's forwards to its peers: $forwards"

docker exec shard-a sh -c "/opt/llama/llama-server -m /models/$MODEL --rpc 127.0.0.1:50052,$forwards -ngl 999 -c 2048 -t 4 \
  --host 127.0.0.1 --port 18080 > /tmp/head.log 2>&1 &"
for _ in $(seq 1 150); do docker exec shard-a sh -c 'grep -q "listening on" /tmp/head.log' 2>/dev/null && break; sleep 2; done

Q='{"prompt":"The three most important ideas in distributed computing are","n_predict":64,"temperature":0,"seed":1,"cache_prompt":false}'
for _ in 1 2; do
  out=$(docker run --rm --network container:shard-a curlimages/curl:8.11.1 -s 127.0.0.1:18080/completion -H "Content-Type: application/json" -d "$Q")
done
echo "$out" | grep -o '"predicted_per_second":[0-9.]*'
echo "$out" | grep -o '"content":"[^"]*"'
echo "The console (http://localhost:15080/console.html, key adm135) shows the matrix under Shard tunnels."
