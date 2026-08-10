# wiki/ — source for the GitHub wiki

This directory mirrors the [wip wiki](https://github.com/slidict/wip/wiki) one file per page.
Filenames are the page names: `wip-up.md` → the "wip up" page, linked as `[wip up](wip-up)`.

Keeping the sources here means wiki changes can be reviewed in a pull request, unlike edits made
through the wiki UI.

> This `README.md` and `publish.sh` are tooling for the directory, not wiki pages — `publish.sh`
> skips them.

## Structure

One page per feature. Hub pages index; feature pages explain.

| Hub | Feature pages |
|---|---|
| `Home.md`, `_Sidebar.md` | navigation |
| `Concepts.md`, `Choosing-a-Mode.md`, `Glossary.md` | introduction |
| `Container-Mode.md`, `Compose-Mode.md`, `Compose-Native-Mode.md` | the three modes |
| `Configuration-Reference.md` | one page per `wip.yml` feature |
| `CLI-Command-Reference.md` | one page per command (`wip-*.md`) |
| `Guides.md` | one page per task |
| `Troubleshooting-and-FAQ.md` | one page per error, plus `FAQ.md` |
| `Comparison.md` | one page per compared tool |
| `Development.md` | `Architecture.md`, `Testing-and-Linting.md`, `Release-Process.md` |

## Publishing

The GitHub wiki is a separate git repository. To push this directory to it:

```bash
./wiki/publish.sh
```

Or by hand:

```bash
git clone https://github.com/slidict/wip.wiki.git /tmp/wip-wiki
cp wiki/*.md /tmp/wip-wiki/
rm -f /tmp/wip-wiki/README.md          # not a wiki page
cd /tmp/wip-wiki
git add -A && git commit -m "docs(wiki): sync from main repo" && git push
```

Requires push access to the wiki repository.

## Conventions

- **Filenames are page names.** Use `Title-Case-With-Hyphens.md`; hyphens render as spaces.
  Command pages keep their command spelling (`wip-up.md`).
- **Links are bare page names**, not paths or `.md`: `[wip up](wip-up)`. Anchors work:
  `[Concepts](Concepts#design-stance)`.
- **Update `_Sidebar.md`** when adding or renaming a page.
- **One feature per page.** If a page grows two topics, split it and cross-link.
- **Keep it accurate to the code.** Error messages, defaults, and flag names are quoted verbatim
  from `lib/wip/` — when you change behavior there, grep this directory for the old text.
