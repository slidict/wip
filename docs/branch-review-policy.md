# Main branch review policy

The repository's active `main` ruleset (ID `20119395`) dismisses stale approvals
when a new reviewable commit changes an approved pull request. The updated diff
must be approved again before merging. A successful CI run alone does not replace
that review.

The configuration snapshot is [main.json](../.github/rulesets/main.json).
GitHub does not apply this file automatically: repository administrators maintain
the live ruleset and compare its writable fields against this snapshot.

The policy keeps one required approval, the existing four required checks, and
the prohibition on non-fast-forward pushes. It does not add bypass actors or
change the allowed merge method. `require_last_push_approval` remains disabled;
stale approval dismissal provides the new-diff review requirement without adding
a separate identity constraint to the automation flow.

The required check contexts remain:
- `Tests (ubuntu-latest)`
- `Tests (windows-latest)`
- `Lifecycle against real WSLC`
- `Native AOT publish`

Each context remains bound to its existing GitHub Actions integration. Changing
review settings does not make a missing or failed check acceptable.

Review automation must approve the updated diff after a push. Do not treat an
approval on an older commit as approval of newly added code. If a reviewer is
unavailable or rate-limited, keep the PR open until a fresh review is completed.
Do not bypass the ruleset, reduce the required checks, or force-push `main` to
complete a PR.

## Verification

Use a feature PR containing a real documentation or code change. Wait for an
approval and the required checks, then push a further reviewable change to that
same feature branch. Record the old approval's dismissal, the updated HEAD,
`REVIEW_REQUIRED` and the blocked merge state before requesting a new review.
After a fresh approval and successful checks, the PR can merge normally.

GitHub dismisses approvals when the reviewable diff changes, not necessarily
for an empty commit that leaves that diff unchanged. Use a real content change
for this verification. The verification never merges unreviewed changes or
changes any of the required checks.

References:
- [GitHub ruleset review protections](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets)
- [Repository ruleset REST API](https://docs.github.com/en/rest/repos/rules#update-a-repository-ruleset)
