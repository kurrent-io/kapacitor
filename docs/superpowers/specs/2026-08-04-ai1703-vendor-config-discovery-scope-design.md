# Vendor MCP config exclusion must follow vendor discovery, not the repository root

Design for AI-1703. Builds on the merged AI-1632 containment (kcap-cli#427) and the merged
AI-1706 review-context server (kcap-cli#443).

Revision 3, after two rounds of spec review. Revision 2 moved the cwd prefix from the filesystem to
git's own bytes. Revision 3 replaces revision 2's unconditional case fold — which was a launch-refusal
primitive — pins the `--show-prefix` byte protocol, persists the prefix across a refresh instead of
re-deriving it, and corrects a factually wrong justification.

## The defect

`WorktreeManager.WorkspaceMcpConfigPaths` is a list of **root-relative** paths, and every consumer
matches it against a path relative to the repository root: the borrowed-snapshot manifest filter
(`IsUnderExcluded`), the owned-worktree strip (`NeutralizeWorkspaceMcpConfig`), the `skip-worktree`
marking (`ApplyReservedIndexPolicyAsync`), and the AI-1706 reserved-path classifier
(`ClassifyReservedPath`).

A borrowed snapshot can execute in a directory **below** the repository root.
`CreateBorrowedSnapshotAsync(sourceRepoRoot, requestedCwd, …)` takes the two independently, and
`AgentOrchestrator` passes `snapshotGitRoot` and `borrowedSnapshotSource` — the git root and the
user's actual cwd. The returned `WorktreeInfo.Path` is the *execution path*, not the snapshot root.
So a review flow started from `<repo>/src` produces a snapshot whose cwd is `<snapshot>/src`, and
`src/.codex/config.toml` is matched against a list containing only `.codex/config.toml`. It is not
excluded, and it lands in the tree the reviewer executes in.

Separately and independently of scope, `.github/mcp.json` is **not on the list at all** — the list has
`.github/copilot/mcp.json`, a different path. That gap is live today at the repository root of every
borrowed snapshot.

## Vendor discovery matrix

| Path | Vendor | Documented discovery | Within ancestor chain? |
|---|---|---|---|
| `.mcp.json` | Claude Code | Searches **upward** through parents, merging; continues above the git root | yes, plus see "above the snapshot root" |
| `.mcp.json`, `.github/mcp.json` | Copilot CLI | Walks cwd → repository root; `.mcp.json` wins in the same directory | yes |
| `.codex/config.toml` | Codex | Layers repository root → cwd, closest wins, trusted projects only | yes |
| `.cursor/mcp.json` | Cursor | Project root only; no documented nested discovery | yes (subset) |
| `.gemini/settings.json` | Gemini | Workspace root; measured root-scoped during AI-1632 | yes (subset) |
| `.kiro/settings/mcp.json` | Kiro | Workspace root; **measured** to spawn at session setup | yes (subset) |
| `.vscode/mcp.json` | editor-generic | Workspace-folder root; GitHub documents Copilot CLI does **not** read it; kept for VS Code and others | yes (subset) |
| `.copilot/mcp.json`, `.copilot/mcp-config.json` | Copilot | GitHub documents `~/.copilot/mcp-config.json` as **user**-scope; no documented workspace form | speculative, kept under the list's standing "wider than known readers" rationale |

No supported vendor is documented to search **downward** into descendants, or into a sibling of the
cwd.

### Above the snapshot root

Claude Code's upward walk does not stop at the git root, so the physical ancestors of the snapshot
matter. Those are `…/borrowed-snapshots/`, then `config.WorktreeRoot`, then whatever contains it —
daemon- and user-owned, holding no branch content.

The property that keeps it that way is `EnsureSeparateRoots(source, root)`, which already refuses a
snapshot root at or under the source checkout with `borrowed_snapshot_root_inside_source`. Round 2
raised this as a Critical bypass; it does not reproduce, because that guard exists.

It has one real residual, which round 2 named and this design closes: the guard is a **lexical**
prefix comparison over `Path.GetFullPath` output, which does not resolve symlinks. A `WorktreeRoot`
configured as a symlink whose target is inside the source checkout passes the string comparison and
lands the snapshot under the source anyway — at which point the source's own root `.mcp.json` is a
physical ancestor of the reviewer's cwd and is loaded by an upward-walking vendor. The guard is
therefore extended to compare fully resolved paths as well as lexical ones, with the same coded
error, and gains the test round 2 asked for.

### Threat-model boundary, stated rather than assumed

The property delivered is: **no branch-authored vendor config is loaded by the vendor process the
daemon launches, at the cwd it launches it in, without model involvement.** That is the AI-1632
property — Kiro was measured spawning a declared command at session setup, no prompt, no tool call.

It does not cover a model that deliberately changes directory and launches another supported CLI in a
descendant or sibling. That is not a config-discovery hole: a model that can spawn a CLI is already
executing arbitrary commands, and the boundary there is the reviewer's OS sandbox (the AI-1584
profile), not this path list.

### Alternative considered and rejected: exclude at every directory

Whole-tree exclusion would also cover the nested-launch case, and — because the AI-1706
review-context server surfaces the content either way — at no review-coverage cost.

**Revision 2 rejected it for a reason that was false**, and round 2 was right to say so. The claim
was that whole-tree exclusion would newly expose the fail-closed `MaxReviewContextBytes` cap. It
would not: one tracked ancestor `.mcp.json` over 256 KiB already trips
`borrowed_snapshot_review_context_capacity_exceeded` today, and the ancestor chain itself admits
`paths × depth` entries rather than "at most 8". Whole-tree exclusion increases the number of trigger
locations for an existing DoS; it does not introduce the class.

The rejection stands on the two reasons that survive scrutiny:

1. **Scope.** Given the explicitly scoped property above — the launched vendor, at its launch cwd,
   without model involvement — the ancestor chain is exactly the closure. Whole-tree buys coverage
   only for the nested-launch case, which is deliberately the sandbox's problem.
2. **Functional regression.** It deletes sibling configs that no vendor in the launch can discover,
   including this repository's own committed `kcap/.mcp.json`.

The pre-existing review-context capacity DoS is real, is not introduced or widened here, and is
**filed separately** rather than folded into this change. Containment must not be gated on
review-context capacity: the right shape is to contain everything and emit a bounded manifest that
declares what it omitted, which is a change to AI-1706's contract, not to this exclusion rule.

## Correction to the issue's premise

AI-1703 states that "AI-1632's owned-worktree neutralization walks real ancestor directories and does
cover this". **That is wrong.** `NeutralizeWorkspaceMcpConfig` walks the components of each *relative
path* (`.kiro`, then `settings`, then `mcp.json`) so it can unlink the first component that is a
symlink. It does not walk directories of the tree. It is exactly as root-scoped as the borrowed path.

It is nonetheless not a live defect, for an unrelated reason: every owned-worktree launch runs at the
worktree root. `CreateAsync` and `BuildStandaloneSnapshotAsync` both return a `WorktreeInfo` whose
`Path` is the worktree root, and `AgentOrchestrator` uses `worktree.Path` directly as the launch cwd
without narrowing. With cwd == root the chain is `[root]` and root-scoped matching is complete.

The direct-borrow path (`WorktreeInfo.Borrowed(cwd)`, non-snapshot) is out of scope by construction:
it is the user's own checkout behind a certified read-only runtime boundary, and nothing is stripped
there today.

## Design

### Deriving the prefix: byte-exact protocol

The prefix comes from git, not the filesystem, so it lands in the same namespace as `ls-files` and
the separator, rooted-path and `..` classes disappear by construction:

```
git -c core.quotePath=false rev-parse --show-prefix
```

run in the source repository with the process cwd set to the requested cwd, captured as raw bytes.
`core.quotePath=false` is required or non-ASCII components come back C-quoted.

Round 2 is right that "then pass it through `NormalizeRelativePath`" is not a specification — that
function rejects LF, an empty string, and a trailing empty component, all three of which this output
has. The parse is therefore pinned:

1. The capture must end with exactly one `0x0A`. Zero, more than one, or any `0x0D` anywhere is
   rejected (`borrowed_snapshot_cwd_prefix_malformed`). Strip that one byte.
2. If what remains is empty, the cwd **is** the repository root: the chain is `[""]` and no further
   parsing happens. This is the common case and it must not go through the path validator at all.
3. Otherwise the remainder must end with exactly one `/`. Strip it.
4. The remainder must be **ASCII** (every byte `< 0x80`). If not, the launch is refused with
   `borrowed_snapshot_cwd_prefix_non_ascii` — see below.
5. Strict-UTF-8 decode, then `NormalizeRelativePath`, which now sees a well-formed relative path and
   applies the existing `\`, CR, LF, `.`/`..`, `.git` and NFC rules.

The filesystem-derived `relativeCwd` is retained **only** for `ContainedPath(final, relativeCwd)` and
`Directory.Exists(executionPath)`, which are filesystem operations. Its rooted-path gap — round 1's
finding, real and independent of this feature — is fixed there by rejecting `Path.IsPathRooted`
alongside the existing `..` checks.

### Case: use the probed volume, not an unconditional fold

Revision 2 folded ASCII case on the directory prefix unconditionally. Round 2 showed that is a
**launch-refusal primitive**: on a case-sensitive destination, tracked `a/.mcp.json` and
`A/.mcp.json` both fold to one canonical candidate, `matchedCanonicalPaths.Add` fails, and
`borrowed_snapshot_review_context_path_collision` refuses every launch of that repository. It also
excluded a sibling no vendor in the launch can discover, contradicting this design's own sibling rule.
Both objections are correct and the unconditional fold is withdrawn.

The comparison instead uses the **existing probed `caseSensitive`**, and the reason it is the right
input is that it is probed on the destination — the volume the vendor actually executes on:

- **Case-insensitive destination.** `SRC` and `src` are the same directory there, so folding is
  correct and no case-varying sibling can exist to collide. This is the case that matters, because it
  is where a `--show-prefix` spelling taken from the on-disk cwd can differ from the index spelling
  and silently fail an exact match.
- **Case-sensitive destination.** `a` and `A` are genuinely distinct, so not folding is correct: the
  sibling rule holds and the collision primitive does not exist.

The cross-volume combination round 1 raised (case-insensitive source, case-sensitive destination,
prefix `SRC` versus index `src`) does not under-exclude: the file is materialised at the index
spelling `src/…`, while the execution path is `ContainedPath(final, "SRC")`, which does not exist on a
case-sensitive destination — so the launch fails closed with `borrowed_snapshot_cwd_missing` rather
than running in a directory whose config was not excluded.

**The ASCII-only prefix restriction (step 4) is what makes this complete.** `AsciiPathEquals` folds
ASCII only; a case-insensitive volume also equates non-ASCII pairs such as `Å`/`å`, which that
matcher would miss — a genuine under-exclusion. Rather than build a second, Unicode-aware folding
path and have two matchers again, a non-ASCII cwd prefix is refused with a coded error. The cost is
bounded and visible: a review flow launched from a subdirectory whose name is not ASCII fails loudly
with a specific error instead of silently under-excluding. The prefix is the operator's own launch
cwd, not branch content, so this is a configuration limitation rather than an attacker-facing one.
The canonical suffixes are all ASCII already, so nothing else is affected.

### One classifier, not two

`IsUnderExcluded` compares decoded strings with `StringComparison.OrdinalIgnoreCase` (full Unicode
folding); `ClassifyReservedPath` compares raw bytes with `AsciiPathEquals` (ASCII only). Invisible
with an ASCII-only canonical list; observable the moment a prefix can vary. So the vendor list flows
through `ClassifyReservedPath` **only**:

```csharp
internal sealed record SnapshotExclusionPlan(
    string GitRelativeCwd,                          // "" for the repository root
    ImmutableArray<string> VendorConfigPaths,       // canonical list × the ancestor chain
    ReadOnlyMemory<byte>[] VendorConfigPathBytes,   // the same set, as the classifier consumes it
    string[] SnapshotExclusions);                   // .capacitor, .attached, caller extras only
```

`ReadSourceManifestAsync` calls `ClassifyReservedPath` on the raw bytes before decoding — preserving
the existing classify-before-decode guarantee — and treats `Exact` and `Descendant` as excluded.
`IsUnderExcluded` is retained only for `.capacitor`, `.attached` and caller-supplied `excludePaths`,
which are ASCII daemon-supplied constants where the namespace question does not arise. The vendor
paths are no longer in `SnapshotExclusions` at all, which is what makes "one classifier" true rather
than asserted.

`SnapshotExcludedPaths` is **removed**, not left beside the plan — leaving it is the two-lists shape
whose comment in the code today reads "Two lists of the same thing is how that happened".

Round 2 is right that `ImmutableArray<byte[]>` is not deeply immutable and that a `string[]` is not
immutable at all. The plan is a value the daemon constructs and never publishes; the byte set is
stored as `ReadOnlyMemory<byte>` and the plan is documented as *not* being a security boundary in
itself — the guarantee is that it is built once per build and passed, not mutated. Stating that is
better than claiming an immutability the type system does not give.

### Refresh: persist the prefix, never re-derive it

Round 2's second finding is a genuine hole in revision 2. `SyncFromSourceCoreAsync` has only
`sourceRepoRoot`, a target root and an `executionPath` **inside the target**. Deriving a source-relative
prefix from the target filesystem would reintroduce exactly the namespace problem the git derivation
exists to remove.

So the git prefix is computed **once**, at `CreateBorrowedSnapshotAsync`, and persisted on
`WorktreeInfo` (`GitRelativeCwd`, alongside `SnapshotRoot` and `ReviewContextRoot`). The refresh path
takes it as a parameter and never recomputes it. A refresh that is not given one is a programming
error and throws; it does not silently fall back to a filesystem derivation.

### Reserved index policy

Revision 2 intersected against `initialIndex`, read from **source**, while `update-index` runs in
**destination** — a fresh clone checked out at `HEAD`. A staged-but-uncommitted `src/.mcp.json` is in
the source index and not in the destination index, so revision 2 would have batched it and, having
promoted the failure to hard, refused a legitimate launch.

Corrected: read the **destination** index after checkout (`git -C destination ls-files -z`), intersect
the plan's vendor paths with that, mark only paths proven present, and let a failure propagate. The
end-of-build source consistency re-check is unchanged.

Bounds, all of which revision 2 got wrong or left open:

- The batch is fed on stdin — `git update-index --skip-worktree -z --stdin` — removing the `ARG_MAX`
  ceiling and pathspec interpretation of a leading `:` or `-` in an ancestor name.
- Aggregate pathname bytes are `O(depth²)`. Concrete caps, enforced at plan construction and rejected
  with `borrowed_snapshot_cwd_too_deep`: **depth ≤ 32** ancestor components, and **aggregate vendor
  path bytes ≤ 64 KiB**. With a 10-entry canonical list that is at most 330 candidates.
- `MaxReviewContextBytes` charges only blob content; the serialized manifest also carries path
  strings, base64 expansion and JSON framing. A separate **1 MiB** cap on the serialized manifest is
  enforced on write *and* checked before parsing on read, so an oversized manifest is rejected before
  allocation rather than after. Revision 2's claim that the content cap was "the real bound" was
  wrong.

### Review-context validation

`ValidateReviewContextManifest` capped `Entries.Length` against the static canonical list. With an
expanded per-build set, the validator needs the same set the extractor used. The concrete reserved
path list is written into the generation alongside the manifest and threaded into every validation
call, so the validator checks that every entry path is a member of that set — strictly stronger than
the old length cap, which only bounded the count.

### Reviewability, narrowed rather than claimed

Containment ranges over the working tree (`ls-files -co --exclude-standard`); review context contains
**index stage-0 blobs only**, and the manifest declares it (`UnstagedAndUntrackedOmitted: true`). An
untracked reserved config, or unstaged bytes of a tracked one, is therefore contained but not
reviewable.

That is pre-existing AI-1706 behaviour and is deliberately **not** widened. Carrying untracked
working-tree bytes into review context is what killed the predecessor effort (AI-1680): a developer's
`skip-worktree` local override would be published to the reviewer's model. The lockstep property this
design claims is correspondingly precise: **for tracked stage-0 content, every path excluded from the
snapshot is classified reserved by the extractor.**

### The non-borrowed sync path

`SyncFromSourceAsync` (the public overloads, `reviewContextRoot: null`) takes an `executionPath` and
previously received `SnapshotExcludedPaths`. Removing that static property must not silently drop even
its root-level exclusions.

It has **no production callers today** — the only call sites are in
`test/Capacitor.Cli.Tests.Unit/WorktreeManagerTests.cs` and
`test/Capacitor.Cli.Tests.Unit/Services/AcpHostedAgentRuntimeFactoryLiveTests.cs`. That is a caller
invariant, not a guarantee: it is a public method that produces a tree an agent could be launched
into. It therefore takes a plan derived for its own execution path, including the widened ancestor
rule, and gets consumers 1 and 2. Only consumer 3 (review context) is absent, because it passes no
review-context root.

### The `.github/mcp.json` addition

Added to `WorkspaceMcpConfigPaths`, independent of the scope change and live even at cwd == root.
`.copilot/mcp-config.json` is added alongside the existing `.copilot/mcp.json` under the list's
standing rationale.

## Testing

Every test names the discovery shape it defends and carries a positive control. Rounds 1 and 2 each
found tests that could pass vacuously; those are rewritten rather than patched.

1. **Codex sub-cwd** — tracked `src/.codex/config.toml`, `requestedCwd = <source>/src`, absent from the
   snapshot. Control: root-cwd build of the same fixture leaves it present.
2. **Copilot `.github/mcp.json` at root** — the file is **tracked**, asserted present in `ls-files -co`
   before asserting it is absent from the snapshot.
3. **Intermediate directory** — cwd `a/b`, tracked `a/.mcp.json` excluded. Control: root-cwd build of
   the same fixture leaves it present.
4. **Sibling not excluded** — cwd `a`, `b/.mcp.json` present.
5. **Case-sensitive sibling** — on a case-sensitive volume, cwd `a`, tracked `A/.mcp.json`: **present**,
   and the build succeeds. This is the test that pins revision 2's withdrawn fold: with it, the file
   was excluded and, with `a/.mcp.json` also tracked, the build failed with a path collision.
6. **`--show-prefix` protocol, against real git** — invoke the actual command from a real
   subdirectory and compare the parsed bytes against the directory prefixes in a real `ls-files`
   listing. Run on macOS (case-insensitive by default) with an alternate-case entry and with
   composed/decomposed names, since that is the volume where the two spellings can diverge. This is
   the independent oracle; the plan builder is not permitted to be its own oracle.
7. **Root prefix** — the empty-prefix path is exercised through the real command output (`"\n"`), not
   through a pre-normalized `""`, and yields a plan equal to `WorkspaceMcpConfigPaths` exactly.
8. **Non-ASCII prefix refused** — a cwd whose git prefix is non-ASCII fails with
   `borrowed_snapshot_cwd_prefix_non_ascii` rather than building.
9. **NFD is rejected, not matched** — an NFD path in the index fails the build via the existing
   normalization rule. Asserted explicitly, because revision 2's phrasing implied NFD and NFC were
   treated as equivalent.
10. **End-to-end exclusion oracle** — for a fixture spanning ancestor, sibling, descendant, mixed-case
    and non-ASCII-filename paths, the set of paths absent from the snapshot equals the set computed
    from the real git prefix and the real source listing. Replaces revision 2's test 6, which asserted
    exclusion against the same classifier exclusion now calls — true by construction.
11. **Refresh parity at the boundary** — initial sub-cwd snapshot; add and modify configs at root, an
    intermediate directory and the cwd; `SyncBorrowedSnapshotFromSourceAsync` with the **persisted**
    prefix; assert every ancestor config absent, each tracked one present in the newly published
    review context, sibling survives, no kcap-created deletion in `git status`. A separate case
    asserts a refresh given no persisted prefix throws rather than re-deriving.
12. **Reserved index policy** — HEAD-tracked `src/.mcp.json` under cwd `src`: destination index
    contains it, skip-worktree bit set, `git status` clean. Negative control: with the policy disabled
    the same fixture reports a deletion.
13. **Staged-only addition** — `src/.mcp.json` in the source index but not committed: the build
    **succeeds**, the file is excluded, and it appears in review context. This is the case revision 2
    would have crashed on.
14. **Caps at the boundary** — depth exactly 32 succeeds, 33 fails; aggregate bytes exactly at the
    limit succeeds, one over fails; a serialized manifest one byte over 1 MiB is rejected on write and
    on read.
15. **Rooted / escaping cwd** — a cwd yielding a rooted `Path.GetRelativePath` result is rejected;
    `..` and `../` remain rejected.
16. **Symlinked snapshot root** — `WorktreeRoot` a symlink resolving inside the source checkout is
    rejected with `borrowed_snapshot_root_inside_source`, with a control proving the lexical check
    alone passes it.
17. **Non-borrowed sync** — `SyncFromSourceAsync` with a sub-cwd execution path excludes ancestor
    configs, proving the static-property removal did not drop its exclusions.
18. **Oversized ancestor config** — one tracked ancestor `.mcp.json` over 256 KiB trips
    `borrowed_snapshot_review_context_capacity_exceeded`, documenting the pre-existing DoS that the
    separately-filed issue covers, so a later reader does not mistake it for a regression here.
19. **List membership** — `.github/mcp.json` and `.copilot/mcp-config.json` required.

The AI-1632 live certification (`KCAP_WORKSPACE_MCP_CERT=1`) is re-run unchanged, with its existing
control asserting the declared command *does* spawn when the guard is removed.

## Out of scope

- The owned-worktree strip (cwd is always the worktree root).
- Widening review context to untracked or working-tree bytes.
- The pre-existing review-context capacity DoS — filed separately; test 18 documents it.
- AI-1675 (`CopyDirectory` recursion / symlink dereference).
