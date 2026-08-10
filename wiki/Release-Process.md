# Release Process

How a change gets from a merged PR to a published gem. Maintainer-facing.

## The pipeline

```
PR merged to main
   │
   ├─► Changelog workflow ──► release-drafter updates the DRAFT release notes
   │
   └─► (when a version bump lands on main)
        Ruby Gem workflow ──► publish to GitHub Packages
                          ──► publish to RubyGems
                          ──► publish the release notes (draft → released)
```

The Ruby Gem workflow is triggered by the Changelog workflow completing successfully on `main`, not
by a tag push.

## 1. Conventional Commits and labels

Every PR's type drives its place in the release notes:

| Label | Section |
|---|---|
| `feat` | 🚀 Features |
| `fix` | 🐛 Fixes |
| `chore`, `ci`, `docs`, `build`, `perf`, `test` | 🧰 Maintenance |

Configured in `.github/release-drafter.yml`. Entries render as
`- $TITLE @$AUTHOR (#$NUMBER)`, so PR titles are user-visible — write them for a reader, not for
yourself.

## 2. Draft notes accumulate

The **Changelog** workflow (`.github/workflows/changelog.yml`) runs release-drafter with
`publish: false` on every push to `main`, keeping a draft release continuously up to date. Nothing
is released at this stage.

## 3. Bump the version

Run the **Bump Version** workflow manually (`workflow_dispatch`), choosing `patch`, `minor`, or
`major`:

| Bump | For |
|---|---|
| patch | bug fixes (`fix`) |
| minor | new commands or features (`feat`) |
| major | breaking changes |

It:

1. Reads the current version from `lib/wip/version.rb`
2. Computes the next one per SemVer
3. Rewrites `lib/wip/version.rb`
4. Verifies the gemspec still builds (`gem build wslc-wip.gemspec`)
5. Commits as `chore: bump version to vX.Y.Z` on a `bump-version-vX.Y.Z` branch
6. Opens a PR

Contributors never bump the version in a feature PR.

## 4. Merge the bump PR

Merging it to `main` triggers Changelog → Ruby Gem.

## 5. Publish

The **Ruby Gem** workflow (`.github/workflows/gem-push.yml`) runs only when the Changelog workflow
succeeded on `main`. It:

1. Extracts the version from `lib/wip/version.rb` → tag `vX.Y.Z`
2. **Checks what already exists**, so a re-run is safe:
   - RubyGems, via the versions API
   - GitHub Packages, via the packages API
   - a **published** (non-draft) GitHub release for the tag
3. Publishes to GitHub Packages, if absent
4. Publishes to RubyGems via trusted publishing (OIDC — no long-lived API key), if absent
5. Publishes the release notes for the tag, if no published release exists

Concurrency group `gem-push`, `cancel-in-progress: false` — publishes never overlap or get
cancelled halfway.

### Why the release check tests `isDraft`

release-drafter keeps a draft release permanently updated, and `gh release view` matches drafts
too. Checking mere existence would see the draft and skip publishing forever. So the workflow
treats only `isDraft == false` as "already released" — the subject of a `fix(ci)` commit worth
knowing about before you touch that step.

## Idempotence

Every publish step is guarded by an existence check, so re-running the workflow after a partial
failure resumes rather than erroring on "version already exists". If RubyGems succeeded and GitHub
Packages failed, a re-run pushes only the latter.

## Manual verification after a release

```bash
gem install wslc-wip
wip version
```

- [RubyGems](https://rubygems.org/gems/wslc-wip)
- [Releases](https://github.com/slidict/wip/releases)

## What ships in the gem

`wslc-wip.gemspec`:

```ruby
spec.files = Dir['lib/**/*', 'exe/*', 'README.md', 'LICENSE']
```

So `spec/`, `docs/`, `.github/`, and this wiki are **not** in the gem — only the library, the
executable, the README, and the licence. Adding a runtime file outside `lib/` or `exe/` means
updating that glob.

`rubygems_mfa_required` is set, and RubyGems publishing uses trusted publishing rather than a
stored API key.

## Related

- [Development](Development)
- [Testing and Linting](Testing-and-Linting)
