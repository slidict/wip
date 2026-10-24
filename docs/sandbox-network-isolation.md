# Sandbox network isolation

The boundary a security scan runs behind. A sandbox may declare one network; declaring it
means the container is attached to that network and nothing else, and that wip refuses to
start the container at all unless it has confirmed the network exists, belongs to wip, and is
internal.

Built for the single-tool penetration testing platform, as its first part. Nothing here is
specific to scanning: it is the generic declaration the scanner adapters build on.

## Schema, version 1 additive extension

```yaml
version: 1
resource_namespace: example-project
networks:
  - name: scan
    internal: true
sandboxes:
  - name: scanner
    image: example/zap:1
    network: scan
  - name: target
    image: example/app:1
    network: scan
```

`internal` has to be written out, the way `volumes[].persistent` does: the value decides
whether anything in the sandbox can reach the internet, so it is never inferred from a
default. A sandbox may only reference an internal network — referencing one declared
`internal: false` is rejected when the configuration loads, because the key would otherwise
read as a restriction while granting unrestricted egress. Referencing a network that is not
declared is rejected the same way.

Omitting `network:` leaves a sandbox exactly as it was before this existed: the backend
attaches whatever it attaches by default, and wip asserts nothing about it.

Loading YAML or running `wip config` still creates nothing. `Config.ToMapping` emits
`networks:` only when the block is present, so a configuration without one normalizes
byte-for-byte as before.

## The mechanism

WSLC's `network create --internal` is the whole of it. The restriction lives on the network,
outside the container, so nothing the scanner or the scanned application does from inside can
lift it — which is the property the platform needs and the reason the alternatives were
rejected:

- **`iptables`/`nftables` inside the sandbox**: changeable by whatever runs in the sandbox,
  which is precisely the untrusted thing.
- **`--dns` alone**: constrains name resolution and nothing else, so a raw IP still leaves.
- **Host firewall rules**: not reproducible in CI, and outside wip's declarative config.

Measured against WSLC on 2026-10-24, a container attached only to an internal network cannot
reach an external IPv4 address, cannot reach a public resolver, and cannot resolve a name at
all.

**External IPv6 reachability is unverified, and WSLC is why.** The fixture holds no global
IPv6 address, so there was no IPv6 egress to block and nothing was established either way — a
`ping -6` that fails for want of a stack says nothing about the boundary. The test asserts
that absence rather than dressing it up as a block, and runs the reachability assertion only
on a sandbox that does hold an address.

This is not a gap in the fixture. Measured against WSLC 3.0.3: a network created with an IPv6
subnet and gateway is accepted and records the subnet in its IPAM configuration, yet
`EnableIPv6` stays false, an IPv4 subnet is assigned alongside, and a container on it gets no
IPv6 address; the driver option `com.docker.network.enable_ipv6=true` is stored and ignored.
An IPv6 declaration therefore does not fail — it silently yields an IPv4-only network. So IPv6
isolation stays unverified until WSLC supports IPv6 at all, and the sandbox networking this
builds on is IPv4 only, as the README states.

Names derive from the resource namespace the same way sandbox and volume names do
(`wip-n-` + a SHA-256 prefix of `<namespace>:network:<name>`), so two declared networks never
collide and the same declared name in another project is another network. Unlike a volume the
name carries no generation suffix: a network holds no data, so there is nothing a later create
could inherit, and a stable name is what lets an orphan left by an abnormal exit still be
found by its declaration.

## Fail closed

Every failure to confirm the boundary stops the operation before a container exists. This is
the point of the design rather than an error-handling detail: the state produced by carrying
on without `--network` is the backend's default bridge, which has unrestricted egress.

| Condition | Result |
| --- | --- |
| The network cannot be created | No container is started |
| Creation reports success but no network is there | No container is started |
| A network of that name exists but is not internal | No container is started; the network is neither adopted, recreated nor deleted |
| A network of that name exists but carries another owner label | Same |
| `network inspect` cannot be read | No container is started |
| The sandbox declares a network but no network lifecycle is configured | Configuration error; no container is started |

## Resume, parallel runs, and teardown

An existing container's attachments are fixed when it is created, so `sandbox create` on a
stopped or already-running container re-reads them and refuses anything that is not exactly
the declared network — including a container that is on the declared network *and* something
else. It is never repaired by attaching or detaching after the fact: a container that came
back from an abnormal exit on the wrong network is reported, and `sandbox destroy` then
`sandbox create` is the way through.

Two sandboxes on the same declared network can reach each other, which is how a scanner
reaches its target. Sandboxes on different declared networks cannot, so parallel scans do not
see each other.

`sandbox destroy` removes the declared network after the container is gone, so it is never
removed from under a live attachment, and only when its owner label and internal flag match.
A network several sandboxes share is kept while any of them is still attached -- destroying
the scanner leaves the target's boundary standing -- and removed by whichever one is last.
That is a no-op rather than an error, because the alternative is a `sandbox destroy` that
fails on a network the backend is right to refuse.

**A stopped sandbox counts as attached.** Attachments are therefore counted by scanning
containers, the way volume references are, and not from `network inspect`'s own
`Containers` map: measured 2026-10-24, that map lists only *running* containers, and WSLC
removes a network a stopped container is still attached to. The container is then unstartable
for good — its attachment is pinned to the removed network's id, so recreating a network of
the same name does not restore it (`start` fails with `network <id> not found`), while the
container's own inspect still names the network, so nothing downstream can tell why. A
container whose attachments cannot be read counts as attached: an orphan network is something
to clean up, a container that can never start is not.

WSLC rejects removing a network that still has attachments; wip never forces or prunes, and a
scan it cannot complete before removal deletes nothing.

## What is tested, and what is not

`tests/Wip.Tests/NetworkIsolationTests.cs` covers the declaration, the argv, and every row of
the fail-closed table against a fake backend, so it runs with no WSLC present.

`tests/e2e/network-isolation.ps1` runs against real WSLC in CI on every pull request
(`.github/workflows/e2e-windows.yml`). It asserts the allowed path works — ICMP and TCP to a
sandbox on the same declared network — and that each forbidden path does not: an internet
IPv4 address over TCP and ICMP, a public DNS resolver, name resolution, the default bridge's
host gateway, the cloud metadata endpoint, and a sandbox on another internal network. It then
stops and resumes the sandbox and asserts the same, and forces the fail-closed path by
putting a non-internal network under the name wip derives.

Each probe is validated before anything is called blocked, because a probe that never ran
exits non-zero too and would otherwise read as a blocked network. `ping` and `nc` are
asserted to work against the sandbox's own loopback, `nslookup` to exist, and any BusyBox
"unrecognized option" or "applet not found" in a probe's output fails the assertion instead
of passing it. Two earlier probes were replaced for failing this standard: `nc -u -z` returns
non-zero even to loopback, so it cannot report UDP reachability at all and egress to a
resolver is now probed with `nslookup example.com 8.8.8.8` (verified to exit 0 from the
default bridge and non-zero here).

IPv6 is asserted for whichever reason actually holds. The fixture image has no global IPv6
address under the WSLC measured on 2026-10-24, and a `ping -6` that fails for want of a stack
is not evidence that a network blocked anything — so the absence is asserted directly, and
the reachability assertion runs only when the sandbox does hold an address to reach from.
External IPv6 blocking is therefore unverified here, as the mechanism section states.

The cross-network TCP assertion carries a listener and a control for the same reason: without
something to refuse the connection it would fail merely because nothing listens on the port,
and would pass even if cross-network TCP were allowed. The control runs after the blocked
probe, since `nc -l` without `-k` serves one connection and exits.

Not covered, and deliberately not a completion condition: behaviour on a host whose WSLC
version differs from CI's. The metadata endpoint in particular can only be observed on the
host in question. Treat a difference there as a new issue rather than as something this
document promises.
