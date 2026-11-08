#!/bin/sh
# Shape the lab's network: every container's egress gets the profile, so a round trip between two pays it twice.
#   ./netem.sh lan        0.25 ms each way, 1 Gbit/s     (a switch in one office)
#   ./netem.sh internet   15 ms each way, 50 Mbit/s       (two homes in one country)
#   ./netem.sh far        60 ms each way, 20 Mbit/s       (another continent)
#   ./netem.sh off
set -e
profile=${1:?lan | internet | far | off}
case "$profile" in
  lan)      rule="delay 0.25ms rate 1gbit" ;;
  internet) rule="delay 15ms rate 50mbit" ;;
  far)      rule="delay 60ms rate 20mbit" ;;
  off)      rule="" ;;
  *) echo "unknown profile $profile" >&2; exit 2 ;;
esac
for c in head worker1 worker2 worker3; do
  docker compose exec -T "$c" sh -c "tc qdisc del dev eth0 root 2>/dev/null || true"
  [ -n "$rule" ] && docker compose exec -T "$c" sh -c "tc qdisc add dev eth0 root netem $rule"
  echo "$c: ${rule:-no shaping}"
done
