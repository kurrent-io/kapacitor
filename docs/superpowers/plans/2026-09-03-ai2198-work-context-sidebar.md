# Work-context sidebar (AI-2198) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the 400px work-context sidebar to the desktop app's session workspace: the session's work item with its declared breakdown and blockers, its pull requests, who is attached, and the session's facts, fed by three existing server reads.

**Architecture:** Two additive fields join the daemon status wire (`session_id`, `branch`). Core gains a `WorkItems` namespace: DTOs mirroring the three server routes, a channel seam, an HTTP client, and a totalized reader. The app composes one leased, disposable source over the profile's server URL and token store, a per-workspace `WorkContextViewModel` that reads under a session-id lease with a 30-second poll, and a `WorkContextView` in a new right column of `WorkspaceView`. Everything the server does not expose over HTTP renders as a SOON pill.

**Tech Stack:** .NET 10, System.Text.Json source generation (NativeAOT-clean Core), Avalonia + ReactiveUI + DynamicData (app), TUnit on Microsoft Testing Platform, WireMock.Net (Core HTTP tests), Microsoft.Extensions.Time.Testing (`FakeTimeProvider`).

**Spec:** `docs/superpowers/specs/2026-09-03-ai2198-work-context-sidebar-design.md`

## Global Constraints

- Core (`src/Capacitor.Cli.Core`) stays BCL + System.Text.Json only; every root the client deserializes is registered in `CapacitorJsonContext` as the exact closed type, and reads go through `JsonTypeInfo<T>` overloads.
- `AgentStatusDto` members are additive trailing nullable members, always emitted (null, never omitted). `FrameType` is untouched.
- Comments: scarce; no ticket ids, spec coordinates, review history or change narration in code. The spec may cite issues; code may not.
- Every daemon-cache consumer does `ObserveOn(RxSchedulers.MainThreadScheduler)` before touching bound state; pool-thread completions hop through `Dispatcher.UIThread.InvokeAsync`.
- App VM tests carry `[NotInParallel("AvaloniaSession")]` and run under `RunOnUiAsync`. Temp files come from Helpers' `TempDir`.
- Commit subjects: one imperative clause, at most 80 characters, no issue reference (AI-2198 has no GitHub issue; do not invent one). Commit bodies end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Run git as `/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny …` from this worktree (the branch is `alexeyzimarev/ai-2198-desktop-shell-work-context-sidebar`).
- README is unchanged (no CLI surface changes). `docs/CHANGES.md` gains one entry (Task 12).
- Copy strings are exactly the spec's; they live as constants on `WorkContextViewModel`.

## File Structure

Create:
- `src/Capacitor.Cli.Core/WorkItems/WorkContextDtos.cs` — the four server shapes and their sub-records.
- `src/Capacitor.Cli.Core/WorkItems/IWorkContextChannel.cs` — `WorkContextOutcome<T>` and the three-route seam.
- `src/Capacitor.Cli.Core/WorkItems/WorkContextIds.cs` — session/work-item id canonicalization and validation.
- `src/Capacitor.Cli.Core/WorkItems/WorkContextClient.cs` — the HTTP implementation of the channel.
- `src/Capacitor.Cli.Core/WorkItems/WorkContextReader.cs` — `WorkContextReadKind`, `WorkContextRead`, `ReadAsync`.
- `src/Capacitor.Cli.Core/WorkItems/WorkContextLabel.cs` — the `"KEY — title"` split.
- `src/Capacitor.App/Services/IWorkContextSource.cs` — the app-facing read seam.
- `src/Capacitor.App/Services/ServerWorkContextSource.cs` — leased client over profile + token store.
- `src/Capacitor.App/Services/ServerClients.cs` — one-shot owner of launch client, source and sign-in subject.
- `src/Capacitor.App/ViewModels/WorkContextViewModel.cs` — phases, facts, lease/fetch, projections.
- `src/Capacitor.App/ViewModels/WorkContextItems.cs` — `WorkContextPartViewModel`, `WorkContextLinkViewModel`.
- `src/Capacitor.App/Views/WorkContextView.axaml` + `.axaml.cs` — the pane.
- Tests: `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/{WorkContextDtoTests,WorkContextClientTests,WorkContextReaderTests,WorkContextLabelTests}.cs`, `test/Capacitor.App.Tests.Unit/{FakeWorkContextSource,WorkContextViewModelTests,ServerWorkContextSourceTests,ServerClientsTests}.cs`.

Modify:
- `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs` — two trailing members.
- `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` — stamp them.
- `src/Capacitor.Cli.Core/Models.cs` — four `[JsonSerializable]` registrations.
- `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` — `WorkContext` property and teardown.
- `src/Capacitor.App/Views/WorkspaceView.axaml` — two columns; `App.axaml` — `KcapPurpleDimBrush`; `Views/MainWindow.axaml` — `MinWidth`.
- `src/Capacitor.App/App.axaml.cs` — source, `ServerClients`, sign-in signal, both cleanup paths.
- Tests: `StatusIpcJsonTests`, `AgentStatusSnapshotTests`, `WorkspaceFixtures`, `WorkspaceViewModelTests`, `WorkspaceViewSmokeTests`, `MainWindowSmokeTests`, `MainWindowViewModelTests`, `WorkspaceNavigationTests`, `AppStartupTests`.
- `docs/CHANGES.md`.

---

### Task 1: `session_id` and `branch` on the status wire

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs:38-65`
- Modify: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs`
- Modify: `test/Capacitor.App.Tests.Unit/WorkspaceFixtures.cs:13-20`

**Interfaces:**
- Produces: `AgentStatusDto.SessionId` (`string?`, wire `session_id`) and `AgentStatusDto.Branch` (`string?`, wire `branch`), trailing after `BorrowedFrom`. `WorkspaceFixtures.Agent(..., string? sessionId = null, string? branch = null)`.

- [ ] **Step 1: Write the failing tests**

Append to `StatusIpcJsonTests`:

```csharp
    /// The session id and branch ride last, and every value is emitted even when null so an
    /// older client reads absence, never a missing key.
    [Test]
    public async Task Session_and_branch_members_serialize_last_and_nulls_are_emitted() {
        var full = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc), null, null,
            HasTerminal: true, WorktreePath: "/repo/.capacitor/worktrees/agent-1", WorkLocation: "owned",
            SessionId: "0123456789abcdef0123456789abcdef", Branch: "feature/sidebar");
        var older = full with { SessionId = null, Branch = null };

        var json = JsonSerializer.Serialize(full, StatusIpcJsonContext.Default.AgentStatusDto);
        var jsonNull = JsonSerializer.Serialize(older, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(json).EndsWith(""","borrowed_from":null,"session_id":"0123456789abcdef0123456789abcdef","branch":"feature/sidebar"}""");
        await Assert.That(jsonNull).EndsWith(""","borrowed_from":null,"session_id":null,"branch":null}""");
    }

    [Test]
    public async Task Old_agent_json_without_session_and_branch_deserializes_to_null() {
        var dto = new AgentStatusDto(
            "a1", "agent", "claude", "/repo", "Running",
            null, null, null, DateTime.UtcNow, null, null, SessionId: "abc", Branch: "main");
        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);
        var stripped = System.Text.RegularExpressions.Regex.Replace(json, ",\"(session_id|branch)\":[^,}]+", "");

        var back = JsonSerializer.Deserialize(stripped, StatusIpcJsonContext.Default.AgentStatusDto);

        await Assert.That(back!.SessionId).IsNull();
        await Assert.That(back.Branch).IsNull();
    }
```

Then update the three existing exact-JSON pins so they still pass once the members exist:

- `DaemonStatus_serializes_exactly_with_nulls_present_and_pinned_field_order`: in the expected string, replace both occurrences of `"borrowed_from":null}` with `"borrowed_from":null,"session_id":null,"branch":null}`.
- The `transcript_path` test whose assertions end with `"borrowed_from":null}"""`: append `,"session_id":null,"branch":null` before the closing `}` in both `EndsWith` strings.
- `Checkout_members_serialize_last_and_nulls_are_emitted`: both `EndsWith` strings gain `,"session_id":null,"branch":null` before the closing `}`.

- [ ] **Step 2: Run the suite to verify the new tests fail to compile**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/StatusIpcJsonTests/*"`
Expected: build error `CS1739: The best overload for 'AgentStatusDto' does not have a parameter named 'SessionId'`.

- [ ] **Step 3: Add the members**

In `StatusIpc.cs`, replace the last parameter of `AgentStatusDto`:

```csharp
    // The checkout root a borrowed reviewer reviews — for a runtime that needs its own snapshot
    // this differs from WorktreePath, and it is the node the reviewer belongs under. Null unless
    // borrowed.
    string? BorrowedFrom = null,
    // The session id the daemon reports to the server: discovered from the transcript for a PTY
    // vendor, taken from the handshake for an ACP one. Null is "older daemon", "not resolved yet"
    // or "no session for this runtime" alike — a client waits, it never distinguishes them.
    string? SessionId = null,
    // The branch of the checkout the agent runs in; null from an older daemon or a launch that
    // recorded none (a borrowed in-place checkout).
    string? Branch = null);
```

- [ ] **Step 4: Run the suite to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/StatusIpcJsonTests/*"`
Expected: all `StatusIpcJsonTests` pass.

- [ ] **Step 5: Extend the shared app fixture**

In `test/Capacitor.App.Tests.Unit/WorkspaceFixtures.cs` replace `Agent`:

```csharp
    public static AgentStatusDto Agent(
            string id, string vendor, bool? hasTerminal, string? repoPath = null,
            string kind = "agent", string? model = null,
            string? worktreePath = null, string? workLocation = null, string? borrowedFrom = null,
            string? sessionId = null, string? branch = null) => new(
        id, kind, vendor, repoPath, "Running",
        FlowRunId: null, FlowRole: null, Requester: null, CreatedAt: DateTime.UtcNow, Model: model,
        RequesterDisplay: null, HasTerminal: hasTerminal,
        WorktreePath: worktreePath, WorkLocation: workLocation, BorrowedFrom: borrowedFrom,
        SessionId: sessionId, Branch: branch);
```

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: builds with no warnings.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs test/Capacitor.App.Tests.Unit/WorkspaceFixtures.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Carry the session id and branch on the agent status wire" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 2: The daemon stamps both members

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs:44-60`
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests.cs`

**Interfaces:**
- Consumes: `AgentStatusDto.SessionId`/`Branch` (Task 1); `AgentInstance.SessionId`, `AgentInstance.Worktree.Branch`, `IAcpTranscriptSource.AcpSessionId` (existing daemon types); test seams `SeedAgentForTest(worktree: …)`, `AgentOrchestratorHarness.SeedAcpAgent`, `FakeAcpRuntime` (`AcpSessionId` defaults to `"acp-sess-1"`).

- [ ] **Step 1: Write the failing tests**

Append to `AgentStatusSnapshotTests`:

```csharp
    /// The wire's session id is whatever the daemon reports to the server: the discovered id for
    /// a PTY agent, the handshake id for an ACP one, and the discovered id when both exist.
    [Test]
    public async Task Status_payload_carries_session_id_from_discovery_or_the_acp_handshake() {
        var fx = Build();
        try {
            var pty = fx.Orchestrator.SeedAgentForTest("pty-1");
            var acp = AgentOrchestratorHarness.SeedAcpAgent(fx.Orchestrator, "acp-1", new FakeAcpRuntime());
            var both = AgentOrchestratorHarness.SeedAcpAgent(fx.Orchestrator, "acp-2", new FakeAcpRuntime { AcpSessionId = "handshake" });
            both.SessionId = "discovered";

            string Json(string id) => JsonSerializer.Serialize(
                fx.Orchestrator.SnapshotAgentsForStatus().Single(a => a.Id == id), StatusIpcJsonContext.Default.AgentStatusDto);

            await Assert.That(Json("pty-1")).Contains("\"session_id\":null");
            pty.SessionId = "0123456789abcdef0123456789abcdef";
            await Assert.That(Json("pty-1")).Contains("\"session_id\":\"0123456789abcdef0123456789abcdef\"");
            await Assert.That(Json("acp-1")).Contains("\"session_id\":\"acp-sess-1\"");
            await Assert.That(Json("acp-2")).Contains("\"session_id\":\"discovered\"");
        } finally { await fx.CleanupAsync(); }
    }

    [Test]
    public async Task Status_payload_normalizes_a_blank_branch_and_passes_a_real_one() {
        var fx = Build();
        try {
            fx.Orchestrator.SeedAgentForTest("blank-branch", worktree: new WorktreeInfo("/repo/w", "", "/repo"));
            fx.Orchestrator.SeedAgentForTest("real-branch", worktree: new WorktreeInfo("/repo/w2", "feature/sidebar", "/repo"));

            var byId = fx.Orchestrator.SnapshotAgentsForStatus().ToDictionary(a => a.Id);

            await Assert.That(byId["blank-branch"].Branch).IsNull();
            await Assert.That(byId["real-branch"].Branch).IsEqualTo("feature/sidebar");
            var json = JsonSerializer.Serialize(byId["real-branch"], StatusIpcJsonContext.Default.AgentStatusDto);
            await Assert.That(json).Contains("\"branch\":\"feature/sidebar\"");
        } finally { await fx.CleanupAsync(); }
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentStatusSnapshotTests/*"`
Expected: the two new tests fail on `"session_id":null` where a value was expected and on `Branch` being null.

- [ ] **Step 3: Stamp the members in the snapshot**

In `AgentOrchestrator.LocalIpc.cs`, `SnapshotAgentsForStatus`, replace the `BorrowedFrom:` line:

```csharp
                BorrowedFrom: a.Checkout.BorrowedFrom,
                SessionId: a.SessionId ?? (a.Runtime as IAcpTranscriptSource)?.AcpSessionId,
                Branch: string.IsNullOrWhiteSpace(a.Worktree.Branch) ? null : a.Worktree.Branch))];
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentStatusSnapshotTests/*"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Stamp the session id and branch into the daemon status snapshot" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 3: Core DTOs and their AOT registration

**Files:**
- Create: `src/Capacitor.Cli.Core/WorkItems/WorkContextDtos.cs`
- Modify: `src/Capacitor.Cli.Core/Models.cs` (the `[JsonSerializable]` block near `CliProjectError`, around line 983)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextDtoTests.cs`

**Interfaces:**
- Produces (namespace `Capacitor.Cli.Core.WorkItems`): `SessionWorkItemAssignmentDto`, `WorkItemRefDto`, `WorkItemTopologyPartDto`, `WorkItemTopologyDto`, `SessionRepositoryDto`, `SessionPullRequestDto`, `SessionSummaryDto`, `WorkItemErrorDto`; context members `CapacitorJsonContext.Default.ListSessionWorkItemAssignmentDto`, `.WorkItemTopologyDto`, `.SessionSummaryDto`, `.WorkItemErrorDto`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextDtoTests.cs`:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

/// The three server shapes the sidebar reads, pinned against literal server-shaped bodies, plus
/// the source-generated metadata for each root: a round trip alone would pass under reflection
/// and only fail on the AOT binary.
public class WorkContextDtoTests {
    [Test]
    public async Task Assignments_deserialize_from_the_server_shape_and_ignore_extra_members() {
        const string body = """[{"work_item_id":"w1","label":"AI-2198 — Desktop shell: work-context sidebar","source":"mcp","confidence":1.0,"is_primary":true,"future":{"x":1}}]""";

        var rows = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.ListSessionWorkItemAssignmentDto)!;

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].WorkItemId).IsEqualTo("w1");
        await Assert.That(rows[0].Label).IsEqualTo("AI-2198 — Desktop shell: work-context sidebar");
        await Assert.That(rows[0].Source).IsEqualTo("mcp");
        await Assert.That(rows[0].IsPrimary).IsTrue();
    }

    [Test]
    public async Task Topology_deserializes_parts_relations_cycle_and_a_null_item() {
        const string body = """{"parts":[{"work_item_id":"p1","title":"Move the override","ordinal":0}],"part_of":{"work_item_id":"w0","title":"Parent"},"blocks":[],"blocked_by":[{"work_item_id":"b1","title":"Pin the helper"}],"cycle":"none","item":null}""";

        var topology = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.WorkItemTopologyDto)!;

        await Assert.That(topology.Parts[0].Title).IsEqualTo("Move the override");
        await Assert.That(topology.PartOf!.Title).IsEqualTo("Parent");
        await Assert.That(topology.BlockedBy[0].WorkItemId).IsEqualTo("b1");
        await Assert.That(topology.Cycle).IsEqualTo("none");
        await Assert.That(topology.Item).IsNull();
    }

    [Test]
    public async Task Summary_deserializes_the_subset_the_pane_reads_and_tolerates_the_rest() {
        const string body = """{"session_id":"s1","title":"t","vendor":"claude","model":"claude-opus-5","status":"active","cwd":"/repo","repo_branch":"feature/x","repo_owner":"kurrent-io","repo_name":"kcap-cli","pr_number":629,"pr_url":"https://github.com/kurrent-io/kcap-cli/pull/629","pr_title":"Pin the env scope","repositories":[{"repo_hash":"h","owner":"kurrent-io","repo_name":"kcap-cli","branch":"feature/x","is_primary":true,"first_seen_at":"2026-09-01T00:00:00Z"}],"pull_requests":[{"repo_hash":"h","owner":"kurrent-io","repo_name":"kcap-cli","number":629,"url":"https://github.com/kurrent-io/kcap-cli/pull/629","title":"Pin the env scope","head_ref":"feature/x"}],"stats":{"events":3}}""";

        var summary = JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.SessionSummaryDto)!;

        await Assert.That(summary.SessionId).IsEqualTo("s1");
        await Assert.That(summary.RepoBranch).IsEqualTo("feature/x");
        await Assert.That(summary.PrNumber).IsEqualTo(629);
        await Assert.That(summary.Repositories[0].Branch).IsEqualTo("feature/x");
        await Assert.That(summary.PullRequests[0].Number).IsEqualTo(629);
        await Assert.That(summary.PullRequests[0].HeadRef).IsEqualTo("feature/x");
    }

    [Test]
    public async Task Error_body_parses() {
        var error = JsonSerializer.Deserialize("""{"error":"work_items_not_in_plan","message":"Upgrade the plan."}""", CapacitorJsonContext.Default.WorkItemErrorDto)!;

        await Assert.That(error.Error).IsEqualTo("work_items_not_in_plan");
        await Assert.That(error.Message).IsEqualTo("Upgrade the plan.");
    }

    [Test]
    public async Task Every_root_the_client_reads_has_generated_metadata() {
        await Assert.That(CapacitorJsonContext.Default.GetTypeInfo(typeof(List<SessionWorkItemAssignmentDto>))).IsNotNull();
        await Assert.That(CapacitorJsonContext.Default.GetTypeInfo(typeof(WorkItemTopologyDto))).IsNotNull();
        await Assert.That(CapacitorJsonContext.Default.GetTypeInfo(typeof(SessionSummaryDto))).IsNotNull();
        await Assert.That(CapacitorJsonContext.Default.GetTypeInfo(typeof(WorkItemErrorDto))).IsNotNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextDtoTests/*"`
Expected: `CS0234: The type or namespace name 'WorkItems' does not exist`.

- [ ] **Step 3: Create the DTOs**

Create `src/Capacitor.Cli.Core/WorkItems/WorkContextDtos.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.WorkItems;

/// One row of <c>GET /api/work-items/session/{id}</c>. <see cref="Label"/> is the server's display
/// label: <c>"KEY — title"</c> for a keyed item, otherwise the title alone, which may itself be a key.
public sealed record SessionWorkItemAssignmentDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("label")]        public required string Label      { get; init; }
    [JsonPropertyName("source")]       public string?         Source     { get; init; }
    [JsonPropertyName("confidence")]   public double          Confidence { get; init; }
    [JsonPropertyName("is_primary")]   public bool            IsPrimary  { get; init; }
}

public sealed record WorkItemRefDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title      { get; init; }
}

public sealed record WorkItemTopologyPartDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title      { get; init; }
    [JsonPropertyName("ordinal")]      public int             Ordinal    { get; init; }
}

/// The body of <c>GET /api/work-items/{id}/topology</c>. <see cref="Cycle"/> is
/// none|cyclic|indeterminate. <see cref="Item"/> is nullable on the wire. There is no completion
/// figure: the server does not compute one.
public sealed record WorkItemTopologyDto {
    [JsonPropertyName("parts")]      public List<WorkItemTopologyPartDto> Parts     { get; init; } = [];
    [JsonPropertyName("part_of")]    public WorkItemRefDto?               PartOf    { get; init; }
    [JsonPropertyName("blocks")]     public List<WorkItemRefDto>          Blocks    { get; init; } = [];
    [JsonPropertyName("blocked_by")] public List<WorkItemRefDto>          BlockedBy { get; init; } = [];
    [JsonPropertyName("cycle")]      public string                        Cycle     { get; init; } = "none";
    [JsonPropertyName("item")]       public WorkItemRefDto?               Item      { get; init; }
}

public sealed record SessionRepositoryDto {
    [JsonPropertyName("repo_hash")]  public required string RepoHash  { get; init; }
    [JsonPropertyName("owner")]      public required string Owner     { get; init; }
    [JsonPropertyName("repo_name")]  public required string RepoName  { get; init; }
    [JsonPropertyName("branch")]     public string?         Branch    { get; init; }
    [JsonPropertyName("is_primary")] public bool            IsPrimary { get; init; }
}

public sealed record SessionPullRequestDto {
    [JsonPropertyName("repo_hash")] public required string RepoHash { get; init; }
    [JsonPropertyName("owner")]     public required string Owner    { get; init; }
    [JsonPropertyName("repo_name")] public required string RepoName { get; init; }
    [JsonPropertyName("number")]    public int             Number   { get; init; }
    [JsonPropertyName("url")]       public string?         Url      { get; init; }
    [JsonPropertyName("title")]     public string?         Title    { get; init; }
    [JsonPropertyName("head_ref")]  public string?         HeadRef  { get; init; }
}

/// The subset of <c>GET /api/sessions/{id}/summary</c> the sidebar reads; every other member of
/// the server's record is ignored on deserialization.
public sealed record SessionSummaryDto {
    [JsonPropertyName("session_id")]    public required string                 SessionId    { get; init; }
    [JsonPropertyName("title")]         public string?                         Title        { get; init; }
    [JsonPropertyName("vendor")]        public string?                         Vendor       { get; init; }
    [JsonPropertyName("model")]         public string?                         Model        { get; init; }
    [JsonPropertyName("repo_owner")]    public string?                         RepoOwner    { get; init; }
    [JsonPropertyName("repo_name")]     public string?                         RepoName     { get; init; }
    [JsonPropertyName("repo_branch")]   public string?                         RepoBranch   { get; init; }
    [JsonPropertyName("pr_number")]     public int?                            PrNumber     { get; init; }
    [JsonPropertyName("pr_url")]        public string?                         PrUrl        { get; init; }
    [JsonPropertyName("pr_title")]      public string?                         PrTitle      { get; init; }
    [JsonPropertyName("repositories")]  public List<SessionRepositoryDto>      Repositories { get; init; } = [];
    [JsonPropertyName("pull_requests")] public List<SessionPullRequestDto>     PullRequests { get; init; } = [];
}

/// The 4xx body every <c>/api/work-items*</c> route shares; <c>work_items_not_in_plan</c> is the plan gate.
public sealed record WorkItemErrorDto {
    [JsonPropertyName("error")]   public required string Error   { get; init; }
    [JsonPropertyName("message")] public string?         Message { get; init; }
}
```

- [ ] **Step 4: Register the roots**

In `src/Capacitor.Cli.Core/Models.cs`, directly after `[JsonSerializable(typeof(CliProjectError))]`, add:

```csharp
[JsonSerializable(typeof(List<WorkItems.SessionWorkItemAssignmentDto>))]
[JsonSerializable(typeof(WorkItems.WorkItemTopologyDto))]
[JsonSerializable(typeof(WorkItems.SessionSummaryDto))]
[JsonSerializable(typeof(WorkItems.WorkItemErrorDto))]
```

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextDtoTests/*"`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.Cli.Core/WorkItems/WorkContextDtos.cs src/Capacitor.Cli.Core/Models.cs test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextDtoTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Add the work-item read DTOs to Core" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 4: Channel seam, id containment, and the HTTP client

**Files:**
- Create: `src/Capacitor.Cli.Core/WorkItems/IWorkContextChannel.cs`
- Create: `src/Capacitor.Cli.Core/WorkItems/WorkContextIds.cs`
- Create: `src/Capacitor.Cli.Core/WorkItems/WorkContextClient.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextClientTests.cs`

**Interfaces:**
- Consumes: the DTOs and context members from Task 3.
- Produces: `WorkContextOutcome<T>(int StatusCode, T? Body, WorkItemErrorDto? Error)` with `bool Succeeded`; `IWorkContextChannel` with `GetSessionAssignmentsAsync(string sessionId, CancellationToken)`, `GetTopologyAsync(string workItemId, CancellationToken)`, `GetSessionSummaryAsync(string sessionId, CancellationToken)`; `WorkContextIds.CanonicalSessionId(string?)` and `WorkContextIds.ValidWorkItemId(string?)` returning `string?`; `WorkContextClient(HttpClient http, string serverUrl)`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextClientTests.cs`:

```csharp
using System.Net;
using Capacitor.Cli.Core.WorkItems;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

/// The routes and the id containment: a session id is canonicalized, validated and escaped before it
/// enters a path, a work item id validated and escaped, and neither can turn into a dot segment.
public class WorkContextClientTests {
    const string Dashed   = "01234567-89ab-cdef-0123-456789abcdef";
    const string Dashless = "0123456789abcdef0123456789abcdef";

    sealed class ThrowingHandler(Exception exception) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    sealed class CancellingHandler : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    static WireMockServer Serve(string path, int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body).WithHeader("Content-Type", "application/json"));
        return server;
    }

    [Test]
    public async Task Assignments_route_strips_dashes_and_parses_the_rows() {
        using var server = Serve($"/api/work-items/session/{Dashless}", 200, """[{"work_item_id":"w1","label":"AI-1 — t","source":"mcp","confidence":1,"is_primary":true}]""");
        using var http = new HttpClient();

        var outcome = await new WorkContextClient(http, server.Urls[0] + "/").GetSessionAssignmentsAsync(Dashed, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Succeeded).IsTrue();
        await Assert.That(outcome.Body![0].WorkItemId).IsEqualTo("w1");
        await Assert.That(server.LogEntries.Single().RequestMessage.Path).IsEqualTo($"/api/work-items/session/{Dashless}");
    }

    [Test]
    public async Task Topology_and_summary_routes_hit_their_paths() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/work-items/w1/topology").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"parts":[],"part_of":null,"blocks":[],"blocked_by":[],"cycle":"none","item":{"work_item_id":"w1","title":"T"}}""").WithHeader("Content-Type", "application/json"));
        server.Given(Request.Create().WithPath($"/api/sessions/{Dashless}/summary").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"session_id":"s1","repositories":[],"pull_requests":[]}""").WithHeader("Content-Type", "application/json"));
        using var http = new HttpClient();
        var client = new WorkContextClient(http, server.Urls[0]);

        var topology = await client.GetTopologyAsync(" w1 ", CancellationToken.None);
        var summary = await client.GetSessionSummaryAsync(Dashed, CancellationToken.None);

        await Assert.That(topology.Body!.Item!.Title).IsEqualTo("T");
        await Assert.That(summary.Body!.SessionId).IsEqualTo("s1");
    }

    [Test]
    [Arguments(".")]
    [Arguments("..")]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("---")]
    public async Task An_id_that_would_escape_or_empty_the_route_is_refused_before_any_request(string id) {
        using var http = new HttpClient(new ThrowingHandler(new InvalidOperationException("a request was sent")));
        var client = new WorkContextClient(http, "http://localhost:1");

        var assignments = await client.GetSessionAssignmentsAsync(id, CancellationToken.None);
        var summary = await client.GetSessionSummaryAsync(id, CancellationToken.None);
        var topology = await client.GetTopologyAsync(id == "---" ? "." : id, CancellationToken.None);

        await Assert.That(assignments.StatusCode).IsEqualTo(0);
        await Assert.That(summary.StatusCode).IsEqualTo(0);
        await Assert.That(topology.StatusCode).IsEqualTo(0);
    }

    [Test]
    public async Task Ids_with_a_slash_or_percent_are_escaped_into_one_segment() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("[]").WithHeader("Content-Type", "application/json"));
        using var http = new HttpClient();
        var client = new WorkContextClient(http, server.Urls[0]);

        await client.GetSessionAssignmentsAsync("a/b%25c", CancellationToken.None);
        await client.GetTopologyAsync("x/y", CancellationToken.None);

        var urls = server.LogEntries.Select(e => e.RequestMessage.AbsoluteUrl).ToList();
        await Assert.That(urls[0]).EndsWith("/api/work-items/session/a%2Fb%2525c");
        await Assert.That(urls[1]).EndsWith("/api/work-items/x%2Fy/topology");
    }

    [Test]
    public async Task A_4xx_body_in_the_error_shape_becomes_the_outcome_error() {
        using var server = Serve($"/api/work-items/session/{Dashless}", 403, """{"error":"work_items_not_in_plan","message":"Upgrade."}""");
        using var http = new HttpClient();

        var outcome = await new WorkContextClient(http, server.Urls[0]).GetSessionAssignmentsAsync(Dashless, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(403);
        await Assert.That(outcome.Body).IsNull();
        await Assert.That(outcome.Error!.Error).IsEqualTo("work_items_not_in_plan");
    }

    [Test]
    public async Task An_unparseable_2xx_body_keeps_the_status_with_a_null_body() {
        using var server = Serve($"/api/work-items/session/{Dashless}", 200, "not json");
        using var http = new HttpClient();

        var outcome = await new WorkContextClient(http, server.Urls[0]).GetSessionAssignmentsAsync(Dashless, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(200);
        await Assert.That(outcome.Body).IsNull();
        await Assert.That(outcome.Succeeded).IsFalse();
    }

    [Test]
    public async Task A_transport_failure_is_status_zero() {
        using var http = new HttpClient(new ThrowingHandler(new HttpRequestException("refused")));

        var outcome = await new WorkContextClient(http, "http://localhost:1").GetSessionAssignmentsAsync(Dashless, CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(0);
    }

    [Test]
    public async Task The_callers_own_cancellation_propagates() {
        using var http = new HttpClient(new CancellingHandler());
        using var cts = new CancellationTokenSource();
        var pending = new WorkContextClient(http, "http://localhost:1").GetSessionAssignmentsAsync(Dashless, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextClientTests/*"`
Expected: `CS0246: The type or namespace name 'WorkContextClient' could not be found`.

- [ ] **Step 3: Create the seam**

Create `src/Capacitor.Cli.Core/WorkItems/IWorkContextChannel.cs`:

```csharp
namespace Capacitor.Cli.Core.WorkItems;

/// One call's result. <see cref="StatusCode"/> 0 is a transport failure or an id refused before any
/// request. <see cref="Body"/> is present on a 2xx whose body parsed; <see cref="Error"/> on a 4xx
/// whose body parsed as the shared error shape.
public sealed record WorkContextOutcome<T>(int StatusCode, T? Body, WorkItemErrorDto? Error) where T : class {
    public bool Succeeded => StatusCode is >= 200 and < 300 && Body is not null;
}

/// The three routes as a seam, so the reader is testable without a socket.
public interface IWorkContextChannel {
    Task<WorkContextOutcome<List<SessionWorkItemAssignmentDto>>> GetSessionAssignmentsAsync(string sessionId, CancellationToken ct);
    Task<WorkContextOutcome<WorkItemTopologyDto>>                 GetTopologyAsync(string workItemId, CancellationToken ct);
    Task<WorkContextOutcome<SessionSummaryDto>>                   GetSessionSummaryAsync(string sessionId, CancellationToken ct);
}
```

- [ ] **Step 4: Create the id containment**

Create `src/Capacitor.Cli.Core/WorkItems/WorkContextIds.cs`:

```csharp
namespace Capacitor.Cli.Core.WorkItems;

/// Ids are opaque values from another process. Canonicalize, then validate, then let the caller
/// escape: `.` is unreserved, so escaping leaves a dot segment intact and URI normalization would
/// walk it out of the route.
public static class WorkContextIds {
    /// Trimmed, dashes stripped — the key the server files a session under; null when nothing usable survives.
    public static string? CanonicalSessionId(string? raw) => Validate(raw?.Trim().Replace("-", ""));

    public static string? ValidWorkItemId(string? raw) => Validate(raw?.Trim());

    static string? Validate(string? id) => id is null || id.Length == 0 || id == "." || id == ".." ? null : id;
}
```

- [ ] **Step 5: Create the client**

Create `src/Capacitor.Cli.Core/WorkItems/WorkContextClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.Core.WorkItems;

/// The HTTP channel. <paramref name="http"/> must already carry the caller's bearer. Degrades rather
/// than throws, except for the caller's own cancellation, which propagates: turning a teardown into
/// a failed read would report an outage that never happened.
public sealed class WorkContextClient(HttpClient http, string serverUrl) : IWorkContextChannel {
    readonly string _base = serverUrl.TrimEnd('/');

    public Task<WorkContextOutcome<List<SessionWorkItemAssignmentDto>>> GetSessionAssignmentsAsync(string sessionId, CancellationToken ct) =>
        WorkContextIds.CanonicalSessionId(sessionId) is { } id
            ? GetAsync($"{_base}/api/work-items/session/{Uri.EscapeDataString(id)}", CapacitorJsonContext.Default.ListSessionWorkItemAssignmentDto, ct)
            : Task.FromResult(Refused<List<SessionWorkItemAssignmentDto>>());

    public Task<WorkContextOutcome<WorkItemTopologyDto>> GetTopologyAsync(string workItemId, CancellationToken ct) =>
        WorkContextIds.ValidWorkItemId(workItemId) is { } id
            ? GetAsync($"{_base}/api/work-items/{Uri.EscapeDataString(id)}/topology", CapacitorJsonContext.Default.WorkItemTopologyDto, ct)
            : Task.FromResult(Refused<WorkItemTopologyDto>());

    public Task<WorkContextOutcome<SessionSummaryDto>> GetSessionSummaryAsync(string sessionId, CancellationToken ct) =>
        WorkContextIds.CanonicalSessionId(sessionId) is { } id
            ? GetAsync($"{_base}/api/sessions/{Uri.EscapeDataString(id)}/summary", CapacitorJsonContext.Default.SessionSummaryDto, ct)
            : Task.FromResult(Refused<SessionSummaryDto>());

    static WorkContextOutcome<T> Refused<T>() where T : class => new(0, null, null);

    async Task<WorkContextOutcome<T>> GetAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class {
        try {
            using var req  = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;

            if (resp.IsSuccessStatusCode) return new(status, await ReadAsync(resp, typeInfo, ct), null);

            return new(status, null, await ReadAsync(resp, CapacitorJsonContext.Default.WorkItemErrorDto, ct));
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0, null, null);
        }
    }

    static async Task<T?> ReadAsync<T>(HttpResponseMessage resp, JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class {
        try {
            return await resp.Content.ReadFromJsonAsync(typeInfo, ct);
        } catch (Exception e) when (IsTransient(e, ct)) {
            return null;
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException;
}
```

- [ ] **Step 6: Run to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextClientTests/*"`
Expected: all pass (the parameterized refusal test runs five times).

- [ ] **Step 7: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.Cli.Core/WorkItems test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextClientTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Add the work-context HTTP channel with id containment" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 5: The reader and the label split

**Files:**
- Create: `src/Capacitor.Cli.Core/WorkItems/WorkContextReader.cs`
- Create: `src/Capacitor.Cli.Core/WorkItems/WorkContextLabel.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextReaderTests.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextLabelTests.cs`

**Interfaces:**
- Consumes: `IWorkContextChannel`, `WorkContextOutcome<T>` (Task 4), the DTOs (Task 3).
- Produces: `enum WorkContextReadKind { Ready, SessionUnknown, SignedOut, NotInPlan, Unreachable }`; `sealed record WorkContextRead(WorkContextReadKind Kind, IReadOnlyList<SessionWorkItemAssignmentDto> Assignments, SessionWorkItemAssignmentDto? Primary, WorkItemTopologyDto? Topology, SessionSummaryDto? Summary, bool TopologyFailed, bool SummaryFailed, string? Detail)` with `static WorkContextRead Of(WorkContextReadKind kind, string? detail = null)`; `WorkContextReader.PlanGateError` (`"work_items_not_in_plan"`), `WorkContextReader.ReadAsync(IWorkContextChannel, string sessionId, CancellationToken)`; `WorkContextLabel.Split(string label)` → `(string? Key, string Display)`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextLabelTests.cs`:

```csharp
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

public class WorkContextLabelTests {
    [Test]
    public async Task A_keyed_label_splits_on_the_em_dash_separator() {
        var (key, display) = WorkContextLabel.Split("AI-2198 — Desktop shell: work-context sidebar");
        await Assert.That(key).IsEqualTo("AI-2198");
        await Assert.That(display).IsEqualTo("Desktop shell: work-context sidebar");
    }

    [Test]
    public async Task A_label_without_the_separator_is_display_only() {
        var (key, display) = WorkContextLabel.Split("Daemon tests flake under the full suite");
        await Assert.That(key).IsNull();
        await Assert.That(display).IsEqualTo("Daemon tests flake under the full suite");
    }

    [Test]
    public async Task A_bare_key_is_display_only() {
        var (key, display) = WorkContextLabel.Split("#412");
        await Assert.That(key).IsNull();
        await Assert.That(display).IsEqualTo("#412");
    }

    [Test]
    public async Task A_separator_with_an_empty_half_does_not_split() {
        var (key, display) = WorkContextLabel.Split(" — only a title");
        await Assert.That(key).IsNull();
        await Assert.That(display).IsEqualTo("— only a title");
    }
}
```

Create `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextReaderTests.cs`:

```csharp
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Core.Tests.Unit.WorkItems;

/// The fetch composition, over a scripted channel: which call's status decides the read's kind, and
/// which failures only degrade a section.
public class WorkContextReaderTests {
    sealed class ScriptedChannel : IWorkContextChannel {
        public WorkContextOutcome<List<SessionWorkItemAssignmentDto>> Assignments = new(200, [], null);
        public WorkContextOutcome<WorkItemTopologyDto> Topology = new(200, new WorkItemTopologyDto(), null);
        public WorkContextOutcome<SessionSummaryDto> Summary = new(200, new SessionSummaryDto { SessionId = "s1" }, null);
        public readonly List<string> Calls = [];
        public TaskCompletionSource SummaryGate = new();
        public bool GateSummary;

        public Task<WorkContextOutcome<List<SessionWorkItemAssignmentDto>>> GetSessionAssignmentsAsync(string sessionId, CancellationToken ct) {
            Calls.Add($"assignments:{sessionId}");
            return Task.FromResult(Assignments);
        }

        public Task<WorkContextOutcome<WorkItemTopologyDto>> GetTopologyAsync(string workItemId, CancellationToken ct) {
            Calls.Add($"topology:{workItemId}");
            return Task.FromResult(Topology);
        }

        public async Task<WorkContextOutcome<SessionSummaryDto>> GetSessionSummaryAsync(string sessionId, CancellationToken ct) {
            Calls.Add($"summary:{sessionId}");
            if (GateSummary) await SummaryGate.Task;
            return Summary;
        }
    }

    static SessionWorkItemAssignmentDto Row(string id, bool primary = false, string label = "AI-1 — Title") =>
        new() { WorkItemId = id, Label = label, Source = "mcp", Confidence = 1, IsPrimary = primary };

    static WorkContextOutcome<T> PlanGate<T>() where T : class =>
        new(403, null, new WorkItemErrorDto { Error = WorkContextReader.PlanGateError, Message = "Upgrade." });

    static Task<WorkContextRead> Read(ScriptedChannel channel) =>
        WorkContextReader.ReadAsync(channel, "s1", CancellationToken.None);

    [Test]
    public async Task Ready_carries_the_primary_its_topology_and_the_summary() {
        var channel = new ScriptedChannel { Assignments = new(200, [Row("w2"), Row("w1", primary: true)], null) };

        var read = await Read(channel);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
        await Assert.That(read.Primary!.WorkItemId).IsEqualTo("w1");
        await Assert.That(read.Topology).IsNotNull();
        await Assert.That(read.Summary!.SessionId).IsEqualTo("s1");
        await Assert.That(read.TopologyFailed).IsFalse();
        await Assert.That(read.SummaryFailed).IsFalse();
        await Assert.That(channel.Calls).Contains("topology:w1");
    }

    [Test]
    public async Task Without_a_primary_flag_the_first_row_is_primary() {
        var channel = new ScriptedChannel { Assignments = new(200, [Row("w3"), Row("w4")], null) };
        var read = await Read(channel);
        await Assert.That(read.Primary!.WorkItemId).IsEqualTo("w3");
    }

    [Test]
    public async Task No_assignments_is_ready_with_a_null_primary_and_the_summary_still_carried() {
        var read = await Read(new ScriptedChannel());

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
        await Assert.That(read.Primary).IsNull();
        await Assert.That(read.Topology).IsNull();
        await Assert.That(read.Summary).IsNotNull();
    }

    [Test]
    public async Task A_2xx_assignments_response_with_no_body_is_unreachable() {
        var channel = new ScriptedChannel { Assignments = new(200, null, null) };
        var read = await Read(channel);
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await Assert.That(read.Detail).IsEqualTo("malformed response");
    }

    [Test]
    [Arguments("assignments")]
    [Arguments("topology")]
    [Arguments("summary")]
    public async Task A_final_401_on_any_call_signs_the_read_out(string call) {
        var channel = new ScriptedChannel { Assignments = new(200, [Row("w1", primary: true)], null) };
        switch (call) {
            case "assignments": channel.Assignments = new(401, null, null); break;
            case "topology":    channel.Topology = new(401, null, null); break;
            case "summary":     channel.Summary = new(401, null, null); break;
        }

        var read = await Read(channel);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
    }

    [Test]
    public async Task A_403_with_the_plan_code_on_assignments_is_not_in_plan() {
        var channel = new ScriptedChannel { Assignments = PlanGate<List<SessionWorkItemAssignmentDto>>() };
        var read = await Read(channel);
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.NotInPlan);
        await Assert.That(read.Detail).IsEqualTo("Upgrade.");
    }

    [Test]
    public async Task A_403_with_the_plan_code_on_topology_is_not_in_plan_too() {
        var channel = new ScriptedChannel { Assignments = new(200, [Row("w1", primary: true)], null), Topology = PlanGate<WorkItemTopologyDto>() };
        var read = await Read(channel);
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.NotInPlan);
    }

    [Test]
    public async Task A_403_with_another_code_or_no_body_is_unreachable() {
        var other = new ScriptedChannel { Assignments = new(403, null, new WorkItemErrorDto { Error = "forbidden" }) };
        var bodyless = new ScriptedChannel { Assignments = new(403, null, null) };

        await Assert.That((await Read(other)).Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await Assert.That((await Read(bodyless)).Kind).IsEqualTo(WorkContextReadKind.Unreachable);
    }

    [Test]
    public async Task A_404_is_session_unknown_and_status_zero_is_unreachable() {
        await Assert.That((await Read(new ScriptedChannel { Assignments = new(404, null, null) })).Kind).IsEqualTo(WorkContextReadKind.SessionUnknown);
        var zero = await Read(new ScriptedChannel { Assignments = new(0, null, null) });
        await Assert.That(zero.Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await Assert.That(zero.Detail).IsEqualTo("no response");
    }

    [Test]
    public async Task Topology_failures_degrade_the_section() {
        var non2xx = new ScriptedChannel { Assignments = new(200, [Row("w1", primary: true)], null), Topology = new(500, null, null) };
        var noBody = new ScriptedChannel { Assignments = new(200, [Row("w1", primary: true)], null), Topology = new(200, null, null) };

        foreach (var channel in new[] { non2xx, noBody }) {
            var read = await Read(channel);
            await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
            await Assert.That(read.Topology).IsNull();
            await Assert.That(read.TopologyFailed).IsTrue();
        }
    }

    [Test]
    public async Task Summary_failures_degrade_the_section() {
        var non2xx = new ScriptedChannel { Summary = new(404, null, null) };
        var noBody = new ScriptedChannel { Summary = new(200, null, null) };

        foreach (var channel in new[] { non2xx, noBody }) {
            var read = await Read(channel);
            await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Ready);
            await Assert.That(read.Summary).IsNull();
            await Assert.That(read.SummaryFailed).IsTrue();
        }
    }

    [Test]
    public async Task The_summary_task_is_awaited_even_when_assignments_end_the_read_early() {
        var channel = new ScriptedChannel { Assignments = new(404, null, null), GateSummary = true };
        var pending = Read(channel);

        await Task.Delay(50);
        await Assert.That(pending.IsCompleted).IsFalse();
        channel.SummaryGate.SetResult();

        await Assert.That((await pending).Kind).IsEqualTo(WorkContextReadKind.SessionUnknown);
    }
}
```

- [ ] **Step 2: Run to verify they fail to compile**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContext*Tests/*"`
Expected: `CS0103: The name 'WorkContextReader' does not exist`.

- [ ] **Step 3: Create the label split**

Create `src/Capacitor.Cli.Core/WorkItems/WorkContextLabel.cs`:

```csharp
namespace Capacitor.Cli.Core.WorkItems;

/// The server composes a keyed item's label as "KEY — title". A label without that separator, or
/// with an empty half, is display text alone: never a guessed key.
public static class WorkContextLabel {
    const string Separator = " — ";

    public static (string? Key, string Display) Split(string label) {
        var at = label.IndexOf(Separator, StringComparison.Ordinal);
        if (at < 0) return (null, label.Trim());

        var key     = label[..at].Trim();
        var display = label[(at + Separator.Length)..].Trim();

        return key.Length == 0 || display.Length == 0 ? (null, label.Trim()) : (key, display);
    }
}
```

- [ ] **Step 4: Create the reader**

Create `src/Capacitor.Cli.Core/WorkItems/WorkContextReader.cs`:

```csharp
namespace Capacitor.Cli.Core.WorkItems;

public enum WorkContextReadKind { Ready, SessionUnknown, SignedOut, NotInPlan, Unreachable }

/// One read of a session's work context, totalized. <see cref="WorkContextReadKind.Ready"/> with a
/// null <see cref="Primary"/> is the no-work-item state; <see cref="TopologyFailed"/> and
/// <see cref="SummaryFailed"/> degrade a section without failing the read.
public sealed record WorkContextRead(
        WorkContextReadKind                         Kind,
        IReadOnlyList<SessionWorkItemAssignmentDto> Assignments,
        SessionWorkItemAssignmentDto?               Primary,
        WorkItemTopologyDto?                        Topology,
        SessionSummaryDto?                          Summary,
        bool                                        TopologyFailed,
        bool                                        SummaryFailed,
        string?                                     Detail) {
    public static WorkContextRead Of(WorkContextReadKind kind, string? detail = null) =>
        new(kind, [], null, null, null, false, false, detail);
}

public static class WorkContextReader {
    public const string PlanGateError = "work_items_not_in_plan";

    /// Assignments and summary run concurrently and are both awaited before anything is
    /// classified. A final 401 anywhere signs the read out: the retry handler has already spent
    /// the refresh, and only that outcome makes the source drop its client.
    public static async Task<WorkContextRead> ReadAsync(IWorkContextChannel channel, string sessionId, CancellationToken ct) {
        var assignmentsTask = channel.GetSessionAssignmentsAsync(sessionId, ct);
        var summaryTask     = channel.GetSessionSummaryAsync(sessionId, ct);
        await Task.WhenAll(assignmentsTask, summaryTask).ConfigureAwait(false);
        var assignments = assignmentsTask.Result;
        var summary     = summaryTask.Result;

        if (assignments.StatusCode == 401 || summary.StatusCode == 401) return WorkContextRead.Of(WorkContextReadKind.SignedOut);

        switch (assignments) {
            case { Succeeded: true }: break;
            case { StatusCode: >= 200 and < 300 }: return WorkContextRead.Of(WorkContextReadKind.Unreachable, "malformed response");
            case { StatusCode: 404 }: return WorkContextRead.Of(WorkContextReadKind.SessionUnknown);
            case { StatusCode: 403, Error: { Error: PlanGateError } gate }: return WorkContextRead.Of(WorkContextReadKind.NotInPlan, gate.Message);
            default: return WorkContextRead.Of(WorkContextReadKind.Unreachable, StatusDetail(assignments.StatusCode));
        }

        var rows    = assignments.Body!;
        var primary = rows.FirstOrDefault(r => r.IsPrimary) ?? rows.FirstOrDefault();

        WorkItemTopologyDto? topology = null;
        var topologyFailed = false;
        if (primary is not null) {
            var outcome = await channel.GetTopologyAsync(primary.WorkItemId, ct).ConfigureAwait(false);
            if (outcome.StatusCode == 401) return WorkContextRead.Of(WorkContextReadKind.SignedOut);
            if (outcome is { StatusCode: 403, Error: { Error: PlanGateError } gate }) return WorkContextRead.Of(WorkContextReadKind.NotInPlan, gate.Message);
            if (outcome.Succeeded) topology = outcome.Body;
            else topologyFailed = true;
        }

        return new WorkContextRead(
            WorkContextReadKind.Ready, rows, primary, topology,
            summary.Succeeded ? summary.Body : null,
            topologyFailed, !summary.Succeeded, null);
    }

    static string StatusDetail(int status) => status == 0 ? "no response" : $"status {status}";
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContext*Tests/*"`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.Cli.Core/WorkItems test/Capacitor.Cli.Core.Tests.Unit/WorkItems
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Compose the three work-context reads into one totalized result" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 6: The app's leased, disposable source

**Files:**
- Create: `src/Capacitor.App/Services/IWorkContextSource.cs`
- Create: `src/Capacitor.App/Services/ServerWorkContextSource.cs`
- Test: `test/Capacitor.App.Tests.Unit/ServerWorkContextSourceTests.cs`

**Interfaces:**
- Consumes: `WorkContextReader`, `WorkContextClient`, `WorkContextRead` (Tasks 4–5); `HttpClientExtensions.CreateClientWithAuthStatusAsync(ConfigRoot, ProfileContext, string, CancellationToken, bool allowAutoRedirect = true, string? rejectedAccessToken = null, bool autoRetryUnauthorized = false)` and `AuthStatus` (existing Core); `ProfileContext.Resolution.ServerUrl` (existing).
- Produces: `interface IWorkContextSource { Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct); }`; `ServerWorkContextSource(ConfigRoot config, ProfileContext? profiles, ServerWorkContextSource.ClientFactory? factory = null) : IWorkContextSource, IAsyncDisposable` with `delegate Task<(HttpClient Client, AuthStatus Status)> ClientFactory(ConfigRoot config, ProfileContext profiles, string serverUrl, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.App.Tests.Unit/ServerWorkContextSourceTests.cs`:

```csharp
using System.Net;
using System.Text;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Tests.Unit;

/// Client ownership under overlapping reads: a client is disposed exactly once, never under a
/// borrower, and disposal of the source drains its reads first. No network: the factory is
/// injected and the handler answers in-process.
public class ServerWorkContextSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session = "0123456789abcdef0123456789abcdef";

    /// Answers every route with the scripted status; a request may be parked on a gate first.
    sealed class ScriptedHandler : HttpMessageHandler {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public readonly Queue<TaskCompletionSource> Gates = new();
        public int Sent;
        public bool Disposed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Interlocked.Increment(ref Sent);
            TaskCompletionSource? gate;
            lock (Gates) gate = Gates.Count > 0 ? Gates.Dequeue() : null;
            if (gate is not null) await gate.Task.WaitAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("/summary", StringComparison.Ordinal) ? """{"session_id":"s","repositories":[],"pull_requests":[]}""" : "[]";
            return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        protected override void Dispose(bool disposing) {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    static ProfileContext Profiles(ConfigRoot config) => Resolutions.At("http://server.test", config);

    static (ServerWorkContextSource Source, List<ScriptedHandler> Handlers) Build(
            ConfigRoot config, ProfileContext? profiles, Func<AuthStatus>? status = null) {
        var handlers = new List<ScriptedHandler>();
        var source = new ServerWorkContextSource(config, profiles, (_, _, _, _) => {
            var handler = new ScriptedHandler();
            handlers.Add(handler);
            return Task.FromResult((new HttpClient(handler), status?.Invoke() ?? AuthStatus.Ok));
        });
        return (source, handlers);
    }

    [Test]
    public async Task A_null_profile_reads_signed_out_without_building_a_client() {
        var (source, handlers) = Build(Config.Root, profiles: null);

        var read = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handlers).IsEmpty();
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_rejected_auth_status_disposes_the_client_it_was_handed() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root), () => AuthStatus.Expired);

        var read = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handlers.Single().Disposed).IsTrue();
        await Assert.That(handlers.Single().Sent).IsEqualTo(0);
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_signed_out_read_retires_the_client_and_the_next_read_builds_a_new_one() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root));
        var first = await source.ReadAsync(Session, CancellationToken.None);
        await Assert.That(first.Kind).IsEqualTo(WorkContextReadKind.Ready);
        handlers[0].Status = HttpStatusCode.Unauthorized;

        var signedOut = await source.ReadAsync(Session, CancellationToken.None);
        var next = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(signedOut.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handlers[0].Disposed).IsTrue();
        await Assert.That(handlers.Count).IsEqualTo(2);
        await Assert.That(next.Kind).IsEqualTo(WorkContextReadKind.Ready);
        await source.DisposeAsync();
    }

    [Test]
    public async Task A_signed_out_read_does_not_dispose_a_client_another_read_still_borrows() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root));
        await source.ReadAsync(Session, CancellationToken.None);
        var handler = handlers.Single();
        var gateB1 = new TaskCompletionSource();
        var gateB2 = new TaskCompletionSource();
        handler.Gates.Enqueue(gateB1);
        handler.Gates.Enqueue(gateB2);
        var b = source.ReadAsync(Session, CancellationToken.None);
        await WorkspaceFixtures.WaitUntilAsync(() => handler.Sent >= 3, what: "B's two requests in flight");

        handler.Status = HttpStatusCode.Unauthorized;
        var a = await source.ReadAsync(Session, CancellationToken.None);

        await Assert.That(a.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handler.Disposed).IsFalse();
        gateB1.SetResult();
        gateB2.SetResult();
        var bRead = await b;
        await Assert.That(bRead.Kind).IsEqualTo(WorkContextReadKind.SignedOut);
        await Assert.That(handler.Disposed).IsTrue();
        await source.DisposeAsync();
    }

    [Test]
    public async Task Disposing_during_an_active_read_cancels_it_awaits_it_and_disposes_the_client_once() {
        var (source, handlers) = Build(Config.Root, Profiles(Config.Root));
        await source.ReadAsync(Session, CancellationToken.None);
        var handler = handlers.Single();
        var gate = new TaskCompletionSource();
        handler.Gates.Enqueue(gate);
        var pending = source.ReadAsync(Session, CancellationToken.None);
        await WorkspaceFixtures.WaitUntilAsync(() => handler.Sent >= 4, what: "the read parked on its gate");

        await source.DisposeAsync();

        var read = await pending;
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await Assert.That(handler.Disposed).IsTrue();
        var after = await source.ReadAsync(Session, CancellationToken.None);
        await Assert.That(after.Kind).IsEqualTo(WorkContextReadKind.Unreachable);
        await source.DisposeAsync(); // idempotent
    }
}
```

`Resolutions.At(serverUrl, config)` is Helpers' existing "a server URL and nothing else" builder (`test/Capacitor.Tests.Helpers/Resolutions.cs`); `TempConfigRoot` (exposing `.Root`) and `WorkspaceFixtures.WaitUntilAsync` already exist.

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerWorkContextSourceTests/*"`
Expected: `CS0246: The type or namespace name 'ServerWorkContextSource' could not be found`.

- [ ] **Step 3: Create the seam and the source**

Create `src/Capacitor.App/Services/IWorkContextSource.cs`:

```csharp
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Services;

/// One read of a session's work context, however the app reaches the server.
public interface IWorkContextSource {
    Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct);
}
```

Create `src/Capacitor.App/Services/ServerWorkContextSource.cs`:

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Services;

/// Reads through an authenticated client built from the profile's server URL and token store, the
/// pair the launch client uses. Reads can overlap, so the client is held by lease: a signed-out
/// result retires it for future borrows and the last borrower disposes it; disposal of the source
/// stops new borrows, cancels active reads, awaits them, then disposes the live client.
public sealed class ServerWorkContextSource : IWorkContextSource, IAsyncDisposable {
    public delegate Task<(HttpClient Client, AuthStatus Status)> ClientFactory(
        ConfigRoot config, ProfileContext profiles, string serverUrl, CancellationToken ct);

    sealed class ClientLease(HttpClient client) {
        public HttpClient Client { get; } = client;
        public int  Borrowers;
        public bool Retired;
    }

    readonly ConfigRoot _config;
    readonly ProfileContext? _profiles;
    readonly ClientFactory _factory;
    readonly SemaphoreSlim _buildGate = new(1, 1);
    readonly object _lock = new();
    readonly CancellationTokenSource _disposeCts = new();
    readonly List<Task> _active = [];
    ClientLease? _lease;
    bool _disposed;

    public ServerWorkContextSource(ConfigRoot config, ProfileContext? profiles, ClientFactory? factory = null) {
        _config   = config;
        _profiles = profiles;
        _factory  = factory ?? ((c, p, url, ct) => HttpClientExtensions.CreateClientWithAuthStatusAsync(c, p, url, ct, autoRetryUnauthorized: true));
    }

    /// Registered under the lock before it can complete, so a concurrent DisposeAsync either
    /// sees it in the drain or refuses it; Monitor is re-entrant, so the synchronous prefix of
    /// ReadCoreAsync taking the same lock is fine.
    public Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct) {
        Task<WorkContextRead> task;
        lock (_lock) {
            if (_disposed) return Task.FromResult(WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed"));
            task = ReadCoreAsync(sessionId, ct);
            _active.Add(task);
        }
        task.ContinueWith(t => { lock (_lock) _active.Remove(t); }, TaskScheduler.Default);
        return task;
    }

    async Task<WorkContextRead> ReadCoreAsync(string sessionId, CancellationToken ct) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var serverUrl = _profiles?.Resolution.ServerUrl;
        if (_profiles is null || string.IsNullOrEmpty(serverUrl)) return WorkContextRead.Of(WorkContextReadKind.SignedOut);

        ClientLease? lease;
        try {
            lease = await BorrowAsync(serverUrl, linked.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            return WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed");
        }
        if (lease is null) return WorkContextRead.Of(WorkContextReadKind.SignedOut);

        try {
            var read = await WorkContextReader.ReadAsync(new WorkContextClient(lease.Client, serverUrl), sessionId, linked.Token).ConfigureAwait(false);
            if (read.Kind == WorkContextReadKind.SignedOut) Retire(lease);
            return read;
        } catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested && !ct.IsCancellationRequested) {
            return WorkContextRead.Of(WorkContextReadKind.Unreachable, "disposed");
        } finally {
            Release(lease);
        }
    }

    async Task<ClientLease?> BorrowAsync(string serverUrl, CancellationToken ct) {
        await _buildGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            lock (_lock) {
                if (_lease is { Retired: false } live) {
                    live.Borrowers++;
                    return live;
                }
            }

            var (client, status) = await _factory(_config, _profiles!, serverUrl, ct).ConfigureAwait(false);
            if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) {
                client.Dispose();
                return null;
            }

            var lease = new ClientLease(client) { Borrowers = 1 };
            lock (_lock) {
                if (_disposed) {
                    client.Dispose();
                    return null;
                }
                _lease = lease;
            }
            return lease;
        } finally {
            _buildGate.Release();
        }
    }

    void Retire(ClientLease lease) {
        lock (_lock) {
            lease.Retired = true;
            if (ReferenceEquals(_lease, lease)) _lease = null;
        }
    }

    void Release(ClientLease lease) {
        bool dispose;
        lock (_lock) {
            lease.Borrowers--;
            dispose = lease.Retired && lease.Borrowers == 0;
        }
        if (dispose) lease.Client.Dispose();
    }

    public async ValueTask DisposeAsync() {
        Task[] active;
        ClientLease? lease;
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            active = [.. _active];
            lease  = _lease;
            _lease = null;
        }

        _disposeCts.Cancel();
        try { await Task.WhenAll(active).ConfigureAwait(false); }
        catch (Exception) { /* each read reported its own outcome; only the drain matters here */ }

        if (lease is not null) {
            bool dispose;
            lock (_lock) {
                lease.Retired = true;
                dispose = lease.Borrowers == 0;
            }
            if (dispose) lease.Client.Dispose();
        }
        _disposeCts.Dispose();
        _buildGate.Dispose();
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerWorkContextSourceTests/*"`
Expected: 5 passed. If `A_signed_out_read_does_not_dispose_a_client_another_read_still_borrows` is flaky on the `Sent >= 3` wait, raise the wait's timeout; the reader issues B's two requests concurrently before either gate releases, so the count is deterministic.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.App/Services/IWorkContextSource.cs src/Capacitor.App/Services/ServerWorkContextSource.cs test/Capacitor.App.Tests.Unit/ServerWorkContextSourceTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Add the app's leased work-context source over the profile's server" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 7: `WorkContextViewModel` — facts, phases, leases, poll, teardown

**Files:**
- Create: `src/Capacitor.App/ViewModels/WorkContextViewModel.cs`
- Create: `test/Capacitor.App.Tests.Unit/FakeWorkContextSource.cs`
- Test: `test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs`

**Interfaces:**
- Consumes: `IWorkContextSource` (Task 6), `WorkContextRead`/`WorkContextReadKind` (Task 5), `AgentStatusDto.SessionId`/`Branch` (Task 1), existing `RepoLabel.Leaf`, `CheckoutLabel.CheckoutPathFor/Format`, `HostedHarnessCatalog.Build/LabelFor/ModelLabelFor/EffectiveFamily`, `WorkLocationText.Borrowed`, `RxSchedulers.MainThreadScheduler`, `IUrlOpener`.
- Produces: `enum WorkContextPhase { WaitingForSession, Loading, Ready, NoWorkItem, SignedOut, NotInPlan, Unreachable, SessionUnknown }`; `WorkContextViewModel(IObservable<AgentStatusDto?> presence, IWorkContextSource source, TimeProvider time, IUrlOpener opener, Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null)` with `PollInterval`, the copy constants, facts (`Repository`, `RepositoryPath`, `Worktree`, `WorktreePath`, `Branch`, `Harness`, `Transport`, `SessionIdText`, `SessionSummaryLine`), `Phase`, `PhaseNote`, `IsReady`, `ShowsSignIn`, `ShowsRetry`, `IsStale`, `IsReading`, `HasSession`, `RefreshCommand`, `SignInCommand`, `TeardownAsync()`, `internal Task? PendingReadForTesting`, and the protected extension points Task 8 fills: `Apply(WorkContextRead)`, `ClearServerProjections()`, `UpdateFacts(AgentStatusDto)`.
- Test fake: `FakeWorkContextSource : IWorkContextSource` with `Requested` (list of ids), `Enqueue(params WorkContextRead[])`, `Default`, `Gate()` returning a `TaskCompletionSource<WorkContextRead>` the next read awaits, `InFlight`.

- [ ] **Step 1: Create the fake source**

Create `test/Capacitor.App.Tests.Unit/FakeWorkContextSource.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Tests.Unit;

/// Scripted IWorkContextSource: reads answer from a queue, or park on a gate so a test can settle
/// them in a chosen order. Every read records the id it was asked for.
sealed class FakeWorkContextSource : IWorkContextSource {
    readonly Queue<WorkContextRead> _scripted = new();
    readonly Queue<TaskCompletionSource<WorkContextRead>> _gates = new();

    public readonly List<string> Requested = [];
    public WorkContextRead Default = WorkContextRead.Of(WorkContextReadKind.SessionUnknown);
    public int InFlight;

    public void Enqueue(params WorkContextRead[] reads) {
        foreach (var read in reads) _scripted.Enqueue(read);
    }

    /// The next read awaits the returned source instead of answering from the queue.
    public TaskCompletionSource<WorkContextRead> Gate() {
        var gate = new TaskCompletionSource<WorkContextRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates.Enqueue(gate);
        return gate;
    }

    public async Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct) {
        Requested.Add(sessionId);
        Interlocked.Increment(ref InFlight);
        try {
            if (_gates.Count > 0) return await _gates.Dequeue().Task.WaitAsync(ct);
            await Task.Yield();
            return _scripted.Count > 0 ? _scripted.Dequeue() : Default;
        } finally {
            Interlocked.Decrement(ref InFlight);
        }
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs`:

```csharp
using System.Reactive;
using System.Reactive.Subjects;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The sidebar VM: facts from the dto, the session-id lease that owns each read, the poll, and
/// teardown. Every read settles through Dispatcher.UIThread, so every test runs under RunOnUiAsync
/// and carries [NotInParallel("AvaloniaSession")].
public class WorkContextViewModelTests {
    const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    sealed class Harness {
        public BehaviorSubject<AgentStatusDto?> Presence { get; } = new(null);
        public FakeWorkContextSource Source { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public Subject<Unit> SignIn { get; } = new();
        public int SignInRequests;
        public WorkContextViewModel Vm { get; }

        public Harness() =>
            Vm = new WorkContextViewModel(Presence, Source, Time, Opener, () => SignInRequests++, SignIn);

        /// For a read that will answer from the queue: pushes and awaits the read it starts.
        public async Task PushAsync(AgentStatusDto dto) {
            Presence.OnNext(dto);
            await (Vm.PendingReadForTesting ?? Task.CompletedTask);
        }

        /// For a read that will park on a gate: pushes and returns, since the read cannot settle
        /// until the test releases the gate.
        public void Push(AgentStatusDto dto) => Presence.OnNext(dto);

        public async Task TickAsync() {
            Time.Advance(WorkContextViewModel.PollInterval);
            await (Vm.PendingReadForTesting ?? Task.CompletedTask);
        }
    }

    static AgentStatusDto Dto(string? sessionId = SessionA, string? repoPath = "/repo/myproj", string? branch = "feature/x") =>
        Agent("a1", "claude", hasTerminal: true, repoPath: repoPath, model: "claude-opus-5",
            worktreePath: "/repo/myproj/.capacitor/worktrees/agent-1", workLocation: "owned",
            sessionId: sessionId, branch: branch);

    static WorkContextRead Ready() => WorkContextRead.Of(WorkContextReadKind.Ready) with {
        Summary = new SessionSummaryDto { SessionId = SessionA },
    };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Facts_derive_from_the_dto_and_the_id_reads_resolving_until_reported() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.WaitingForSession);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.WaitingNote);
            await Assert.That(h.Vm.SessionIdText).IsEqualTo("resolving…");

            await h.PushAsync(Dto(sessionId: null));

            await Assert.That(h.Vm.Repository).IsEqualTo("myproj");
            await Assert.That(h.Vm.RepositoryPath).IsEqualTo("/repo/myproj");
            await Assert.That(h.Vm.Worktree).IsEqualTo("agent-1");
            await Assert.That(h.Vm.WorktreePath).IsEqualTo("/repo/myproj/.capacitor/worktrees/agent-1");
            await Assert.That(h.Vm.Branch).IsEqualTo("feature/x");
            await Assert.That(h.Vm.Harness).IsEqualTo("Claude Code · Claude Opus 5");
            await Assert.That(h.Vm.Transport).IsEqualTo("PTY");
            await Assert.That(h.Vm.SessionSummaryLine).IsEqualTo("Claude Code · Claude Opus 5 · PTY");
            await Assert.That(h.Vm.SessionIdText).IsEqualTo("resolving…");
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.WaitingForSession);
            await Assert.That(h.Source.Requested).IsEmpty();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_borrowed_launch_without_a_branch_shows_a_dash_and_the_borrowed_marker() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var dto = Agent("r1", "codex", hasTerminal: true, repoPath: "/repo/myproj", kind: "review",
                worktreePath: "/repo/myproj", workLocation: "borrowed", borrowedFrom: "/repo/myproj", branch: null, sessionId: null);

            await h.PushAsync(dto);

            await Assert.That(h.Vm.Branch).IsEqualTo("—");
            await Assert.That(h.Vm.Worktree).IsEqualTo("main checkout · borrowed");
            await Assert.That(h.Vm.Transport).IsEqualTo("PTY");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Transport_follows_the_effective_family() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.PushAsync(Agent("c1", "cursor", hasTerminal: false, repoPath: "/repo/x", sessionId: null));
            await Assert.That(h.Vm.Transport).IsEqualTo("ACP");
            await h.PushAsync(Agent("c1", "claude", hasTerminal: false, repoPath: "/repo/x", sessionId: null));
            await Assert.That(h.Vm.Transport).IsEqualTo("chat");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_first_session_id_reads_at_once_with_the_id_as_reported() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready());

            await h.PushAsync(Dto());

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA });
            await Assert.That(h.Vm.HasSession).IsTrue();
            await Assert.That(h.Vm.SessionIdText).IsEqualTo(SessionA);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Each_read_kind_maps_to_its_phase() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gate = h.Source.Gate();
            h.Push(Dto());
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.LoadingNote);
            gate.SetResult(WorkContextRead.Of(WorkContextReadKind.SessionUnknown));
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.SessionUnknown);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.WaitingNote);

            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.SignedOut);
            await Assert.That(h.Vm.ShowsSignIn).IsTrue();

            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.NotInPlan, "Upgrade."));
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NotInPlan);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.NotInPlanNote);

            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.Unreachable, "no response"));
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Unreachable);
            await Assert.That(h.Vm.ShowsRetry).IsTrue();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task No_work_item_on_a_repo_less_session_shows_the_no_repository_copy() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready());
            await h.PushAsync(Dto(repoPath: null));
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.NoRepositoryNote);

            h.Source.Enqueue(Ready());
            await h.PushAsync(Dto());
            await h.TickAsync();
            await Assert.That(h.Vm.PhaseNote).IsEqualTo(WorkContextViewModel.NoWorkItemNote);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_timer_re_reads_and_skips_a_tick_or_a_refresh_while_a_read_is_in_flight() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready());
            await h.PushAsync(Dto());
            var gate = h.Source.Gate();
            h.Time.Advance(WorkContextViewModel.PollInterval); // the read parks on the gate, so TickAsync's await would never return
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);
            await Assert.That(h.Vm.IsReading).IsTrue();
            await Assert.That(await h.Vm.RefreshCommand.CanExecute.FirstAsync()).IsFalse();

            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);

            gate.SetResult(Ready());
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.IsReading).IsFalse();
            await Assert.That(await h.Vm.RefreshCommand.CanExecute.FirstAsync()).IsTrue();
            h.Source.Enqueue(Ready());
            await h.Vm.RefreshCommand.Execute();
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Source.Requested.Count).IsEqualTo(3);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unreachable_refresh_after_ready_keeps_the_phase_and_marks_it_stale() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready(), WorkContextRead.Of(WorkContextReadKind.Unreachable), Ready());
            await h.PushAsync(Dto());
            await h.TickAsync();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await Assert.That(h.Vm.IsStale).IsTrue();
            await h.TickAsync();
            await Assert.That(h.Vm.IsStale).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_session_id_switch_drops_the_old_read_and_reads_the_new_id_at_once() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gateA = h.Source.Gate();
            h.Push(Dto(sessionId: SessionA));
            var gateB = h.Source.Gate();
            h.Push(Dto(sessionId: SessionB));

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA, SessionB });
            await Assert.That(h.Vm.SessionIdText).IsEqualTo(SessionB);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
            await Assert.That(h.Vm.IsReading).IsTrue();

            gateA.SetResult(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await Task.Yield();
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
            await Assert.That(h.Vm.IsReading).IsTrue();
            await Assert.That(await h.Vm.RefreshCommand.CanExecute.FirstAsync()).IsFalse();
            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);

            gateB.SetResult(Ready());
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_rapid_switch_applies_only_the_last_id() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gateA = h.Source.Gate();
            var gateB = h.Source.Gate();
            h.Push(Dto(sessionId: SessionA));
            h.Push(Dto(sessionId: SessionB));
            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.NotInPlan));
            await h.PushAsync(Dto(sessionId: "cccccccccccccccccccccccccccccccc"));
            gateA.SetResult(Ready());
            gateB.SetResult(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.Vm.TeardownAsync();

            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NotInPlan);
            await Assert.That(h.Vm.SessionIdText).IsEqualTo("cccccccccccccccccccccccccccccccc");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_id_going_back_to_null_changes_nothing() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(Ready());
            await h.PushAsync(Dto());
            await h.PushAsync(Dto(sessionId: null));

            await Assert.That(h.Vm.SessionIdText).IsEqualTo(SessionA);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sign_in_reads_at_once_when_idle_and_is_coalesced_into_the_next_read_otherwise() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.PushAsync(Dto());
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.SignedOut);
            await h.Vm.SignInCommand.Execute();
            await Assert.That(h.SignInRequests).IsEqualTo(1);

            h.Source.Enqueue(Ready());
            h.SignIn.OnNext(Unit.Default);
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);

            var gate = h.Source.Gate();
            h.Time.Advance(WorkContextViewModel.PollInterval); // parked on the gate; do not await it
            h.SignIn.OnNext(Unit.Default);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(3);
            h.Source.Enqueue(Ready());
            gate.SetResult(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await h.Vm.PendingReadForTesting!;
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Source.Requested.Count).IsEqualTo(4);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_sign_in_refresh_is_discarded_when_its_lease_was_superseded() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gateA = h.Source.Gate();
            h.Push(Dto(sessionId: SessionA));
            h.SignIn.OnNext(Unit.Default);
            var gateB = h.Source.Gate();
            h.Push(Dto(sessionId: SessionB));
            gateA.SetResult(WorkContextRead.Of(WorkContextReadKind.SignedOut));
            await Task.Yield();

            await Assert.That(h.Source.Requested).IsEquivalentTo(new[] { SessionA, SessionB });
            gateB.SetResult(Ready());
            await h.Vm.PendingReadForTesting!;
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Teardown_cancels_and_awaits_every_outstanding_read_and_ignores_later_signals() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var gateA = h.Source.Gate();
            h.Push(Dto(sessionId: SessionA));
            var gateB = h.Source.Gate();
            h.Push(Dto(sessionId: SessionB));
            await Assert.That(h.Source.InFlight).IsEqualTo(1); // the switch already cancelled A; only B is parked

            var teardown = h.Vm.TeardownAsync();
            await teardown;

            await Assert.That(h.Source.InFlight).IsEqualTo(0);
            gateA.TrySetResult(Ready());
            gateB.TrySetResult(Ready());
            h.SignIn.OnNext(Unit.Default);
            h.Time.Advance(WorkContextViewModel.PollInterval);
            await Assert.That(h.Source.Requested.Count).IsEqualTo(2);
            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Loading);
        });
    }
}
```

`FirstAsync()` on `CanExecute` needs `using System.Reactive.Linq;` — add it to the usings.

- [ ] **Step 3: Run to verify they fail to compile**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextViewModelTests/*"`
Expected: `CS0246: The type or namespace name 'WorkContextViewModel' could not be found`.

- [ ] **Step 4: Create the view model**

Create `src/Capacitor.App/ViewModels/WorkContextViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public enum WorkContextPhase { WaitingForSession, Loading, Ready, NoWorkItem, SignedOut, NotInPlan, Unreachable, SessionUnknown }

/// The work-context sidebar for one session: facts from the daemon's dto, the work item from the
/// server. Ctor-scoped; TeardownAsync is the one exit.
///
/// The session id is the read's identity. A lease owns one id, its cancellation and its pending
/// read; a result applies only for the current lease, every lease is kept until its read settles
/// so teardown can await them all, and every lease transition happens on the UI thread.
public sealed partial class WorkContextViewModel : ReactiveObject {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    internal const string WaitingNote      = "Waiting for the session to register…";
    internal const string LoadingNote      = "Loading work context…";
    internal const string NoWorkItemNote   = "No work item attached yet. The agent's declare tool attaches one.";
    internal const string NoRepositoryNote = "This session has no repository. A work item cannot attach until the work lands in one — breakdown and blockers come with it.";
    internal const string SignedOutNote    = "Sign in to see the work context.";
    internal const string NotInPlanNote    = "Work items are not in this workspace's plan.";
    internal const string UnreachableNote  = "Couldn't reach the server.";

    sealed class ReadLease(string sessionId) {
        public string SessionId { get; } = sessionId;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Pending;
        public bool RefreshPending;
        public bool IsReading => Pending is { IsCompleted: false };
    }

    readonly IWorkContextSource _source;
    readonly IUrlOpener _opener;
    readonly CompositeDisposable _disposables = new();
    readonly List<ReadLease> _outstanding = [];
    ReadLease? _current;
    ITimer? _timer;
    bool _tornDown;
    AgentStatusDto? _dto;

    string _repository = "—";
    public string Repository { get => _repository; private set => this.RaiseAndSetIfChanged(ref _repository, value); }
    string? _repositoryPath;
    public string? RepositoryPath { get => _repositoryPath; private set => this.RaiseAndSetIfChanged(ref _repositoryPath, value); }
    string _worktree = "—";
    public string Worktree { get => _worktree; private set => this.RaiseAndSetIfChanged(ref _worktree, value); }
    string? _worktreePath;
    public string? WorktreePath { get => _worktreePath; private set => this.RaiseAndSetIfChanged(ref _worktreePath, value); }
    string _branch = "—";
    public string Branch { get => _branch; private set => this.RaiseAndSetIfChanged(ref _branch, value); }
    string _harness = "—";
    public string Harness { get => _harness; private set => this.RaiseAndSetIfChanged(ref _harness, value); }
    string _transport = "—";
    public string Transport { get => _transport; private set => this.RaiseAndSetIfChanged(ref _transport, value); }
    string _sessionIdText = "resolving…";
    public string SessionIdText { get => _sessionIdText; private set => this.RaiseAndSetIfChanged(ref _sessionIdText, value); }
    string _sessionSummaryLine = "—";
    public string SessionSummaryLine { get => _sessionSummaryLine; private set => this.RaiseAndSetIfChanged(ref _sessionSummaryLine, value); }

    WorkContextPhase _phase = WorkContextPhase.WaitingForSession;
    public WorkContextPhase Phase {
        get => _phase;
        private set {
            if (_phase == value) return;
            this.RaiseAndSetIfChanged(ref _phase, value);
            this.RaisePropertyChanged(nameof(PhaseNote));
            this.RaisePropertyChanged(nameof(IsReady));
            this.RaisePropertyChanged(nameof(ShowsSignIn));
            this.RaisePropertyChanged(nameof(ShowsRetry));
        }
    }

    public string PhaseNote => Phase switch {
        WorkContextPhase.WaitingForSession or WorkContextPhase.SessionUnknown => WaitingNote,
        WorkContextPhase.Loading     => LoadingNote,
        WorkContextPhase.NoWorkItem  => _dto?.RepoPath is null ? NoRepositoryNote : NoWorkItemNote,
        WorkContextPhase.SignedOut   => SignedOutNote,
        WorkContextPhase.NotInPlan   => NotInPlanNote,
        WorkContextPhase.Unreachable => UnreachableNote,
        _                            => "",
    };

    public bool IsReady     => Phase == WorkContextPhase.Ready;
    public bool ShowsSignIn => Phase == WorkContextPhase.SignedOut;
    public bool ShowsRetry  => Phase == WorkContextPhase.Unreachable;

    bool _isStale;
    public bool IsStale { get => _isStale; private set => this.RaiseAndSetIfChanged(ref _isStale, value); }
    bool _isReading;
    public bool IsReading { get => _isReading; private set => this.RaiseAndSetIfChanged(ref _isReading, value); }
    bool _hasSession;
    public bool HasSession { get => _hasSession; private set => this.RaiseAndSetIfChanged(ref _hasSession, value); }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<Unit, Unit> SignInCommand { get; }

    /// Test-only seam: the current lease's read, or the last one started.
    internal Task? PendingReadForTesting => _current?.Pending ?? _outstanding.LastOrDefault()?.Pending;

    public WorkContextViewModel(
            IObservable<AgentStatusDto?> presence, IWorkContextSource source, TimeProvider time, IUrlOpener opener,
            Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null) {
        _source = source;
        _opener = opener;
        InitializeProjections();

        RefreshCommand = ReactiveCommand.Create(
            () => { if (_current is { IsReading: false } lease) StartRead(lease); },
            this.WhenAnyValue(x => x.HasSession, x => x.IsReading, (has, reading) => has && !reading));
        _disposables.Add(RefreshCommand);
        SignInCommand = ReactiveCommand.Create(() => { requestSignIn?.Invoke(); });
        _disposables.Add(SignInCommand);

        presence
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnDto)
            .DisposeWith(_disposables);
        signInCompleted?
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => OnSignInCompleted())
            .DisposeWith(_disposables);
        _timer = time.CreateTimer(_ => RunOnUi(OnTick), null, PollInterval, PollInterval);
    }

    static void RunOnUi(Action action) {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    void OnDto(AgentStatusDto? dto) {
        if (_tornDown || dto is null) return;
        _dto = dto;
        UpdateFacts(dto);
        if (dto.SessionId is { Length: > 0 } id && (_current is null || !string.Equals(_current.SessionId, id, StringComparison.Ordinal)))
            SwitchSession(id);
        this.RaisePropertyChanged(nameof(PhaseNote));
    }

    internal static string TransportLabel(string family) => family switch {
        "pty" => "PTY",
        "acp" => "ACP",
        _     => "chat",
    };

    void UpdateFacts(AgentStatusDto dto) {
        Repository = RepoLabel.Leaf(dto.RepoPath);
        RepositoryPath = dto.RepoPath;
        var checkout = CheckoutLabel.CheckoutPathFor(dto);
        WorktreePath = checkout;
        Worktree = checkout is null
            ? "—"
            : CheckoutLabel.Format(checkout, dto.RepoPath ?? "") + (dto.WorkLocation == WorkLocationText.Borrowed ? " · borrowed" : "");
        Branch = string.IsNullOrWhiteSpace(dto.Branch) ? "—" : dto.Branch;
        var vendorLabel = HostedHarnessCatalog.LabelFor(HostedHarnessCatalog.Build(null), dto.Vendor);
        Harness = $"{vendorLabel} · {HostedHarnessCatalog.ModelLabelFor(dto.Vendor, dto.Model ?? "")}";
        Transport = TransportLabel(HostedHarnessCatalog.EffectiveFamily(dto.HasTerminal, dto.Vendor));
        SessionSummaryLine = $"{Harness} · {Transport}";
        if (_current is null) SessionIdText = dto.SessionId ?? "resolving…";
        UpdateRequester(dto, vendorLabel);
    }

    void SwitchSession(string id) {
        var old = _current;
        _current = new ReadLease(id);
        old?.Cts.Cancel();
        HasSession = true;
        SessionIdText = id;
        ClearServerProjections();
        IsStale = false;
        Phase = WorkContextPhase.Loading;
        StartRead(_current);
    }

    void StartRead(ReadLease lease) {
        lease.RefreshPending = false;
        lease.Pending = RunReadAsync(lease);
        _outstanding.Add(lease);
        if (ReferenceEquals(lease, _current)) IsReading = true;
    }

    async Task RunReadAsync(ReadLease lease) {
        WorkContextRead? read = null;
        try {
            read = await _source.ReadAsync(lease.SessionId, lease.Cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            read = WorkContextRead.Of(WorkContextReadKind.Unreachable, ex.Message);
        }
        await Dispatcher.UIThread.InvokeAsync(() => Settle(lease, read));
    }

    void Settle(ReadLease lease, WorkContextRead? read) {
        _outstanding.Remove(lease);
        var current = ReferenceEquals(lease, _current) && !_tornDown;
        if (!current) {
            lease.Cts.Dispose();
            return;
        }
        if (read is not null) Apply(read);
        IsReading = false;
        if (lease.RefreshPending) StartRead(lease);
    }

    void OnTick() {
        if (_tornDown || _current is not { IsReading: false } lease) return;
        StartRead(lease);
    }

    void OnSignInCompleted() {
        if (_tornDown || _current is not { } lease) return;
        if (lease.IsReading) lease.RefreshPending = true;
        else StartRead(lease);
    }

    /// Applies one read for the current lease. Section-level merging lives in the projections half.
    void Apply(WorkContextRead read) {
        switch (read.Kind) {
            case WorkContextReadKind.SignedOut:
                ClearServerProjections();
                Phase = WorkContextPhase.SignedOut;
                IsStale = false;
                return;
            case WorkContextReadKind.NotInPlan:
                ClearServerProjections();
                Phase = WorkContextPhase.NotInPlan;
                IsStale = false;
                return;
            case WorkContextReadKind.SessionUnknown:
                ClearServerProjections();
                Phase = WorkContextPhase.SessionUnknown;
                IsStale = false;
                return;
            case WorkContextReadKind.Unreachable:
                if (Phase is WorkContextPhase.Ready or WorkContextPhase.NoWorkItem) IsStale = true;
                else Phase = WorkContextPhase.Unreachable;
                return;
        }
        ApplyReady(read);
    }

    public async Task TeardownAsync() {
        if (_tornDown) return;
        _tornDown = true;
        _timer?.Dispose();
        _timer = null;
        _disposables.Dispose();
        var leases = _outstanding.ToArray();
        foreach (var lease in leases) lease.Cts.Cancel();
        _current = null;
        foreach (var lease in leases)
            if (lease.Pending is { } pending) await pending;
        foreach (var lease in leases) lease.Cts.Dispose();
    }
}
```

The class is `partial`: Task 8 adds `WorkContextViewModel.Projections.cs` with `InitializeProjections`, `UpdateRequester`, `ClearServerProjections` and `ApplyReady`. For this task's tests to compile, create that file now with the minimal bodies Task 8 replaces:

```csharp
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.ViewModels;

public sealed partial class WorkContextViewModel {
    void InitializeProjections() { }

    void UpdateRequester(AgentStatusDto dto, string vendorLabel) { }

    void ClearServerProjections() { }

    void ApplyReady(WorkContextRead read) {
        Phase = read.Primary is null ? WorkContextPhase.NoWorkItem : WorkContextPhase.Ready;
        IsStale = read.TopologyFailed || read.SummaryFailed;
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextViewModelTests/*"`
Expected: 14 passed. If `The_timer_re_reads_and_skips_a_tick…` sees three requests after the second `Advance`, the tick reached `StartRead` while `IsReading` was true — check that `OnTick` reads `lease.IsReading` off the lease's own `Pending`, not a VM-wide flag.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.App/ViewModels/WorkContextViewModel.cs src/Capacitor.App/ViewModels/WorkContextViewModel.Projections.cs test/Capacitor.App.Tests.Unit/FakeWorkContextSource.cs test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Add the work-context view model's session lease and poll" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 8: `WorkContextViewModel` — projections, merge policy, links, requester, toggles

**Files:**
- Create: `src/Capacitor.App/ViewModels/WorkContextItems.cs`
- Modify: `src/Capacitor.App/ViewModels/WorkContextViewModel.Projections.cs` (replace the whole file from Task 7)
- Modify: `test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs` (append)

**Interfaces:**
- Consumes: Task 7's VM, `LinkPolicy.IsOpenable` (existing), `WorkContextLabel.Split` (Task 5), `Avalonia.Collections.AvaloniaList<T>`.
- Produces: `enum WorkContextPartMark { ThisSession, Unknown }`; `WorkContextPartViewModel(string Title, WorkContextPartMark Mark)` with `IsThisSession`; `WorkContextLinkViewModel` with `Eyebrow`, `Key`, `Title`, `Url`, `CanOpen`, `OpenCommand`; on the VM: `Key`, `Title`, `PartOfTitle`, `Parts` (`IAvaloniaReadOnlyList<WorkContextPartViewModel>`), `PartsHeader`, `HasParts`, `BlockedBy` (`IAvaloniaReadOnlyList<string>`), `HasBlockers`, `CycleNote`, `Links` (`IAvaloniaReadOnlyList<WorkContextLinkViewModel>`), `Requester`, `RequesterRole`, `RequesterInitial`, `PartsExpanded`, `PeopleExpanded`, `SessionExpanded`, `TogglePartsCommand`, `TogglePeopleCommand`, `ToggleSessionCommand`; `internal static bool SamePullRequest(SessionPullRequestDto, SessionSummaryDto, int)`.

- [ ] **Step 1: Write the failing tests**

Append inside `WorkContextViewModelTests` (before the closing brace), and add `using Avalonia.Collections;` is not needed — the assertions read the lists through `Count`/indexers:

```csharp
    static SessionWorkItemAssignmentDto Row(string id, string label, bool primary = true) =>
        new() { WorkItemId = id, Label = label, Source = "mcp", Confidence = 1, IsPrimary = primary };

    static WorkItemTopologyPartDto Part(string id, string title, int ordinal) =>
        new() { WorkItemId = id, Title = title, Ordinal = ordinal };

    static SessionPullRequestDto Pr(string owner, string repo, int number, string? url = null, string? title = null) =>
        new() { RepoHash = "h", Owner = owner, RepoName = repo, Number = number, Url = url, Title = title };

    static WorkContextRead ReadyWith(
            SessionWorkItemAssignmentDto? primary, WorkItemTopologyDto? topology = null, SessionSummaryDto? summary = null,
            bool topologyFailed = false, bool summaryFailed = false, IReadOnlyList<SessionWorkItemAssignmentDto>? assignments = null) =>
        new(WorkContextReadKind.Ready, assignments ?? (primary is null ? [] : [primary]), primary, topology,
            summary ?? new SessionSummaryDto { SessionId = SessionA }, topologyFailed, summaryFailed, null);

    static WorkItemTopologyDto Topology(params WorkItemTopologyPartDto[] parts) => new() {
        Parts = [.. parts],
        Item = new WorkItemRefDto { WorkItemId = "w1", Title = "Desktop shell: work-context sidebar" },
    };

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_card_shows_key_title_parts_marks_part_of_blockers_and_the_cycle_note() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var topology = Topology(Part("p2", "Second", 1), Part("p1", "First", 0)) with {
                PartOf = new WorkItemRefDto { WorkItemId = "w0", Title = "Parent epic" },
                BlockedBy = [new WorkItemRefDto { WorkItemId = "b1", Title = "Pin the helper" }],
                Cycle = "indeterminate",
            };
            h.Source.Enqueue(ReadyWith(Row("w1", "AI-2198 — old label"), topology,
                assignments: [Row("w1", "AI-2198 — old label"), Row("p1", "part", primary: false)]));

            await h.PushAsync(Dto());

            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.Ready);
            await Assert.That(h.Vm.Key).IsEqualTo("AI-2198");
            await Assert.That(h.Vm.Title).IsEqualTo("Desktop shell: work-context sidebar");
            await Assert.That(h.Vm.PartOfTitle).IsEqualTo("Parent epic");
            await Assert.That(h.Vm.Parts.Select(p => p.Title)).IsEquivalentTo(new[] { "First", "Second" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(h.Vm.Parts[0].Mark).IsEqualTo(WorkContextPartMark.ThisSession);
            await Assert.That(h.Vm.Parts[1].Mark).IsEqualTo(WorkContextPartMark.Unknown);
            await Assert.That(h.Vm.PartsHeader).IsEqualTo("2 parts");
            await Assert.That(h.Vm.BlockedBy[0]).IsEqualTo("Pin the helper");
            await Assert.That(h.Vm.HasBlockers).IsTrue();
            await Assert.That(h.Vm.CycleNote).IsEqualTo("Dependencies could not be fully resolved");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_label_display_is_the_title_when_the_topology_has_no_item_and_a_cycle_note_needs_no_blockers() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(ReadyWith(Row("w1", "Daemon tests flake"), new WorkItemTopologyDto { Cycle = "cyclic" }));

            await h.PushAsync(Dto());

            await Assert.That(h.Vm.Key).IsNull();
            await Assert.That(h.Vm.Title).IsEqualTo("Daemon tests flake");
            await Assert.That(h.Vm.PartsHeader).IsEqualTo("0 parts");
            await Assert.That(h.Vm.HasParts).IsFalse();
            await Assert.That(h.Vm.HasBlockers).IsFalse();
            await Assert.That(h.Vm.CycleNote).IsEqualTo("Dependencies form a cycle");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_topology_blip_keeps_parts_for_the_same_primary_and_clears_them_for_a_new_one() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            h.Source.Enqueue(
                ReadyWith(Row("w1", "AI-1 — t"), Topology(Part("p1", "First", 0))),
                ReadyWith(Row("w1", "AI-1 — t"), topology: null, topologyFailed: true),
                ReadyWith(Row("w9", "AI-9 — other"), topology: null, topologyFailed: true),
                ReadyWith(Row("w9", "AI-9 — other"), new WorkItemTopologyDto()));
            await h.PushAsync(Dto());
            await h.TickAsync();
            await Assert.That(h.Vm.Parts.Count).IsEqualTo(1);
            await Assert.That(h.Vm.IsStale).IsTrue();

            await h.TickAsync();
            await Assert.That(h.Vm.Key).IsEqualTo("AI-9");
            await Assert.That(h.Vm.Parts).IsEmpty();
            await Assert.That(h.Vm.IsStale).IsTrue();

            await h.TickAsync();
            await Assert.That(h.Vm.IsStale).IsFalse();
            await Assert.That(h.Vm.Parts).IsEmpty();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Links_come_from_the_list_with_the_top_level_triple_as_a_repository_aware_fallback() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var dup = new SessionSummaryDto {
                SessionId = SessionA, RepoOwner = "kurrent-io", RepoName = "kcap-cli", PrNumber = 42, PrUrl = "https://github.com/kurrent-io/kcap-cli/pull/42", PrTitle = "Top",
                PullRequests = [Pr("kurrent-io", "kcap-cli", 42, "https://github.com/kurrent-io/kcap-cli/pull/42", "Listed")],
            };
            var otherRepo = dup with { PullRequests = [Pr("kurrent-io", "kcap-server", 42, "https://github.com/kurrent-io/kcap-server/pull/42", "Server")] };
            var noIdentity = dup with { RepoOwner = null, RepoName = null, PullRequests = [Pr("x", "y", 42, null, "Elsewhere")] };
            h.Source.Enqueue(ReadyWith(null, summary: dup), ReadyWith(null, summary: otherRepo), ReadyWith(null, summary: noIdentity));

            await h.PushAsync(Dto());
            await Assert.That(h.Vm.Links.Select(l => l.Title)).IsEquivalentTo(new[] { "Listed" });
            await Assert.That(h.Vm.Links[0].Eyebrow).IsEqualTo("PULL REQUEST");
            await Assert.That(h.Vm.Links[0].Key).IsEqualTo("#42");

            await h.TickAsync();
            await Assert.That(h.Vm.Links.Select(l => l.Title)).IsEquivalentTo(new[] { "Server", "Top" });

            await h.TickAsync();
            await Assert.That(h.Vm.Links.Select(l => l.Title)).IsEquivalentTo(new[] { "Elsewhere" });
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Links_open_through_the_policy_and_a_throwing_opener_is_caught() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var summary = new SessionSummaryDto {
                SessionId = SessionA,
                PullRequests = [Pr("o", "r", 1, "https://github.com/o/r/pull/1", "Https"), Pr("o", "r", 2, "file:///etc/passwd", "File"), Pr("o", "r", 3, "pull/3", "Relative"), Pr("o", "r", 4, null, "None")],
            };
            h.Source.Enqueue(ReadyWith(null, summary: summary));
            await h.PushAsync(Dto());

            await Assert.That(h.Vm.Links.Select(l => l.CanOpen)).IsEquivalentTo(new[] { true, false, false, false }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await h.Vm.Links[0].OpenCommand.Execute();
            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { "https://github.com/o/r/pull/1" });

            h.Opener.ThrowOnOpen = new InvalidOperationException("no browser");
            await h.Vm.Links[0].OpenCommand.Execute();
            await Assert.That(h.Opener.Opened.Count).IsEqualTo(2);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_summary_blip_keeps_the_link_cards_and_an_empty_summary_clears_them() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var withPr = new SessionSummaryDto { SessionId = SessionA, PullRequests = [Pr("o", "r", 1, null, "One")] };
            h.Source.Enqueue(ReadyWith(null, summary: withPr), ReadyWith(null, summary: null, summaryFailed: true), ReadyWith(null));
            await h.PushAsync(Dto());
            await h.TickAsync();
            await Assert.That(h.Vm.Links.Count).IsEqualTo(1);
            await Assert.That(h.Vm.IsStale).IsTrue();
            await h.TickAsync();
            await Assert.That(h.Vm.Links).IsEmpty();
            await Assert.That(h.Vm.IsStale).IsFalse();
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Terminal_phases_clear_every_server_projection_but_keep_the_facts_and_requester() {
        await RunOnUiAsync(async () => {
            foreach (var kind in new[] { WorkContextReadKind.SignedOut, WorkContextReadKind.NotInPlan, WorkContextReadKind.SessionUnknown }) {
                var h = new Harness();
                var summary = new SessionSummaryDto { SessionId = SessionA, PullRequests = [Pr("o", "r", 1, null, "One")] };
                h.Source.Enqueue(ReadyWith(Row("w1", "AI-1 — t"), Topology(Part("p1", "First", 0)), summary), WorkContextRead.Of(kind));
                await h.PushAsync(Dto());
                await h.TickAsync();

                await Assert.That(h.Vm.Key).IsNull();
                await Assert.That(h.Vm.Title).IsEqualTo("");
                await Assert.That(h.Vm.Parts).IsEmpty();
                await Assert.That(h.Vm.BlockedBy).IsEmpty();
                await Assert.That(h.Vm.CycleNote).IsNull();
                await Assert.That(h.Vm.Links).IsEmpty();
                await Assert.That(h.Vm.Repository).IsEqualTo("myproj");
                await Assert.That(h.Vm.Requester).IsEqualTo("You");
                await h.Vm.TeardownAsync();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_null_primary_clears_the_card_to_no_work_item_but_keeps_the_links() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            var summary = new SessionSummaryDto { SessionId = SessionA, PullRequests = [Pr("o", "r", 1, null, "One")] };
            h.Source.Enqueue(ReadyWith(Row("w1", "AI-1 — t"), Topology(Part("p1", "First", 0)), summary), ReadyWith(null, summary: summary));
            await h.PushAsync(Dto());
            await h.TickAsync();

            await Assert.That(h.Vm.Phase).IsEqualTo(WorkContextPhase.NoWorkItem);
            await Assert.That(h.Vm.Parts).IsEmpty();
            await Assert.That(h.Vm.Key).IsNull();
            await Assert.That(h.Vm.Links.Count).IsEqualTo(1);
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_requester_row_prefers_the_display_name_then_the_id_then_you_and_skips_blanks() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await h.PushAsync(Dto(sessionId: null) with { RequesterDisplay = "  Ada Lovelace ", Requester = "github:1" });
            await Assert.That(h.Vm.Requester).IsEqualTo("Ada Lovelace");
            await Assert.That(h.Vm.RequesterInitial).IsEqualTo("A");
            await Assert.That(h.Vm.RequesterRole).IsEqualTo("This session · Claude Code");

            await h.PushAsync(Dto(sessionId: null) with { RequesterDisplay = "   ", Requester = "github:1" });
            await Assert.That(h.Vm.Requester).IsEqualTo("github:1");
            await Assert.That(h.Vm.RequesterInitial).IsEqualTo("G");

            await h.PushAsync(Dto(sessionId: null) with { RequesterDisplay = null, Requester = "" });
            await Assert.That(h.Vm.Requester).IsEqualTo("You");
            await Assert.That(h.Vm.RequesterInitial).IsEqualTo("Y");
            await h.Vm.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Sections_default_open_parts_and_collapsed_people_and_session_and_toggle() {
        await RunOnUiAsync(async () => {
            var h = new Harness();
            await Assert.That(h.Vm.PartsExpanded).IsTrue();
            await Assert.That(h.Vm.PeopleExpanded).IsFalse();
            await Assert.That(h.Vm.SessionExpanded).IsFalse();

            await h.Vm.TogglePartsCommand.Execute();
            await h.Vm.TogglePeopleCommand.Execute();
            await h.Vm.ToggleSessionCommand.Execute();

            await Assert.That(h.Vm.PartsExpanded).IsFalse();
            await Assert.That(h.Vm.PeopleExpanded).IsTrue();
            await Assert.That(h.Vm.SessionExpanded).IsTrue();
            await h.Vm.TeardownAsync();
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextViewModelTests/*"`
Expected: compile errors for `Key`, `Parts`, `Links`, `Requester`, `TogglePartsCommand` and friends.

- [ ] **Step 3: Create the item view models**

Create `src/Capacitor.App/ViewModels/WorkContextItems.cs`:

```csharp
using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

public enum WorkContextPartMark { ThisSession, Unknown }

/// One declared part. The server exposes no completion state over HTTP, so a part is either the
/// one this session is attached to or unknown.
public sealed class WorkContextPartViewModel(string title, WorkContextPartMark mark) {
    public string Title { get; } = title;
    public WorkContextPartMark Mark { get; } = mark;
    public bool IsThisSession => Mark == WorkContextPartMark.ThisSession;
}

/// A pull-request card. The URL is server-returned, so it crosses the same trust boundary the chat
/// tab applies before a link reaches the shell opener.
public sealed class WorkContextLinkViewModel {
    public string  Eyebrow { get; }
    public string  Key     { get; }
    public string  Title   { get; }
    public string? Url     { get; }
    public bool    CanOpen { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public WorkContextLinkViewModel(string eyebrow, string key, string title, string? url, IUrlOpener opener) {
        Eyebrow = eyebrow;
        Key     = key;
        Title   = title;
        Url     = url;
        CanOpen = LinkPolicy.IsOpenable(url);
        OpenCommand = ReactiveCommand.Create(() => {
            try { opener.Open(url!); }
            catch (Exception ex) { Console.Error.WriteLine($"kcap: open link failed: {ex.Message}"); }
        }, Observable.Return(CanOpen));
    }
}
```

- [ ] **Step 4: Replace the projections half**

Replace the whole of `src/Capacitor.App/ViewModels/WorkContextViewModel.Projections.cs` with:

```csharp
using System.Reactive;
using Avalonia.Collections;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// The server-derived half of the pane and how one read merges into it, section by section: a
/// failed section keeps its last projection and marks the pane stale, an authoritative empty
/// answer clears it, and a terminal phase clears everything the server gave.
public sealed partial class WorkContextViewModel {
    readonly AvaloniaList<WorkContextPartViewModel> _parts = new();
    readonly AvaloniaList<string> _blockedBy = new();
    readonly AvaloniaList<WorkContextLinkViewModel> _links = new();
    string? _primaryId;

    public IAvaloniaReadOnlyList<WorkContextPartViewModel> Parts => _parts;
    public IAvaloniaReadOnlyList<string> BlockedBy => _blockedBy;
    public IAvaloniaReadOnlyList<WorkContextLinkViewModel> Links => _links;

    string? _key;
    public string? Key { get => _key; private set => this.RaiseAndSetIfChanged(ref _key, value); }
    string _title = "";
    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }
    string? _partOfTitle;
    public string? PartOfTitle { get => _partOfTitle; private set => this.RaiseAndSetIfChanged(ref _partOfTitle, value); }
    string? _cycleNote;
    public string? CycleNote { get => _cycleNote; private set => this.RaiseAndSetIfChanged(ref _cycleNote, value); }

    public string PartsHeader => _parts.Count == 1 ? "1 part" : $"{_parts.Count} parts";
    public bool HasParts => _parts.Count > 0;
    public bool HasBlockers => _blockedBy.Count > 0;

    string _requester = "You";
    public string Requester { get => _requester; private set => this.RaiseAndSetIfChanged(ref _requester, value); }
    string _requesterRole = "";
    public string RequesterRole { get => _requesterRole; private set => this.RaiseAndSetIfChanged(ref _requesterRole, value); }
    string _requesterInitial = "Y";
    public string RequesterInitial { get => _requesterInitial; private set => this.RaiseAndSetIfChanged(ref _requesterInitial, value); }

    bool _partsExpanded = true;
    public bool PartsExpanded { get => _partsExpanded; private set => this.RaiseAndSetIfChanged(ref _partsExpanded, value); }
    bool _peopleExpanded;
    public bool PeopleExpanded { get => _peopleExpanded; private set => this.RaiseAndSetIfChanged(ref _peopleExpanded, value); }
    bool _sessionExpanded;
    public bool SessionExpanded { get => _sessionExpanded; private set => this.RaiseAndSetIfChanged(ref _sessionExpanded, value); }

    public ReactiveCommand<Unit, Unit> TogglePartsCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> TogglePeopleCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> ToggleSessionCommand { get; private set; } = null!;

    void InitializeProjections() {
        TogglePartsCommand   = ReactiveCommand.Create(() => { PartsExpanded = !PartsExpanded; });
        TogglePeopleCommand  = ReactiveCommand.Create(() => { PeopleExpanded = !PeopleExpanded; });
        ToggleSessionCommand = ReactiveCommand.Create(() => { SessionExpanded = !SessionExpanded; });
        _disposables.Add(TogglePartsCommand);
        _disposables.Add(TogglePeopleCommand);
        _disposables.Add(ToggleSessionCommand);
    }

    void UpdateRequester(AgentStatusDto dto, string vendorLabel) {
        Requester = FirstNonBlank(dto.RequesterDisplay, dto.Requester) ?? "You";
        RequesterRole = $"This session · {vendorLabel}";
        RequesterInitial = Requester[..1].ToUpperInvariant();
    }

    static string? FirstNonBlank(params string?[] values) {
        foreach (var value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    void ClearServerProjections() {
        ClearCard();
        _links.Clear();
    }

    void ClearCard() {
        _primaryId = null;
        Key = null;
        Title = "";
        ClearTopology();
    }

    void ClearTopology() {
        PartOfTitle = null;
        CycleNote = null;
        _parts.Clear();
        _blockedBy.Clear();
        RaiseCardCounts();
    }

    void RaiseCardCounts() {
        this.RaisePropertyChanged(nameof(PartsHeader));
        this.RaisePropertyChanged(nameof(HasParts));
        this.RaisePropertyChanged(nameof(HasBlockers));
    }

    void ApplyReady(WorkContextRead read) {
        if (read.Primary is null) {
            ClearCard();
            ApplyLinks(read);
            Phase = WorkContextPhase.NoWorkItem;
            IsStale = read.SummaryFailed;
            return;
        }

        var samePrimary = string.Equals(read.Primary.WorkItemId, _primaryId, StringComparison.Ordinal);
        _primaryId = read.Primary.WorkItemId;
        var (key, display) = WorkContextLabel.Split(read.Primary.Label);
        Key = key;
        Title = read.Topology?.Item?.Title is { Length: > 0 } itemTitle ? itemTitle : display;

        if (read.Topology is { } topology) ApplyTopology(topology, read.Assignments);
        else if (!samePrimary) ClearTopology();

        ApplyLinks(read);
        Phase = WorkContextPhase.Ready;
        IsStale = read.TopologyFailed || read.SummaryFailed;
    }

    void ApplyTopology(WorkItemTopologyDto topology, IReadOnlyList<SessionWorkItemAssignmentDto> assignments) {
        var attached = new HashSet<string>(assignments.Select(a => a.WorkItemId), StringComparer.Ordinal);
        PartOfTitle = topology.PartOf?.Title;
        _parts.Clear();
        _parts.AddRange(topology.Parts
            .OrderBy(p => p.Ordinal)
            .Select(p => new WorkContextPartViewModel(p.Title, attached.Contains(p.WorkItemId) ? WorkContextPartMark.ThisSession : WorkContextPartMark.Unknown)));
        _blockedBy.Clear();
        _blockedBy.AddRange(topology.BlockedBy.Select(b => b.Title));
        CycleNote = topology.Cycle switch {
            "cyclic"        => "Dependencies form a cycle",
            "indeterminate" => "Dependencies could not be fully resolved",
            _               => null,
        };
        RaiseCardCounts();
    }

    void ApplyLinks(WorkContextRead read) {
        if (read.Summary is not { } summary) return;

        var cards = summary.PullRequests
            .Select(pr => Link(pr.Number, pr.Title, pr.Url))
            .ToList();
        if (summary.PrNumber is { } number && !summary.PullRequests.Any(pr => SamePullRequest(pr, summary, number)))
            cards.Add(Link(number, summary.PrTitle, summary.PrUrl));

        _links.Clear();
        _links.AddRange(cards);
    }

    WorkContextLinkViewModel Link(int number, string? title, string? url) =>
        new("PULL REQUEST", $"#{number}", title ?? $"Pull request #{number}", url, _opener);

    /// PR numbers are repository-local; without a repository identity on the summary the number
    /// alone decides, which never shows one PR twice.
    internal static bool SamePullRequest(SessionPullRequestDto pr, SessionSummaryDto summary, int number) {
        if (pr.Number != number) return false;
        if (string.IsNullOrEmpty(summary.RepoOwner) || string.IsNullOrEmpty(summary.RepoName)) return true;

        return string.Equals(pr.Owner, summary.RepoOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(pr.RepoName, summary.RepoName, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 5: Run to verify they pass**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextViewModelTests/*"`
Expected: all 24 pass.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.App/ViewModels/WorkContextItems.cs src/Capacitor.App/ViewModels/WorkContextViewModel.Projections.cs test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Project the work item, its parts, blockers and links into the sidebar" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 9: Hang the sidebar off the workspace

**Files:**
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs:101-147, 198-204`
- Modify: `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs:24-27` (+ new test)
- Modify: `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs:43-45`
- Modify: `test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs:33-35, 293-294, 380-381`
- Modify: `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs:44-45`
- Modify: `test/Capacitor.App.Tests.Unit/WorkspaceNavigationTests.cs:88-89`

**Interfaces:**
- Consumes: `WorkContextViewModel` (Tasks 7–8), `IWorkContextSource` (Task 6), `FakeWorkContextSource` (Task 7).
- Produces: `WorkspaceViewModel(string agentId, IDaemonClientService daemon, AgentActionService actions, TerminalAttachClientFactory factory, Func<ITerminalSurface> surfaceFactory, TimeProvider time, IUrlOpener opener, IPermissionService permissions, IWorkContextSource workContext, Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null)`; `WorkspaceViewModel.WorkContext` (`WorkContextViewModel`).

- [ ] **Step 1: Write the failing test**

Append to `WorkspaceViewModelTests`:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task WorkContext_is_fed_by_the_same_presence_and_torn_down_with_the_workspace() {
        await RunOnUiAsync(async () => {
            var daemon = new FakeDaemonClientService();
            var source = new FakeWorkContextSource();
            var factory = new FakeTerminalAttachClientFactory();
            var vm = new WorkspaceViewModel("a1", daemon, NewActions(new ScriptedLocalControlOps(), new RecordingNotifier(), new RecordingOpener()),
                factory.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), source);
            await Assert.That(vm.WorkContext.Phase).IsEqualTo(WorkContextPhase.WaitingForSession);

            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj", sessionId: "0123456789abcdef0123456789abcdef"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (vm.WorkContext.PendingReadForTesting ?? Task.CompletedTask);

            await Assert.That(vm.WorkContext.Repository).IsEqualTo("myproj");
            await Assert.That(source.Requested).IsEquivalentTo(new[] { "0123456789abcdef0123456789abcdef" });

            await vm.TeardownAsync();
            source.Default = WorkContextRead.Of(WorkContextReadKind.Ready);
            daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true, repoPath: "/repo/myproj", sessionId: "ffffffffffffffffffffffffffffffff"));
            await Assert.That(source.Requested.Count).IsEqualTo(1);
        });
    }
```

Add `using Capacitor.Cli.Core.WorkItems;` to the file's usings.

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkspaceViewModelTests/*"`
Expected: `CS1729: 'WorkspaceViewModel' does not contain a constructor that takes 9 arguments`.

- [ ] **Step 3: Extend the workspace view model**

In `WorkspaceViewModel.cs`:

Add `using System;` is already implicit; add after the `Chat` property:

```csharp
    /// The right pane. Fed by the same presence stream as the header, so the daemon cache has one
    /// subscription per workspace, not two.
    public WorkContextViewModel WorkContext { get; }
```

Replace the constructor signature:

```csharp
    public WorkspaceViewModel(
            string agentId, IDaemonClientService daemon, AgentActionService actions,
            TerminalAttachClientFactory factory, Func<ITerminalSurface> surfaceFactory, TimeProvider time,
            IUrlOpener opener, IPermissionService permissions, IWorkContextSource workContext,
            Action? requestSignIn = null, IObservable<Unit>? signInCompleted = null) {
```

After the `presence` pipeline is built (after `.RefCount();`), add:

```csharp
        WorkContext = new WorkContextViewModel(presence.Select(p => p.Dto), workContext, time, opener, requestSignIn, signInCompleted);
```

Replace `TeardownAsync`:

```csharp
    /// Disposes this workspace's own daemon-cache projections, then tears down Chat (if built), the
    /// work-context pane, and Terminal last -- the caller that closes a workspace tab calls this once.
    public async Task TeardownAsync() {
        _disposables.Dispose();
        if (Chat is { } chat) await chat.TeardownAsync();
        await WorkContext.TeardownAsync();
        await Terminal.TeardownAsync();
    }
```

- [ ] **Step 4: Update every construction site**

Each site gains `new FakeWorkContextSource()` after the `new FakePermissionService()` argument:

`WorkspaceViewModelTests.cs:27`:
```csharp
        new(agentId, daemon, actions, factory.Factory, () => new FakeTerminalSurface(), time, new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource());
```

`WorkspaceViewSmokeTests.cs:43-45`:
```csharp
        var vm = new WorkspaceViewModel(
            agentId, daemon, NewActions(), attach.Factory, surface ?? (() => new FakeTerminalSurface()),
            new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource());
```

`MainWindowSmokeTests.cs:33-35`:
```csharp
    static WorkspaceViewModel NewWorkspace(FakeDaemonClientService service, AgentActionService actions, string agentId) =>
        new(agentId, service, actions, new FakeTerminalAttachClientFactory().Factory,
            () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource());
```

`MainWindowSmokeTests.cs:293-294` and `:380-381` (both lambdas):
```csharp
                workspaceFactory: agentId => new WorkspaceViewModel(
                    agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource()));
```

`MainWindowViewModelTests.cs:44-45`:
```csharp
        return new WorkspaceViewModel(
            agentId, service, actions, attach.Factory, () => new FakeTerminalSurface(), new FakeTimeProvider(), new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource());
```

`WorkspaceNavigationTests.cs:88-89`:
```csharp
                return new WorkspaceViewModel(
                    agentId, daemon, actions, attach.Factory, () => new FakeTerminalSurface(), time, new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource());
```

`App.axaml.cs` does not compile yet either; Task 11 rewires it. Until then, make the production `BuildWorkspace` lambda pass a placeholder so the app builds: in `App.axaml.cs:335-336` append `, new ServerWorkContextSource(_config, profiles)` after `permissions` (Task 11 replaces this with the held instance).

- [ ] **Step 5: Run the app suite**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: everything passes, including the new workspace test and every suite that constructs a workspace.

- [ ] **Step 6: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.App/ViewModels/WorkspaceViewModel.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Build the work-context pane per workspace off the shared presence stream" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 10: The view, the second column, the palette entry and the window floor

**Files:**
- Create: `src/Capacitor.App/Views/WorkContextView.axaml`, `src/Capacitor.App/Views/WorkContextView.axaml.cs`
- Modify: `src/Capacitor.App/Views/WorkspaceView.axaml:25-174`
- Modify: `src/Capacitor.App/App.axaml:9-23`
- Modify: `src/Capacitor.App/Views/MainWindow.axaml:11`
- Modify: `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs:95-120` (+ new test)
- Modify: `test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs` (+ new test)

**Interfaces:**
- Consumes: every bound member of `WorkContextViewModel` (Tasks 7–8), `WorkspaceViewModel.WorkContext` (Task 9), the `Kcap*` brushes.
- Produces: `KcapPurpleDimBrush` (`#29233E`); named controls `WorkContextHost`, `RefreshButton`, `StaleDot`, `WorkContextKey`, `WorkContextTitle`, `PartOfLine`, `PartsToggle`, `PartsList`, `BlockedByBlock`, `CycleNoteText`, `PhaseNoteText`, `SignInButton`, `RetryButton`, `LinkCards`, `IssueSoonCard`, `WhoToggle`, `SessionToggle`, `SessionSummaryText`, `SessionFacts`.

- [ ] **Step 1: Write the failing smoke tests**

In `WorkspaceViewSmokeTests.WorkspaceView_resolves_all_named_controls`, extend the `names` array and add a second scope check:

```csharp
            var names = new[] {
                "WorkspaceTitle", "WorkspaceRepo", "WorkspaceVendorChip", "ChatTabButton",
                "TerminalTabButton", "NoTerminalNote", "TerminalHost", "TerminalBanners",
                "DetachButton", "ReattachButton", "SessionEndedNote", "ChatHost", "WorkContextHost",
            };
            foreach (var name in names)
                await Assert.That(Find<Control>(window, name)).IsNotNull().Because($"{name} should resolve");

            var chatHost = Find<ChatTabView>(window, "ChatHost")!;
            foreach (var name in new[] { "ChatItems", "ChatPhaseNote", "ComposerInput", "SendButton" })
                await Assert.That(chatHost.FindControl<Control>(name)).IsNotNull().Because($"{name} should resolve");

            var pane = Find<WorkContextView>(window, "WorkContextHost")!;
            foreach (var name in new[] {
                "RefreshButton", "StaleDot", "WorkContextKey", "WorkContextTitle", "PartOfLine", "PartsToggle", "PartsList",
                "BlockedByBlock", "CycleNoteText", "PhaseNoteText", "SignInButton", "RetryButton", "LinkCards", "IssueSoonCard",
                "WhoToggle", "SessionToggle", "SessionSummaryText", "SessionFacts",
            })
                await Assert.That(pane.FindControl<Control>(name)).IsNotNull().Because($"{name} should resolve");
```

Append a layout test to the same class:

```csharp
    /// The pane takes its fixed 400 and the terminal the rest, so the PTY size the terminal
    /// reports is the real center-pane width.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_is_400_wide_and_the_terminal_takes_the_remainder() {
        await RunOnUiAsync(async () => {
            var (window, vm, _, _) = await ShowPtyAsync();

            var pane = Find<WorkContextView>(window, "WorkContextHost")!;
            var terminal = Find<TerminalControl>(window, "TerminalHost")!;
            await Assert.That(pane.Bounds.Width).IsEqualTo(400);
            await Assert.That(terminal.Bounds.Width).IsEqualTo(window.Bounds.Width - 400);

            window.Close();
            Dispatcher.UIThread.RunJobs();
            await vm.TeardownAsync();
        });
    }
```

Append to `MainWindowSmokeTests`:

```csharp
    /// 310 of rail plus 400 of pane must never squeeze the center column to nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task MainWindow_pins_its_minimum_width_to_the_default_width() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var window = new MainWindow { DataContext = new MainWindowViewModel(new FakeDaemonClientService(), CancellationToken.None, TestActivity.New()) };

            await Assert.That(window.MinWidth).IsEqualTo(1200);
            await Assert.That(window.Width).IsEqualTo(1200);
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkspaceViewSmokeTests/*"`
Expected: `CS0246: The type or namespace name 'WorkContextView' could not be found`.

- [ ] **Step 3: Add the palette entry and the window floor**

In `App.axaml`, after `KcapPurpleBrush`:

```xml
            <SolidColorBrush x:Key="KcapPurpleDimBrush" Color="#29233E" />
```

In `MainWindow.axaml`, replace `Width="1200" Height="760"` with:

```xml
                     Width="1200" MinWidth="1200" Height="760"
```

- [ ] **Step 4: Create the view**

Create `src/Capacitor.App/Views/WorkContextView.axaml`:

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="clr-namespace:Capacitor.App.ViewModels"
             x:Class="Capacitor.App.Views.WorkContextView"
             x:DataType="vm:WorkContextViewModel"
             Background="{StaticResource KcapSurfaceBrush}">
    <UserControl.Styles>
        <Style Selector="TextBlock.eyebrow">
            <Setter Property="FontSize" Value="10" />
            <Setter Property="FontWeight" Value="Bold" />
            <Setter Property="LetterSpacing" Value="1.2" />
            <Setter Property="Foreground" Value="{StaticResource KcapFaintBrush}" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>
        <Style Selector="TextBlock.cardEyebrow">
            <Setter Property="FontSize" Value="9" />
            <Setter Property="FontWeight" Value="Bold" />
            <Setter Property="LetterSpacing" Value="1" />
            <Setter Property="Foreground" Value="{StaticResource KcapFaintBrush}" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>
        <Style Selector="Border.soon">
            <Setter Property="Background" Value="{StaticResource KcapPurpleDimBrush}" />
            <Setter Property="CornerRadius" Value="999" />
            <Setter Property="Padding" Value="6,2" />
            <Setter Property="VerticalAlignment" Value="Center" />
        </Style>
        <Style Selector="Border.soon > TextBlock">
            <Setter Property="Text" Value="SOON" />
            <Setter Property="FontSize" Value="8.5" />
            <Setter Property="FontWeight" Value="Bold" />
            <Setter Property="LetterSpacing" Value="0.6" />
            <Setter Property="Foreground" Value="{StaticResource KcapPurpleBrush}" />
        </Style>
        <Style Selector="Button.toggle">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="BorderThickness" Value="0" />
            <Setter Property="Padding" Value="0" />
            <Setter Property="HorizontalAlignment" Value="Stretch" />
            <Setter Property="HorizontalContentAlignment" Value="Stretch" />
        </Style>
        <Style Selector="Border.card">
            <Setter Property="Background" Value="{StaticResource KcapSurfaceRaisedBrush}" />
            <Setter Property="BorderBrush" Value="{StaticResource KcapBorderBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="10" />
            <Setter Property="Padding" Value="13" />
        </Style>
        <Style Selector="Path.chevron">
            <Setter Property="Stroke" Value="{StaticResource KcapMutedBrush}" />
            <Setter Property="StrokeThickness" Value="1.8" />
            <Setter Property="StrokeLineCap" Value="Round" />
            <Setter Property="StrokeJoin" Value="Round" />
        </Style>
    </UserControl.Styles>

    <Border BorderBrush="{StaticResource KcapBorderBrush}" BorderThickness="1,0,0,0">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <StackPanel Margin="16,18,16,18" Spacing="0">

                <!-- Header: eyebrow, the stale dot, Refresh. -->
                <DockPanel>
                    <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" Spacing="8">
                        <Ellipse x:Name="StaleDot" Width="6" Height="6" Fill="{StaticResource KcapWarningBrush}"
                                 IsVisible="{Binding IsStale}" ToolTip.Tip="Last refresh failed; showing the previous result" />
                        <Button x:Name="RefreshButton" Command="{Binding RefreshCommand}" Classes="toggle" ToolTip.Tip="Refresh">
                            <Path Stroke="{StaticResource KcapMutedBrush}" StrokeThickness="1.6" StrokeLineCap="Round" Width="13" Height="13" Stretch="Uniform"
                                  Data="M11.5,6.5 A5,5 0 1 1 10,3 M10,0.5 L10,3 L7.5,3" />
                        </Button>
                    </StackPanel>
                    <TextBlock Text="ABOUT THIS WORK" Classes="eyebrow" />
                </DockPanel>

                <!-- Work item card. -->
                <Border Classes="card" Margin="0,12,0,0">
                    <StackPanel Spacing="0">
                        <DockPanel>
                            <Border DockPanel.Dock="Right" Classes="soon"><TextBlock /></Border>
                            <TextBlock Text="WORK ITEM" Classes="cardEyebrow" />
                        </DockPanel>

                        <!-- Ready body. -->
                        <StackPanel IsVisible="{Binding IsReady}">
                            <TextBlock x:Name="WorkContextKey" Text="{Binding Key}" FontSize="12" FontWeight="Bold"
                                       Foreground="{StaticResource KcapAccentBrush}" Margin="0,8,0,0"
                                       IsVisible="{Binding Key, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                            <TextBlock x:Name="WorkContextTitle" Text="{Binding Title}" FontSize="12.5" LineHeight="18"
                                       Foreground="{StaticResource KcapTextBrush}" TextWrapping="Wrap" Margin="0,4,0,0" />
                            <TextBlock x:Name="PartOfLine" Text="{Binding PartOfTitle, StringFormat='Part of {0}'}" FontSize="10.5"
                                       Foreground="{StaticResource KcapMutedBrush}" TextWrapping="Wrap" Margin="0,4,0,0"
                                       IsVisible="{Binding PartOfTitle, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />

                            <!-- Parts: header toggle, then one row per part. -->
                            <Button x:Name="PartsToggle" Command="{Binding TogglePartsCommand}" Classes="toggle" Margin="0,12,0,0">
                                <DockPanel>
                                    <Border DockPanel.Dock="Right" Classes="soon"><TextBlock /></Border>
                                    <StackPanel Orientation="Horizontal" Spacing="7">
                                        <!-- Stroked chevron, not a text glyph — the 9px ▸ rendered as a dot. -->
                                        <Panel Width="12" Height="12" VerticalAlignment="Center">
                                            <Path Classes="chevron" Data="M3,4.5 L6,7.5 L9,4.5" IsVisible="{Binding PartsExpanded}" />
                                            <Path Classes="chevron" Data="M4.5,3 L7.5,6 L4.5,9" IsVisible="{Binding !PartsExpanded}" />
                                        </Panel>
                                        <TextBlock Text="{Binding PartsHeader}" FontSize="10.5" Foreground="{StaticResource KcapMutedBrush}" VerticalAlignment="Center" />
                                    </StackPanel>
                                </DockPanel>
                            </Button>
                            <ItemsControl x:Name="PartsList" ItemsSource="{Binding Parts}" Margin="0,8,0,0" IsVisible="{Binding PartsExpanded}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate x:DataType="vm:WorkContextPartViewModel">
                                        <StackPanel Orientation="Horizontal" Spacing="9" Margin="0,0,0,7">
                                            <Panel Width="12" Height="12" VerticalAlignment="Center">
                                                <Ellipse Width="12" Height="12" Stroke="{StaticResource KcapBorderBrush}" StrokeThickness="1.5" IsVisible="{Binding !IsThisSession}" />
                                                <Ellipse Width="12" Height="12" Fill="{StaticResource KcapAccentBrush}" IsVisible="{Binding IsThisSession}" />
                                                <Path Stroke="#07120E" StrokeThickness="1.6" StrokeLineCap="Round" StrokeJoin="Round"
                                                      Data="M3,6 L5.2,8.2 L9,4" IsVisible="{Binding IsThisSession}" />
                                            </Panel>
                                            <TextBlock Text="{Binding Title}" FontSize="11.5" TextWrapping="Wrap" VerticalAlignment="Center"
                                                       Foreground="{StaticResource KcapTextBrush}" />
                                        </StackPanel>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>

                            <!-- Blocked by. -->
                            <Border x:Name="BlockedByBlock" Background="{StaticResource KcapWarningDimBrush}" CornerRadius="7" Padding="10,8" Margin="0,11,0,0"
                                    IsVisible="{Binding HasBlockers}">
                                <StackPanel Spacing="4">
                                    <TextBlock Text="BLOCKED BY" Classes="cardEyebrow" Foreground="{StaticResource KcapWarningBrush}" />
                                    <ItemsControl ItemsSource="{Binding BlockedBy}">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate x:DataType="x:String">
                                                <TextBlock Text="{Binding}" FontSize="10.5" LineHeight="15" TextWrapping="Wrap"
                                                           Foreground="{StaticResource KcapWarningBrush}" />
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </StackPanel>
                            </Border>
                            <TextBlock x:Name="CycleNoteText" Text="{Binding CycleNote}" FontSize="10.5" Foreground="{StaticResource KcapMutedBrush}"
                                       TextWrapping="Wrap" Margin="0,8,0,0"
                                       IsVisible="{Binding CycleNote, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                        </StackPanel>

                        <!-- Every other phase: the note and, where it applies, one action. -->
                        <StackPanel IsVisible="{Binding !IsReady}" Spacing="10" Margin="0,10,0,0">
                            <TextBlock x:Name="PhaseNoteText" Text="{Binding PhaseNote}" FontSize="11.5" LineHeight="16"
                                       Foreground="{StaticResource KcapMutedBrush}" TextWrapping="Wrap" />
                            <Button x:Name="SignInButton" Content="Sign in" Command="{Binding SignInCommand}" IsVisible="{Binding ShowsSignIn}"
                                    Background="{StaticResource KcapAccentBrush}" Foreground="#07120E" FontWeight="SemiBold"
                                    CornerRadius="7" Padding="13,5" HorizontalAlignment="Left" />
                            <Button x:Name="RetryButton" Content="Retry" Command="{Binding RefreshCommand}" IsVisible="{Binding ShowsRetry}"
                                    Background="{StaticResource KcapSurfaceBrush}" BorderBrush="{StaticResource KcapBorderBrush}"
                                    Foreground="{StaticResource KcapTextBrush}" CornerRadius="7" Padding="13,5" HorizontalAlignment="Left" />
                        </StackPanel>
                    </StackPanel>
                </Border>

                <!-- Link cards: one per pull request, then the issue slot. -->
                <ItemsControl x:Name="LinkCards" ItemsSource="{Binding Links}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate x:DataType="vm:WorkContextLinkViewModel">
                            <Button Command="{Binding OpenCommand}" Classes="toggle" Margin="0,9,0,0" ToolTip.Tip="{Binding Url}">
                                <Border Classes="card" Padding="13,11">
                                    <StackPanel Spacing="6">
                                        <TextBlock Text="{Binding Eyebrow}" Classes="cardEyebrow" />
                                        <StackPanel Orientation="Horizontal" Spacing="8">
                                            <TextBlock Text="{Binding Key}" FontSize="11.5" FontWeight="Bold" Foreground="{StaticResource KcapAccentBrush}" VerticalAlignment="Center" />
                                            <TextBlock Text="{Binding Title}" FontSize="11.5" Foreground="{StaticResource KcapTextBrush}" TextTrimming="CharacterEllipsis" VerticalAlignment="Center" />
                                        </StackPanel>
                                    </StackPanel>
                                </Border>
                            </Button>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
                <Border x:Name="IssueSoonCard" Classes="card" Padding="13,11" Margin="0,9,0,0">
                    <DockPanel>
                        <Border DockPanel.Dock="Right" Classes="soon"><TextBlock /></Border>
                        <TextBlock Text="ISSUE" Classes="cardEyebrow" />
                    </DockPanel>
                </Border>

                <!-- Who's on it. -->
                <Button x:Name="WhoToggle" Command="{Binding TogglePeopleCommand}" Classes="toggle" Margin="0,20,0,0">
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <TextBlock Text="WHO'S ON IT" Classes="eyebrow" />
                        <Border Classes="soon"><TextBlock /></Border>
                        <Panel Width="12" Height="12" VerticalAlignment="Center">
                            <Path Classes="chevron" Data="M3,4.5 L6,7.5 L9,4.5" IsVisible="{Binding PeopleExpanded}" />
                            <Path Classes="chevron" Data="M4.5,3 L7.5,6 L4.5,9" IsVisible="{Binding !PeopleExpanded}" />
                        </Panel>
                    </StackPanel>
                </Button>
                <StackPanel Orientation="Horizontal" Spacing="10" Margin="0,11,0,0">
                    <Border Width="24" Height="24" CornerRadius="999" Background="{StaticResource KcapAccentDimBrush}">
                        <TextBlock Text="{Binding RequesterInitial}" FontSize="10.5" FontWeight="Bold" Foreground="{StaticResource KcapAccentBrush}"
                                   HorizontalAlignment="Center" VerticalAlignment="Center" />
                    </Border>
                    <StackPanel VerticalAlignment="Center" IsVisible="{Binding PeopleExpanded}">
                        <TextBlock Text="{Binding Requester}" FontSize="11.5" FontWeight="SemiBold" Foreground="{StaticResource KcapTextBrush}" />
                        <TextBlock Text="{Binding RequesterRole}" FontSize="10.5" Foreground="{StaticResource KcapFaintBrush}" Margin="0,1,0,0" />
                    </StackPanel>
                </StackPanel>

                <Border Height="1" Background="{StaticResource KcapBorderBrush}" Margin="0,20,0,16" />

                <!-- Session facts. -->
                <Button x:Name="SessionToggle" Command="{Binding ToggleSessionCommand}" Classes="toggle">
                    <StackPanel Orientation="Horizontal" Spacing="8">
                        <TextBlock Text="SESSION" Classes="eyebrow" />
                        <Panel Width="12" Height="12" VerticalAlignment="Center">
                            <Path Classes="chevron" Data="M3,4.5 L6,7.5 L9,4.5" IsVisible="{Binding SessionExpanded}" />
                            <Path Classes="chevron" Data="M4.5,3 L7.5,6 L4.5,9" IsVisible="{Binding !SessionExpanded}" />
                        </Panel>
                    </StackPanel>
                </Button>
                <TextBlock x:Name="SessionSummaryText" Text="{Binding SessionSummaryLine}" FontSize="11" Foreground="{StaticResource KcapMutedBrush}"
                           Margin="0,10,0,0" IsVisible="{Binding !SessionExpanded}" />
                <Grid x:Name="SessionFacts" ColumnDefinitions="96,*" RowDefinitions="Auto,Auto,Auto,Auto,Auto,Auto" Margin="0,11,0,0"
                      IsVisible="{Binding SessionExpanded}">
                    <Grid.Styles>
                        <Style Selector="TextBlock.factLabel">
                            <Setter Property="FontSize" Value="9" />
                            <Setter Property="FontWeight" Value="Bold" />
                            <Setter Property="LetterSpacing" Value="0.8" />
                            <Setter Property="Foreground" Value="{StaticResource KcapFaintBrush}" />
                            <Setter Property="Margin" Value="0,0,0,11" />
                        </Style>
                        <Style Selector="SelectableTextBlock.factValue">
                            <Setter Property="FontSize" Value="11" />
                            <Setter Property="LineHeight" Value="15" />
                            <Setter Property="Foreground" Value="{StaticResource KcapTextBrush}" />
                            <Setter Property="TextWrapping" Value="Wrap" />
                            <Setter Property="Margin" Value="0,0,0,11" />
                        </Style>
                    </Grid.Styles>
                    <TextBlock Grid.Row="0" Grid.Column="0" Text="REPOSITORY" Classes="factLabel" />
                    <SelectableTextBlock Grid.Row="0" Grid.Column="1" Text="{Binding Repository}" Classes="factValue" ToolTip.Tip="{Binding RepositoryPath}" />
                    <TextBlock Grid.Row="1" Grid.Column="0" Text="WORKTREE" Classes="factLabel" />
                    <SelectableTextBlock Grid.Row="1" Grid.Column="1" Text="{Binding Worktree}" Classes="factValue" ToolTip.Tip="{Binding WorktreePath}" />
                    <TextBlock Grid.Row="2" Grid.Column="0" Text="BRANCH" Classes="factLabel" />
                    <SelectableTextBlock Grid.Row="2" Grid.Column="1" Text="{Binding Branch}" Classes="factValue" />
                    <TextBlock Grid.Row="3" Grid.Column="0" Text="HARNESS" Classes="factLabel" />
                    <SelectableTextBlock Grid.Row="3" Grid.Column="1" Text="{Binding Harness}" Classes="factValue" />
                    <TextBlock Grid.Row="4" Grid.Column="0" Text="TRANSPORT" Classes="factLabel" />
                    <SelectableTextBlock Grid.Row="4" Grid.Column="1" Text="{Binding Transport}" Classes="factValue" />
                    <TextBlock Grid.Row="5" Grid.Column="0" Text="ID" Classes="factLabel" />
                    <SelectableTextBlock Grid.Row="5" Grid.Column="1" Text="{Binding SessionIdText}" Classes="factValue" />
                </Grid>
            </StackPanel>
        </ScrollViewer>
    </Border>
</UserControl>
```

Create `src/Capacitor.App/Views/WorkContextView.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// The work-context pane. DataContext is the workspace's WorkContextViewModel, supplied by
/// WorkspaceView; this view builds nothing of its own.
public partial class WorkContextView : UserControl {
    public WorkContextView() {
        InitializeComponent();
    }
}
```

- [ ] **Step 5: Give the workspace its second column**

In `WorkspaceView.axaml` the root element after `</UserControl.Styles>` is `<Grid RowDefinitions="56,42,*">` (line 25) and the file ends with its `</Grid>` followed by `</UserControl>`. Wrap that grid in a two-column grid so it becomes column 0 unchanged and the pane takes column 1. Replace line 25 with:

```xml
    <!-- Two columns: the workspace proper, and the work-context pane at its fixed width. The
         terminal keeps its own column, so the size it reports to the PTY is the real center width. -->
    <Grid ColumnDefinitions="*,400">
    <Grid Grid.Column="0" RowDefinitions="56,42,*">
```

and replace the final two lines of the file with:

```xml
    </Grid>
    <views:WorkContextView x:Name="WorkContextHost" Grid.Column="1" DataContext="{Binding WorkContext}" />
    </Grid>
</UserControl>
```

Nothing inside the inner grid changes — names, bindings and comments stay as they are. The `views:` prefix is already declared on the `UserControl`.

- [ ] **Step 6: Run the smoke suites**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkspaceViewSmokeTests/*"` then the same with `MainWindowSmokeTests`.
Expected: all pass. If the width assertion is off by the border, the pane's `Border` is inside the 400 column and `Bounds.Width` of the `UserControl` is still 400; check the assertion targets the `WorkContextView`, not its inner `Border`.

- [ ] **Step 7: Build the app project and clear every warning**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj --no-incremental 2>&1 | grep -E 'warning|error' || echo clean`
Expected: `clean`. An `AVLN` warning names a binding or style the XAML got wrong; fix it here, not later.

- [ ] **Step 8: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.App/Views src/Capacitor.App/App.axaml test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs test/Capacitor.App.Tests.Unit/MainWindowSmokeTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Render the work-context pane in a 400px column of the workspace" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 11: App composition — the source, the sign-in signal, and one-shot cleanup on both paths

**Files:**
- Create: `src/Capacitor.App/Services/ServerClients.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (field block around line 101, composition at 328-336, `FinishSignInAsync` at 413-420, `DisposeLifecycleAndServiceAsync` at 1216, the startup-failure catch at 209, `DisposeLaunchClientAsync` at 1238-1247)
- Test: `test/Capacitor.App.Tests.Unit/ServerClientsTests.cs`

**Interfaces:**
- Consumes: `ServerLaunchClient : IAsyncDisposable` (existing), `ServerWorkContextSource : IAsyncDisposable` (Task 6), `WorkspaceViewModel`'s new parameters (Task 9), `OpenSignInDialog(profiles, notifier)` and `FinishSignInAsync` (existing).
- Produces: `ServerClients(IAsyncDisposable? launch, IAsyncDisposable? workContext) : IAsyncDisposable` with `IObservable<Unit> SignInCompleted`, `void NotifySignInCompleted()`, `bool CleanupStarted`; `internal static Task ServerClients.CleanupAsync(IAsyncDisposable? launch, IAsyncDisposable? workContext, Subject<Unit> signIn)`.

- [ ] **Step 1: Write the failing tests**

Create `test/Capacitor.App.Tests.Unit/ServerClientsTests.cs`:

```csharp
using System.Reactive;
using System.Reactive.Subjects;
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

/// The one place the app's server clients are torn down: the static sequence is ordered and
/// fault-isolated; the holder makes it happen once however many times, or how concurrently,
/// the two cleanup paths reach it.
public class ServerClientsTests {
    sealed class Spy(List<string> log, string name, Exception? throwOn = null) : IAsyncDisposable {
        public int Disposals;
        public TaskCompletionSource Gate = new();
        public bool Gated;

        public async ValueTask DisposeAsync() {
            Disposals++;
            log.Add(name);
            if (Gated) await Gate.Task;
            if (throwOn is not null) throw throwOn;
        }
    }

    [Test]
    public async Task The_sequence_disposes_launch_then_source_then_completes_and_disposes_the_subject() {
        var log = new List<string>();
        var subject = new Subject<Unit>();
        var completed = false;
        subject.Subscribe(_ => { }, () => completed = true);

        await ServerClients.CleanupAsync(new Spy(log, "launch"), new Spy(log, "source"), subject);

        await Assert.That(log).IsEquivalentTo(new[] { "launch", "source" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(completed).IsTrue();
        await Assert.That(subject.IsDisposed).IsTrue();
    }

    [Test]
    public async Task A_throwing_launch_disposal_still_disposes_the_source() {
        var log = new List<string>();
        var source = new Spy(log, "source");

        await ServerClients.CleanupAsync(new Spy(log, "launch", new InvalidOperationException("gate gone")), source, new Subject<Unit>());

        await Assert.That(source.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task The_holder_disposes_each_once_across_sequential_and_concurrent_calls() {
        var log = new List<string>();
        var launch = new Spy(log, "launch") { Gated = true };
        var source = new Spy(log, "source");
        var holder = new ServerClients(launch, source);

        var first = holder.DisposeAsync().AsTask();
        var second = holder.DisposeAsync().AsTask();
        await Assert.That(holder.CleanupStarted).IsTrue();
        launch.Gate.SetResult();
        await Task.WhenAll(first, second);
        await holder.DisposeAsync();

        await Assert.That(launch.Disposals).IsEqualTo(1);
        await Assert.That(source.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Sign_in_completion_reaches_subscribers_before_cleanup_and_is_inert_after() {
        var holder = new ServerClients(null, null);
        var seen = 0;
        using var sub = holder.SignInCompleted.Subscribe(_ => seen++);

        holder.NotifySignInCompleted();
        await holder.DisposeAsync();
        holder.NotifySignInCompleted();

        await Assert.That(seen).IsEqualTo(1);
    }
}
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerClientsTests/*"`
Expected: `CS0246: The type or namespace name 'ServerClients' could not be found`.

- [ ] **Step 3: Create the holder**

Create `src/Capacitor.App/Services/ServerClients.cs`:

```csharp
using System.Reactive;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// The app's server-side clients as one set with one cleanup. Ownership is here: the three are
/// taken in one exchange and the cleanup task memoized, so the startup-failure and shutdown paths
/// can both reach it, sequentially or overlapping, and nothing is disposed twice. The sequence
/// itself is the static below, which is deliberately not idempotent.
public sealed class ServerClients : IAsyncDisposable {
    readonly Subject<Unit> _signIn = new();
    readonly Lazy<Task> _cleanup;

    public ServerClients(IAsyncDisposable? launch, IAsyncDisposable? workContext) =>
        _cleanup = new Lazy<Task>(() => CleanupAsync(launch, workContext, _signIn), LazyThreadSafetyMode.ExecutionAndPublication);

    public IObservable<Unit> SignInCompleted => _signIn;

    public bool CleanupStarted => _cleanup.IsValueCreated;

    /// Raised where the app learns a sign-in completed. Ignored once cleanup has started: the
    /// subject is completed and disposed in the sequence, and a disposed subject throws on OnNext.
    public void NotifySignInCompleted() {
        if (CleanupStarted) return;
        _signIn.OnNext(Unit.Default);
    }

    public ValueTask DisposeAsync() => new(_cleanup.Value);

    /// Launch client, then the work-context source, then the subject completed and disposed —
    /// each step guarded so a throwing disposal never skips the next.
    internal static async Task CleanupAsync(IAsyncDisposable? launch, IAsyncDisposable? workContext, Subject<Unit> signIn) {
        await DisposeGuarded(launch, "launch client").ConfigureAwait(false);
        await DisposeGuarded(workContext, "work-context source").ConfigureAwait(false);
        try {
            signIn.OnCompleted();
            signIn.Dispose();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app failed to complete the sign-in signal during teardown: {ex}");
        }
    }

    static async Task DisposeGuarded(IAsyncDisposable? disposable, string what) {
        if (disposable is null) return;
        try {
            await disposable.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app failed to dispose the {what} during teardown: {ex}");
        }
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/ServerClientsTests/*"`
Expected: 4 passed.

- [ ] **Step 5: Wire the app**

In `App.axaml.cs`:

1. Replace the `_launch` field and its comment (around line 97-103) with:

```csharp
    // The server-side clients that outlive a window rebuild (MainWindowCoordinator can build a
    // second window over the same launch client) and own live transports. Torn down through one
    // holder on both teardown paths, after _home — never before, or a launch still in flight would
    // lose its transport mid-invoke.
    ServerClients? _serverClients;
```

2. Replace the composition at lines 328-336:

```csharp
        // One launch client and one work-context source for the app, not one per window the
        // coordinator builds — each owns a live transport, and only a held instance can be
        // disposed at teardown.
        var launch = new ServerLaunchClient(_config, profiles);
        var workContext = new ServerWorkContextSource(_config, profiles);
        var serverClients = new ServerClients(launch, workContext);
        _serverClients = serverClients;

        // One attach client per attempt, dialed at the daemon's own control socket; 80x24 is a
        // placeholder only — TerminalControl resizes its model to the real pane the moment it is
        // attached to the visual tree (WorkspaceView's own header comment).
        var attachFactory = CoreTerminalAttachClient.Factory(() => _daemonStore.SocketPath(service.DaemonName));
        WorkspaceViewModel BuildWorkspace(string agentId) => new(
            agentId, service, actions, attachFactory, () => new XtermTerminalSurface(80, 24, PtyDumpPath), TimeProvider.System, opener, permissions,
            workContext, requestSignIn: () => OpenSignInDialog(profiles, notifier), signInCompleted: serverClients.SignInCompleted);
```

(This also removes the placeholder `new ServerWorkContextSource(_config, profiles)` Task 9 added to the lambda.)

3. In `FinishSignInAsync`, after `if (graph.SignIn.Satisfied) _home?.NotifySignInCompleted();` add:

```csharp
            if (graph.SignIn.Satisfied) _serverClients?.NotifySignInCompleted();
```

4. Replace `DisposeLaunchClientAsync` (lines 1236-1247) with:

```csharp
    // Reached from both teardown paths; the holder memoizes the cleanup, so a second call awaits
    // the first rather than disposing anything again.
    async ValueTask DisposeServerClientsAsync() {
        if (_serverClients is null) return;
        await _serverClients.DisposeAsync().ConfigureAwait(false);
    }
```

5. Rename both call sites: `await DisposeLaunchClientAsync(); // after _home above — its only caller` (line 209) becomes `await DisposeServerClientsAsync(); // after _home above`, and `await DisposeLaunchClientAsync().ConfigureAwait(false);` inside `DisposeLifecycleAndServiceAsync` becomes `await DisposeServerClientsAsync().ConfigureAwait(false);`. Update the comment above `DisposeLifecycleAndServiceAsync` from "its launch client is torn down here" to "its server clients are torn down here".

- [ ] **Step 6: Build and run the whole app suite**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj --no-incremental 2>&1 | grep -E 'warning|error' || echo clean`
Expected: `clean`.

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: all pass, `AppStartupTests` included (they call `HandleStartupFailureAsync` and the shutdown helpers directly and are unaffected by the holder).

- [ ] **Step 7: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add src/Capacitor.App/Services/ServerClients.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/ServerClientsTests.cs
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Own the app's server clients as one set with one-shot cleanup" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

### Task 12: Change note and full verification

**Files:**
- Modify: `docs/CHANGES.md` (append an entry)

- [ ] **Step 1: Write the change note**

Append to `docs/CHANGES.md`:

```markdown
## Desktop shell: the work-context sidebar

**AI-2198** (spec: `docs/superpowers/specs/2026-09-03-ai2198-work-context-sidebar-design.md`) adds the
400px right column of the session workspace: the session's work item with its declared parts and
blockers, its pull requests, who is attached, and the session's facts. **It is built on the three
reads the server exposes over HTTP** — a session's assignments, a work item's topology, and the
session summary — and everything work-item detail the server serves only in-process to its own
dashboard (state, overview, per-part completion, links with URL and state, contributors) renders as
a SOON pill until a read endpoint exists. The card shows the session's primary work item; a
repo-less session has no work item at all, because the server requires a repository on one, and the
pane says so rather than showing an item without a key.

**The key is split from the server's label by convention, not contract.** The assignments route
labels a keyed item `"KEY — title"`; the pane takes the half before the separator as the key and the
topology item's title as the title. A change to that composition shows the whole label as the title
and drops the key chip — safe, but silent, which is why the dependency is named here.

**Reads are leased by session id.** The daemon puts `session_id` and `branch` on the status wire, and
each read carries a lease with its own cancellation; a switch starts the new session's read at once
and drops the old one's result, teardown cancels and awaits every outstanding lease, and all lease
bookkeeping runs on the UI thread. The reader fails closed: a 2xx with an unparseable body is a
failure, a final 401 on any route signs the pane out, a 403 is "not in plan" only with the exact plan
code. A section blip dims the pane and keeps the last good section; an authoritative empty answer
clears it; signed-out, not-in-plan and unknown-session clear every server-derived projection.

**The app's server clients are one set with one cleanup.** The work-context source holds its HTTP
client by lease so overlapping reads never see it disposed, retires it on sign-out, and is torn down
with the launch client through a holder that memoizes the cleanup, so both teardown paths reach it
and nothing is disposed twice. The window gains a minimum width equal to its default: 310 of rail plus
400 of pane must never squeeze the terminal column to nothing.
```

- [ ] **Step 2: Build every touched project without incremental caching**

Run:
```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj --no-incremental 2>&1 | grep -E 'warning|error' || echo cli-clean
dotnet build src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj --no-incremental 2>&1 | grep -E 'warning|error' || echo daemon-clean
dotnet build src/Capacitor.App/Capacitor.App.csproj --no-incremental 2>&1 | grep -E 'warning|error' || echo app-clean
```
Expected: three `*-clean` lines.

- [ ] **Step 3: Run the full solution test run**

Run: `TMPDIR=/private/tmp dotnet test --solution Capacitor.slnx`
Expected: green across every suite. A red `CodexConfigToml*` test on macOS is the known `/var` symlink case and not this change.

- [ ] **Step 4: AOT publish check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' || echo aot-clean`
Expected: `aot-clean`. An `IL2026`/`IL3050` naming a `WorkItems` type means a read bypassed the `JsonTypeInfo<T>` overloads or a root is unregistered.

- [ ] **Step 5: Commit**

```bash
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny add docs/CHANGES.md
/usr/bin/git -C /Users/alexey/dev/eventstore/kcap-cli/.claude/worktrees/graceful-wiggling-cherny commit -q -m "Record why the work-context sidebar is shaped the way it is" -m "Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>"
```

---

## Follow-ups outside this plan

Filed by the owner once the branch is up, not by a task here:

- kcap-server (Linear only): a work-item read endpoint for the desktop app — lifecycle, overview, key, links with URL and state, parts with a settled flag, contributors with avatars — behind the existing visibility service and plan gate.
- kcap-cli (GitHub): the `declare_work_breakdown` / `declare_work_relation` tool descriptions and the flows server instructions still claim a same-repository rule the server dropped.
- kcap-cli (GitHub): `ReviewerVendorLookup` compares the worktree's git root against the daemon's main-root paths and reports `no_repo_hosting_daemon` from every worktree session.
