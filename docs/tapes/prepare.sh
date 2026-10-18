#!/usr/bin/env bash
# Run in a disposable recording directory containing docs/tapes.
set -euo pipefail
slug=${1:?Usage: bash docs/tapes/prepare.sh SLUG}
case "$slug" in
  getting-started|wip-up|wip-build|wip-shell|wip-logs|wip-sync|wip-sandbox) ;;
  *) echo 'Unknown tape' >&2; exit 1 ;;
esac
if [[ ! -f .wip-tapes-recording ]]; then
  echo 'Create .wip-tapes-recording in a disposable recording directory first.' >&2
  exit 1
fi
if [[ -f wip.yml ]]; then
  # Missing containers or sandboxes must not prevent the next take.
  wip down || true
  wip sandbox destroy demo || true
fi
mkdir -p context/payload source
cp docs/tapes/Dockerfile context/Dockerfile
# A nonempty ignore file enables staging progress on local filesystems too.
printf 'ignored\n' > context/.dockerignore
for ((index=0; index<128; index++)); do
  printf 'recording fixture %s\n' "$index" > "context/payload/$index.txt"
done
printf 'original\n' > source/message.txt
# Each take shows network creation; previous networks can be removed after recording.
network="wip-tapes-$(date +%s)-$$"
sed "s/^network: wip-tapes$/network: $network/" docs/tapes/wip.yml > recording.yml
cp recording.yml wip.yml
# Warm the public base image and package layer before recording.
wip build
# Invalidate COPY and the one-second build step for the visible build.
printf '%s\n' "$network" > context/payload/take.txt
case "$slug" in
  getting-started) mv wip.yml starter.yml ;;
  wip-shell|wip-logs|wip-sync) wip up -d ;;
esac
