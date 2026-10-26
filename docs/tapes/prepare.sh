#!/usr/bin/env bash
# Run in a disposable recording directory containing docs/tapes.
set -euo pipefail
slug=${1:?Usage: bash docs/tapes/prepare.sh SLUG}
case "$slug" in
  getting-started|wip-up|wip-build|wip-shell|wip-logs|wip-sync|wip-sandbox|wip-security-scan) ;;
  *) echo 'Unknown tape' >&2; exit 1 ;;
esac
if [[ ! -f .wip-tapes-recording ]]; then
  echo 'Create .wip-tapes-recording in a disposable recording directory first.' >&2
  exit 1
fi
if [[ -f wip.yml ]]; then
  # Missing containers or sandboxes must not prevent the next take.
  wip down || true
  for sandbox in demo scanner target; do
    if grep -q "^  - name: ${sandbox}$" wip.yml; then
      wip sandbox destroy "$sandbox" || true
    fi
  done
fi
mkdir -p context/payload source
cp docs/tapes/Dockerfile context/Dockerfile
# A nonempty ignore file enables staging progress on local filesystems too.
printf 'ignored\n' > context/.dockerignore
for ((index=0; index<128; index++)); do
  printf 'recording fixture %s\n' "$index" > "context/payload/$index.txt"
done
printf 'original\n' > source/message.txt
# Reuse one warmup network instead of allocating another pool on every take.
network=wip-tapes-warm
sed "s/^network: wip-tapes$/network: $network/" docs/tapes/wip.yml > recording.yml
cp recording.yml wip.yml
# Warm the public base image and package layer before recording.
wip build
# Warm sidecar images before any visible startup, then leave no containers running.
wip up -d
wip down
# Use a fresh network so visible startup still includes network creation.
network="wip-tapes-$(date +%s)-$$-take"
sed "s/^network: wip-tapes$/network: $network/" docs/tapes/wip.yml > recording.yml
cp recording.yml wip.yml
# Invalidate COPY and the one-second build step for the visible build.
printf '%s\n' "$network" > context/payload/take.txt
case "$slug" in
  getting-started) mv wip.yml starter.yml ;;
  wip-shell|wip-logs|wip-sync) wip up -d ;;
  wip-security-scan)
    # Clear the previous run first. A prep that fails after target is recreated would
    # otherwise leave the old IPAM address behind, and the take would record a scan of an
    # unreachable seed -- which ZAP reports as exit 0 with no findings, indistinguishable
    # from a clean target.
    rm -f .target-url
    zap_image='ghcr.io/zaproxy/zaproxy@sha256:5ccfff9a411743ab2615136a2edd7416bec9ce41572fa94e935df38c14adb2b6'
    cat >> wip.yml <<EOF
  - name: target
    image: ${zap_image}
    network: scan
    command: [python3, -m, http.server, '8080', --directory, /tmp]
  - name: scanner
    image: ${zap_image}
    network: scan
    command: [sleep, infinity]
networks:
  - name: scan
    internal: true
EOF
    cp wip.yml recording.yml
    # Creating target warms the pinned ZAP image and internal network before recording.
    wip sandbox create target
    wip sandbox exec target -- python3 -c "from pathlib import Path; Path('/tmp/index.html').write_text('<!doctype html><title>demo</title><p>ok</p>\n', encoding='utf-8')"
    target_ip=$(wip sandbox exec target -- python3 -c "import fcntl, socket, struct; s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); print(socket.inet_ntoa(fcntl.ioctl(s.fileno(), 0x8915, struct.pack('256s', b'eth0'))[20:24]))" | tr -d '\r\n')
    # sandbox create only reports the container as running, so on a cold host the server
    # is not listening yet. Retry instead of aborting the prep under set -e.
    for attempt in 1 2 3 4 5 6 7 8 9 10; do
      if wip sandbox exec target -- python3 -c "import urllib.request; urllib.request.urlopen('http://${target_ip}:8080/', timeout=5).read()"; then
        break
      fi
      if [[ $attempt -eq 10 ]]; then
        echo "target did not serve http://${target_ip}:8080/ after 10 attempts" >&2
        exit 1
      fi
      sleep 2
    done
    printf 'http://%s:8080/\n' "$target_ip" > .target-url
    ;;
esac
