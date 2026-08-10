# Dockerignore

`wslc build` sends the build context as-is, with no `.dockerignore` support of its own. `wip build`
fills that gap: it reads `.dockerignore` from the build context, stages only the files that survive
it, and hands `wslc` the filtered copy.

For a large project this is the difference between shipping a few megabytes and shipping
`node_modules/` plus `.git/`.

## Where the file is read from

The `.dockerignore` at the **root of the build context** — that is, next to whatever `context:`
resolves to, not necessarily next to `wip.yml`:

```yaml
interaction:
  build:
    type: build
    context: .          # relative to wip.yml's directory
    tag: myapp:dev
```

Under [Compose Native Mode](Compose-Native-Mode), each service's `build.context` gets its own
`.dockerignore` treatment when `wip up` builds it. See [Compose Build](Compose-Build).

## Pattern rules

The same gitignore-style rules the Docker CLI uses:

| Pattern | Matches |
|---|---|
| `node_modules` | any path component named `node_modules`, at any depth |
| `/node_modules` | only at the context root (leading `/` anchors) |
| `tmp/` | same as `tmp` — a trailing slash is stripped |
| `*.log` | any `.log` file at any depth |
| `build/**/*.o` | globs with `/` are matched against the full relative path |
| `!keep.log` | negation — re-includes something an earlier rule excluded |
| `# …`, blank lines | ignored |

Details that matter in practice:

- **Later rules win.** Rules are applied in order; the last one that matches decides.
- **A pattern with no `/` is implicitly `**/pattern`** — it matches at any depth.
- **Matching a directory excludes everything under it.** Every prefix of a path is tested, so
  `node_modules` excludes `node_modules/pkg/index.js` without needing a `/**` suffix.
- **Dotfiles are matched.** `*` matches `.env`.

## Staging behavior

```console
$ wip build
wip: staging build context (/home/me/my-project)
wip: copying build context files: 1240/1240
```

- If `.dockerignore` is missing or has no effective rules, the context is passed through **in
  place** — no copy at all.
- Otherwise wip copies the included files into a temporary directory, runs the build from there,
  and removes it afterward.
- The walk **prunes** ignored directories instead of descending into them. A 3 GB `node_modules/`
  is never walked, let alone copied — unless a later negated rule (`!node_modules/pkg`) could match
  something beneath it, in which case pruning is skipped for correctness.
- **Symlinks are copied as symlinks**, never dereferenced. Following them could pull arbitrary host
  files (`~/.ssh/id_rsa`) into the context.

Progress is reported on its own thread, so one very large file doesn't make the copy look hung.

## Interaction with the shadow context

If `shadow_context:` is configured and applicable, the same `.dockerignore` filtering feeds the
persistent Windows-side shadow directory instead of a throwaway temp dir — so only added/changed
files are copied on later builds. See [Shadow Build Context](Shadow-Build-Context).

## Why the build runs from inside the context

`wslc build` crashes with `ERROR_UNHANDLED_EXCEPTION` when handed an absolute context path, so wip
changes into the staged directory and passes `.` instead. You'll see this reflected in `--debug`
output:

```console
wip: [debug] running: wslc.exe build -t myapp:dev .
```

## A good starting `.dockerignore`

```gitignore
.git
.dockerignore
node_modules
tmp
log
coverage
*.log
.env
```

Note this is separate from `sync.exclude`, which controls the rsync mirror rather than the build
context — they often overlap but serve different steps. See [Source Sync](Source-Sync).

## Related

- [wip build](wip-build)
- [Shadow Build Context](Shadow-Build-Context)
- [Compose Build](Compose-Build)
