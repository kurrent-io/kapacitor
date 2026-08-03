# AI-1706 — Reviewable branch-authored MCP config outside the executable worktree

**Status:** proposed design, 2026-08-02, against `origin/main` (`1c413ef`). Approved for
spec review; implementation has not started.
**Repository:** `kurrent-io/kcap-cli`
**Supersedes:** AI-1680 and closed PR #437. The in-tree `.kcap-quarantined` mechanism
must not be rebuilt.
**Server impact:** none. `kcap-server` remains on `review-flow-v4`; this is a CLI-only
daemon, launcher, and local MCP change.

## 1. Decision

Keep execution containment and review discoverability separate:

1. Continue excluding every `WorkspaceMcpConfigPaths` entry from borrowed snapshots.
   No vendor-recognized MCP config path or renamed copy is materialized anywhere in the
   executable worktree.
2. Extract branch-authored config from Git's object store into a daemon-owned sidecar
   adjacent to the snapshot.
3. Expose that sidecar through a mandatory, narrowly scoped MCP server injected only for
   borrowed-snapshot review flows.
4. Tell the reviewer in every result that these are Git index bytes, not working-tree
   bytes. Unstaged and untracked MCP config is deliberately omitted.

The MCP surface is implemented by `kcap mcp review` in a daemon-only context mode and is
injected under the reserved server name `kcap-review-context`. It exposes only the local
review-context tool. It does not expose the normal PR/session tools, does not receive
`KCAP_URL`, and does not widen the flow definition's `McpAllowlist`.

This is reserved launch infrastructure like `kcap-flow-result`, not a user-selectable MCP
server. It exists because the daemon's containment step removed review-relevant content;
the same layer is responsible for restoring read-only discoverability.

## 2. Why this mechanism

### 2.1 Rejected: in-tree quarantine or suffixes

PR #437 showed why this is not an acceptable basis. A tracked name does not prove that
on-disk bytes came from the branch: `skip-worktree` and `assume-unchanged` entries remain
tracked and absent from `git status` while their working-tree bytes may be a private local
override. Copying those bytes discloses them to the reviewer and its model.

The suffix also spread destination identity into manifest mapping, index flags,
`.git/info/exclude`, case behavior, collision handling, encoding, and source/destination
symlink safety. The design created the write path that later followed a clone-side symlink
and truncated an external file. No content from this issue is carried into the snapshot,
under any name.

### 2.2 Rejected: add content to the review-flow prompt

`FlowPromptRenderer.PromptContractVersion` and `review-flow-v4` live in `kcap-server`.
Putting local content in that prompt requires a new daemon-to-server transport, a prompt
contract/version change, and a decision about whether the server persists branch content.
It also makes a local containment fix depend on a coordinated server rollout.

The prompt route has stronger guaranteed visibility, but its cross-repo and retention
cost is unnecessary while supported hosted reviewers already consume daemon-injected MCP
servers. This issue therefore does not change `kcap-server`.

### 2.3 Rejected: give the reviewer the sidecar path

A direct path requires widening the reviewer's OS sandbox to another filesystem root and
lets the vendor process attempt writes or substitute path components. The reviewer needs
the bytes, not filesystem authority over their storage. A loopback capability keeps the
sidecar daemon-owned and gives the MCP process one authenticated read operation.

### 2.4 Rejected: inject ordinary `kcap-review`

The built-in server review flows do not currently allowlist `kcap-review`. Appending it in
the daemon would silently broaden a server-authored policy and expose unrelated backend
review/session tools. `kcap-review-context` instead runs the same command in a local-only
mode with exactly one tool and no backend credential.

## 3. Provenance contract

### 3.1 The Git index is authoritative

The sidecar represents stage-0 entries in the source checkout's Git index:

- enumerate with byte-returning Git plumbing equivalent to
  `git ls-files --stage -z`;
- split records on NUL bytes before decoding anything;
- strictly decode each path as UTF-8, per record;
- parse the stage, mode, and object id without consulting a filesystem path;
- select entries that match `WorkspaceMcpConfigPaths` under the probed semantics of the
  filesystem on which the executable snapshot runs;
- read content with `git cat-file blob <oid>` (or batch equivalent) using the object id
  from the index record.

The extractor never opens the corresponding source-worktree pathname. Source leaf and
parent symlinks, `skip-worktree`, `assume-unchanged`, racy filesystem replacement, clean
filters, and private on-disk overrides therefore cannot affect the disclosed bytes.

The index is chosen instead of `HEAD` because a borrowed review intentionally includes
the requester's in-progress work. The index includes committed content plus staged edits,
while still providing an object-store identity. A staged deletion removes the entry. A
staged addition or modification is reviewable. An untracked or unstaged local config is
not branch-authored under this contract and is never read.

### 3.2 The inconsistency is explicit

The rest of a borrowed snapshot mirrors working-tree bytes. MCP config is the deliberate
exception: it exposes index bytes because working-tree provenance cannot be proven safely.

Every tool result includes these machine-readable and human-readable fields:

```json
{
  "provenance": "git-index-stage-0",
  "workingTreeBytes": false,
  "unstagedAndUntrackedOmitted": true,
  "sourceHead": "<commit oid>",
  "entries": []
}
```

The MCP server instructions and tool description state: call the tool before concluding a
borrowed review is clean; the returned bytes are the staged/committed Git index version,
not the on-disk working-tree version. An empty `entries` array is an affirmative result,
not a missing integration.

### 3.3 Ambiguous Git states fail closed

A matching config with an unmerged stage (1, 2, or 3), an invalid/zero object id, a
non-blob object, an undecodable path, or two entries that collide under the probed
destination filesystem semantics aborts the borrowed launch. It is not silently omitted.

The extractor captures the index listing before and after materialization. A different
listing retries the complete snapshot/sidecar generation through the existing
`SourceChangedException` path. A second change fails with
`borrowed_snapshot_source_changed`. Git blobs are immutable once their object ids are
captured.

## 4. Sidecar ownership and lifecycle

### 4.1 Location and format

For snapshot root:

```text
<WorktreeRoot>/borrowed-snapshots/borrowed-<agent-id>
```

the daemon owns a sibling root:

```text
<WorktreeRoot>/borrowed-snapshots/borrowed-<agent-id>.review-context/
```

The sidecar root is outside `WorktreeInfo.SnapshotRoot`, outside the reviewer's cwd, and
outside the snapshot's Git repository. It is created owner-only (`0700` where POSIX modes
apply). Files are owner-only (`0600`). Creation happens beneath the already daemon-owned
`borrowed-snapshots` root with per-component no-follow checks; a pre-existing link or
unexpected entry aborts the launch.

Each generation is assembled under an unguessable name and atomically promoted. Its
manifest contains the schema version, generation id, source `HEAD`, exact Git paths,
index modes, blob object ids, byte counts, SHA-256 values, and base64 content. UTF-8 text
is included only when strict decoding succeeds. Base64 is always authoritative, so
invalid content bytes remain exactly reviewable.

There are at most the entries named by `WorkspaceMcpConfigPaths`. Total raw blob content
is capped at 256 KiB before base64 expansion. Exceeding the cap fails the borrowed launch
with `borrowed_snapshot_review_context_capacity_exceeded`; it never launches a reviewer
that can return clean without seeing the config.

The bundle-derived snapshot necessarily retains Git objects reachable from its cloned
`HEAD`, including a committed MCP-config blob. That is an accepted, non-executable
residual: vendors discover configuration by workspace pathname, not by walking Git
objects. The sidecar is still needed because relying on a reviewer to reconstruct paths
from history is neither discoverable nor sufficient for staged index content. Sidecar
creation must not add index-only blobs, refs, paths, or new recoverability to the
snapshot's Git database.

### 4.2 Creation and refresh

`CreateBorrowedSnapshotAsync` builds the executable snapshot and sidecar as one stabilized
generation. Neither is promoted if extraction or snapshot verification fails.

`SyncFromSourceAsync` builds a fresh snapshot and sidecar while the reviewer is idle. The
snapshot replacement completes before the local bridge publishes the new immutable
sidecar generation. A tool call sees either the complete previous generation or the
complete new generation, never a partially written manifest. If refresh fails, the
existing behavior terminates the reviewer with `borrowed_snapshot_refresh_failed`.

Old generations are deleted only after they are no longer published to a reader. A daemon
crash cannot leave a live reviewer using stale state: the existing PID-record/orphan-reaper
path reaps the process, and startup cleanup removes unowned snapshot and sidecar roots.

### 4.3 Cleanup

`WorktreeInfo` carries the review-context root or an equivalent owned-resource handle.
Every existing owned-worktree cleanup path removes it with `DeleteTreeNoFollow`:

- creation rollback;
- pre-registration launch failure;
- post-registration `CleanupAgentAsync`;
- normal reviewer exit or stop;
- borrowed-refresh failure;
- daemon startup orphan sweep.

The orphan sweep treats `.review-context` like `.vendor-state`: preserve it only when the
corresponding snapshot is active. Cleanup is best-effort across resources but a later step
still runs if an earlier step fails. The loopback capability is revoked before filesystem
cleanup, so a late tool call receives 404 and cannot race deletion.

## 5. Reviewer delivery

### 5.1 Local capability endpoint

The existing loopback `LocalPermissionBridge` gains a separate review-context grant. The
grant binds an unguessable per-reviewer token to one immutable sidecar generation. The
shared interactive permission token cannot access review context.

The only accepted request is an authenticated read of the bound workspace-MCP manifest.
Unknown/revoked tokens, other methods, other paths, and attempts to choose a filesystem
path return 404. The request contains no user-supplied path. The bridge validates the
sidecar root again before reading its daemon-owned manifest.

Codex borrowed reviewers may use the same per-reviewer token record for permission and
review-context grants, but those grants remain independent fields. Other snapshot-backed
vendors receive a context-only token with no permission-auto-approval authority.

### 5.2 Reserved MCP server

Every borrowed-snapshot review flow receives:

```text
server: kcap-review-context
command: <daemon's kcap binary> mcp review
env:
  KCAP_REVIEW_CONTEXT_MODE=1
  KCAP_REVIEW_CONTEXT_URL=<unguessable loopback capability URL>
```

No `KCAP_URL`, auth token, source path, sidecar path, or normal `kcap-review` arguments are
provided. `Program.cs` recognizes this daemon-only mode before server URL resolution and
the update check, then starts the local context MCP loop directly. Manual invocation
without a valid capability URL fails before serving tools.

The context mode exposes exactly one tool:

```text
get_branch_authored_mcp_configs()
```

It returns the complete bounded manifest, including exact paths and base64 bytes. It makes
one GET to the capability URL and performs no Git or source-filesystem access itself.

The server is injected by the Claude, Codex, and ACP review-flow builders whenever
`RuntimeStartContext.IsBorrowedSnapshot` is true. It is also included in each vendor's
exact unattended tool-availability/auto-approval set. It is not registered globally,
accepted in user flow definitions, inherited from ambient MCP config, or present for owned
worktrees and ordinary interactive sessions.

Conformance tests pin that context mode exposes only this tool and that every
snapshot-capable launcher either injects it correctly or refuses the borrowed launch.

### 5.3 Why no server change is required

The server continues sending its existing `review-flow-v4` prompt and MCP allowlist. The
daemon does not modify that prompt and does not append `kcap-review` to the allowlist.
`kcap-review-context` is a local containment companion with a smaller authority than any
allowlisted backend server. No sidecar bytes, object ids, or capability URLs cross SignalR
or enter server persistence.

## 6. Independent snapshot hardening to salvage

These fixes are independent of the discarded suffix mechanism and land in the same CLI
PR with their tests:

1. **Destination component links:** call the no-follow component walker before
   `EnsureParentDirectories`. Refuse any linked parent. Remove or refuse a linked leaf
   according to the existing no-follow policy before opening with `FileMode.Create`.
   Checking after directory creation is too late: `Directory.CreateDirectory` may already
   have created directories in the link target.
2. **Strict per-record UTF-8:** use byte-returning Git capture, split on NUL, then decode
   each record using `UTF8Encoding(..., throwOnInvalidBytes: true)`. Never infer invalid
   bytes by searching a decoded string for U+FFFD; U+FFFD is legal filename content.
3. **Unrepresentable ignore paths:** refuse CR/LF path records before writing the
   line-oriented `.git/info/exclude` file. Do not escape, normalize, or split them.
4. **Filesystem case behavior:** probe the actual destination filesystem. Never infer
   semantics from macOS/Linux/Windows. All path-identity decisions in this change consume
   the same probed result.
5. **Unguessable probes:** every case probe uses a fresh CSPRNG/GUID name and validates
   both spellings. A fixed sentinel is branch-matchable and therefore not a probe.

No quarantine destination mapping, suffix collision logic, or quarantine-driven
`skip-worktree` policy is salvaged.

## 7. Threat model

| Threat or prior failure | Required disposition |
|---|---|
| `skip-worktree`/`assume-unchanged` hides a private on-disk override | disclose only the index blob object id and bytes; never open the path |
| untracked or unstaged local MCP config contains secrets | omit and never read it; state that omission in every result |
| source leaf or parent is a symlink | irrelevant to extraction because Git objects are read by id |
| destination leaf symlink is followed by `FileMode.Create` | detect before open and remove/refuse without following |
| destination parent symlink is followed by `EnsureParentDirectories` | refuse every component before creating any directory |
| invalid UTF-8 path is lossily rewritten | split byte records first and fail strict decoding |
| a valid filename contains U+FFFD | accept it because strict decoding succeeds |
| CR/LF path injects `.git/info/exclude` patterns | refuse as unrepresentable |
| slash, backslash, or Unicode normalization changes path identity | validate and preserve Git's exact decoded path; never normalize identity |
| OS-based case assumption is wrong for the mounted volume | probe destination semantics with an unguessable name |
| case/path collision selects or hides the wrong entry | fail the borrowed launch |
| config content is invalid UTF-8 | preserve exact base64; omit only the optional text field |
| large hostile blob exhausts disk/model context | enforce the 256 KiB raw total and fail the launch |
| branch guesses a probe or sidecar name | use per-use unguessable generations/tokens under a daemon-owned parent |
| reviewer or another local process guesses the endpoint | 128-bit-or-greater token, loopback binding, exact route, revocation |
| vendor gains ordinary backend review tools | context mode exposes one local tool and receives no `KCAP_URL` |
| sidecar lands in or is restored into Git history | sibling root, never copied or committed into the snapshot |
| refresh exposes mixed/partial generations | build privately, stabilize index/HEAD, atomically publish immutable generation |
| crash or failed launch leaves review content | owned cleanup handle plus orphan sweep after process reaping |

The trust boundary is the daemon process and its owned `WorktreeRoot`. The reviewer is
allowed to receive the extracted branch-authored bytes through the MCP result. It is not
trusted with the source checkout, sidecar path, daemon filesystem authority, backend kcap
credentials, or mutation authority over the sidecar.

## 8. Test and mutation standard

All tests use TUnit on Microsoft Testing Platform. Targeted runs use:

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/<Class>/*"
```

`dotnet test` is not used. Platform-specific test bodies call `Skip.Unless(...)` with a
filesystem/feature probe; they do not branch on the OS as a proxy for case semantics or
filename legality.

### 8.1 Provenance and visibility

- Commit MCP config A; mark it `skip-worktree`; write different private bytes B to the
  working-tree path; assert as preconditions that `git status --porcelain` is empty,
  `git ls-files -z` contains the path, and the index/worktree byte hashes differ. The
  sidecar and MCP result must contain A and must not contain B.
- Repeat the authority assertion for `assume-unchanged` or cover both flags in a
  parameterized test.
- Stage an MCP config change and assert the staged index blob is returned.
- Make only an unstaged change and assert the previous index blob is returned with the
  working-tree warning.
- Add an untracked MCP config and assert it is neither read nor disclosed.
- Add a matching config with unmerged index stages and assert launch refusal.
- Assert exact path, object id, byte length, SHA-256, base64 content, provenance fields,
  and the empty-manifest affirmative result.

### 8.2 Execution containment

- In the same run, prove a vendor executes a workspace MCP config from an ordinary
  control checkout, then prove the borrowed snapshot contains no recognized config and
  the marker is not created. The positive control must reach the same vendor/code path.
- Recursively assert no `WorkspaceMcpConfigPaths` entry and no copy of its content exists
  at a vendor-recognized workspace pathname under `SnapshotRoot`. A committed blob already
  reachable through the cloned `HEAD` is the explicit Git-history residual from §4.1, not
  a containment failure; staged index-only sidecar bytes must not be added to snapshot Git
  metadata.
- Assert the context sidecar is a sibling, not a descendant, and is absent from snapshot
  `git status`, index, and history.

### 8.3 Path and write containment

- Construct a clone-side linked leaf whose target is external and prove the external file
  is unchanged.
- Construct a clone-side linked parent, with the remaining child directories absent.
  Assert refusal occurs before any directory appears in the external target.
- Each negative assertion includes a positive control proving the write would leave the
  snapshot when the guard is removed.
- Assert sidecar creation refuses a pre-existing linked root/component and does not touch
  the target.

### 8.4 Encoding, representation, and case

- Insert an invalid UTF-8 filename into the index using raw Git plumbing. Assert the
  production precondition with the bytes from `ls-files -z`, then assert refusal.
- Insert a valid UTF-8 filename containing U+FFFD and assert it remains valid and exact.
- Insert CR and LF filenames where the filesystem supports them; prove Git reports them
  as NUL-delimited records, then assert refusal before `.git/info/exclude` changes.
- On a probed case-sensitive volume, cover distinct case spellings. On a probed
  case-insensitive volume, cover aliases/collisions. macOS case-sensitive coverage uses a
  case-sensitive APFS image when needed.
- Pre-create the spelling that would spoof a fixed case sentinel and assert the random
  probe still reports the real filesystem semantics.

### 8.5 MCP authority and lifecycle

- Context mode lists exactly `get_branch_authored_mcp_configs`; normal `kcap mcp review`
  does not expose it.
- Context mode performs no server URL resolution, update check, Git detection, token load,
  or backend request.
- Every snapshot-capable launcher injects the reserved server and exact environment;
  non-snapshot launches do not.
- Missing, malformed, mismatched, and revoked capability URLs fail without returning
  sidecar content.
- A refresh publishes a complete new generation; a concurrent read gets a complete old or
  new response. Refresh failure terminates the reviewer.
- Normal exit, every failed-launch boundary, stop, and orphan sweep revoke the grant and
  delete the sidecar. A live snapshot's sidecar survives the orphan sweep.

### 8.6 Mutation verification

Every security assertion above is mutation-verified before push:

1. Mutate the whole protected path, not only the line most recently edited.
2. Confirm the mutant applied by inspecting/grepping the production file.
3. Run the targeted test and require it to fail for the intended reason.
4. Restore the production change.
5. Run the same test and require it to pass.
6. Record the mutant, confirmation, failing assertion, and restored passing command in the
   PR description or attached review evidence.

A green test against code where the relevant guard never executes is not evidence. Every
containment test includes a positive control in the same run.

## 9. Implementation boundaries

Expected CLI areas are:

- `WorktreeManager.cs` and a focused partial for review-context extraction/lifecycle;
- `WorktreeManager.WorkspaceMcp.cs` for the shared path classification contract;
- `AgentOrchestrator.cs` for grant ownership, failure cleanup, and refresh publication;
- `LocalPermissionBridge.cs` for the separate read-only context capability;
- `McpReviewServer.cs` and the early `Program.cs` mode dispatch;
- Claude, Codex, and ACP review-flow MCP builders;
- unattended-safe tool classification and launcher conformance tests;
- focused unit/integration tests, with selected mutation-verified tests salvaged from the
  closed branch rather than copying its quarantine implementation.

No public interactive CLI surface is added. The daemon-only environment mode does not need
a README command entry. Operator-visible failure codes and the borrowed-review containment
description do require a concise README update if implementation changes documented
behavior.

Before every push:

```bash
rg -n "AI-[0-9]+" src/ test/ --type cs
```

must return no matches introduced by this work. Issue references stay in commits, the PR
description, and this design document.

## 10. Definition of done

- A borrowed reviewer receives exact Git-index bytes and exact Git paths for every
  branch-authored vendor MCP config within the bounded contract.
- The result explicitly says it is `git-index-stage-0`, not working-tree content, and that
  unstaged/untracked config is omitted.
- The mandatory skip-worktree test proves differing private working-tree bytes are not
  disclosed.
- No recognized config pathname, renamed working-tree copy, or sidecar exists in the
  executable snapshot. Sidecar creation adds no index-only content to snapshot Git
  metadata; committed blobs already reachable from cloned `HEAD` remain the explicit
  non-executable residual from §4.1.
- No vendor can execute branch-authored MCP config from the agent worktree; a same-run
  positive control proves the vendor vector is live.
- No snapshot or sidecar write can escape through a linked leaf or parent component.
- The added reserved server exposes only the local context tool; the reviewer retains its
  existing result channel and any server-allowlisted tools. The flow's server-authored MCP
  allowlist and `review-flow-v4` prompt are unchanged.
- Sidecars and capability grants are refreshed atomically, revoked on termination, removed
  on all cleanup paths, and swept after crashes.
- Strict path decoding, representation refusal, real-filesystem case probing, and
  unguessable probe names are covered by mutation-proven tests.
- Targeted tests, the full unit suite, AOT publish warning check, and the no-Linear-ID C#
  scan pass before the implementation PR is opened.
- The implementation PR is titled `[AI-1706] <summary>` and documents the provenance
  product decision and mutation evidence.

Implementation begins only after this written spec completes the requested Claude review
flow and the user approves the reviewed document.
