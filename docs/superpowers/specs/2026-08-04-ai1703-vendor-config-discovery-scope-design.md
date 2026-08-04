# Vendor MCP config exclusion must follow vendor discovery, not the repository root

Design for AI-1703. Builds on the merged AI-1632 containment (kcap-cli#427) and the merged
AI-1706 review-context server (kcap-cli#443).

Revision 2 — rewritten after round 1 of spec review. The load-bearing change from revision 1 is
that the cwd prefix is now derived from **git's own path bytes**, not from the filesystem, and that
one byte-level classifier serves both the exclusion filter and the review-context extractor.

## The defect

`WorktreeManager.WorkspaceMcpConfigPaths` is a list of **root-relative** paths. Every consumer
matches it against a path relative to the repository root:

- `IsUnderExcluded(rel, exclusions, caseSensitive)` — the borrowed-snapshot manifest filter, a plain
  `rel == prefix || rel.StartsWith(prefix + "/")` over a decoded string;
- `NeutralizeWorkspaceMcpConfig(worktreePath)` — the owned-worktree strip;
- `ApplyReservedIndexPolicyAsync` — the `skip-worktree` marking;
- `ExtractReviewContextEntriesAsync` → `ClassifyReservedPath` — the AI-1706 reserved-path classifier,
  over raw bytes.

A borrowed snapshot can execute in a directory **below** the repository root.
`CreateBorrowedSnapshotAsync(sourceRepoRoot, requestedCwd, …)` takes the two independently, and
`AgentOrchestrator` passes `snapshotGitRoot` and `borrowedSnapshotSource` — the git root and the
user's actual cwd. The returned `WorktreeInfo.Path` is the *execution path*, not the snapshot root.
So a review flow started from `<repo>/src` produces a snapshot whose cwd is `<snapshot>/src`, and
`src/.codex/config.toml` is matched against a list containing only `.codex/config.toml`. It is not
excluded, and it lands in the tree the reviewer executes in.

Separately and independently of scope, `.github/mcp.json` is **not on the list at all** — the list
has `.github/copilot/mcp.json`, a different path. That one is live today at the repository root of
every borrowed snapshot.

## Vendor discovery matrix

Round 1 correctly refused to accept an ancestor-chain closure proved from two vendors and applied to
eight paths. Each canonical entry, with the discovery rule that justifies its scope:

| Path | Vendor | Documented discovery | Within ancestor chain? |
|---|---|---|---|
| `.mcp.json` | Claude Code | Searches **upward** through parent directories, merging; continues above the git root | yes |
| `.mcp.json`, `.github/mcp.json` | Copilot CLI | Walks **cwd → repository root**; `.mcp.json` wins in the same directory | yes |
| `.codex/config.toml` | Codex | Layers **repository root → cwd**, closest wins, trusted projects only | yes |
| `.cursor/mcp.json` | Cursor | Project root only; no documented nested discovery | yes (subset) |
| `.gemini/settings.json` | Gemini | Workspace root; measured root-scoped during AI-1632 | yes (subset) |
| `.kiro/settings/mcp.json` | Kiro | Workspace root; **measured** to spawn at session setup during AI-1632 | yes (subset) |
| `.vscode/mcp.json` | editor-generic | Workspace-folder root. GitHub documents that Copilot CLI does **not** read it; kept because VS Code and other CLIs do | yes (subset) |
| `.copilot/mcp.json`, `.copilot/mcp-config.json` | Copilot | GitHub documents `~/.copilot/mcp-config.json` as **user**-scope; no documented workspace form | speculative, see below |

No supported vendor is documented to search **downward** into descendants, or to load config from a
sibling of the cwd. Claude Code searches above the git root; above the snapshot root is the daemon's
own `borrowed-snapshots/` parent, which holds no branch content, so that direction is closed by
construction rather than by this rule.

The two `.copilot/…` entries are not documented workspace discovery paths. They are kept, and
`.copilot/mcp-config.json` added, under the list's standing rationale — recorded verbatim in the code
today — that the list is deliberately wider than the set of vendors known to read each file, "the
point is that the next vendor is safe before anyone thinks about it". An entry that no vendor reads
costs one string.

### Threat-model boundary, stated rather than assumed

The property this design delivers is: **no branch-authored vendor config is loaded by the vendor
process the daemon launches, at the cwd the daemon launches it in, without model involvement.** That
is the AI-1632 property — Kiro was measured spawning a declared command at session setup, with no
prompt and no tool call.

It does **not** cover a model that deliberately changes directory and launches another supported CLI
in a descendant or sibling. That is not a config-discovery hole: a model that can spawn a CLI is
already executing arbitrary commands, and the boundary for that is the reviewer's OS sandbox (the
AI-1584 profile), not this path list. Saying so explicitly is the point — revision 1 left it
implicit, which reads as a closure claim the design does not make.

### Alternative considered and rejected: exclude at every directory

Excluding these names at *every* directory in the tree would also cover the nested-launch case, and
the AI-1706 review-context server means it would cost no review coverage — the content is surfaced
to the reviewer either way.

It is rejected because it makes a fail-closed cap attacker-reachable. `MaxReviewContextBytes` is
256 KiB across the whole manifest, and today at most eight files can be admitted. Under a whole-tree
rule a branch could commit 300 KiB spread across many `.mcp.json` files and make
`borrowed_snapshot_review_context_capacity_exceeded` refuse **every** launch of that repository. The
earlier work on this surface was bitten twice by exactly this shape — a hostile branch weaponizing a
fail-closed guard — and the ancestor chain keeps the candidate set bounded by `paths × depth`.

It would also strip this repository's own committed `kcap/.mcp.json` from every snapshot.

## Correction to the issue's premise

AI-1703 states that "AI-1632's owned-worktree neutralization walks real ancestor directories and does
cover this". **That is wrong**, and the fix must not be designed around it.
`NeutralizeWorkspaceMcpConfig` walks the components of each *relative path* (`.kiro`, then
`settings`, then `mcp.json`) so it can unlink the first component that is a symlink. It does not walk
directories of the tree. It is exactly as root-scoped as the borrowed path.

It is nonetheless **not** a live defect, for a reason unrelated to the walk: every owned-worktree
launch runs at the worktree root. `CreateAsync` and `BuildStandaloneSnapshotAsync` return a
`WorktreeInfo` whose `Path` is the worktree root, and no caller narrows it. With cwd == root the
ancestor chain is `[root]` and root-scoped matching is complete. The owned path is therefore left
alone here, and this document records why, so the next reader does not re-derive it.

The direct-borrow path (`WorktreeInfo.Borrowed(cwd)`, non-snapshot) is out of scope by construction:
it is the user's own checkout, guarded by a certified read-only runtime boundary, and nothing is
stripped there today.

## Design

### The pathname-namespace rule

Round 1's first finding is the one that reshapes this design: **a prefix derived from the filesystem
is not in the same namespace as the paths git reports**, and concatenating one onto the other
produces a comparison that can silently fail to match. Three concrete divergences were named, all
real:

1. **Unicode normalization.** On macOS a directory created as NFC is reported by the filesystem as
   NFD. `NormalizeRelativePath` already rejects any git path that is not NFC — so the *git* side is
   guaranteed NFC or the build fails closed — but nothing normalizes the *prefix*. An NFD prefix
   against an NFC git path under-excludes.
2. **Case sensitivity read from the wrong volume.** `caseSensitive` is probed with
   `ProbeCaseSensitiveFileSystem(destination)` and then applied to paths resolved against the
   *source*. A case-insensitive source plus a case-sensitive snapshot volume yields `SRC` from the
   requested cwd and `src/.mcp.json` from git, and no match.
3. **Rooted relative results.** `Path.GetRelativePath` returns a *rooted* path when the two paths are
   on different Windows volumes. The existing guard tests only for `".."` and a `"../"` prefix, so
   revision 1's claim that escape "is already rejected" was false.

The fix is to stop deriving the prefix from the filesystem at all.

**Derive the prefix from git.** Run, in the source repository, with the process cwd set to the
requested cwd:

```
git -c core.quotePath=false rev-parse --show-prefix
```

and read the raw bytes. This returns the cwd's path relative to the work-tree top **in git's own
spelling and byte representation** — the same namespace as `ls-files` output — or empty for the
root. It removes the separator, rooted-path and `..` classes outright, because the value never
passes through `Path.GetRelativePath`. `core.quotePath=false` is required or non-ASCII components
come back C-quoted.

The result is then put through the existing `NormalizeRelativePath` (after strict UTF-8 decoding),
so a prefix carrying `\`, CR, LF, a `.git` component, or non-NFC bytes fails the build closed
exactly as a manifest path would. The existing filesystem-derived `relativeCwd` is kept **only** for
`ContainedPath(final, relativeCwd)` and `Directory.Exists(executionPath)`, which are filesystem
operations and belong in filesystem terms — and its rooted-path gap is fixed there independently
(`Path.IsPathRooted` rejected alongside the `..` checks), because that check guards a path escape
regardless of this feature.

**Compare prefixes permissively, and say which way the error goes.** Even with a git-derived prefix,
an exact byte comparison is not obviously right: git's index spelling and git's cwd resolution can
still disagree on a case-insensitive volume. So the directory-prefix portion of a match is compared
with ASCII case folding applied *unconditionally*, independent of the probed `caseSensitive`, and
with both sides already NFC by construction.

This is deliberately over-broad, and the error direction is the argument: an over-broad prefix
excludes a vendor config file at a *differently-cased sibling directory* — content that is excluded
anyway under any spelling, and that the review-context server still surfaces. An under-broad prefix
leaves a hostile config live in the tree. Over-exclusion is a non-event; under-exclusion is the
vulnerability this issue exists to close.

### One classifier, not two

Round 1's fifth finding: passing one array to two matchers does not make them agree.
`IsUnderExcluded` compares decoded strings with `StringComparison.OrdinalIgnoreCase` (full Unicode
case folding); `ClassifyReservedPath` compares raw bytes with `AsciiPathEquals` (ASCII folding only).
With a purely ASCII canonical list the difference was unobservable. A cwd prefix can contain
non-ASCII, so it becomes observable — a path could be excluded by one and `Unrelated` to the other,
which is precisely the "contained but not reviewable" state this change must not create.

So the expansion is classified **once**, over raw bytes, by `ClassifyReservedPath`, and both
consumers read that one result:

```csharp
internal sealed record SnapshotExclusionPlan(
    ImmutableArray<string> VendorConfigPaths,   // canonical list × the git-derived ancestor chain
    ImmutableArray<byte[]> VendorConfigPathBytes,
    string[] SnapshotExclusions);               // .capacitor, .attached, vendor paths, caller extras

internal static SnapshotExclusionPlan PlanSnapshotExclusions(
    string gitRelativeCwd, IEnumerable<string>? additional = null);
```

`ReadSourceManifestAsync` already iterates raw records and calls `NormalizeRelativePath` on the
decoded form; it gains a `ClassifyReservedPath` call on the raw bytes *before* decoding, and treats
`Exact` and `Descendant` as excluded. `IsUnderExcluded` is retained only for `.capacitor`,
`.attached` and caller-supplied `excludePaths`, which are ASCII constants and daemon-supplied — the
namespace question does not arise for them. The vendor list no longer flows through it.

A test asserts the invariant directly: for a corpus of paths spanning case and normalization
variants, `excluded(path) == (ClassifyReservedPath(path) != Unrelated)` for every vendor path in the
plan. That is the lockstep property, stated as an equivalence rather than as "we passed the same
array to both".

The `SnapshotExcludedPaths` static property is **removed**, not left beside the new plan. Leaving it
is precisely the two-lists shape whose comment in the code today reads "Two lists of the same thing
is how that happened".

### What "reviewable" actually means — narrowed, not claimed

Round 1 is right that containment and reviewability range over different data. Containment operates
on the working tree (`ls-files -co --exclude-standard`); review context contains **index stage-0
blobs only**, and the manifest says so in a field (`UnstagedAndUntrackedOmitted: true`). So an
*untracked* reserved config, or unstaged working-tree bytes of a tracked one, is contained but not
reviewable.

That is a pre-existing property of AI-1706, not something this change introduces, and it is not
silently inherited: the reviewer is told, by that manifest field, which bytes it is looking at. This
design states the narrowing explicitly and does not widen it — carrying untracked working-tree bytes
into review context would reintroduce the AI-1680 failure that killed the previous attempt, where
a developer's `skip-worktree` local override would have been published to the reviewer's model.

The lockstep property this design does claim is therefore precise: **for tracked stage-0 content,
every path excluded from the snapshot is classified as reserved by the extractor.**

### Ordering

`CreateReviewContextGenerationAsync` runs before `ReadSourceManifestAsync` in
`BuildIndependentSnapshotOnceAsync`. That is safe here because the plan is computed once, before
either, and is immutable; both read the same `VendorConfigPathBytes`. Both also read the same
`initialIndex` / source, and the existing end-of-build re-check (`sourceHead`, `initialIndex`,
`ManifestsEqual`) still fails the whole build with `SourceChangedException` if the source moved
underneath. Failed generations are deleted on every throw path already.

### Reserved index policy

Round 1's third finding is a real bug in revision 1: `initialIndex` is read from **source**, while
`update-index` runs in **destination**. The destination is a fresh clone checked out at `HEAD`, so a
path that is staged-but-not-committed in the source is in the source index and *not* in the
destination index. Batching it in would make `update-index --skip-worktree` fail on a legitimate
snapshot — and revision 1 also proposed promoting that failure from a swallowed catch to a hard
error, which together would refuse the launch.

Corrected: read the **destination** index after checkout (`git -C destination ls-files -z`),
intersect the plan's vendor paths with that, and mark only paths proven present. With membership
established, a failure is a real failure and propagates. The source-side consistency re-check at the
end of the build is unchanged and still catches a source that moved.

Round 1's fourth finding bounds the mechanism:

- The batch is fed on **stdin**: `git update-index --skip-worktree -z --stdin`. This removes the
  `ARG_MAX` ceiling for a deep cwd and removes pathspec interpretation of a leading `:` or `-` in an
  ancestor directory name — `--stdin` paths are literal.
- Aggregate pathname bytes are `O(depth²)`, not `O(depth)`. An explicit cap on the plan's candidate
  count and on its aggregate path bytes is added, rejected at plan construction with a coded error
  (`borrowed_snapshot_cwd_too_deep`), so the bound is stated rather than inherited from `ARG_MAX`.
- `MaxReviewContextBytes` charges only blob content. The serialized manifest is bounded separately —
  path strings, base64 expansion and JSON overhead are not free — with its own cap and coded error.
  Revision 1's claim that the content cap was "the real bound" was wrong.

### Deriving the prefix on both build paths

`CreateBorrowedSnapshotAsync` and `SyncFromSourceCoreAsync` both compute the git-relative cwd the
same way, through one private helper, from the same `git rev-parse --show-prefix` primitive. If they
diverged, a per-round refresh would reintroduce a file the initial build excluded.

That is asserted at the security boundary, not at the helper — see test 7.

## Testing

Every test names the discovery shape it defends and carries a **positive control** proving the file
would otherwise be present. Round 1 found four of revision 1's tests could pass vacuously; those are
rewritten here.

1. **Codex sub-cwd** — source repo with tracked `src/.codex/config.toml`; borrowed snapshot with
   `requestedCwd = <source>/src`; assert absent from the snapshot. Control: the same build with
   `requestedCwd = <source>` leaves it present.
2. **Copilot `.github/mcp.json` at root** — the file is **tracked**, and the test asserts it appears
   in `ls-files -co` before asserting it is absent from the snapshot. (Revision 1 used a surviving
   sibling, which proved only that something under `.github/` was copied.)
3. **Intermediate directory** — `relativeCwd = "a/b"`, tracked config at `a/.mcp.json`; excluded.
   Control: a root-cwd build of the same fixture leaves `a/.mcp.json` present.
4. **Sibling not excluded** — `relativeCwd = "a"`, config at `b/.mcp.json`; **present**. Keeps the
   rule from silently becoming the whole-tree variant rejected above.
5. **Root cwd unchanged** — for an empty git prefix, `plan.VendorConfigPaths` equals
   `WorkspaceMcpConfigPaths` exactly. Pins the no-regression claim for the common launch.
6. **Classifier equivalence** — over a corpus including NFC/NFD pairs, mixed case, and non-ASCII
   ancestor names: for every path, snapshot exclusion and `ClassifyReservedPath != Unrelated` agree.
   This is the lockstep test.
7. **Refresh parity at the boundary** — an initial sub-cwd snapshot; then add and modify configs at
   the root, an intermediate directory and the cwd; run `SyncBorrowedSnapshotFromSourceAsync`; assert
   every ancestor config is absent, each tracked one appears in the newly published review context, a
   sibling survives, and no kcap-created deletion appears in `git status`. Includes a refresh whose
   source spelling differs in case from the destination spelling. (Revision 1 compared two plan
   objects, which proves nothing about the consumers.)
8. **Reserved index policy** — a **HEAD-tracked** `src/.mcp.json` under `relativeCwd = "src"`: assert
   the destination index contains it, that its skip-worktree bit is set, and that `git status` in the
   snapshot is clean. Negative control: with the policy disabled the same fixture reports a deletion,
   proving the assertion is not vacuous.
9. **Staged-only addition** — `src/.mcp.json` added to the source index but not committed: the build
   succeeds (it is not in the destination index, so it is not batched), the file is excluded from the
   snapshot, and it appears in review context. This is the case revision 1's design would have
   crashed on.
10. **Rooted / escaping cwd** — a cwd that yields a rooted `Path.GetRelativePath` result is rejected;
    `..` and `../` remain rejected.
11. **Depth cap** — a cwd deep enough to exceed the candidate/aggregate-byte cap is rejected with the
    coded error rather than producing an oversized batch.
12. **List membership** — the existing `WorkspaceMcpNeutralizationTests` membership assertion is
    extended to require `.github/mcp.json` and `.copilot/mcp-config.json`.

The AI-1632 live certification (`KCAP_WORKSPACE_MCP_CERT=1`) is re-run unchanged, with its existing
control, which asserts the declared command *does* spawn when the guard is removed. This change
widens the excluded set and must not disturb the measured root-level behaviour.

## Out of scope

- The owned-worktree strip, for the reason recorded above (cwd is always the worktree root). If a
  sub-cwd owned launch is ever added, `NeutralizeWorkspaceMcpConfig` gains the same chain and this
  document is the reason it must.
- Widening review context to untracked or working-tree bytes. Stated above as a deliberate
  narrowing.
- AI-1675 (`CopyDirectory` recursion / symlink dereference). Untouched, and repairing it here would
  arm the exfiltration that issue describes.
