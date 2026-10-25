#!/bin/sh
# A tiny scanner, not a ZAP substitute: exercise target access, blocked probes and report I/O.
set -eu

target=$1
report=$2
unperformed=$3
work=/tmp/wip-scan-fixture
mkdir -p "$work"

# A known successful internal request makes blocked external probes meaningful.
wget -q -T 5 -O "$work/body" "$target"
grep -q 'wip-e2e-build-marker' "$work/body"
for applet in nc ping nslookup timeout; do
    command -v "$applet" >/dev/null
done

attempted=0
successful=0
blocked_probe() {
    attempted=$((attempted + 1))
    if timeout 5 "$@" >"$work/probe" 2>&1; then
        successful=$((successful + 1))
    fi
    # A missing utility or unsupported flag must not look like a blocked connection.
    if grep -Ei 'unrecognized option|invalid option|applet not found|: not found|^Usage:|BusyBox v' "$work/probe"; then
        echo 'external probe was unsupported; isolation was not tested' >&2
        exit 90
    fi
}

# These run inside the engine's scanner exec, not before or after it.
blocked_probe nc -z -w 3 1.1.1.1 443
blocked_probe ping -c 1 -W 2 1.1.1.1
blocked_probe nslookup example.com 8.8.8.8
blocked_probe nslookup example.com
blocked_probe nc -z -w 3 172.17.0.1 22
blocked_probe nc -z -w 3 169.254.169.254 80

printf '{"target_http_completed":true,"external_attempted":%s,"external_successful":%s,"unperformed_checks":[{"id":"authenticated-pages","reason":"the fixture supplies no credentials"}]}\n' \
    "$attempted" "$successful" >"$report"

printf '[{"id":"authenticated-pages","reason":"the fixture supplies no credentials"}]\n' >"$unperformed"

# A nonzero scanner status proves the engine preserves findings independently of report I/O.
if [ "$successful" -ne 0 ]; then exit 91; fi
exit 7
