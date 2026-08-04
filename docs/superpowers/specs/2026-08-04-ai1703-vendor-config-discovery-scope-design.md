# Vendor MCP config exclusion must follow vendor discovery, not the repository root

Design for AI-1703. Builds on the merged AI-1632 containment (kcap-cli#427) and the merged
AI-1706 review-context server (kcap-cli#443).

## The defect

`WorktreeManager.WorkspaceMcpConfigPaths` is a list of **root-relative** paths. Every consumer
matches it against a path that is relative to the repository root:

- `IsUnderExcluded(rel, exclusions, caseSensitive)` — the borrowed-snapshot manifest filter
  (`WorktreeManager.cs`), a plain `rel == prefix || rel.StartsWith(prefix + "/")`;
- `NeutralizeWorkspaceMcpConfig(worktreePath)` — the owned-worktree strip, which walks the
  *components of each relative path* from the worktree root;
- `ApplyReservedIndexPolicyAsync` — the `skip-worktree` marking;
- `ExtractReviewContextEntriesAsync` — the AI-1706 reserved-path classifier.

A borrowed snapshot can execute in a directory **below** the repository root.
`CreateBorrowedSnapshotAsync(sourceRepoRoot, requestedCwd, …)` takes the two independently, and
`AgentOrchestrator` passes `snapshotGitRoot` and `borrowedSnapshotSource` — the git root and the
user's actual cwd. The returned `WorktreeInfo.Path` is the *execution path*, not the snapshot root.
So a review flow started from `<repo>/src` produces a snapshot whose cwd is `<snapshot>/src`, and
`src/.codex/config.toml` is matched against a list containing only `.codex/config.toml`. It is not
excluded, and it lands in the tree the reviewer executes in.

Both vendor claims in the issue were re-verified against vendor documentation rather than accepted:

- **Codex** loads project config from `.codex/config.toml` "ordered from the project root down to
  your current working directory (closest wins; trusted projects only)". A worktree under the repo's
  own `.capacitor/` inherits the repo's trust by design (that is why worktrees are placed there), so
  the trust gate does not save us.
- **Copilot CLI** walks "from your current working directory up to the repository root" loading
  both `.mcp.json` and `.github/mcp.json`, with `.mcp.json` winning in the same directory.
  `.github/mcp.json` is **not on our list at all** — the list has `.github/copilot/mcp.json`, a
  different path. GitHub's docs also state Copilot CLI does *not* read `.vscode/mcp.json`; that entry
  stays, because VS Code and other CLIs do, and the list is deliberately wider than the set of
  vendors known to read each file.

Note that the two vendors walk the chain in opposite directions. That is the same set of
directories either way: **the ancestor chain of the execution cwd, from the snapshot root down to
the cwd, inclusive.**

## Correction to the issue's premise

AI-1703 states that "AI-1632's owned-worktree neutralization walks real ancestor directories and does
cover this". **That is wrong**, and the fix must not be designed around it.
`NeutralizeWorkspaceMcpConfig` walks the components of each *relative path* (`.kiro`, then
`settings`, then `mcp.json`) so it can unlink the first component that is a symlink. It does not walk
directories of the tree. It is exactly as root-scoped as the borrowed path.

It is nonetheless **not** a live defect, for a reason unrelated to the walk: every owned-worktree
launch runs at the worktree root. `CreateAsync` and `BuildStandaloneSnapshotAsync` return a
`WorktreeInfo` whose `Path` is the worktree root, and no caller narrows it. With cwd == root, the
ancestor chain is `[root]` and root-scoped matching is complete. The owned path is therefore left
alone here, and this document records why, so the next reader does not re-derive it.

The direct-borrow path (`WorktreeInfo.Borrowed(cwd)`, non-snapshot) is out of scope by construction:
it is the user's own checkout, guarded by a certified read-only runtime boundary, and nothing is
stripped there today.

## Design

### One derivation, several consumers

The existing code has a load-bearing comment: "Two lists of the same thing is how that happened" —
the `.kiro/settings/mcp.json` escape came from `SnapshotExcludedPaths` and `WorkspaceMcpConfigPaths`
being maintained separately. Widening the model must not reintroduce a second list.

Introduce a single value computed once per snapshot build:

```csharp
internal sealed record SnapshotExclusionPlan(
    ImmutableArray<string> VendorConfigPaths,  // expanded along the cwd ancestor chain
    string[] SnapshotExclusions);              // .capacitor, .attached, vendor paths, caller extras

internal static SnapshotExclusionPlan PlanSnapshotExclusions(
    string relativeCwd, IEnumerable<string>? additional = null);
```

`VendorConfigPaths` is the cross product of `WorkspaceMcpConfigPaths` with the ancestor chain of
`relativeCwd`: for `relativeCwd = "src/cli"` and the canonical entry `.mcp.json`, it yields
`.mcp.json`, `src/.mcp.json`, `src/cli/.mcp.json`. For `relativeCwd = "."` it is exactly today's
list, so the overwhelmingly common root-cwd launch is byte-for-byte unchanged.

`SnapshotExclusions` is `[".capacitor", ".attached", ..VendorConfigPaths, ..additional]`, replacing
the `SnapshotExcludedPaths` static property. The static property is removed rather than left beside
the new one — leaving it is precisely the two-lists shape that caused the original escape.

`BuildIndependentSnapshotAsync` / `BuildIndependentSnapshotOnceAsync` take the plan instead of a
`string[] exclusions`, so the manifest filter, the reserved index policy and the review-context
extractor are all driven from one object that a single call site built.

### Consumers

1. **Manifest filter** (`ReadSourceManifestAsync` → `IsUnderExcluded`): pass
   `plan.SnapshotExclusions`. No change to the matching logic; only the input widens. The
   `.attached` / `.capacitor` reserved-path throw stays keyed on those two names, unchanged.

2. **Reserved index policy** (`ApplyReservedIndexPolicyAsync`): mark `plan.VendorConfigPaths`
   `skip-worktree`, for the same reason the current code marks the canonical list — a tracked
   `src/.mcp.json` absent from the snapshot would otherwise show as a deletion in the reviewer's
   `git status` and diff, and could produce a review finding about a deletion kcap performed.
   Today this issues one `git update-index` per path inside a `try`/`catch` for "absent from the
   index". The expanded list is `paths × depth`, so instead intersect with the index listing already
   read at the top of `BuildIndependentSnapshotOnceAsync` (`ls-files --stage -z`) and issue a single
   batched `update-index --skip-worktree --` call for the paths actually present. This removes the
   catch-swallow as well: with membership established from the listing, a failure is a real failure
   and should propagate.

3. **Review context** (`ExtractReviewContextEntriesAsync`, AI-1706): build `reserved` from
   `plan.VendorConfigPaths`, not from `WorkspaceMcpConfigPaths`. Without this the fix would *widen*
   the AI-1680 blind spot it is adjacent to: `src/.mcp.json` would become excluded from the snapshot
   (good) while remaining invisible to the reviewer (bad) — a hostile config one directory down,
   contained but unreviewable. The two must move together.

   `ValidateReviewContextManifest`'s cap `manifest.Entries.Length > WorkspaceMcpConfigPaths.Length`
   becomes `> plan.VendorConfigPaths.Length`, threaded to the validator. The invariant it encodes —
   at most one entry per reserved path — is preserved, over the expanded set.
   `matchedCanonicalPaths` already keys on the matched canonical path, which is now the concrete
   expanded path, so the collision check keeps its meaning without change.

   `MaxReviewContextBytes` (256 KiB total) is unchanged and is the real bound on this surface. A
   deep cwd multiplies the number of *candidate* paths, not the bytes admitted.

### Deriving `relativeCwd`

`CreateBorrowedSnapshotAsync` already computes `relativeCwd` and already rejects an escape
(`borrowed_snapshot_cwd_outside_source`). `SyncFromSourceCoreAsync` receives `executionPath` and
already validates containment in `target`; it derives its own relative form the same way.

Both must produce the same normalized shape (`/` separators, `.` for the root, no trailing slash,
no `.` or `..` components) or the two build paths would exclude different sets and a per-round
refresh would reintroduce a file the initial build excluded. One private normalizer, used by both,
with a test asserting the initial build and the refresh produce identical plans for the same cwd.

Depth is bounded by the existing containment checks; no separate cap is introduced, and the batched
`update-index` means depth no longer costs a process spawn per path.

### The `.github/mcp.json` addition

Added to `WorkspaceMcpConfigPaths`. Independent of the scope change and live even at cwd == root:
`.github/mcp.json` is unprotected today at the repository root of every borrowed snapshot.

`.copilot/mcp-config.json` is added alongside the existing `.copilot/mcp.json`. GitHub documents
`~/.copilot/mcp-config.json` as user-scope, so the workspace-relative form is not a documented
discovery path; it is added under the same standing rationale as the rest of the list — the entry
costs nothing and the list exists so the next vendor is safe before anyone thinks about it. The
existing `.copilot/mcp.json` entry is kept for the same reason.

## Testing

Every test states which discovery shape it defends and carries a **positive control** proving the
file would otherwise be present — the DoD requires it, and this codebase has been bitten by
containment tests that passed because the fixture never produced the file at all.

1. **Codex sub-cwd** — source repo with `src/.codex/config.toml`; borrowed snapshot with
   `requestedCwd = <source>/src`; assert absent from the snapshot. Control: the same build with
   `requestedCwd = <source>` leaves `src/.codex/config.toml` present, proving the fixture writes a
   real file and that the assertion is about the cwd chain rather than about the file never existing.
2. **Copilot `.github/mcp.json` at root** — present in source, absent from the snapshot. Control: a
   sibling `.github/unrelated.json` survives, proving the exclusion is path-scoped and the fixture
   populates `.github/`.
3. **Intermediate directory** — `relativeCwd = "a/b"`, config at `a/.mcp.json`; excluded. Asserts the
   whole chain is covered, not just the two endpoints.
4. **Sibling not excluded** — `relativeCwd = "a"`, config at `b/.mcp.json`; **present**. This is the
   test that keeps the rule from silently becoming "every directory in the tree", which would strip
   this repository's own committed `kcap/.mcp.json` from every snapshot.
5. **Root cwd unchanged** — for `relativeCwd = "."`, `plan.VendorConfigPaths` equals
   `WorkspaceMcpConfigPaths` exactly. Pins the no-regression claim for the common launch.
6. **Review-context parity** — a tracked hostile `src/.kiro/settings/mcp.json` with
   `requestedCwd = <source>/src`: absent from the snapshot *and* present in the review-context
   manifest with its exact path. This is the test that keeps containment and reviewability moving
   together; the AI-1706 tests only cover the root case.
7. **Refresh parity** — initial build and `SyncBorrowedSnapshotFromSourceAsync` for the same cwd
   produce identical exclusion plans.
8. **Reserved index policy** — a tracked `src/.mcp.json` under `relativeCwd = "src"` does not appear
   as a deletion in the snapshot's `git status`.
9. **List membership** — the existing `WorkspaceMcpNeutralizationTests` membership assertion is
   extended to require `.github/mcp.json` and `.copilot/mcp-config.json`.

The AI-1632 live certification (`KCAP_WORKSPACE_MCP_CERT=1`) is re-run unchanged, control included:
this widens the excluded set and must not disturb the measured spawn behaviour at the root.

## Out of scope

- The owned-worktree strip, for the reason recorded above (cwd is always the worktree root). If a
  sub-cwd owned launch is ever added, `NeutralizeWorkspaceMcpConfig` gains the same chain and this
  document is the reason it must.
- Re-checking vendor discovery beyond Codex and Copilot. Kiro, Gemini and Cursor were measured
  root-scoped during AI-1632 and the ancestor-chain rule strictly widens their coverage; nothing
  here narrows any vendor.
- AI-1675 (`CopyDirectory` recursion / symlink dereference). Untouched, and repairing it here would
  arm the exfiltration that issue describes.
