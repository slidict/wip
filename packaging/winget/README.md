# WinGet packaging

wip ships to WinGet as a **zip installer with a nested portable executable** — the manifest
shape built for exactly this case, an archive containing one binary. `winget install` unpacks
it, registers `wip.exe` under a links directory, and puts that directory on PATH.

`.github/workflows/winget.yml` opens the pull request against
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) automatically when a release
is published. [`manifests/`](manifests) holds what that produces, so the shape is reviewable
in this repository and can be validated by hand before the first submission.

## The token is the risky part — read this first

Automating the submission means storing a credential in a **public** repository's secrets.
That is workable, but the shape of the credential matters more than the repository's
visibility does.

`winget-releaser` requires a **classic** PAT with `public_repo` scope; its documentation
states that fine-grained tokens are not supported. Classic scopes are account-wide: a
`public_repo` token can write to **every public repository that account can push to** — not
just the winget-pkgs fork. On a maintainer's own account that includes this repository.

What is *not* a risk here, having checked the triggers:

- **Pull requests from forks cannot read it.** GitHub does not pass secrets to `pull_request`
  runs from forked repositories, and the only workflow that fork PRs can start is `test.yml`,
  which uses no secrets.
- **No `pull_request_target` and no `workflow_run`.** Both run in the base repository's
  context with secrets available, and both are the usual way a public repository leaks one.
  Neither is used.

What remains, and what is done about it:

| Risk | Mitigation |
|---|---|
| A classic PAT cannot be scoped to one repository | Put the fork on a **dedicated bot account** that owns nothing else, and set the `WINGET_FORK_USER` repository variable to that account. The blast radius becomes one forked public repository. |
| A third-party action is handed the token | Every action is **pinned to a commit SHA**, so a moved tag cannot swap the code that receives it. |
| The token is available to any run of the workflow | The job declares `environment: winget`. Store the secret **on that environment** and add required reviewers, and each use needs a human approval. |
| A leaked token stays useful indefinitely | Give it an **expiry**, and rotate on schedule. |

**If that is more moving parts than you want:** leave `WINGET_TOKEN` unset. The job skips
itself and explains why in the run summary, releases are unaffected, and submissions can be
made by hand with `wingetcreate` — which needs no stored credential at all. The first
submission is human-reviewed anyway, so the automation earns its keep from the second release
onward, not the first.

## One-time setup

1. **Fork microsoft/winget-pkgs**, preferably on a dedicated bot account. If it is not the
   same account that owns this repository, set the `WINGET_FORK_USER` repository variable to
   that account's username.
2. **Create a classic personal access token** with the `public_repo` scope, with an expiry.
   Store it as `WINGET_TOKEN` — on the `winget` environment rather than as a repository
   secret, so required reviewers can gate it. `GITHUB_TOKEN` cannot be used here: it has no
   rights on another repository.
3. **Confirm the package identifier.** `Slidict.Wip` is assumed throughout. The publisher
   half has to correspond to a real publisher name, so check it before the first submission:
   changing an identifier after the fact means a new package, not a rename.

## Validating locally before the first submission

The first submission gets a human review, and a rejected one costs days. Check it locally
first, against a real published release. Both commands are Windows-only — winget does not
run under WSL2, so use a PowerShell or cmd prompt on the host:

```powershell
cd packaging\winget
winget validate --manifest .\manifests
winget install --manifest .\manifests
```

`winget install --manifest` really installs, so expect `wip` on PATH afterwards; undo it with
`winget uninstall Slidict.Wip`.

Both commands need a winget client that knows the schema version the manifests declare —
`winget --version` should be 1.10 or newer for the 1.10.0 manifests here. An older client
rejects them at parse time, which looks like a manifest error but is not one.

## Manifest shape

Three files are required, and they live in [`manifests/`](manifests):

| File | `ManifestType` | What it carries |
|---|---|---|
| [`Slidict.Wip.yaml`](manifests/Slidict.Wip.yaml) | `version` | The identifier and which locale is the default |
| [`Slidict.Wip.installer.yaml`](manifests/Slidict.Wip.installer.yaml) | `installer` | The archive URL, its hash, and the nested-portable wiring |
| [`Slidict.Wip.locale.en-US.yaml`](manifests/Slidict.Wip.locale.en-US.yaml) | `defaultLocale` | Publisher, license and description metadata |

`PackageVersion`, `ReleaseDate`, `InstallerUrl` and `InstallerSha256` change per release;
everything else is stable. The hash is the one the release publishes in `SHA256SUMS`
alongside the zip, so it can be copied from there rather than recomputed.

`ManifestVersion` is **not** a set-and-forget field: winget-pkgs retires older schema
versions and can reject a submission that uses one. These files declare `1.10.0`, the newest
schema in `microsoft/winget-cli` at the time they were written. Before the first submission,
confirm that is still current — the schema directory in
[winget-cli](https://github.com/microsoft/winget-cli/tree/master/schemas/JSON/manifests) is
the authority — and let `winget validate` have the last word. Past the first submission the
automation emits whatever schema its own tooling targets, and these files are the
hand-checked reference rather than the thing submitted.

## If code signing is adopted

`wip.exe` currently ships unsigned. Packaging it as a ZIP is **not** a SmartScreen
mitigation — SmartScreen judges the file and its publisher reputation, not the container it
arrived in — so shipping unsigned is an accepted risk rather than an avoided one.

If that changes, the order in `release.yml` matters: **sign `wip.exe` first**, then zip, then
hash, then attest. Signing after any of those steps rewrites the bytes that the published
checksum and the build attestation describe, and the `InstallerSha256` here would no longer
match what a user downloads.

## Artifact naming is load-bearing

`InstallerUrl` embeds both the tag and the file name, so the naming the release workflow uses
cannot change once the first manifest is accepted:

```
wip-<version>-win-x64.zip
```

It is already shaped to take an `arm64` sibling: adding one means another `Installers:` entry
and another publish job, not a new convention.

## After installing

From PowerShell or cmd, `wip` works directly. **From a WSL2 shell the extension is
required — `wip.exe`** — because bash does not consult PATHEXT. See the
[migration plan](../../docs/csharp-migration-plan.md) §3.5 for the options being weighed
there.
