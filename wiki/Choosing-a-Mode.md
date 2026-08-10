# Choosing a Mode

`mode:` is the first line of `wip.yml` that actually changes behavior. Everything else — which
keys are legal, what `wip up` does, whether `wip run` gets a real ephemeral container — follows
from it.

## Decision table

| Situation | Use |
|---|---|
| No `compose.yml`; you want containers declared in `wip.yml` itself | [`mode: container`](Container-Mode) (default) |
| You have a `compose.yml` and don't want to install anything extra | [`mode: compose-native`](Compose-Native-Mode) |
| You have a `compose.yml` and already use a third-party compose-for-`wslc` tool | [`mode: compose`](Compose-Mode) |

## Decision flow

```
Do you already have a compose.yml?
│
├─ No ──────────────────► mode: container
│                         Declare containers in wip.yml's dependencies:
│
└─ Yes
   │
   ├─ Do you already run a compose-for-wslc binary you trust?
   │  │
   │  ├─ Yes ───────────► mode: compose
   │  │                   wip bridges to it (up/down/exec/logs)
   │  │
   │  └─ No ────────────► mode: compose-native
   │                      wip parses compose.yml itself, no extra binary
   │
   └─ Does your compose.yml use features outside the supported subset
      (health-check conditions, long-syntax volumes, scaling, ...)?
      │
      ├─ Yes ───────────► mode: compose (let the external tool handle it)
      └─ No ────────────► mode: compose-native
```

## What changes between the modes

| | `container` | `compose-native` | `compose` |
|---|---|---|---|
| Where containers are declared | `dependencies:` in `wip.yml` | `services:` in `compose.yml` | `services:` in `compose.yml` |
| Who drives `wslc` | wip | wip | the external compose binary |
| External binary required | no | no | yes (`compose.command`) |
| Names the app | `container:` | `compose.service` | `compose.service` |
| `wip run` | real `wslc run --rm` | real `wslc run --rm` | falls back to `exec` |
| `wip logs` | not available | one service at a time | full compose `logs` |
| `wip up --watch` | yes | yes | no |
| `interaction.type: run` / `build` | yes | yes | no (exec only) |
| `sync.mode` default | `exec` | `exec` | `run` (and `exec` is rejected) |
| `sync.image` / `sync.build` | optional | optional | one is required |

## `wip init` picks for you

`wip init` writes `mode: compose-native` when it finds a compose file next to the `wip.yml` it's
creating, and `mode: container` otherwise. It never writes `mode: compose` on its own, because
that mode needs a `compose.command` only you can name. See [wip init](wip-init).

## Rules the loader enforces

- `mode:` must be one of `container`, `compose`, `compose-native` — anything else is a `ConfigError`.
- `mode: compose` and `mode: compose-native` both require a `compose:` block.
- A `compose:` block without one of those two modes is an error.
- `compose:` is mutually exclusive with `dependencies:` and with `network:`.
- `compose.command` is **required** under `mode: compose` and **rejected** under `mode: compose-native`.

The full list, with the exact messages, is on [Configuration Errors](Configuration-Errors).

## Can I switch later?

Yes, and the shape of `wip.yml` is intended to stay stable across the switch: `mode:` plus a
`compose:` block is the whole surface. Going from `compose` to `compose-native` usually means
deleting `compose.command`; going the other way means adding it back. Your `interaction:` entries,
`.env` handling, and `sync:` block carry over unchanged.
