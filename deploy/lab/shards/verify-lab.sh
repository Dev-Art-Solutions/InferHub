#!/usr/bin/env bash
# Phase 140: verification day for the sharded-models track (133–139). Runs both modes end to end on the PUBLISHED images
# of one release — the private cloud (a group, a member leaving, a drain), the community (the pick, the sealed door, the
# client's yes) and the community across NAT (the relay) — and says, check by check, what held. It runs the same lab
# scripts a person runs by hand and reads their output; nothing here is a second implementation of a check.
#
#   LAB_MODEL_DIR=D:/models ./verify-lab.sh 3.99.0          # the release to verify (GHCR tags carry no "v")
#   VERIFY_REVISION=<commit> to also require that both images were built from that commit
#   HUB_IMAGE=… NODE_IMAGE=… to verify other images (e.g. :dev built from source) — the revision check is then skipped
#
# Needs: docker, curl, node; the lab's llama.cpp image (docker build -t inferhub-lab-llama:b12182 .). Exit 0 = every
# check held. The transcript of each lab is kept in $LAB_OUT (default C:/tmp) as verify-<lab>.txt.
set -u
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"
TAG=${1:-}
: "${LAB_MODEL_DIR:?set LAB_MODEL_DIR to the directory holding the GGUF}"
OUT=${LAB_OUT:-C:/tmp}; mkdir -p "$OUT" 2>/dev/null
export LAB_OUT=$OUT
REGISTRY=ghcr.io/dev-art-solutions
pass=0; fail=0
check() { # check "<what>" <file> <extended regex that must match>   |   check "<what>" <file> ! <regex that must not>
  local what=$1 file=$2
  if [ "$3" = "!" ]; then grep -Eqi -- "$4" "$file" && ok=1 || ok=0; else grep -Eqi -- "$3" "$file" && ok=0 || ok=1; fi
  if [ "$ok" = 0 ]; then pass=$((pass+1)); echo "  PASS  $what"; else fail=$((fail+1)); echo "  FAIL  $what"; fi
}

if [ -z "${HUB_IMAGE:-}" ] || [ -z "${NODE_IMAGE:-}" ]; then
  [ -n "$TAG" ] || { echo "usage: LAB_MODEL_DIR=… $0 <version, e.g. 3.99.0>"; exit 2; }
  echo "== the published images of $TAG"
  docker pull -q "$REGISTRY/inferhub-coordinator:$TAG" >/dev/null || { echo "cannot pull $REGISTRY/inferhub-coordinator:$TAG"; exit 2; }
  docker pull -q "$REGISTRY/inferhub-node:$TAG" >/dev/null || { echo "cannot pull $REGISTRY/inferhub-node:$TAG"; exit 2; }
  : > "$OUT/verify-images.txt"
  for image in coordinator node; do
    echo "$image version=$(docker inspect -f '{{index .Config.Labels "org.opencontainers.image.version"}}' "$REGISTRY/inferhub-$image:$TAG") revision=$(docker inspect -f '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$REGISTRY/inferhub-$image:$TAG")" | tee -a "$OUT/verify-images.txt"
  done
  check "both images carry version $TAG" "$OUT/verify-images.txt" "^coordinator version=$TAG .*"
  check "the node image carries version $TAG" "$OUT/verify-images.txt" "^node version=$TAG .*"
  if [ -n "${VERIFY_REVISION:-}" ]; then
    check "the coordinator was built from $VERIFY_REVISION" "$OUT/verify-images.txt" "^coordinator .*revision=$VERIFY_REVISION"
    check "the node was built from $VERIFY_REVISION" "$OUT/verify-images.txt" "^node .*revision=$VERIFY_REVISION"
  fi
  # The published node carries no llama.cpp (a node uses its owner's); the lab adds its own beside it, and nothing else.
  docker build -q -f node.Dockerfile --build-arg "NODE=$TAG" -t "inferhub-lab-node:$TAG" . >/dev/null || { echo "cannot build the lab node on $TAG"; exit 2; }
  export HUB_IMAGE="$REGISTRY/inferhub-coordinator:$TAG" NODE_IMAGE="inferhub-lab-node:$TAG"
fi
echo "hub: $HUB_IMAGE   node: $NODE_IMAGE"

echo "== 1. private cloud: a group the admin defined answers (136)"
bash ./tunnel-lab.sh > "$OUT/verify-private.txt" 2>&1
check "the group runs on its head" "$OUT/verify-private.txt" '"state":"running"'
check "the split model answers through the hub" "$OUT/verify-private.txt" '"content":"[^"]+'

echo "== 2. private cloud: a member leaves, a spare stands in, a planned stop drains (137)"
bash ./survive-lab.sh > "$OUT/verify-survive.txt" 2>&1
check "the gap is a 503 with Retry-After and the reason" "$OUT/verify-survive.txt" ' 503 retry-after: 15 .*reassembling'
check "the head reports which worker holds which layers" "$OUT/verify-survive.txt" '"layers":\[\{'
check "the spare stands in" "$OUT/verify-survive.txt" 'shard-d stands in for shard-c'
check "the same text after reassembly" "$OUT/verify-survive.txt" '^SAME TEXT after reassembly'
check "the request in flight completes during the drain" "$OUT/verify-survive.txt" 'long request in flight: HTTP 200'
check "the next request is a 503 saying why the group is not running" "$OUT/verify-survive.txt" 'next request: 503 .*is not running: '

echo "== 3. community: the pick, the sealed door, the client's yes (138)"
bash ./community-lab.sh > "$OUT/verify-community.txt" 2>&1
check "the coordinator picked shard-b and shard-c" "$OUT/verify-community.txt" '"members":\["shard-b","shard-c"\]'
check "never the node that did not opt in" "$OUT/verify-community.txt" ! '"members":\[[^]]*shard-d'
check "the sealed node refused to start, naming the door" "$OUT/verify-community.txt" 'Node:Shards:Community joins groups'
check "/v1/models says inferhub-community" "$OUT/verify-community.txt" '"owned_by":"inferhub-community"'
check "without the header: 403 with the sentence" "$OUT/verify-community.txt" 'HTTP 403 .*X-InferHub-Community: accept'
check "with the header: the answer" "$OUT/verify-community.txt" '^"content":"[^"]+'

echo "== 4. community across NAT: the relay (139)"
bash ./relay-lab.sh > "$OUT/verify-relay.txt" 2>&1
check "shard-a has no path to shard-c" "$OUT/verify-relay.txt" '^a -> c .*no path'
check "shard-c has no path to shard-a" "$OUT/verify-relay.txt" '^c -> a .*no path'
check "the group runs with shard-c in it" "$OUT/verify-relay.txt" '"members":\["shard-b","shard-c"\]'
check "shard-a reaches shard-c through the relay" "$OUT/verify-relay.txt" '^shard-a -> shard-c: relay [0-9.]+ ms'
check "shard-c reaches shard-a through the relay" "$OUT/verify-relay.txt" '^shard-c -> shard-a: relay [0-9.]+ ms'
check "shard-a reaches shard-b directly" "$OUT/verify-relay.txt" '^shard-a -> shard-b: direct [0-9.]+ ms'
check "the group answers" "$OUT/verify-relay.txt" '^"content":"[^"]+'
check "the relay carried bytes" "$OUT/verify-relay.txt" '"bytesRelayed":[1-9][0-9]{5,}'
check "after a revoke no pair is left at the relay" "$OUT/verify-relay.txt" '"pairs":0,'
check "and the group stops answering with the reason" "$OUT/verify-relay.txt" 'next request: HTTP 503 '

docker rm -f lab-hub shard-a shard-b shard-c shard-d shard-s >/dev/null 2>&1
docker network rm lab135 lab139-home >/dev/null 2>&1
echo "== $pass held, $fail did not. Transcripts: $OUT/verify-{private,survive,community,relay}.txt"
[ "$fail" = 0 ]
