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

# Each probe tool has to be shown working against something reachable before its failures
# count as evidence. Being present on PATH is not the same as being usable: ping without
# CAP_NET_RAW dies with "permission denied (are you root?)", and a tool that cannot send at
# all fails for its own reasons while looking exactly like a blocked connection. The target
# is on this network and answered the request above, so it is the control.
host=$(printf '%s' "$target" | sed -e 's|^[a-z]*://||' -e 's|[:/].*$||')
if ! timeout 5 nc -z -w 3 "$host" 8080 >"$work/control" 2>&1; then
    echo "nc cannot reach the in-network target; its probes would prove nothing" >&2
    cat "$work/control" >&2
    exit 92
fi
if ! timeout 5 ping -c 1 -W 2 "$host" >"$work/control" 2>&1; then
    echo "ping cannot reach the in-network target; its probes would prove nothing" >&2
    cat "$work/control" >&2
    exit 93
fi

attempted=0
successful=0
blocked_probe() {
    attempted=$((attempted + 1))
    if timeout 5 "$@" >"$work/probe" 2>&1; then
        successful=$((successful + 1))
    fi
    # A missing utility, an unsupported flag, or a refusal that never reached the network must
    # not look like a blocked connection. The controls above cover nc and ping; this catches
    # the rest, including the capability failure that would otherwise read as isolation.
    if grep -Ei 'unrecognized option|invalid option|applet not found|: not found|^Usage:|BusyBox v|permission denied|operation not permitted|are you root' "$work/probe"; then
        echo 'external probe was unsupported; isolation was not tested' >&2
        exit 90
    fi
}

# These run inside the engine's scanner exec, not before or after it.
blocked_probe nc -z -w 3 1.1.1.1 443
blocked_probe ping -c 1 -W 2 1.1.1.1
blocked_probe nslookup example.com 8.8.8.8
# The only probe with no in-network control: there is no resolver on this network to prove
# nslookup works against. It stays because it fails closed -- a success here is a leak however
# it came about -- but a failure is weaker evidence than the probes above. example.com is the
# RFC 2606 reserved name, so the query names nobody's host even if the boundary were open.
blocked_probe nslookup example.com
blocked_probe nc -z -w 3 172.17.0.1 22
blocked_probe nc -z -w 3 169.254.169.254 80

printf '{"target_http_completed":true,"external_attempted":%s,"external_successful":%s,"unperformed_checks":[{"id":"authenticated-pages","reason":"the fixture supplies no credentials"}]}\n' \
    "$attempted" "$successful" >"$report"

printf '[{"id":"authenticated-pages","reason":"the fixture supplies no credentials"}]\n' >"$unperformed"

# A nonzero scanner status proves the engine preserves findings independently of report I/O.
if [ "$successful" -ne 0 ]; then exit 91; fi
exit 7
