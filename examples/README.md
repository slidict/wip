# Examples

Working `wip.yml` configs for real-world stacks, each with its own setup instructions.

| Example | Mode | Stack |
|---|---|---|
| [`rails/`](rails) | `container` | Rails + Postgres + Redis, no `compose.yml` |
| [`node/`](node) | `compose-native` | Node.js + MySQL + Redis, driven from a `compose.yml` |

Each directory is meant to be copied into (or adapted for) an existing app — they're not runnable
on their own, since there's no real application code here, just the placeholder Dockerfile a
fresh clone needs to build. See each example's own README for setup steps, and the main
[README](../README.md#which-mode-should-you-use) for how `mode: container` and
`mode: compose-native` differ.
