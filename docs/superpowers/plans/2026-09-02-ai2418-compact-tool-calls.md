# Compact tool calls in the Chat tab (AI-2418) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fold consecutive completed tool calls in the desktop Chat tab into one expandable summary line, keep live calls visible, and mark a row that is waiting on a permission.

**Architecture:** A new `ToolGroupItem` row holds a run of `ToolCallItem`s; `ChatTabViewModel.Apply` appends calls to the trailing group and any prose closes it. Summary wording comes from a per-call `ToolCategory` fixed at creation, with Codex shell commands classified by `CodexCommandClassifier`, ported from the server into Core. The permission wire gains an optional `tool_use_id` the daemon reads from the hook body; the view-model recomputes "awaiting permission" marks from pending requests and running calls on every change.

**Tech Stack:** .NET 10, Avalonia 12 (headless tests), ReactiveUI, DynamicData, TUnit on Microsoft Testing Platform, System.Text.Json source-gen.

**Spec:** `docs/superpowers/specs/2026-09-02-ai2418-compact-tool-calls-design.md`

## Global Constraints

- Comments: scarce, no ticket ids, no change narration, no design coordinates (CLAUDE.md "Comments"). A ported file keeps its code verbatim but loses comment lines that violate this.
- Commit subjects: one imperative clause, at most 80 characters, no issue reference (none exists yet for AI-2418).
- Tests: TUnit. Every App test that touches the dispatcher runs under `RunOnUiAsync` and carries `[NotInParallel("AvaloniaSession")]`. Throwaway files come from `[TempDir] public required TempDir Tmp { get; init; }`.
- Never `Path.Combine(tmp.Path, …)`; use `Tmp.CreateFile(...)` / `Tmp.PathTo(...)`.
- Use `JsonElementExtensions` (`Str`, `Arr`, `Obj`, `IsObject`) instead of checking `ValueKind` by hand where one exists.
- Wire: `PermissionPendingDto` is JSON snake_case via `PermissionIpcJsonContext`; a missing member decodes to null. `FrameType` values are never touched.
- `dotnet build` does not surface AOT warnings; the final task runs `dotnet publish -c Release` and greps `IL[23][01][0-9]{2}`.
- Run one suite: `dotnet run --project test/<Suite>/<Suite>.csproj -- --treenode-filter '/*/*/<ClassName>/*'`.
- The server checkout for the port is `/Users/alexey/dev/temp/kcap-server`; copy, never edit it.

---

## File structure

| File | Responsibility |
|---|---|
| `src/Capacitor.Cli.Core/Harness/Codex/CodexCommandClassifier.cs` (create) | Verbatim port of the server's Codex `parse_command` classifier: `CodexCommandHint`, `CodexCommandClassifier.Classify`, internal `ShellTokenizer`. |
| `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexCommandClassifierTests.cs` (create) | The server's pure `Classify` tests, namespace changed. |
| `src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs` (modify) | `PermissionPendingDto.ToolUseId`, `PermissionWire.MaxToolUseIdBytes`. |
| `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionWireContractsTests.cs` (modify) | Round-trip, absence, worst-case frame with the new field. |
| `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` (modify) | `BuildPending` takes the hook body's `tool_use_id`, capped. |
| `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeInteractiveTests.cs` (modify) | Bounds and the end-to-end hook-body case. |
| `src/Capacitor.App/Services/IPermissionService.cs` (modify) | `PendingPermissionRequest.ToolUseId`. |
| `src/Capacitor.App/ViewModels/ToolSummary.cs` (create) | `ToolCategory`, the name map, `Categorize`, `Describe`. |
| `test/Capacitor.App.Tests.Unit/ToolSummaryTests.cs` (create) | Pure tests for the above. |
| `src/Capacitor.App/ViewModels/ChatItems.cs` (modify) | `ToolCallItem` gains `Category`, `IsAwaitingPermission`, `IsSettled`; new `ToolGroupItem`. |
| `test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs` (create) | Group behaviour in isolation. |
| `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (modify) | `_openGroup` grouping; `_requests`/`_marked`/`Reconcile()`. |
| `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs` (modify) | Grouping, folding, marking. |
| `test/Capacitor.App.Tests.Unit/FakePermissionService.cs` (modify) | `PermissionEntries.Entry(toolUseId:)`. |
| `src/Capacitor.App/Views/ChatTabView.axaml` (modify) | `ToolGroupItem` template. |
| `src/Capacitor.App/Views/ChatTabView.axaml.cs` (modify) | One-shot follow-tail hold on a summary click. |
| `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs` (modify) | Rendering, follow-tail, expansion, realization. |
| `docs/CHANGES.md` (modify) | The feature's section. |

---

### Task 1: Port `CodexCommandClassifier` into Core

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Codex/CodexCommandClassifier.cs`
- Create: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexCommandClassifierTests.cs`

**Interfaces:**
- Produces: `namespace Capacitor.Cli.Core.Harness.Codex` — `public sealed record CodexCommandHint(string Type, string? Path = null, string? Name = null, string? Query = null)`; `public static class CodexCommandClassifier { public static CodexCommandHint? Classify(string? cmd); }`. `Type` is `"read"`, `"search"` or `"list_files"`; null means unknown/unclassifiable.

- [ ] **Step 1: Copy the server's classifier and re-namespace it**

```bash
cp /Users/alexey/dev/temp/kcap-server/src/Capacitor.Server.Core/CodexCommandClassifier.cs \
   src/Capacitor.Cli.Core/Harness/Codex/CodexCommandClassifier.cs
```

Edit line 1 of the new file: `namespace Capacitor;` → `namespace Capacitor.Cli.Core.Harness.Codex;`. Then read the file top to bottom and delete comment lines (only comment lines) that name a ticket (`AI-713` and similar), narrate history ("no longer", "0.5.x rollouts no longer ship…" style), or point at a dashboard; keep comments that describe a trap or a rule the code enforces (the "any-unknown collapses the pipeline" rule, the outer-redirection detection reason, the `bash -lc` peel condition). Do not change code. `ShellTokenizer` stays `internal static`.

- [ ] **Step 2: Copy the pure tests**

```bash
mkdir -p test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex
cp /Users/alexey/dev/temp/kcap-server/test/Capacitor.Server.Tests.Chat/CodexCommandClassifierTests.cs \
   test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexCommandClassifierTests.cs
```

Edit the copy:
1. Replace the header (`using Capacitor.Server.TestHelpers.Helpers;` and the namespace line) with:

```csharp
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

/// Parity pin against the server's copy of the classifier: these cases must pass identically
/// on both until the server deletes its own.
public class CodexCommandClassifierTests {
```

   (delete the original `/// <summary> … </summary>` class comment and the `public class` line it precedes).
2. Delete the `BuildInv(...)` static helper and every test whose name starts with `EffectiveCodexHint_` or `EffectiveCodexPatch_` (they call server-only `CodexAccessors`). Keep: `Classifies_ReadCommands`, `Classifies_ListFilesCommands`, `Classifies_SearchCommands`, `Classifies_UnknownCommands_AsNull`, `UnwrapsBashLcWrapper`, `Pipeline_WithUnknownStage_FallsBackToUnknown`, `NullOrWhitespace_ReturnsNull`, `HelperStages_DoNotCollapsePipeline`, `DestructiveXargs_CollapsesPipeline`, `DisplayOnlyXargs_KeepsPrimaryClassification`, `Redirections_CollapsePipeline`, `BashLc_OuterSideEffects_CollapsePipeline`, `BashLc_CleanWrapper_StillClassifies`, `QuotedAngleBrackets_StayClassified`, `HelperOnlyPipelines_ReturnNull`.
3. Strip any `//` comment inside the kept tests that names a ticket or narrates history.

- [ ] **Step 3: Build and run the ported tests**

Run:
```bash
dotnet build test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj 2>&1 | tail -3
dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/CodexCommandClassifierTests/*'
```
Expected: build succeeds with 0 warnings from the new file; every kept test passes (the `[Arguments]` rows expand to many cases).

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.Cli.Core/Harness/Codex/CodexCommandClassifier.cs test/Capacitor.Cli.Core.Tests.Unit/Harness/Codex/CodexCommandClassifierTests.cs
git commit -m "Port the Codex shell command classifier into Core" -m "Verbatim copy of the server's port of Codex's parse_command; the server switches to this copy and deletes its own on the next submodule bump, so the code must not drift."
```

---

### Task 2: `tool_use_id` on the permission wire

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs:8-11` (the record) and the `PermissionWire` constants block
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionWireContractsTests.cs`

**Interfaces:**
- Produces: `PermissionPendingDto(..., string RequestedAt, string? ToolUseId = null)` serialized as `tool_use_id`; `PermissionWire.MaxToolUseIdBytes = 128`.

- [ ] **Step 1: Write the failing tests**

Add to `PermissionWireContractsTests`:

```csharp
    [Test]
    public async Task Pending_dto_carries_an_optional_tool_use_id_and_decodes_without_it() {
        var dto = new PermissionPendingDto("r1", "a1", "s1", "claude", "Bash", null, null, false, false, "t", "toolu_01ABC");
        var json = JsonSerializer.Serialize(dto, PermissionIpcJsonContext.Default.PermissionPendingDto);
        await Assert.That(json).Contains("\"tool_use_id\":\"toolu_01ABC\"");
        var back = JsonSerializer.Deserialize(json, PermissionIpcJsonContext.Default.PermissionPendingDto)!;
        await Assert.That(back.ToolUseId).IsEqualTo("toolu_01ABC");

        var older = JsonSerializer.Deserialize(
            """{"request_id":"r1","agent_id":"a1","session_id":"s1","vendor":"claude","tool_name":"Bash","requested_at":"t"}""",
            PermissionIpcJsonContext.Default.PermissionPendingDto)!;
        await Assert.That(older.ToolUseId).IsNull();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(older)).IsTrue();
    }
```

And in `Worst_case_pending_frame_writes_and_reads_under_the_codec_cap`, change the dto line to include a worst-case id:

```csharp
        var id   = new string('"', PermissionWire.MaxToolUseIdBytes);               // every byte escapes to \"
        var dto  = new PermissionPendingDto("r1", key, "s1", "claude", name, El(big), El(big), false, false, "t", id);
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj 2>&1 | grep -E 'error' | head -3`
Expected: compile errors (no 11-argument constructor, no `MaxToolUseIdBytes`).

- [ ] **Step 3: Add the field and the cap**

In `PermissionIpc.cs`, the record becomes:

```csharp
public sealed record PermissionPendingDto(
    string RequestId, string AgentId, string SessionId, string Vendor, string ToolName,
    JsonElement? ToolInput, JsonElement? Suggestions, bool ToolInputOmitted, bool SuggestionsOmitted,
    string RequestedAt, string? ToolUseId = null);
```

In `PermissionWire`, after `MaxAgentIdBytes`:

```csharp
    public const int MaxToolUseIdBytes = 128;
```

- [ ] **Step 4: Run the wire tests**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/PermissionWireContractsTests/*'`
Expected: all pass, including `Empty_object_decodes_to_nulls_and_false_flags` unchanged.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionWireContractsTests.cs
git commit -m "Carry an optional tool-use id on the pending permission frame"
```

---

### Task 3: The daemon forwards the hook body's `tool_use_id`

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` — the `BuildPending` call at ~line 624 and the method at ~line 694
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeInteractiveTests.cs`

**Interfaces:**
- Consumes: `PermissionPendingDto.ToolUseId`, `PermissionWire.MaxToolUseIdBytes` (Task 2).
- Produces: `LocalPermissionBridge.BuildPending(string requestId, string agentId, string sessionId, string vendor, string? toolName, JsonElement? toolInput, JsonElement? suggestions, string requestedAt, string? toolUseId = null)`.

- [ ] **Step 1: Write the failing tests**

Extend `Build_pending_bounds` with two lines at its end:

```csharp
        await Assert.That(LocalPermissionBridge.BuildPending("r", "a1", Session, "claude", "Bash", null, null, "t", "toolu_1")!.ToolUseId).IsEqualTo("toolu_1");
        await Assert.That(LocalPermissionBridge.BuildPending("r", "a1", Session, "claude", "Bash", null, null, "t", new string('i', PermissionWire.MaxToolUseIdBytes + 1))!.ToolUseId).IsNull();
```

Add an end-to-end test beside `App_claim_first_...`:

```csharp
    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task The_hook_bodys_tool_use_id_rides_the_pending_dto() {
        await using var h = new Harness();
        h.Server.AwaitScript = (_, ct) => new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);
        await h.StartAsync();

        var response = h.Client.PostAsync($"{h.Bridge.BaseUrl}/claude/permission-request",
            JsonContent.Create(new { session_id = Session, tool_name = "Bash", tool_input = new { command = "ls" }, tool_use_id = "toolu_01X", agent_id = "agent-1", cwd = "/repo" }));
        var pending = await h.WaitPendingAsync();
        await Assert.That(pending.ToolUseId).IsEqualTo("toolu_01X");

        await Assert.That(h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app")).IsTrue();
        await Assert.That(await Harness.BehaviorOf(await response)).IsEqualTo("allow");
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj 2>&1 | grep -E 'error' | head -3`
Expected: compile error on the 9-argument `BuildPending`.

- [ ] **Step 3: Read and forward the id**

In `LocalPermissionBridge.BuildPending`:

```csharp
    internal static PermissionPendingDto? BuildPending(
            string requestId, string agentId, string sessionId, string vendor, string? toolName,
            JsonElement? toolInput, JsonElement? suggestions, string requestedAt, string? toolUseId = null) {
        var name = toolName ?? "";
        if (Encoding.UTF8.GetByteCount(name) > PermissionWire.MaxToolNameBytes) return null;
        if (Encoding.UTF8.GetByteCount(agentId) > PermissionWire.MaxAgentIdBytes) return null;
        var (input, inputOmitted)   = Bound(toolInput);
        var (sugg,  suggOmitted)    = Bound(suggestions);
        // Over-cap is dropped, not refused: the id only decorates a chat row.
        var id = toolUseId is { Length: > 0 } t && Encoding.UTF8.GetByteCount(t) <= PermissionWire.MaxToolUseIdBytes ? t : null;
        return new PermissionPendingDto(requestId, agentId, sessionId, vendor, name, input, sugg, inputOmitted, suggOmitted, requestedAt, id);
```

At the call site (the `attributed is { } a ? BuildPending(...)` expression), pass the ninth argument:

```csharp
                var pending = attributed is { } a
                    ? BuildPending(Guid.NewGuid().ToString("N"), a.AgentId, canonicalSessionId!, vendor, toolName, toolInput, suggestions,
                        DateTimeOffset.UtcNow.ToString("O"), ToolUseIdOf(node))
                    : null;
```

and add beside `ExtractElement`:

```csharp
    static string? ToolUseIdOf(JsonNode node) =>
        node["tool_use_id"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;
```

- [ ] **Step 4: Run the bridge tests**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter '/*/*/LocalPermissionBridgeInteractiveTests/*'`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeInteractiveTests.cs
git commit -m "Forward the hook's tool_use_id on the pending permission"
```

---

### Task 4: `ToolSummary` — categories and wording

**Files:**
- Create: `src/Capacitor.App/ViewModels/ToolSummary.cs`
- Create: `test/Capacitor.App.Tests.Unit/ToolSummaryTests.cs`

**Interfaces:**
- Consumes: `CodexCommandClassifier.Classify` (Task 1), `JsonElementExtensions.Str/Arr`.
- Produces: `public enum ToolCategory { Read, Edit, Command, Search, WebSearch, Fetch, Skill, Agent, Plan, Question, Other }`; `public static class ToolSummary { internal static IReadOnlyDictionary<string, ToolCategory> Names; public static ToolCategory Categorize(string name, string? inputJson); public static string Describe(IEnumerable<ToolCategory> categories); }`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/ToolSummaryTests.cs`:

```csharp
using Capacitor.App.ViewModels;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Pure: no dispatcher, so no session constraint.
public class ToolSummaryTests {
    /// The spec's table, held here so the test pins the map in both directions.
    static readonly (ToolCategory Category, string[] Names)[] Rows = [
        (ToolCategory.Read,      ["Read", "NotebookRead", "read_file", "view_image"]),
        (ToolCategory.Edit,      ["Edit", "MultiEdit", "Write", "NotebookEdit", "apply_patch", "write_file"]),
        (ToolCategory.Command,   ["Bash", "BashOutput", "KillShell", "shell", "shell_command", "exec", "exec_command", "write_stdin", "local_shell", "container.exec"]),
        (ToolCategory.Search,    ["Grep", "Glob", "LS"]),
        (ToolCategory.WebSearch, ["WebSearch", "web_search"]),
        (ToolCategory.Fetch,     ["WebFetch"]),
        (ToolCategory.Skill,     ["Skill"]),
        (ToolCategory.Agent,     ["Task", "Agent", "TaskOutput", "TaskStop", "spawn_agent", "wait_agent", "send_input", "send_message", "resume_agent", "interrupt_agent", "close_agent", "list_agents"]),
        (ToolCategory.Plan,      ["TodoWrite", "update_plan"]),
        (ToolCategory.Question,  ["AskUserQuestion", "request_user_input"]),
    ];

    [Test]
    public async Task Every_declared_name_maps_to_its_row_case_insensitively_and_nothing_else_is_declared() {
        var expected = Rows.SelectMany(r => r.Names.Select(n => (n, r.Category))).ToDictionary(p => p.n, p => p.Category, StringComparer.Ordinal);
        foreach (var (name, category) in expected) {
            await Assert.That(ToolSummary.Categorize(name, null)).IsEqualTo(category);
            await Assert.That(ToolSummary.Categorize(name.ToUpperInvariant(), null)).IsEqualTo(category);
        }
        await Assert.That(ToolSummary.Names.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys)).IsTrue();
    }

    [Test]
    [Arguments("mcp__github__create_issue")]
    [Arguments("SomethingNew")]
    [Arguments("")]
    public async Task Unknown_and_mcp_names_are_other(string name) {
        await Assert.That(ToolSummary.Categorize(name, """{"x":1}""")).IsEqualTo(ToolCategory.Other);
    }

    [Test]
    public async Task Describe_uses_article_for_one_plural_for_many_first_appearance_order_and_lower_cases_after_the_first() {
        await Assert.That(ToolSummary.Describe([])).IsEqualTo("");
        await Assert.That(ToolSummary.Describe([ToolCategory.Command])).IsEqualTo("Ran a command");
        await Assert.That(ToolSummary.Describe([ToolCategory.Read, ToolCategory.Read])).IsEqualTo("Read files");
        await Assert.That(ToolSummary.Describe([ToolCategory.Read, ToolCategory.Command, ToolCategory.Read, ToolCategory.Edit]))
            .IsEqualTo("Read files, ran a command, edited a file");
        await Assert.That(ToolSummary.Describe([ToolCategory.Agent, ToolCategory.Skill, ToolCategory.Other, ToolCategory.Other]))
            .IsEqualTo("Ran an agent, loaded a skill, called tools");
        await Assert.That(ToolSummary.Describe([ToolCategory.Search, ToolCategory.Search, ToolCategory.WebSearch, ToolCategory.Fetch, ToolCategory.Fetch, ToolCategory.Plan, ToolCategory.Question]))
            .IsEqualTo("Searched files, searched the web, fetched pages, updated the plan, asked a question");
    }

    [Test]
    [Arguments("Read", """{"file_path":"/repo/.claude/skills/review/SKILL.md"}""", ToolCategory.Skill)]
    [Arguments("Read", """{"file_path":"/repo/SKILL.md.bak"}""", ToolCategory.Read)]
    [Arguments("exec_command", """{"cmd":"sed -n '1,40p' a.cs"}""", ToolCategory.Read)]
    [Arguments("exec_command", """{"cmd":"rg foo src"}""", ToolCategory.Search)]
    [Arguments("exec_command", """{"cmd":"ls src"}""", ToolCategory.Search)]
    [Arguments("exec_command", """{"cmd":"cat a && make"}""", ToolCategory.Command)]
    [Arguments("exec_command", """{"cmd":"cat skills/review/SKILL.md"}""", ToolCategory.Skill)]
    [Arguments("shell", """{"command":["rg","foo","src"]}""", ToolCategory.Search)]
    [Arguments("shell", """{"command":["bash","-lc","cat a.md"]}""", ToolCategory.Read)]
    [Arguments("Bash", """{"description":"List files"}""", ToolCategory.Command)]
    [Arguments("Bash", """{"command":"git status"}""", ToolCategory.Command)]
    [Arguments("Bash", "not json", ToolCategory.Command)]
    [Arguments("Bash", """["cat","a"]""", ToolCategory.Command)]
    [Arguments("exec", """{"input":"const r = 1;"}""", ToolCategory.Command)]
    [Arguments("spawn_agent", """{"task":"t"}""", ToolCategory.Agent)]
    public async Task Categorize_refines_reads_and_shell_commands_from_the_input(string name, string? input, ToolCategory expected) {
        await Assert.That(ToolSummary.Categorize(name, input)).IsEqualTo(expected);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head -3`
Expected: compile errors (`ToolSummary`, `ToolCategory` undefined).

- [ ] **Step 3: Implement `ToolSummary`**

`src/Capacitor.App/ViewModels/ToolSummary.cs`:

```csharp
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.App.ViewModels;

public enum ToolCategory { Read, Edit, Command, Search, WebSearch, Fetch, Skill, Agent, Plan, Question, Other }

/// What a group of settled tool calls says about itself. The name map keys on the name the
/// transcript carries (Codex's rollout says `shell`, its hook says `Bash`); a name in no row is
/// Other, so an unknown vendor tool still reads "Called a tool" rather than nothing.
public static class ToolSummary {
    internal static readonly IReadOnlyDictionary<string, ToolCategory> Names = new Dictionary<string, ToolCategory>(StringComparer.OrdinalIgnoreCase) {
        ["Read"] = ToolCategory.Read, ["NotebookRead"] = ToolCategory.Read, ["read_file"] = ToolCategory.Read, ["view_image"] = ToolCategory.Read,
        ["Edit"] = ToolCategory.Edit, ["MultiEdit"] = ToolCategory.Edit, ["Write"] = ToolCategory.Edit, ["NotebookEdit"] = ToolCategory.Edit,
        ["apply_patch"] = ToolCategory.Edit, ["write_file"] = ToolCategory.Edit,
        ["Bash"] = ToolCategory.Command, ["BashOutput"] = ToolCategory.Command, ["KillShell"] = ToolCategory.Command, ["shell"] = ToolCategory.Command,
        ["shell_command"] = ToolCategory.Command, ["exec"] = ToolCategory.Command, ["exec_command"] = ToolCategory.Command,
        ["write_stdin"] = ToolCategory.Command, ["local_shell"] = ToolCategory.Command, ["container.exec"] = ToolCategory.Command,
        ["Grep"] = ToolCategory.Search, ["Glob"] = ToolCategory.Search, ["LS"] = ToolCategory.Search,
        ["WebSearch"] = ToolCategory.WebSearch, ["web_search"] = ToolCategory.WebSearch,
        ["WebFetch"] = ToolCategory.Fetch,
        ["Skill"] = ToolCategory.Skill,
        ["Task"] = ToolCategory.Agent, ["Agent"] = ToolCategory.Agent, ["TaskOutput"] = ToolCategory.Agent, ["TaskStop"] = ToolCategory.Agent,
        ["spawn_agent"] = ToolCategory.Agent, ["wait_agent"] = ToolCategory.Agent, ["send_input"] = ToolCategory.Agent, ["send_message"] = ToolCategory.Agent,
        ["resume_agent"] = ToolCategory.Agent, ["interrupt_agent"] = ToolCategory.Agent, ["close_agent"] = ToolCategory.Agent, ["list_agents"] = ToolCategory.Agent,
        ["TodoWrite"] = ToolCategory.Plan, ["update_plan"] = ToolCategory.Plan,
        ["AskUserQuestion"] = ToolCategory.Question, ["request_user_input"] = ToolCategory.Question,
    };

    // Indexed by ToolCategory.
    static readonly (string One, string Many)[] Phrases = [
        ("Read a file", "Read files"),
        ("Edited a file", "Edited files"),
        ("Ran a command", "Ran commands"),
        ("Searched files", "Searched files"),
        ("Searched the web", "Searched the web"),
        ("Fetched a page", "Fetched pages"),
        ("Loaded a skill", "Loaded skills"),
        ("Ran an agent", "Ran agents"),
        ("Updated the plan", "Updated the plan"),
        ("Asked a question", "Asked questions"),
        ("Called a tool", "Called tools"),
    ];

    public static ToolCategory Categorize(string name, string? inputJson) {
        var category = Names.TryGetValue(name, out var known) ? known : ToolCategory.Other;
        if (category is not (ToolCategory.Read or ToolCategory.Command) || string.IsNullOrEmpty(inputJson)) return category;
        try {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (!root.IsObject) return category;
            if (category == ToolCategory.Read)
                return IsSkillFile(root.Str("file_path")) ? ToolCategory.Skill : category;
            var hint = CodexCommandClassifier.Classify(CommandText(root));
            return hint?.Type switch {
                "read"                   => IsSkillFile(hint.Name) ? ToolCategory.Skill : ToolCategory.Read,
                "search" or "list_files" => ToolCategory.Search,
                _                        => category,
            };
        } catch (JsonException) {
            return category;
        }
    }

    public static string Describe(IEnumerable<ToolCategory> categories) {
        var order = new List<ToolCategory>();
        var counts = new Dictionary<ToolCategory, int>();
        foreach (var c in categories) {
            if (!counts.TryAdd(c, 1)) counts[c]++;
            else order.Add(c);
        }
        var sb = new StringBuilder();
        foreach (var c in order) {
            var (one, many) = Phrases[(int)c];
            var phrase = counts[c] == 1 ? one : many;
            if (sb.Length == 0) sb.Append(phrase);
            else sb.Append(", ").Append(char.ToLowerInvariant(phrase[0])).Append(phrase, 1, phrase.Length - 1);
        }
        return sb.ToString();
    }

    static bool IsSkillFile(string? path) =>
        path is not null && (path == "SKILL.md" || path.EndsWith("/SKILL.md", StringComparison.Ordinal));

    /// `cmd` (unified exec), then `command` as a string, then `command` as an argv array — a
    /// `bash -lc <script>` array hands its script through, since the classifier only peels the
    /// wrapper when the script is one quoted token.
    static string? CommandText(JsonElement root) {
        if (root.Str("cmd") is { } cmd) return cmd;
        if (root.Str("command") is { } command) return command;
        if (root.Arr("command") is not { } argv) return null;
        var parts = argv.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList();
        if (parts.Count == 3 && parts[1] is "-lc" or "-c" && parts[0].EndsWith("sh", StringComparison.Ordinal)) return parts[2];
        return string.Join(' ', parts);
    }
}
```

`JsonElementExtensions.Str` returns `string?` and `Arr` returns `JsonElement?`, both null when the property is absent or of another kind.

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ToolSummaryTests/*'`
Expected: all pass. If `cat skills/review/SKILL.md` does not yield a `read` hint with `Name == "SKILL.md"`, check the classifier's read-case for `cat` (it is in the ported `Classifies_ReadCommands` rows: `cat README.md` → name `README.md`), then fix the test input, not the classifier.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ToolSummary.cs test/Capacitor.App.Tests.Unit/ToolSummaryTests.cs
git commit -m "Categorize tool calls and phrase a group summary"
```

---

### Task 5: `ToolCallItem` state and `ToolGroupItem`

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatItems.cs`
- Create: `test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs`

**Interfaces:**
- Consumes: `ToolCategory`, `ToolSummary.Describe` (Task 4).
- Produces: `ToolCallItem(string name, string detail, ToolCategory category = ToolCategory.Other)` with `Category`, `Outcome`, `IsAwaitingPermission`, `IsSettled`, `IsError`, `OutcomeGlyph`; `ToolGroupItem` with `Calls`, `LiveCalls`, `VisibleCalls`, `IsExpanded`, `ToggleCommand`, `Toggle()`, `Summary`, `HasSummary`, `HasFailure`, `Add(ToolCallItem)`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs`:

```csharp
using Capacitor.App.ViewModels;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The group in isolation: folding, summary, failure, and the visible-list swap. Under the
/// session constraint because ToggleCommand is a ReactiveCommand.
public class ToolGroupItemTests {
    static ToolCallItem Call(string name, ToolCategory category) => new(name, "", category);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Live_calls_show_until_they_settle_and_the_summary_appears_with_the_first_settlement() {
        await RunOnUiAsync(async () => {
            var group = new ToolGroupItem();
            var a = Call("Bash", ToolCategory.Command);
            var b = Call("Read", ToolCategory.Read);
            group.Add(a);
            group.Add(b);
            await Assert.That(group.HasSummary).IsFalse();
            await Assert.That(group.Summary).IsEqualTo("");
            await Assert.That(group.LiveCalls).IsEquivalentTo(new[] { a, b }, CollectionOrdering.Matching);
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { a, b }, CollectionOrdering.Matching);

            b.Outcome = ToolOutcome.Done;
            await Assert.That(group.HasSummary).IsTrue();
            await Assert.That(group.Summary).IsEqualTo("Read a file");
            await Assert.That(group.LiveCalls).IsEquivalentTo(new[] { a });
            await Assert.That(group.Calls).IsEquivalentTo(new[] { a, b }, CollectionOrdering.Matching);

            a.Outcome = ToolOutcome.Error;
            await Assert.That(group.LiveCalls).IsEmpty();
            await Assert.That(group.Summary).IsEqualTo("Ran a command, read a file");
            await Assert.That(group.HasFailure).IsTrue();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Toggle_swaps_the_visible_list_between_live_and_every_call() {
        await RunOnUiAsync(async () => {
            var group = new ToolGroupItem();
            var settled = Call("Bash", ToolCategory.Command);
            var live = Call("Read", ToolCategory.Read);
            group.Add(settled);
            group.Add(live);
            settled.Outcome = ToolOutcome.Done;
            var raised = new List<string?>();
            group.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            await Assert.That(group.IsExpanded).IsFalse();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { live });

            group.Toggle();
            await Assert.That(group.IsExpanded).IsTrue();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { settled, live }, CollectionOrdering.Matching);
            await Assert.That(raised).Contains(nameof(ToolGroupItem.VisibleCalls));

            group.ToggleCommand.Execute().Subscribe();
            await Assert.That(group.IsExpanded).IsFalse();
            await Assert.That(group.VisibleCalls).IsEquivalentTo(new[] { live });
        });
    }

    [Test]
    public async Task A_call_glyph_shows_the_question_mark_only_while_running_and_awaiting() {
        var call = Call("Bash", ToolCategory.Command);
        await Assert.That(call.OutcomeGlyph).IsEqualTo("");
        call.IsAwaitingPermission = true;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("?");
        await Assert.That(call.IsSettled).IsFalse();
        call.Outcome = ToolOutcome.Done;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");
        await Assert.That(call.IsSettled).IsTrue();
        call.IsAwaitingPermission = false;
        await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj 2>&1 | grep -E 'error' | head -3`
Expected: compile errors (`ToolGroupItem`, the 3-argument `ToolCallItem`).

- [ ] **Step 3: Implement**

Replace the `ToolCallItem` class in `ChatItems.cs` and add `ToolGroupItem` after it; add the usings the file needs:

```csharp
using System.ComponentModel;
using System.Reactive;
using Avalonia.Collections;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One row of the Chat tab. Five shapes, matched by DataTemplates on the concrete type.
public abstract class ChatItemViewModel : ReactiveObject { }

public sealed class UserTurnItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

public sealed class AssistantTextItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

/// System-attributed text — a finished background task, a reconnect note — never anyone's speech.
public sealed class SystemNoteItem(string text) : ChatItemViewModel {
    public string Text { get; } = text;
}

public enum ToolOutcome { Running, Done, Error }

public sealed class ToolCallItem(string name, string detail, ToolCategory category = ToolCategory.Other) : ChatItemViewModel {
    public string Name { get; } = name;
    public string Detail { get; } = detail;
    public ToolCategory Category { get; } = category;

    ToolOutcome _outcome;
    /// Flipped in place when the matching tool_result arrives; a result is terminal.
    public ToolOutcome Outcome {
        get => _outcome;
        set {
            if (_outcome == value) return;
            _outcome = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(OutcomeGlyph));
            this.RaisePropertyChanged(nameof(IsError));
            this.RaisePropertyChanged(nameof(IsSettled));
        }
    }

    bool _isAwaitingPermission;
    /// Owned by the permission cache, not the transcript: the two arrive in either order.
    public bool IsAwaitingPermission {
        get => _isAwaitingPermission;
        set {
            if (_isAwaitingPermission == value) return;
            _isAwaitingPermission = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(OutcomeGlyph));
        }
    }

    public bool IsSettled => _outcome != ToolOutcome.Running;
    public bool IsError => _outcome == ToolOutcome.Error;
    public string OutcomeGlyph => _outcome switch {
        ToolOutcome.Done  => "✓",
        ToolOutcome.Error => "✕",
        _                 => _isAwaitingPermission ? "?" : "",
    };
}

/// A run of consecutive tool calls. Settled calls fold into Summary; live ones stay listed.
/// VisibleCalls is the one list the view binds, swapped on toggle so a folded group holds no
/// containers for its settled rows.
public sealed class ToolGroupItem : ChatItemViewModel {
    readonly AvaloniaList<ToolCallItem> _calls = new();
    readonly AvaloniaList<ToolCallItem> _live = new();

    public IAvaloniaReadOnlyList<ToolCallItem> Calls => _calls;
    public IAvaloniaReadOnlyList<ToolCallItem> LiveCalls => _live;
    public IAvaloniaReadOnlyList<ToolCallItem> VisibleCalls => _isExpanded ? _calls : _live;

    bool _isExpanded;
    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(VisibleCalls));
        }
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    string _summary = "";
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    bool _hasSummary;
    public bool HasSummary { get => _hasSummary; private set => this.RaiseAndSetIfChanged(ref _hasSummary, value); }

    bool _hasFailure;
    public bool HasFailure { get => _hasFailure; private set => this.RaiseAndSetIfChanged(ref _hasFailure, value); }

    public ToolGroupItem() {
        ToggleCommand = ReactiveCommand.Create(Toggle);
    }

    public void Toggle() => IsExpanded = !IsExpanded;

    public void Add(ToolCallItem call) {
        _calls.Add(call);
        if (call.IsSettled) { Recompute(); return; }
        _live.Add(call);
        call.PropertyChanged += OnCallChanged;
    }

    void OnCallChanged(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName != nameof(ToolCallItem.Outcome) || sender is not ToolCallItem call || !call.IsSettled) return;
        call.PropertyChanged -= OnCallChanged;
        _live.Remove(call);
        Recompute();
    }

    void Recompute() {
        var settled = _calls.Where(c => c.IsSettled).ToList();
        Summary = ToolSummary.Describe(settled.Select(c => c.Category));
        HasFailure = settled.Any(c => c.IsError);
        HasSummary = settled.Count > 0;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ToolGroupItemTests/*'`
Expected: all three pass. The existing `ChatTabViewModelTests` still compile (the `ToolCallItem` ctor's third argument defaults) but three of them will fail once Task 6 lands; do not touch them here.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ChatItems.cs test/Capacitor.App.Tests.Unit/ToolGroupItemTests.cs
git commit -m "Add the tool-call group row and the awaiting-permission state"
```

---

### Task 6: Group consecutive calls in `Apply`

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` — fields near line 37, `SwitchPath` (~line 253), `Apply` (~line 292)
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`

**Interfaces:**
- Consumes: `ToolGroupItem`, `ToolSummary.Categorize`.
- Produces: `Items` holds `ToolGroupItem` rows in place of `ToolCallItem` rows; `_pendingTools` unchanged.

- [ ] **Step 1: Update the existing tests and add the grouping tests**

In `ChatTabViewModelTests`, add fixtures beside the existing consts:

```csharp
    const string ReadCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/src/a.cs"}}]}}""";
    const string NoteLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll good.\n</result>\n</task-notification>"}}""";

    static ToolGroupItem Group(ChatTabViewModel chat, int index) => (ToolGroupItem)chat.Items[index];
```

Edit `Waits_until_a_path_then_renders_the_initial_load_in_file_order`: the expected type names become `nameof(ToolGroupItem)` for the third item and the two detail/outcome assertions read through the group:

```csharp
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(UserTurnItem), nameof(AssistantTextItem), nameof(ToolGroupItem) }, CollectionOrdering.Matching);
            await Assert.That(((UserTurnItem)h.Chat.Items[0]).Text).IsEqualTo("hello");
            await Assert.That(Group(h.Chat, 2).Calls[0].Detail).IsEqualTo("ls -la");
            await Assert.That(Group(h.Chat, 2).Calls[0].Outcome).IsEqualTo(ToolOutcome.Running);
            await Assert.That(Group(h.Chat, 2).Calls[0].Category).IsEqualTo(ToolCategory.Command);
```

Edit `Tool_results_flip_their_call_in_place_and_unmatched_results_are_ignored` body:

```csharp
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ToolResultLine]);
            await h.PushAsync(Dto(path));
            var call = Group(h.Chat, 0).Calls.Single();
            await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(call.OutcomeGlyph).IsEqualTo("✓");

            File.AppendAllText(path, ToolCallLine + "\n" + ToolErrorLine + "\n" + ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);
            await Assert.That(Group(h.Chat, 0).Calls[1].Outcome).IsEqualTo(ToolOutcome.Error);
            await Assert.That(Group(h.Chat, 0).Calls[1].IsError).IsTrue();
            await h.TeardownAsync();
```

Edit `Length_regression_resets_items_missing_recovers_and_failed_keeps_items`: `IsTypeOf<ToolCallItem>()` → `IsTypeOf<ToolGroupItem>()`.

Add:

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Consecutive_calls_across_reads_share_a_group_and_any_prose_closes_it() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);

            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls.Select(c => c.Name)).IsEquivalentTo(new[] { "Bash", "Read" }, CollectionOrdering.Matching);

            File.AppendAllText(path, AssistantLine + "\n" + ToolCallLine.Replace("t1", "t3") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(
                new[] { nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(ToolGroupItem) }, CollectionOrdering.Matching);
            await Assert.That(Group(h.Chat, 2).Calls).Count().IsEqualTo(1);

            File.AppendAllText(path, NoteLine + "\n" + ToolCallLine.Replace("t1", "t4") + "\n" + UserLine + "\n" + ToolCallLine.Replace("t1", "t5") + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items.Select(i => i.GetType().Name)).IsEquivalentTo(new[] {
                nameof(ToolGroupItem), nameof(AssistantTextItem), nameof(ToolGroupItem),
                nameof(SystemNoteItem), nameof(ToolGroupItem), nameof(UserTurnItem), nameof(ToolGroupItem),
            }, CollectionOrdering.Matching);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_result_folds_its_call_and_the_summary_follows_the_settled_calls() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));
            var group = Group(h.Chat, 0);
            await Assert.That(group.HasSummary).IsFalse();
            await Assert.That(group.LiveCalls).Count().IsEqualTo(2);

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls.Select(c => c.Name)).IsEquivalentTo(new[] { "Read" });
            await Assert.That(group.Summary).IsEqualTo("Ran a command");
            await Assert.That(group.HasSummary).IsTrue();
            await Assert.That(group.HasFailure).IsFalse();

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "unknown") + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls).Count().IsEqualTo(1);

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "t2") + "\n");
            await h.TickAsync();
            await Assert.That(group.LiveCalls).IsEmpty();
            await Assert.That(group.Summary).IsEqualTo("Ran a command, read a file");
            await Assert.That(group.HasFailure).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_reset_and_a_path_switch_start_a_fresh_group_for_later_calls() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine]);
            await h.PushAsync(Dto(path));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(2);

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);

            var other = Tmp.CreateFile("o.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(other));
            await Assert.That(h.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(1);
            File.AppendAllText(other, ReadCallLine + "\n");
            await h.TickAsync();
            await Assert.That(Group(h.Chat, 0).Calls).Count().IsEqualTo(2);
            await h.TeardownAsync();
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ChatTabViewModelTests/*'`
Expected: the edited and new tests fail with `InvalidCastException` (items are still `ToolCallItem`).

- [ ] **Step 3: Group in `Apply`**

In `ChatTabViewModel`, add a field beside `_pendingTools`:

```csharp
    ToolGroupItem? _openGroup;
```

In `SwitchPath`, after `_pendingTools.Clear();` add `_openGroup = null;`. In `Apply`'s `TailStatus.Reset` case, after `_pendingTools.Clear();` add `_openGroup = null;`. Replace the envelope loop:

```csharp
        var fresh = new List<ChatItemViewModel>();
        foreach (var e in envelopes) {
            switch (e.Kind) {
                case AcpEventKind.UserMessage:
                    _openGroup = null;
                    fresh.Add(new UserTurnItem(e.Text ?? ""));
                    break;
                case AcpEventKind.AssistantText:
                    _openGroup = null;
                    fresh.Add(new AssistantTextItem(e.Text ?? ""));
                    break;
                case AcpEventKind.SystemNote:
                    _openGroup = null;
                    fresh.Add(new SystemNoteItem(e.Text ?? ""));
                    break;
                case AcpEventKind.ToolCall: {
                    var name = e.ToolName ?? "tool";
                    var item = new ToolCallItem(name, ToolDetail.From(e.ToolInputJson, _root), ToolSummary.Categorize(name, e.ToolInputJson));
                    if (e.ToolCallId is { } id) _pendingTools[id] = item;
                    if (_openGroup is null) {
                        _openGroup = new ToolGroupItem();
                        fresh.Add(_openGroup);
                    }
                    _openGroup.Add(item);
                    break;
                }
                case AcpEventKind.ToolResult:
                    if (e.ToolCallId is { } resultId && _pendingTools.Remove(resultId, out var call))
                        call.Outcome = e.ToolIsError ? ToolOutcome.Error : ToolOutcome.Done;
                    break;
            }
        }
        if (fresh.Count > 0) _items.AddRange(fresh);
```

- [ ] **Step 4: Run the view-model tests**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ChatTabViewModelTests/*'`
Expected: all pass. `ChatTabViewSmokeTests` will now fail on the three tool-row tests; they are rewritten in Task 8.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs
git commit -m "Fold consecutive tool calls into one group row in the chat"
```

---

### Task 7: Mark the row a pending permission is waiting on

**Files:**
- Modify: `src/Capacitor.App/Services/IPermissionService.cs` (`PendingPermissionRequest`)
- Modify: `test/Capacitor.App.Tests.Unit/FakePermissionService.cs` (`PermissionEntries.Entry`)
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` — ctor after `cards.Subscribe()`, `Apply`, `SwitchPath`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`

**Interfaces:**
- Consumes: `PermissionPendingDto.ToolUseId` (Task 2), `ToolCallItem.IsAwaitingPermission` (Task 5).
- Produces: `PendingPermissionRequest.ToolUseId`; `PermissionEntries.Entry(..., string? toolUseId = null)`.

- [ ] **Step 1: Extend the fixture and write the failing tests**

In `FakePermissionService.cs`, `PermissionEntries.Entry` gains a trailing parameter and passes it to the DTO:

```csharp
    public static PendingPermissionRequest Entry(
            string requestId = "r1", string agentId = "a1", string vendor = "claude", string toolName = "Bash",
            string? toolInputJson = """{"command":"ls"}""", bool omitted = false, string requestedAt = "2026-08-28T10:00:00.0000000+00:00",
            string? toolUseId = null) {
        System.Text.Json.JsonElement? input = null;
        if (toolInputJson is not null) { using var d = System.Text.Json.JsonDocument.Parse(toolInputJson); input = d.RootElement.Clone(); }
        return new PendingPermissionRequest(new PermissionPendingDto(requestId, agentId, "s1", vendor, toolName, input, null, omitted, false, requestedAt, toolUseId));
    }
```

Add to `ChatTabViewModelTests` (rows are addressed by position through the `Group(...)` helper from Task 6):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_with_an_id_marks_its_row_in_either_order_and_clears_on_resolve() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine, ReadCallLine]);
            await h.PushAsync(Dto(path));
            var bash = Group(h.Chat, 0).Calls[0];
            var read = Group(h.Chat, 0).Calls[1];
            await WaitUntilAsync(() => bash.IsAwaitingPermission, what: "the card-first mark");
            await Assert.That(bash.OutcomeGlyph).IsEqualTo("?");
            await Assert.That(read.IsAwaitingPermission).IsFalse();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => !bash.IsAwaitingPermission, what: "cleared on resolve");

            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t2"));
            await WaitUntilAsync(() => read.IsAwaitingPermission, what: "the row-first mark");
            await Assert.That(bash.IsAwaitingPermission).IsFalse();

            h.Permissions.Add(PermissionEntries.Entry("r3", "a1", toolUseId: "nope"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 2, what: "the unmatched card");
            await Assert.That(bash.IsAwaitingPermission).IsFalse();
            await Assert.That(read.IsAwaitingPermission).IsTrue();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Two_requests_on_one_row_keep_the_mark_until_both_go_and_a_settled_row_is_cleared() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            var bash = Group(h.Chat, 0).Calls[0];
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => bash.IsAwaitingPermission, what: "marked");

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "one card left");
            await Assert.That(bash.IsAwaitingPermission).IsTrue();

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(bash.Outcome).IsEqualTo(ToolOutcome.Done);
            await Assert.That(bash.IsAwaitingPermission).IsFalse();
            await Assert.That(h.Chat.PendingCards).Count().IsEqualTo(1);
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_request_without_an_id_marks_the_sole_running_call_and_abstains_on_two() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", vendor: "codex"));
            var path = Tmp.CreateFile("t.jsonl", [ToolCallLine]);
            await h.PushAsync(Dto(path));
            var first = Group(h.Chat, 0).Calls[0];
            await WaitUntilAsync(() => first.IsAwaitingPermission, what: "the sole running call, row after card");

            File.AppendAllText(path, ReadCallLine + "\n");
            await h.TickAsync();
            var second = Group(h.Chat, 0).Calls[1];
            await Assert.That(first.IsAwaitingPermission).IsFalse();
            await Assert.That(second.IsAwaitingPermission).IsFalse();

            File.AppendAllText(path, ToolResultLine + "\n");
            await h.TickAsync();
            await Assert.That(first.IsAwaitingPermission).IsFalse();
            await Assert.That(second.IsAwaitingPermission).IsTrue();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => !second.IsAwaitingPermission, what: "cleared on resolve");

            File.AppendAllText(path, ToolErrorLine.Replace("t1", "t2") + "\n");
            await h.TickAsync();
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", vendor: "codex"));
            await WaitUntilAsync(() => h.Chat.PendingCards.Count == 1, what: "a card with nothing running");
            await Assert.That(second.IsAwaitingPermission).IsFalse();
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_request_marks_the_rebuilt_row_after_a_reset_and_a_path_switch() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var path = Tmp.CreateFile("t.jsonl", [UserLine, ToolCallLine]);
            await h.PushAsync(Dto(path));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", vendor: "codex"));
            await WaitUntilAsync(() => Group(h.Chat, 1).Calls[0].IsAwaitingPermission, what: "marked before the reset");

            File.WriteAllLines(path, [ToolCallLine]);
            await h.TickAsync();
            await Assert.That(Group(h.Chat, 0).Calls[0].IsAwaitingPermission).IsTrue();

            var other = Tmp.CreateFile("o.jsonl", [ToolCallLine.Replace("t1", "t9")]);
            await h.PushAsync(Dto(other));
            await Assert.That(Group(h.Chat, 0).Calls[0].IsAwaitingPermission).IsTrue();

            h.Permissions.Remove("r2");
            await WaitUntilAsync(() => !Group(h.Chat, 0).Calls[0].IsAwaitingPermission, what: "only the id-less request fitted t9");
            await h.TeardownAsync();
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ChatTabViewModelTests/*'`
Expected: the four new tests time out in `WaitUntilAsync` (nothing ever marks a row) or fail on a false flag.

- [ ] **Step 3: Implement the marking**

`IPermissionService.cs`, in `PendingPermissionRequest`, after `ToolInputOmitted`:

```csharp
    public string? ToolUseId => Dto.ToolUseId;
```

`ChatTabViewModel.cs`, fields beside `_pendingTools`:

```csharp
    readonly Dictionary<string, PendingPermissionRequest> _requests = new(StringComparer.Ordinal);
    readonly HashSet<ToolCallItem> _marked = new(ReferenceEqualityComparer.Instance);
```

In the ctor, after `cards.Subscribe().DisposeWith(_disposables);`:

```csharp
        permissions.Pending
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Filter(p => p.AgentId == agentId)
            .Subscribe(changes => {
                foreach (var change in changes) {
                    switch (change.Reason) {
                        case ChangeReason.Add or ChangeReason.Update: _requests[change.Key] = change.Current; break;
                        case ChangeReason.Remove: _requests.Remove(change.Key); break;
                    }
                }
                Reconcile();
            })
            .DisposeWith(_disposables);
```

In `SwitchPath`, after `_openGroup = null;`: `_marked.Clear();`. In `Apply`'s `Reset` case likewise `_marked.Clear();`. At the end of `Apply`, after `if (fresh.Count > 0) _items.AddRange(fresh);` add `Reconcile();`. Add the method after `Apply`:

```csharp
    /// A row is marked iff some pending request targets it: by tool-use id when the request has
    /// one, else the sole running call. Recomputed whole on every change to either set and diffed
    /// against the last marks, because a settled call has already left _pendingTools by the time
    /// its outcome flips, so the running set alone could never reach it to clear it.
    void Reconcile() {
        var targets = new HashSet<ToolCallItem>(ReferenceEqualityComparer.Instance);
        var sole = _pendingTools.Count == 1 ? _pendingTools.Values.First() : null;
        foreach (var request in _requests.Values) {
            if (request.ToolUseId is { } id) {
                if (_pendingTools.TryGetValue(id, out var call)) targets.Add(call);
            } else if (sole is not null) {
                targets.Add(sole);
            }
        }
        foreach (var call in _marked) if (!targets.Contains(call)) call.IsAwaitingPermission = false;
        foreach (var call in targets) call.IsAwaitingPermission = true;
        _marked.Clear();
        _marked.UnionWith(targets);
    }
```

`ReferenceEqualityComparer` is `System.Collections.Generic.ReferenceEqualityComparer`; `ChangeReason` is DynamicData's (already imported).

- [ ] **Step 4: Run the view-model tests**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ChatTabViewModelTests/*'`
Expected: all pass. If a `WaitUntilAsync` on a mark times out while the card count is right, the change-set subscription is missing `ObserveOn` or `Reconcile()` is not called after `Apply`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/IPermissionService.cs src/Capacitor.App/ViewModels/ChatTabViewModel.cs test/Capacitor.App.Tests.Unit/FakePermissionService.cs test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs
git commit -m "Mark the chat row a pending permission is waiting on"
```

---

### Task 8: Render the group, hold the tail on expansion

**Files:**
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml` — the `DataTemplates` block
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs`

**Interfaces:**
- Consumes: `ToolGroupItem` (`HasSummary`, `Summary`, `HasFailure`, `IsExpanded`, `ToggleCommand`, `VisibleCalls`, `Toggle()`), `PermissionEntries.Entry(toolUseId:)`.

- [ ] **Step 1: Rewrite the three tool-row smoke tests and add the new ones**

Add fixtures and helpers to `ChatTabViewSmokeTests`:

```csharp
    const string ReadCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/src/a.cs"}}]}}""";
    const string ReadResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":"ok"}]}}""";

    static string CallLine(int n) => ToolCallLine.Replace("\"t1\"", $"\"t{n}\"");
    static string ResultLine(int n) => ToolResultLine.Replace("\"t1\"", $"\"t{n}\"");

    static List<StackPanel> ToolRows(ChatTabView view) => view.GetVisualDescendants().OfType<StackPanel>()
        .Where(p => p.Orientation == Orientation.Horizontal && p.DataContext is ToolCallItem).ToList();
    static Button Summary(ChatTabView view) => view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("toolSummary"));
    static ToolGroupItem OnlyGroup(Host host) => (ToolGroupItem)host.Chat.Items.Single();

    static void Click(Host host, Control target) {
        var origin = target.TranslatePoint(new Point(2, 2), host.Window)!.Value;
        host.Window.MouseDown(origin, MouseButton.Left);
        host.Window.MouseUp(origin, MouseButton.Left);
        host.Settle();
    }
```

Add to `Host`:

```csharp
        public async Task AppendLinesAndTickAsync(string path, params string[] lines) {
            File.AppendAllLines(path, lines);
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
```

Rewrite `A_paired_tool_row_paints_its_glyph_with_the_outcome_brush` so it expands the group first:

```csharp
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("tools.jsonl",
                [ToolCallLine, ToolResultLine, ToolCallLine.Replace("t1", "t2"), ToolErrorLine.Replace("t1", "t2")]));
            OnlyGroup(host).Toggle();
            host.Settle();

            await Assert.That(OnlyGroup(host).Calls.Select(i => i.Outcome))
                .IsEquivalentTo([ToolOutcome.Done, ToolOutcome.Error], CollectionOrdering.Matching);
            var glyphs = host.View.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.DataContext is ToolCallItem && t.Text is "✓" or "✕").ToList();
```

(the rest of that test unchanged).

Rewrite `Tool_rows_stack_densely_and_keep_their_distance_from_text` so it expands the group after loading and asserts on `ToolRows(host.View)`:

```csharp
            await host.LoadAsync(Tmp.CreateFile("rows.jsonl",
                [ToolCallLine, ToolResultLine, ToolCallLine.Replace("t1", "t2"), ToolResultLine.Replace("t1", "t2"), AssistantLinkLine]));
            ((ToolGroupItem)host.Chat.Items[0]).Toggle();
            host.Settle();
            var rows = ToolRows(host.View);
```

Add:

```csharp
    /// Pins the fold: settled calls become one summary line, live calls stay as rows, and a click
    /// on the summary reveals every call and hides them again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_group_folds_settled_calls_into_a_summary_and_expands_on_click() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("fold.jsonl", [ToolCallLine, ToolResultLine, ReadCallLine, ReadResultLine, CallLine(3)]));

            await Assert.That(host.Chat.Items).Count().IsEqualTo(1);
            var summary = Summary(host.View);
            await Assert.That(summary.IsVisible).IsTrue();
            await Assert.That(summary.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)).Contains("Ran a command, read a file");
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await Assert.That(((ToolCallItem)ToolRows(host.View)[0].DataContext!).Outcome).IsEqualTo(ToolOutcome.Running);

            Click(host, summary);
            await Assert.That(OnlyGroup(host).IsExpanded).IsTrue();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(3);

            Click(host, summary);
            await Assert.That(OnlyGroup(host).IsExpanded).IsFalse();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await host.CloseAsync();
        });
    }

    /// A group with nothing settled has no summary line at all.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_group_of_only_live_calls_shows_rows_and_no_summary() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("live.jsonl", [ToolCallLine, ReadCallLine]));
            await Assert.That(Summary(host.View).IsVisible).IsFalse();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(2);
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_call_inside_a_folded_group_shows_the_danger_cross_on_the_summary() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("fail.jsonl", [ToolCallLine, ToolErrorLine]));
            var cross = Summary(host.View).GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "✕");
            await Assert.That(cross.IsVisible).IsTrue();
            await Assert.That(cross.Foreground).IsSameReferenceAs(Avalonia.Application.Current!.FindResource("KcapDangerBrush"));
            await host.CloseAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_awaiting_row_paints_the_question_glyph_with_the_accent_brush() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            await host.LoadAsync(Tmp.CreateFile("ask.jsonl", [ToolCallLine]));
            host.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolUseId: "t1"));
            await WaitUntilAsync(() => OnlyGroup(host).Calls[0].IsAwaitingPermission, what: "the mark");
            host.Settle();
            var glyph = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.DataContext is ToolCallItem && t.Text == "?");
            await Assert.That(glyph.Foreground).IsSameReferenceAs(Brush(isError: false));
            await host.CloseAsync();
        });
    }

    /// The inner lists mutate without an outer collection notification; follow-tail still reads
    /// the extent change.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Follow_tail_tracks_inner_group_growth_and_folding_and_leaves_a_scrolled_up_reader_alone() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var path = Tmp.CreateFile("inner.jsonl", [.. Enumerable.Repeat(UserLine, 60), CallLine(1)]);
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendLinesAndTickAsync(path, CallLine(2), CallLine(3));
            await Assert.That(host.Chat.Items).Count().IsEqualTo(61);
            await Assert.That(host.AtBottom()).IsTrue();

            await host.AppendLinesAndTickAsync(path, ResultLine(1));
            await Assert.That(host.AtBottom()).IsTrue();

            host.Scroll.Offset = new Vector(0, 0);
            host.Window.UpdateLayout();
            await host.AppendLinesAndTickAsync(path, CallLine(4), ResultLine(2));
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(0);
            await host.CloseAsync();
        });
    }

    /// Expanding keeps the viewport: the hold is one-shot, so a reader who returns to the bottom is
    /// followed again on the next append.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Expanding_the_trailing_group_keeps_the_offset_and_the_hold_is_one_shot() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>(Enumerable.Repeat(UserLine, 60));
            for (var i = 1; i <= 30; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            var path = Tmp.CreateFile("expand.jsonl", lines.ToArray());
            await host.LoadAsync(path);
            await Assert.That(host.AtBottom()).IsTrue();
            var before = host.Scroll.Offset.Y;

            Click(host, Summary(host.View));
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(((ToolGroupItem)host.Chat.Items[^1]).IsExpanded).IsTrue();
            await Assert.That(host.Scroll.Offset.Y).IsEqualTo(before);
            await Assert.That(host.AtBottom()).IsFalse();

            host.Scroll.ScrollToEnd();
            host.Window.UpdateLayout();
            await host.AppendAndTickAsync(path, 5);
            Dispatcher.UIThread.RunJobs();
            host.Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(host.AtBottom()).IsTrue();
            await host.CloseAsync();
        });
    }

    /// Expansion realizes every row; folding releases them again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_thousand_call_group_realizes_on_expansion_and_releases_on_fold() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var lines = new List<string>();
            for (var i = 1; i <= 1000; i++) { lines.Add(CallLine(i)); lines.Add(ResultLine(i)); }
            lines.Add(CallLine(1001));
            await host.LoadAsync(Tmp.CreateFile("thousand.jsonl", lines.ToArray()));
            var items = host.View.FindControl<ItemsControl>("ChatItems")!;

            await Assert.That(host.Chat.Items).Count().IsEqualTo(1);
            await Assert.That(items.GetRealizedContainers().Count()).IsEqualTo(1);
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);

            OnlyGroup(host).Toggle();
            host.Settle();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1001);
            await Assert.That(items.GetRealizedContainers().Count()).IsEqualTo(1);

            OnlyGroup(host).Toggle();
            host.Settle();
            await Assert.That(ToolRows(host.View)).Count().IsEqualTo(1);
            await host.CloseAsync();
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ChatTabViewSmokeTests/*'`
Expected: the new tests fail — no `toolSummary` button exists (`Single()` throws), rows for a group are not rendered at all (no template for `ToolGroupItem` renders the type name as text).

- [ ] **Step 3: Add the template**

In `ChatTabView.axaml`, inside `<ItemsControl.DataTemplates>` after the `vm:ToolCallItem` template:

```xml
                <DataTemplate x:DataType="vm:ToolGroupItem">
                    <StackPanel>
                        <Button Classes="toolSummary" IsVisible="{Binding HasSummary}" Command="{Binding ToggleCommand}"
                                Background="Transparent" BorderBrush="Transparent" Padding="0" Margin="0,0,0,6" Cursor="Hand"
                                HorizontalAlignment="Left">
                            <StackPanel Orientation="Horizontal" Spacing="9">
                                <!-- Stroked chevron, not a text glyph — the 9px ▸ rendered as a dot. -->
                                <Panel Width="12" Height="12" VerticalAlignment="Center">
                                    <Path Stroke="{StaticResource KcapMutedBrush}" StrokeThickness="1.8"
                                          StrokeLineCap="Round" StrokeJoin="Round"
                                          Data="M3,4.5 L6,7.5 L9,4.5" IsVisible="{Binding IsExpanded}" />
                                    <Path Stroke="{StaticResource KcapMutedBrush}" StrokeThickness="1.8"
                                          StrokeLineCap="Round" StrokeJoin="Round"
                                          Data="M4.5,3 L7.5,6 L4.5,9" IsVisible="{Binding !IsExpanded}" />
                                </Panel>
                                <TextBlock Text="{Binding Summary}" FontSize="11.5" Foreground="{StaticResource KcapMutedBrush}" VerticalAlignment="Center" />
                                <TextBlock Text="✕" FontSize="11" Foreground="{StaticResource KcapDangerBrush}"
                                           IsVisible="{Binding HasFailure}" VerticalAlignment="Center" />
                            </StackPanel>
                        </Button>
                        <ItemsControl ItemsSource="{Binding VisibleCalls}" />
                    </StackPanel>
                </DataTemplate>
```

- [ ] **Step 4: Hold the tail on a summary click**

`ChatTabView.axaml.cs`: make `OnScrollChanged` an instance method with the one-shot hold, and arm it from a bubbling click:

```csharp
public partial class ChatTabView : UserControl {
    const double BottomTolerance = 2;

    ScrollViewer? _scroll;
    /// Armed by a click on a group's summary line, consumed by the extent change that click causes:
    /// expanding a group must not scroll the clicked line out of view.
    bool _holdTail;

    public ChatTabView() {
        InitializeComponent();
        ComposerInput.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        ChatItems.AddHandler(Button.ClickEvent, OnItemButtonClick);
        ChatItems.TemplateApplied += (_, _) => {
            if (_scroll is not null) _scroll.ScrollChanged -= OnScrollChanged;
            _scroll = ChatItems.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
        };
    }

    void OnItemButtonClick(object? sender, RoutedEventArgs e) {
        if (e.Source is Button button && button.Classes.Contains("toolSummary")) _holdTail = true;
    }

    void OnScrollChanged(object? sender, ScrollChangedEventArgs e) {
        if (sender is not ScrollViewer scroll || (e.ExtentDelta.Y == 0 && e.ViewportDelta.Y == 0)) return;
        if (_holdTail && e.ExtentDelta.Y != 0) { _holdTail = false; return; }
        var offsetBefore   = scroll.Offset.Y - e.OffsetDelta.Y;
        var viewportBefore = scroll.Viewport.Height - e.ViewportDelta.Y;
        var extentBefore   = scroll.Extent.Height - e.ExtentDelta.Y;
        var stayed = e.OffsetDelta.Y >= 0;
        if (stayed && offsetBefore + viewportBefore >= extentBefore - BottomTolerance) scroll.ScrollToEnd();
    }
```

Keep the existing comments on the tunnel handler and the template-applied hook; keep `OnComposerKeyDown` and `FocusComposer` as they are.

- [ ] **Step 5: Run the smoke tests**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter '/*/*/ChatTabViewSmokeTests/*'`
Expected: all pass. Likely first failures and their fixes:
- `Summary(...)` finds no button on a folded group: the `ToolCallItem` template is not being resolved inside the nested `ItemsControl` → confirm the nested list has no `ItemTemplate` of its own and the outer `DataTemplates` block still holds the row template.
- `Expanding_the_trailing_group_keeps_the_offset...` sees the offset move: the click's `ScrollChanged` carried `ExtentDelta.Y == 0` on the first pass and the hold was consumed by a later one — log `e.ExtentDelta` in the handler once, then consume the hold only on a non-zero extent delta (as written) and make sure the handler is the instance method (the static one cannot see `_holdTail`).
- `A_thousand_call_group...` is slow but must not time out; if it exceeds the suite's per-test budget, drop to 500 rows in both the file and the assertion and say so in the test's doc comment.

- [ ] **Step 6: Run the whole App suite**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`
Expected: green (the pre-existing nudge test failures noted in memory are not in this suite).

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/Views/ChatTabView.axaml src/Capacitor.App/Views/ChatTabView.axaml.cs test/Capacitor.App.Tests.Unit/ChatTabViewSmokeTests.cs
git commit -m "Render folded tool groups and hold the tail on expansion"
```

---

### Task 9: CHANGES entry and final verification

**Files:**
- Modify: `docs/CHANGES.md` — insert after the "Elicitation question cards for PTY sessions" section (before `## Launch and stop command routing`)

- [ ] **Step 1: Write the section**

```markdown
## Compact tool calls in the Chat tab

**AI-2418** (spec: `docs/superpowers/specs/2026-09-02-ai2418-compact-tool-calls-design.md`) folds a
run of consecutive tool calls into one `ToolGroupItem` row: settled calls collapse to a summary line
("Read files, ran a command"), live calls stay listed beneath it, and any prose (user turn, assistant
text, system note) closes the run. **The fold is uniform** — a lone settled call still reads "Ran a
command" — and **folding never hides an error**: a failed call inside a folded group puts the danger
`✕` on the summary line. The group binds ONE inner list whose source swaps on toggle, because a
hidden `ItemsControl` keeps its containers; expanding a group realizes every row and folding releases
them. Expanding holds follow-tail once, so the clicked summary stays in view. Summary wording keys on
the transcript's tool name (Codex's rollout says `shell`, its hook says `Bash`), with Codex shell
commands classified by `CodexCommandClassifier`, ported verbatim from the server into Core so the
server can delete its copy on the next submodule bump. A row waiting on a permission shows an accent
`?` in the outcome slot: `PermissionPendingDto` gains an optional `tool_use_id` the daemon reads from
the hook body (Claude's PermissionRequest hook carries it; Codex's deliberately does not), and the
view-model recomputes the marks from pending requests and running calls on every change, diffing
against the last marks so a call that settles while its request is still pending is cleared rather
than masked by its `✓`. A request without an id marks the agent's sole running call and abstains
when two or more are running.
```

- [ ] **Step 2: Full solution build and every suite**

Run:
```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj 2>&1 | tail -3
dotnet test --solution Capacitor.slnx 2>&1 | tail -20
```
Expected: build clean; the four suites green apart from the pre-existing nudge session-start failures recorded in memory (`local-preexisting-nudge-test-failures`), which are unrelated.

- [ ] **Step 3: AOT publish check**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`
Expected: no output.

- [ ] **Step 4: Commit**

```bash
git add docs/CHANGES.md
git commit -m "Record the compact tool-call design in CHANGES"
```

---

## Self-review

**Spec coverage.** Decision 1 and §1 grouping → Tasks 5, 6. Decision 2 (uniform fold) → `HasSummary` on any settled call, Task 5. Decision 3 (live rows beneath) → `VisibleCalls`, Tasks 5, 8. Decision 4 and §4 (id then sole-running, `_marked` diff, both orders, reset/switch) → Tasks 2, 3, 7. Decision 5 (✕ on summary) → Task 8 template and test. Decision 6 and §2 wording, table, refinements → Task 4. Decision 7 (`?` glyph, accent brush) → Tasks 5, 8. Decision 8 (classifier port) → Task 1, consumed in Task 4. §3 template, one inner list, chevron, follow-tail hold, thousand-call realization → Task 8. §5 tests → each task's tests; the wire worst-case frame → Task 2; the daemon over-cap drop → Task 3. §6 docs → Task 9.

**Placeholders.** None; every code step carries its code.

**Type consistency.** `ToolCallItem(string, string, ToolCategory = Other)` everywhere; `ToolGroupItem.Toggle()` used by tests in Tasks 5 and 8; `PermissionEntries.Entry(..., toolUseId:)` defined in Task 7 and used in Tasks 7 and 8; `BuildPending`'s ninth parameter defined and consumed in Task 3; `PendingPermissionRequest.ToolUseId` defined in Task 7 and read by `Reconcile()` there.
