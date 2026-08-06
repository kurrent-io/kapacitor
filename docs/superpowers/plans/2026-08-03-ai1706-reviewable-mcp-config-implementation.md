# AI-1706 Reviewable Branch-Authored MCP Config Implementation Plan

**Execution status (2026-08-03):** implementation and focused local verification complete.
Full unit/integration suites and NativeAOT publishing are intentionally reserved for CI.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expose exact, bounded Git-index bytes for branch-authored workspace MCP configuration to independent-snapshot reviewers without placing executable configuration in their worktree.

**Architecture:** `WorktreeManager` extracts matching stage-0 blobs from raw Git plumbing while it builds each independent snapshot, writes an owner-only sibling sidecar generation, and returns an immutable in-memory generation. `LocalPermissionBridge` stores that generation beside the existing per-reviewer permission authority and serves it through one exact loopback GET route. The ACP review builder injects a daemon-only `kcap-review-context` MCP server for snapshot-backed review launches; the orchestrator publishes refreshed generations by atomically replacing the same token's immutable grant value.

**Tech Stack:** .NET 10, C#, TUnit on Microsoft Testing Platform, Git plumbing, `HttpListener`, stdio MCP JSON-RPC, source-generated `System.Text.Json`.

## Global Constraints

- The source Git index stage 0 is authoritative; never open a reserved source-worktree pathname.
- Preserve exact blob bytes in base64 and exact strict-UTF-8 Git paths; omit unstaged and untracked config.
- Keep all recognized workspace MCP paths absent from the executable snapshot.
- Sidecars are sibling directories, owner-only (`0700` directories and `0600` files on POSIX), bounded to 256 KiB raw content, and removed on every cleanup path.
- Treat returned paths and content as untrusted branch-authored evidence, never instructions.
- Preserve the existing Codex review permission token path; add snapshot context authority as a union.
- Add no server/API persistence and no public interactive CLI command.
- Run only targeted tests locally. Full unit/integration suites and NativeAOT publish verification run off-machine in CI.
- Before push, `rg -n "AI-[0-9]+" src/ test/ --type cs` must report no identifiers introduced by this work.

---

### Task 1: Git-index review-context extraction and sidecar lifecycle

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.ReviewContext.cs`
- Create: `test/Capacitor.Cli.Tests.Unit/BorrowedReviewContextTests.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/WorktreeManagerTests.cs`

**Interfaces:**
- Produces: `BorrowedReviewContextGeneration(string Id, string StoragePath, byte[] JsonUtf8)`.
- Produces: `WorktreeInfo.ReviewContextRoot` and internal `WorktreeInfo.ReviewContextGeneration`.
- Produces: `WorktreeManager.DeleteReviewContextGeneration(string storagePath)` for post-publication retirement.
- Changes the snapshot build core to return the stabilized review-context generation alongside the snapshot candidate.

- [ ] **Step 1: Write failing provenance, ambiguity, capacity, and cleanup tests**

Add `BorrowedReviewContextTests` cases that create a temporary Git repository and assert:

```csharp
var snapshot = await manager.CreateBorrowedSnapshotAsync(repo, "review", CancellationToken.None);
var json = JsonNode.Parse(snapshot.ReviewContextGeneration!.JsonUtf8)!.AsObject();
await Assert.That(json["provenance"]!.GetValue<string>()).IsEqualTo("git-index-stage-0");
await Assert.That(json["entries"]![0]!["base64"]!.GetValue<string>())
    .IsEqualTo(Convert.ToBase64String(indexBytes));
await Assert.That(File.Exists(Path.Combine(snapshot.SnapshotRoot!, ".mcp.json"))).IsFalse();
```

Cover staged versus unstaged/untracked bytes, `skip-worktree`, unmerged stages, reserved descendants, non-regular modes, invalid matching and unrelated UTF-8 paths, exact/plus-one capacity, case collisions under a probed filesystem, and cleanup/orphan-sweep survival.

- [ ] **Step 2: Run the extraction tests and verify RED**

Run:

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/BorrowedReviewContextTests/*"
```

Expected: compile/test failure because the review-context types and `WorktreeInfo` properties do not exist.

- [ ] **Step 3: Implement raw index parsing and immutable manifest generation**

Create records and a source-generation context equivalent to:

```csharp
internal sealed record BorrowedReviewContextGeneration(string Id, string StoragePath, byte[] JsonUtf8);
internal sealed record BorrowedReviewContextManifest(
    int SchemaVersion, string GenerationId, string SourceHead, string Provenance,
    bool WorkingTreeBytes, bool UnstagedAndUntrackedOmitted, string ContentWarning,
    BorrowedReviewContextEntry[] Entries);
internal sealed record BorrowedReviewContextEntry(
    string Path, string IndexMode, string BlobObjectId, long ByteCount,
    string Sha256, string Base64, string? Text);
```

Use raw `git ls-files --stage -z` bytes. Split on NUL before decoding, parse the ASCII header, raw-byte classify against the ASCII `WorkspaceMcpConfigPaths`, ignore unrelated records before strict UTF-8 decoding, reject descendants and collisions, require stage 0 plus `100644`/`100755`, resolve only blobs, and read exact bytes with `git cat-file blob <oid>`. Enforce the exact reviewed failure-code prefixes and 256 KiB aggregate limit.

- [ ] **Step 4: Integrate extraction into the snapshot stabilization gate**

Capture the initial raw index listing and extracted blobs inside `BuildIndependentSnapshotOnceAsync`, capture the raw listing again with the final `HEAD`/working manifest, and throw `SourceChangedException` if any input differs. Prepare `manifest.json` beneath an unguessable private generation directory, parse it back through the source-generated context, and publish the directory only after the snapshot candidate is promoted/replaced.

- [ ] **Step 5: Implement lifecycle and owner-only storage**

Add a deterministic sibling-root helper (`<snapshot>.review-context`), no-follow component validation, owner-only creation, `RemoveAsync` cleanup, and orphan-sweep protection for a live snapshot's sidecar. Delete superseded generation directories only through `DeleteReviewContextGeneration` after bridge publication.

- [ ] **Step 6: Run targeted extraction and worktree tests and verify GREEN**

Run:

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/BorrowedReviewContextTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/WorktreeManagerTests/*"
```

Expected: targeted classes pass.

- [ ] **Step 7: Commit the extraction increment**

```bash
git add src/Capacitor.Cli.Daemon/Services/WorktreeManager.cs \
  src/Capacitor.Cli.Daemon/Services/WorktreeManager.ReviewContext.cs \
  test/Capacitor.Cli.Tests.Unit/BorrowedReviewContextTests.cs \
  test/Capacitor.Cli.Tests.Unit/WorktreeManagerTests.cs
git commit -m "feat: extract borrowed review MCP context"
```

### Task 2: Unified reviewer grants and exact local context route

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/LocalPermissionBridgeTests.cs`

**Interfaces:**
- Produces: immutable `ReviewerGrant(string[] AutoApproveServers, BorrowedReviewContextGeneration? ReviewContextGeneration)`.
- Extends: `RegisterReviewerToken(IReadOnlyList<string>, BorrowedReviewContextGeneration?)`.
- Produces: `PublishReviewerContext(string reviewerUrlOrToken, BorrowedReviewContextGeneration generation)` returning the retired generation, if any.

- [ ] **Step 1: Write failing permission-only, context-only, combined, route, refresh, and revocation tests**

Test the three grant shapes and pin exactly:

```text
GET /<token>/review-context/workspace-mcp-configs
```

Assert wrong methods, query strings, extra segments, vendor-shaped suffixes, shared/unknown/revoked tokens return 404. Assert a refresh returns wholly old or new JSON, one revocation removes both authorities, and permission behavior remains unchanged.

- [ ] **Step 2: Run bridge tests and verify RED**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/LocalPermissionBridgeTests/*"
```

Expected: compile/test failure for the missing grant and context route.

- [ ] **Step 3: Widen the dictionary and dispatch the exact GET before permission parsing**

Replace `ConcurrentDictionary<string,string[]>` with `ConcurrentDictionary<string,ReviewerGrant>`. On an exact authenticated context GET, capture the immutable grant value once and write `ReviewContextGeneration.JsonUtf8`. Keep the existing POST permission path byte-for-byte equivalent except that it reads `grant.AutoApproveServers`.

- [ ] **Step 4: Implement atomic refresh and unified revocation**

Use `TryUpdate` to replace the same token's immutable value, preserving `AutoApproveServers`. Return the retired generation so the orchestrator can delete its on-disk directory after publication. Keep `RevokeReviewerToken` as the only removal path.

- [ ] **Step 5: Run bridge tests and verify GREEN**

Run the Task 2 targeted command. Expected: pass.

- [ ] **Step 6: Commit the bridge increment**

```bash
git add src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs \
  test/Capacitor.Cli.Tests.Unit/LocalPermissionBridgeTests.cs
git commit -m "feat: serve borrowed review context locally"
```

### Task 3: Daemon-only MCP context server

**Files:**
- Create: `src/Capacitor.Cli/Commands/McpReviewContextServer.cs`
- Modify: `src/Capacitor.Cli/Program.cs`
- Create: `test/Capacitor.Cli.Tests.Integration/McpReviewContextServerTests.cs`

**Interfaces:**
- Consumes: `KCAP_REVIEW_CONTEXT_MODE=1` and `KCAP_REVIEW_CONTEXT_URL`.
- Produces: stdio MCP server `kcap-review-context` with only `get_branch_authored_mcp_configs`.

- [ ] **Step 1: Write failing process-level MCP tests**

Spawn `kcap mcp review` with context-mode environment and no `KCAP_URL`. Assert initialization, one-tool listing, a single GET to the capability URL, verbatim manifest return with untrusted-content framing, and early failures for missing/malformed capability URLs. Assert no `/auth/config` request and no update-check artifact.

- [ ] **Step 2: Run the new integration class and verify RED**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj -- \
  --treenode-filter "/*/*/McpReviewContextServerTests/*"
```

Expected: context-mode tests fail because normal startup still requires `KCAP_URL` and exposes `kcap-review`.

- [ ] **Step 3: Add the early program dispatch and one-tool server**

Before `ResolveServerUrl` and the update check, detect only `args == ["mcp","review"]` plus `KCAP_REVIEW_CONTEXT_MODE=1`, validate an absolute loopback capability URL with the exact suffix and no query, then run the local stdio loop. The tool performs one `HttpClient.GetAsync` and returns the body; it performs no auth, Git, token, or source-path work.

- [ ] **Step 4: Run the integration class and verify GREEN**

Run the Task 3 targeted command. Expected: pass.

- [ ] **Step 5: Commit the MCP-mode increment**

```bash
git add src/Capacitor.Cli/Commands/McpReviewContextServer.cs src/Capacitor.Cli/Program.cs \
  test/Capacitor.Cli.Tests.Integration/McpReviewContextServerTests.cs
git commit -m "feat: add borrowed review context MCP mode"
```

### Task 4: Capability-driven runtime injection and lifecycle wiring

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AcpReviewFlowMcp.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/Services/AcpHostedAgentRuntimeFactoryTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorReviewerTokenTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorBorrowLaunchTests.cs`

**Interfaces:**
- Adds: `RuntimeStartContext.ReviewContextCapabilityUrl`.
- Consumes: `WorktreeInfo.ReviewContextGeneration` for snapshot-backed launches.
- Uses: `LocalPermissionBridge.PublishReviewerContext` after every successful snapshot refresh.

- [ ] **Step 1: Write failing runtime and orchestrator wiring tests**

Assert snapshot-backed ACP review MCP contains `kcap-review-context`, command `kcap mcp review`, exactly the two context env variables, and no `KCAP_URL`. Assert direct-borrow Codex and Claude receive no context server. Assert context-only snapshot grants, existing permissions-only Codex grants, combined grants, failed-launch revocation/sidecar cleanup, refresh publication, and refresh-failure termination.

- [ ] **Step 2: Run the three targeted classes and verify RED**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/AcpHostedAgentRuntimeFactoryTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/AgentOrchestratorReviewerTokenTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- \
  --treenode-filter "/*/*/AgentOrchestratorBorrowLaunchTests/*"
```

Expected: new assertions fail for missing context grant/injection/publication.

- [ ] **Step 3: Implement unioned grant minting**

Mint when `(isReviewFlow && vendor == "codex") || snapshotBorrow`. Populate `AutoApproveServers` only for the existing Codex arm and `ReviewContextGeneration` only for the snapshot arm. Fail closed if a snapshot launch lacks a bridge or generation. Pass the exact context capability URL into `RuntimeStartContext`.

- [ ] **Step 4: Inject the reserved server and clamp its tool availability**

Append the reserved server inside `AcpReviewFlowMcp.Build` only for `ctx.IsBorrowedSnapshot`. For transports with explicit available-tool ids, add only `kcap-review-context-get_branch_authored_mcp_configs`. Keep it outside `KcapMcpRegistry` so server-authored allowlists cannot select it.

- [ ] **Step 5: Publish refreshes and retire old storage**

Have the worktree refresh return the new immutable generation. Atomically publish it through the same token, then delete the retired generation directory. On extraction, publication, or cleanup failure, preserve the existing fail-closed reviewer termination.

- [ ] **Step 6: Run targeted runtime/orchestrator tests and verify GREEN**

Run the Task 4 targeted commands. Expected: pass.

- [ ] **Step 7: Commit the wiring increment**

```bash
git add src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntimeFactory.cs \
  src/Capacitor.Cli.Daemon/Services/AcpReviewFlowMcp.cs \
  src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntimeFactory.cs \
  src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs \
  test/Capacitor.Cli.Tests.Unit/Services/AcpHostedAgentRuntimeFactoryTests.cs \
  test/Capacitor.Cli.Tests.Unit/AgentOrchestratorReviewerTokenTests.cs \
  test/Capacitor.Cli.Tests.Unit/AgentOrchestratorBorrowLaunchTests.cs
git commit -m "feat: wire review context into borrowed reviewers"
```

### Task 5: Snapshot destination no-follow hardening

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/WorktreeManager.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/WorktreeManagerTests.cs`

**Interfaces:**
- Strengthens: `EnsureParentDirectories` and `CopyManifestAsync` without changing their callers.

- [ ] **Step 1: Write failing linked-parent and linked-leaf containment tests**

Create snapshot-side linked parents/leaves with external sentinels and a positive control. Assert the guarded copy refuses before external directories/files change.

- [ ] **Step 2: Run `WorktreeManagerTests` and verify RED**

Use the Task 1 class-filter command. Expected: the new containment test demonstrates the current write escape or missing failure code.

- [ ] **Step 3: Add no-follow checks before any directory creation or file open**

Walk existing destination components with `File.GetAttributes`, reject any reparse point before `Directory.CreateDirectory`, and reject/remove a linked leaf before opening with `FileMode.Create`. Preserve existing ordinary-file replacement behavior.

- [ ] **Step 4: Run `WorktreeManagerTests` and verify GREEN**

Use the targeted class-filter command. Expected: pass.

- [ ] **Step 5: Commit the hardening increment**

```bash
git add src/Capacitor.Cli.Daemon/Services/WorktreeManager.cs \
  test/Capacitor.Cli.Tests.Unit/WorktreeManagerTests.cs
git commit -m "fix: prevent borrowed snapshot link escapes"
```

### Task 6: Focused verification and handoff

**Files:**
- Modify only if needed: `README.md`
- Modify: `docs/superpowers/plans/2026-08-03-ai1706-reviewable-mcp-config-implementation.md`

**Interfaces:** None.

- [ ] **Step 1: Run only the focused local verification set**

Run the five targeted class filters above plus:

```bash
rg -n "AI-[0-9]+" src/ test/ --type cs
git diff --check origin/main...HEAD
```

Expected: targeted tests pass, the issue-id scan reports no matches introduced by this work, and the diff check is clean. Do not run the full unit suite, full integration suite, or AOT publish locally.

- [ ] **Step 2: Review the implementation against every Definition of Done bullet**

Confirm exact provenance, snapshot absence, one-tool MCP authority, unified grant lifecycle, refresh atomicity, no-follow containment, stable failure codes, and targeted mutation-sensitive negative tests. Record any full-suite/AOT jobs as CI-only verification.

- [ ] **Step 3: Commit final documentation adjustments**

```bash
git add README.md docs/superpowers/plans/2026-08-03-ai1706-reviewable-mcp-config-implementation.md
git commit -m "docs: document borrowed review context"
```
