# AI-1505 — Cursor subagent classification: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the Cursor live-subagent-linking code from reading as live, correct three source comments this spec proves false, and pin the measured payload contract plus the stale-state behaviour — without changing any runtime behaviour.

**Architecture:** Documentation + tests only. The transcript-derived classification arm has no producer on `cursor-agent 2026.07.23-e383d2b` (a `sessionStart` never carries a `transcript_path`, and a subagent child never fires `sessionStart`), so nothing here can alter runtime behaviour. Tests that currently assert the unreachable child-lifecycle architecture are removed or reseeded to set persistent state directly.

**Tech Stack:** C# / .NET 10, TUnit on Microsoft Testing Platform, `kcap-cli` repo.

**Spec:** `docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md` (rev 10, codex spec-review clean at round 9).

## Global Constraints

- **No runtime behaviour change.** Only comments, test files, and docs. If a change would alter production control flow, stop — it is out of scope (spec D2a).
- **No Linear IDs in `src/**/*.cs` or `test/**/*.cs`** — `scripts/check-linear-ids.sh` rejects `AI-<digits>`. Reference the spec by path in comments.
- **Do not use "correct by construction"** for `ClassificationAuthoritative` (spec D4).
- Test framework is **TUnit**: `[Test]`, `await Assert.That(x).IsEqualTo(y)`. Never add `Microsoft.NET.Test.Sdk`. Always `await` assertions.
- Run tests as executables: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/<Class>/*"`.
- **Baseline:** the CLI unit suite has ~42 pre-existing macOS failures (MCP-registration / config-file / uninstall). Compare against a baseline, never expect green.
- `PathHelpers.ConfigPath` resolves `ConfigDir` into a **`static readonly`** field — process-wide, initialized once. Never try to redirect it by mutating `KCAP_CONFIG_DIR` inside a test.

---

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `src/Capacitor.Cli/Commands/CursorHookCommand.cs` | Hook dispatcher; holds the memory note (D4) and the ack-gate comment (D4a.2) | Modify comments only |
| `src/Capacitor.Cli/Commands/CursorLiveSubagentLinker.cs` | Marker store + correlator wrapper; holds the `SaveLink` comment (D4a.1) | Modify comments only |
| `test/Capacitor.Cli.Tests.Unit/Cursor/CursorLiveSubagentIntegrationTests.cs` | Currently asserts the unreachable child-lifecycle architecture | Remove 4 tests, keep 3 |
| `test/Capacitor.Cli.Tests.Unit/Cursor/CursorPayloadContractTests.cs` | **New** — pins the measured `sessionStart` payload contract (D3.1, D3.2) | Create |
| `test/Capacitor.Cli.Tests.Unit/Cursor/CursorSubagentStaleStateTests.cs` | **New** — pins D2a's decided stale-state behaviour incl. 2 characterization tests | Create |

---

### Task 1: Correct the three false source comments (D4, D4a)

**Files:**
- Modify: `src/Capacitor.Cli/Commands/CursorLiveSubagentLinker.cs` (the `SaveLink` catch comment)
- Modify: `src/Capacitor.Cli/Commands/CursorHookCommand.cs` (the ack-gate comment; the memory-classification note)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing. Comments only — no signature changes.

- [ ] **Step 1: Replace the `SaveLink` failure comment (D4a.1)**

In `CursorLiveSubagentLinker.SaveLink`, the `catch` currently claims the only consequence is a top-level fallback healed by import. That holds *only* when the write fails before any start side effect. Replace with:

```csharp
        } catch {
            // Fail-open, but the consequence depends on WHEN the write failed, and the
            // optimistic reading below is only half the story:
            //
            //  - Failure with no start side effect yet: later hooks miss TryLoadLink and treat
            //    the child as top-level. Recovered by a later `kcap import --cursor`.
            //  - Failure followed by a start POST or spool: the caller assigned
            //    subagentParentId BEFORE calling this method, so the divert still runs. A
            //    successful start marks the ack and spawns the {parent}-{child} watcher; a
            //    failed one spools an entry whose later drain does the same. Either way the
            //    child transcript can be routed BOTH under the parent and as its own
            //    top-level session — duplication, not a graceful fallback.
            //
            // This is a known, accepted corrupt-state risk; remedies are recorded in
            // docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
            // (D2a). It has no producer on the measured cursor-agent contract, because the
            // only caller sits behind a guard that never opens there.
        }
```

- [ ] **Step 2: Replace the ack-gate "diagnosable" comment (D4a.2)**

In `CursorHookCommand.HandleSubagentChildEventAsync`, immediately above
`if (!isStart && !CursorMarkers.HasSubagentStartAck(childSessionId))`, the existing comment ends by calling the loss "an accepted, diagnosable loss". Replace that final clause so it reads:

```csharp
        // ... Gate on the durable positive-ack marker instead of "no backlog" so a dropped
        // start permanently blocks this child. This is fail-closed and SILENT: the return
        // below emits no log, metric or surfaced marker, so the loss is NOT diagnosable from
        // the running system. Accepted to preserve start-before-content ordering; the child's
        // live capture is recovered only by `kcap import --cursor` plus the server-side
        // adoption sweep. See the design spec (D2a) under
        // docs/superpowers/specs/ for the full state table.
```

- [ ] **Step 3: Replace the memory-classification note (D4)**

In `CursorHookCommand.RunMemoryOrchestrationAsync`, replace the ~20-line "Subagent-classification note" (the block asserting a residual misclassification risk and "no cheap signal exists") with:

```csharp
                // ClassificationAuthoritative is hardcoded true, and this is VALID UNDER THE
                // MEASURED EVENT CONTRACT rather than proven from this file alone:
                //
                //  - What the source constructs: this method has exactly one call site, behind
                //    `if (eventName != "sessionStart") return null`. That is an internal
                //    invariant a unit test can pin.
                //  - What makes the flag WARRANTED: on cursor-agent 2026.07.23-e383d2b a
                //    subagent child never fires sessionStart at all (measured over four probe
                //    runs and two subagent kinds), so this method is never reached for a child.
                //    That is an external vendor behaviour a Cursor update could change.
                //
                // Deliberately NOT described as "correct by construction" — the dependency on
                // the vendor contract must stay visible so a future reader knows what to
                // re-check. Evidence, the re-probe procedure, and the untested Cursor IDE gap
                // are in docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
```

- [ ] **Step 4: Verify no Linear IDs were introduced and the project still builds**

```bash
cd /Users/tony/dev/kcap-cli-wt/ai-1505
./scripts/check-linear-ids.sh
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj -v q
```
Expected: check passes; build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/CursorLiveSubagentLinker.cs src/Capacitor.Cli/Commands/CursorHookCommand.cs
git commit -m "docs(cursor): correct three comments the AI-1505 probe disproved"
```

---

### Task 2: Annotate the inert path (D2.1)

**Files:**
- Modify: `src/Capacitor.Cli/Commands/CursorLiveSubagentLinker.cs` (type-level doc comment)
- Modify: `src/Capacitor.Cli/Commands/CursorHookCommand.cs` (classification block at `:282–301`; `HandleSubagentChildEventAsync` doc comment)

**Interfaces:**
- Consumes: nothing. Produces: nothing.

- [ ] **Step 1: Add a "no producer today" note to the `CursorLiveSubagentLinker` type doc**

Append to the existing `<summary>`:

```csharp
/// <para>
/// NO PRODUCER TODAY. The only caller that writes a marker sits behind a guard requiring
/// BOTH <c>eventName == "sessionStart"</c> AND a non-empty <c>transcript_path</c>, and on the
/// measured cursor-agent contract neither holds: a sessionStart payload always carries a null
/// transcript_path, and a subagent child never fires sessionStart at all. So
/// <see cref="SaveLink"/> never runs there. <see cref="TryLoadLink"/> DOES still run on every
/// event, so a marker persisted by another surface or an older build is still consumed.
/// </para>
/// <para>
/// Retained rather than deleted because Cursor already implements a native
/// <c>subagentStart</c> hook carrying an explicit parent id; if its dispatch is enabled, the
/// marker store and the marker-driven gate are reusable. The lifecycle builders below are NOT:
/// they key off the CHILD's sessionStart/sessionEnd, which a child never fires, so a native
/// revival must trigger from the PARENT's subagentStart/subagentStop — and must also add the
/// event to CursorHooksParser.CursorHookEvents and CursorHookEventMap, neither of which lists
/// it today. See docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
/// </para>
```

- [ ] **Step 2: Annotate the classification block in the dispatcher**

Above the `} else if (eventName == "sessionStart" && !string.IsNullOrEmpty(transcriptPath)) {` arm, add:

```csharp
                    // NO PRODUCER on the measured cursor-agent contract: a sessionStart payload
                    // always carries a null transcript_path, so this arm never opens. (The
                    // TryLoadLink gate above is NOT inert — it runs on every event and still
                    // consumes a marker persisted by another surface or an older build.)
                    // Kept as the landing site for a native subagentStart revival; see the
                    // design spec under docs/superpowers/specs/ for why the trigger must move.
```

- [ ] **Step 3: Annotate `HandleSubagentChildEventAsync`**

Append to its `<summary>`:

```csharp
    /// <para>
    /// UNREACHABLE in a fresh installation on the measured cursor-agent contract: entry requires
    /// <c>isSubagentChild</c>, which requires a marker that has no producer there. It remains
    /// reachable from a marker persisted by another surface or an older build. Note also that
    /// the sessionStart/sessionEnd arms below can never fire from a real child, which never
    /// emits either event — a native revival must be driven by the PARENT's subagentStart /
    /// subagentStop instead.
    /// </para>
```

- [ ] **Step 4: Build and check**

```bash
./scripts/check-linear-ids.sh && dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj -v q
```
Expected: both succeed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/
git commit -m "docs(cursor): mark the live subagent-linking path as having no producer"
```

---

### Task 3: Pin the measured payload contract (D3.1, D3.2)

**Files:**
- Create: `test/Capacitor.Cli.Tests.Unit/Cursor/CursorPayloadContractTests.cs`

**Interfaces:**
- Consumes: `CursorHookCommand.HandleCore(HttpClient, string baseUrl, TextReader stdin, HookSpool spool, TimeSpan budgetTotal)`; `CursorLiveSubagentLinker.TryLoadLink(string) -> LinkMarker?`.
- Produces: nothing consumed by later tasks.

- [ ] **Step 1: Write the failing tests**

Create the file with two tests. The first drives a realistic `sessionStart` (null `transcript_path`, as measured) and asserts no marker is written — i.e. the classification arm did not run. The second asserts the orchestrator call-site guard by driving a non-`sessionStart` event and confirming no memory envelope is emitted.

```csharp
using System.Net;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// Pins the MEASURED Cursor hook payload contract, so the classification arm cannot be
/// quietly assumed live again. A sessionStart payload carries a null transcript_path
/// (verified against cursor-agent 2026.07.23-e383d2b; probe logs archived under
/// docs/probes/2026-07-30-cursor-subagent-hooks/), which is why the marker-writing arm has
/// no producer. See docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public class CursorPayloadContractTests {
    [Test]
    public async Task sessionStart_with_null_transcript_path_does_not_run_the_classification_arm() {
        var sid = Guid.NewGuid().ToString("N");
        using var fx = new ContractFixture();

        // The REAL shape: transcript_path is JSON null at sessionStart.
        await fx.HandleAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","transcript_path":null,"workspace_roots":["{{fx.WorkspaceJson}}"]}""");

        // No marker => ResolveParent/SaveLink never ran.
        await Assert.That(CursorLiveSubagentLinker.TryLoadLink(sid)).IsNull();
    }

    [Test]
    public async Task memory_envelope_is_emitted_only_for_sessionStart() {
        var sid = Guid.NewGuid().ToString("N");
        using var fx = new ContractFixture();

        var start = await fx.RenderAsync($$"""{"hook_event_name":"sessionStart","session_id":"{{sid}}","transcript_path":null}""");
        var other = await fx.RenderAsync($$"""{"hook_event_name":"postToolUse","session_id":"{{sid}}","transcript_path":null}""");

        // sessionStart converges on a JSON envelope ("{}" when there is nothing to inject);
        // every other event emits nothing at all. This pins the ORCHESTRATOR CALL-SITE GUARD —
        // an internal invariant. It does NOT prove ClassificationAuthoritative is warranted;
        // that additionally needs the external fact that a child never receives sessionStart,
        // which no unit test can establish.
        await Assert.That(start).IsNotNull();
        await Assert.That(other).IsNull();
    }

    sealed class ContractFixture : IDisposable {
        readonly string _root = Path.Combine(Path.GetTempPath(), $"kcap-cursor-contract-{Guid.NewGuid():N}");
        public string WorkspaceJson => _root.Replace(@"\", @"\\");
        readonly HttpClient _client;
        readonly HookSpool  _spool;

        public ContractFixture() {
            Directory.CreateDirectory(_root);
            _spool  = new HookSpool(Path.Combine(_root, "spool"));
            _client = new HttpClient(new ContractHandler());
        }

        public Task<int> HandleAsync(string json) =>
            CursorHookCommand.HandleCore(_client, "http://localhost", new StringReader(json), _spool, TimeSpan.FromSeconds(2));

        public async Task<string?> RenderAsync(string json) {
            var sw = new StringWriter();
            var prev = Console.Out;
            Console.SetOut(sw);
            try { await HandleAsync(json); } finally { Console.SetOut(prev); }
            var s = sw.ToString().Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        public void Dispose() {
            _client.Dispose();
            try { Directory.Delete(_root, true); } catch { }
        }
    }

    sealed class ContractHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(r.Method == HttpMethod.Get ? HttpStatusCode.NotFound : HttpStatusCode.OK));
    }
}
```

- [ ] **Step 2: Run and observe**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CursorPayloadContractTests/*"
```
Expected: both pass (they pin existing behaviour). If `RenderAsync` returns nothing for `sessionStart`, `HandleCore` writes its envelope via a return value rather than `Console.Out` — inspect `CursorHookCommand.HandleCore`'s signature and adapt the fixture to capture the returned string instead. Do not weaken the assertion to make it pass.

- [ ] **Step 3: Mutation-check both pins**

Temporarily change the guard `if (eventName != "sessionStart") return null;` in `RunMemoryOrchestrationAsync`'s call site to always proceed, and re-run. Expected: `memory_envelope_is_emitted_only_for_sessionStart` FAILS. Then temporarily drop `&& !string.IsNullOrEmpty(transcriptPath)` from the classification guard and re-run. Expected: `sessionStart_with_null_transcript_path…` FAILS (a marker gets written). **Revert both mutations** with `git checkout -- src/` and confirm `git diff src/` is empty.

- [ ] **Step 4: Re-run to confirm green after revert**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CursorPayloadContractTests/*"
```
Expected: both pass.

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.Cli.Tests.Unit/Cursor/CursorPayloadContractTests.cs
git commit -m "test(cursor): pin the measured sessionStart payload contract"
```

---

### Task 4: Pin the stale-state behaviour (D2a, D3.3)

**Files:**
- Create: `test/Capacitor.Cli.Tests.Unit/Cursor/CursorSubagentStaleStateTests.cs`

**Interfaces:**
- Consumes: `CursorLiveSubagentLinker.SaveLink(child, parent, type)`, `.TryLoadLink(child)`; `CursorMarkers.HasSubagentStartAck(child)`; `PathHelpers.ConfigPath("cursor-subagent-links")`.
- Produces: nothing consumed by later tasks.

**How to simulate a `SaveLink` write failure deterministically:** create a **directory** at the exact marker file path (`ConfigPath("cursor-subagent-links")/<childId>`). `File.WriteAllLines` then throws and `SaveLink` swallows it, leaving no marker — without touching `KCAP_CONFIG_DIR` (which is a process-wide `static readonly` and must not be mutated).

- [ ] **Step 1: Write the tests**

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Cursor;

/// <summary>
/// Pins the DECIDED behaviour for durable state that can outlive a session (design spec D2a):
/// a marker-only child fails closed and silent; a malformed marker fails open to top-level.
/// Also characterizes two known-bug states — see the explicit note on those two tests.
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public class CursorSubagentStaleStateTests {
    static string MarkerPath(string child) =>
        Path.Combine(PathHelpers.ConfigPath("cursor-subagent-links"), child);

    [Test]
    public async Task a_well_formed_marker_is_loaded_and_activates_the_divert() {
        var child = Guid.NewGuid().ToString("N");
        try {
            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "researcher");
            var marker = CursorLiveSubagentLinker.TryLoadLink(child);
            await Assert.That(marker).IsNotNull();
            await Assert.That(marker!.Value.ParentSessionId).IsEqualTo("parent-sid");
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task a_malformed_marker_fails_open_to_top_level() {
        var child = Guid.NewGuid().ToString("N");
        try {
            Directory.CreateDirectory(PathHelpers.ConfigPath("cursor-subagent-links"));
            // One line only: TryLoadLink requires >= 2 with a non-empty first.
            File.WriteAllText(MarkerPath(child), "only-one-line\n");

            // Fails OPEN: no link => the session is treated as top-level, which is the safe
            // direction. Contrast with the well-formed-but-unacked case, which fails CLOSED.
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task an_empty_parent_id_in_the_marker_also_fails_open() {
        var child = Guid.NewGuid().ToString("N");
        try {
            Directory.CreateDirectory(PathHelpers.ConfigPath("cursor-subagent-links"));
            File.WriteAllText(MarkerPath(child), "\ntask\n");
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally { TryDeleteMarker(child); }
    }

    [Test]
    public async Task a_marker_without_an_ack_leaves_the_child_gated_and_silent() {
        var child = Guid.NewGuid().ToString("N");
        try {
            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "task");

            // No ack was ever recorded, so every non-start hook for this child returns early:
            // its raw event AND its transcript backfill are suppressed indefinitely. This is
            // FAIL-CLOSED AND SILENT by decision (start-before-content ordering); recovery is
            // `kcap import --cursor` plus the server-side adoption sweep.
            await Assert.That(CursorMarkers.HasSubagentStartAck(child)).IsFalse();
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNotNull();
        } finally { TryDeleteMarker(child); }
    }

    // ---------------------------------------------------------------------------------
    // KNOWN-BUG CHARACTERIZATION TESTS — NOT a contract.
    //
    // Both tests below record that a SaveLink write failure leaves start side effects with no
    // marker, which can route the same child transcript twice (once under the parent, once as
    // its own top-level session). The design spec (D2a) labels this state UNSUPPORTED and lists
    // remedies; the leading one is to have SaveLink report success and fail open BEFORE the
    // start is posted.
    //
    // These are therefore EXCLUDED from the mutation rule that governs every other pin here.
    // When a remedy lands these assertions are EXPECTED to fail — rewrite or delete them then;
    // do not "fix" them to keep passing.
    // ---------------------------------------------------------------------------------

    [Test]
    public async Task savelink_write_failure_currently_leaves_no_marker_known_risk() {
        var child = Guid.NewGuid().ToString("N");
        try {
            Directory.CreateDirectory(MarkerPath(child)); // a DIRECTORY where the file must go

            // Swallowed by SaveLink's catch — the caller has already set subagentParentId, so
            // the divert still proceeds and can post/spool a start with no marker on disk.
            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "task");

            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally {
            try { Directory.Delete(MarkerPath(child), true); } catch { }
        }
    }

    [Test]
    public async Task savelink_failure_does_not_signal_the_caller_known_risk() {
        var child = Guid.NewGuid().ToString("N");
        try {
            Directory.CreateDirectory(MarkerPath(child));

            // SaveLink returns void and throws nothing: the caller cannot tell the marker was
            // lost, which is exactly why the start's side effects still run. Remedy 1 in the
            // spec is to make this observable and fail open instead.
            CursorLiveSubagentLinker.SaveLink(child, "parent-sid", "task");
            await Assert.That(CursorLiveSubagentLinker.TryLoadLink(child)).IsNull();
        } finally {
            try { Directory.Delete(MarkerPath(child), true); } catch { }
        }
    }

    static void TryDeleteMarker(string child) {
        try { File.Delete(MarkerPath(child)); } catch { }
    }
}
```

- [ ] **Step 2: Run them**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CursorSubagentStaleStateTests/*"
```
Expected: all six pass. Confirm the reported `total:` is 6 — a `--treenode-filter` typo silently runs zero tests.

- [ ] **Step 3: Mutation-check the non-characterization pins only**

Temporarily change `TryLoadLink`'s guard `lines.Length >= 2 && !string.IsNullOrEmpty(lines[0])` to `lines.Length >= 1`. Re-run. Expected: `a_malformed_marker_fails_open_to_top_level` FAILS. Revert with `git checkout -- src/` and confirm `git diff src/` is empty. Do **not** mutation-check the two `_known_risk` tests.

- [ ] **Step 4: Re-run after revert**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CursorSubagentStaleStateTests/*"
```
Expected: 6 pass.

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.Cli.Tests.Unit/Cursor/CursorSubagentStaleStateTests.cs
git commit -m "test(cursor): pin decided stale-state behaviour and characterize the SaveLink-failure risk"
```

---

### Task 5: Remove the tests that assert the unreachable child-lifecycle architecture (D3)

**Files:**
- Modify: `test/Capacitor.Cli.Tests.Unit/Cursor/CursorLiveSubagentIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing. Produces: nothing.

Per the spec's disposition table, these four assert child `sessionStart` → `subagent-start` / child `sessionEnd` → `subagent-stop`, the trigger a native revival must *not* use. They are reached only through `Fixture.SetupLinkedPair` + a `sessionStart` carrying a non-null `transcript_path` — a payload the harness never produces.

- [ ] **Step 1: Delete the four wrong-architecture tests**

Remove these methods entirely:
- `linked_child_sessionStart_posts_subagent_start_not_session_start` (`:18–33`)
- `linked_child_transcript_is_routed_under_the_parent_with_agent_id` (`:35–45`)
- `linked_child_sessionEnd_posts_subagent_stop_not_session_end` (`:68–85`)
- `linked_child_subagent_stop_is_not_delivered_ahead_of_a_spooled_subagent_start` (`:106–143`)

Keep `linked_child_mid_lifecycle_hook_is_suppressed_but_transcript_still_backfills`, `unlinked_session_still_posts_top_level_session_start`, and `live_agent_id_matches_the_dashless_id_the_import_path_would_use`.

- [ ] **Step 2: Reseed the retained mid-lifecycle test so it does not drive a child `sessionStart`**

That test currently establishes the link by calling `fx.HandleAsync(childId, "sessionStart", childPath)` first. Replace that first call with a direct marker seed, so the test depends on marker state without asserting a child lifecycle hook produces it:

```csharp
        // Seed the link directly. Establishing it via a child sessionStart would assert a
        // trigger a real child never fires — see the design spec's test-disposition table.
        CursorLiveSubagentLinker.SaveLink(childId, parentId, "task");
        CursorMarkers.MarkSubagentStartAcked(childId);
        fx.Sent.Clear();
        fx.RouteOrder.Clear();
```

Note the ack seed is required: without it the mid-lifecycle hook returns early at the no-ack gate and the backfill assertion fails. Add `using Capacitor.Cli.Core;` if not already present.

- [ ] **Step 3: Rewrite the class doc comment**

Replace the `<summary>` with:

```csharp
/// <summary>
/// Coverage for the marker-driven Cursor subagent divert. Scenarios here seed the link marker
/// (and ack) DIRECTLY: a test may depend on that persistent state, but must never assert that
/// a child lifecycle hook produces it — a real Cursor subagent child fires neither sessionStart
/// nor sessionEnd, so the four tests that asserted those triggers were removed. See
/// docs/superpowers/specs/2026-07-30-ai1505-cursor-subagent-classification-design.md
/// </summary>
```

- [ ] **Step 4: Run the file**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CursorLiveSubagentIntegrationTests/*"
```
Expected: 3 tests, all pass. Verify `total: 3`.

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.Cli.Tests.Unit/Cursor/CursorLiveSubagentIntegrationTests.cs
git commit -m "test(cursor): drop tests asserting the child-lifecycle subagent trigger"
```

---

### Task 6: Reseed the watcher-spawn tests and run the full suite

**Files:**
- Modify: `test/Capacitor.Cli.Tests.Unit/Cursor/CursorWatcherSpawnTests.cs` (`:102–153`, `:190–239`, `:254–318`)

**Interfaces:**
- Consumes: `CursorLiveSubagentLinker.SaveLink`, `CursorMarkers.MarkSubagentStartAcked`. Produces: nothing.

- [ ] **Step 1: Inspect the three ranges**

```bash
sed -n '100,160p;186,242p;250,320p' test/Capacitor.Cli.Tests.Unit/Cursor/CursorWatcherSpawnTests.cs
```
Identify every place a child `sessionStart` (or `sessionEnd`) drives the flow rather than merely seeding state.

- [ ] **Step 2: Reseed, don't re-trigger**

For each test that *generates* the subagent-start by invoking a child `sessionStart`, replace that invocation with a direct seed (`SaveLink` + `MarkSubagentStartAcked`, or a spooled entry where the test is about drain ordering) and drive the behaviour under test with a non-lifecycle hook. Where a test's whole subject *is* the child-lifecycle trigger — i.e. it cannot be expressed without asserting child `sessionStart` produces `subagent-start` — delete it and note the deletion in the commit message.

Preserve each test's real invariant: the no-ack gate, the deferred watcher spawn after a spooled start is acked, and start-before-stop ordering are all still worth covering.

- [ ] **Step 3: Run the file**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/CursorWatcherSpawnTests/*"
```
Expected: all pass. Check `total:` is non-zero and matches the number of remaining tests.

- [ ] **Step 4: Run the whole unit suite against the baseline**

```bash
dotnet build-server shutdown
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj 2>&1 | tail -30
```
Expected: failures limited to the known ~42 pre-existing macOS failures (MCP-registration / config-file / uninstall). **No Cursor-suite failures.** If a Cursor test fails, fix it — do not add it to the baseline.

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.Cli.Tests.Unit/Cursor/CursorWatcherSpawnTests.cs
git commit -m "test(cursor): seed subagent state directly instead of via child lifecycle hooks"
```

---

## Self-Review

**Spec coverage:**

| Spec item | Task |
|---|---|
| D1 (do not build the heuristic) | No task — the decision is to build nothing |
| D2 retention + D2.1 doc comments | Task 2 |
| D2.2 inertness tested | Tasks 3, 4 |
| D2.3 / D2a decided stale state | Task 4 |
| D2.4 single-sourced reachability | Tasks 1, 2 (comments cite the spec path, never restate) |
| D3 test disposition table | Tasks 5, 6 |
| D3 pins 1–3 | Tasks 3, 4 |
| D3 characterization exclusion | Task 4 (two `_known_risk` tests, mutation-check skipped) |
| D4 memory note | Task 1 Step 3 |
| D4a two inline comments | Task 1 Steps 1–2 |
| D5 / §7 IDE gate | No task — explicitly not pre-merge blocking; carried in the spec + PR body |

**Placeholder scan:** Task 6 Step 2 is the only judgement-based step, deliberately — the three ranges must be read before deciding reseed-vs-delete, and inventing exact replacement code for tests I have not fully read would be a worse failure than naming the rule to apply. Rule and preserved invariants are stated explicitly.

**Type consistency:** `TryLoadLink` returns `LinkMarker?` (a `readonly record struct`), so `.Value.ParentSessionId` is correct after `IsNotNull()`. `SaveLink` returns `void` — Task 4's characterization test asserts absence of the marker rather than a return value, which is the point of remedy 1. `HasSubagentStartAck` returns `bool`.
