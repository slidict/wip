#!/usr/bin/env bash
# Publish wiki/*.md to the GitHub wiki repository.
#
# The wiki lives in a separate git repo (slidict/wip.wiki.git), so it can't be
# updated by a pull request against this repository. This script mirrors the
# directory into a clone of it and pushes.
#
# Usage:
#   ./wiki/publish.sh [commit message]
#
# Requires push access to the wiki repository.

set -euo pipefail

WIKI_REMOTE="${WIKI_REMOTE:-https://github.com/slidict/wip.wiki.git}"
MESSAGE="${1:-docs(wiki): sync from main repo}"

SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

echo "Cloning $WIKI_REMOTE"
git clone --quiet "$WIKI_REMOTE" "$WORK_DIR/wiki"

# Remove pages that no longer exist in the source, then copy the current set.
# README.md and publish.sh are tooling for this directory, not wiki pages.
find "$WORK_DIR/wiki" -maxdepth 1 -name '*.md' -delete
for file in "$SOURCE_DIR"/*.md; do
  name="$(basename "$file")"
  [ "$name" = "README.md" ] && continue
  cp "$file" "$WORK_DIR/wiki/$name"
done

cd "$WORK_DIR/wiki"

if git diff --quiet && git diff --cached --quiet && [ -z "$(git status --porcelain)" ]; then
  echo "No changes to publish."
  exit 0
fi

git add -A
git commit --quiet -m "$MESSAGE"
git push --quiet origin HEAD
echo "Published to $WIKI_REMOTE"
