# Consent Prompt Window + Activity Feed (AI-1652) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the desktop consent UX: an auto-raised prompt window resolving daemon launch-consent requests, an Activity tab rendering the consent decision log, and pending-consent wired into the tray's Attention state — on a hardened v2 consent wire (daemon-minted `prompt_id` identity, `rule_saved` disclosure, structurally fail-closed v2 frames).

**Architecture:** Daemon side: `LaunchConsentGate` mints a GUID `prompt_id` per prompt; `LaunchConsentBroker.TryResolve` atomically claims by identity echo; two new append-only frame types (`ConsentSubscribeV2 = 17`, `ConsentResolveV2 = 18`) carry the consent surface so a v1 daemon fails closed at its codec. App side: a `ConsentService` owns the pending cache (status-driven subscription, identity-guarded removals, service-lifetime tombstones, `PruneAfter` hygiene), a coordinator-owned topmost prompt window renders the queue, and an `ActivityViewModel` polls the decision-log file through a shared Core reader.

**Tech Stack:** .NET 10 NativeAOT, Avalonia + ReactiveUI (`RxSchedulers.MainThreadScheduler` — `RxApp` scheduler properties do NOT exist in this ReactiveUI version) + DynamicData, System.Text.Json source-gen, TUnit on Microsoft Testing Platform, `Avalonia.Headless` for VM/window tests.

**Spec:** `docs/superpowers/specs/2026-08-08-ai1652-consent-prompt-activity-feed-design.md` (referenced as "spec §N" throughout). The spec survived a 7-round hosted-Codex review; its exact contracts are load-bearing — do not "simplify" a guard you don't understand.

## Global Constraints

- **AOT:** `dotnet build` does NOT surface trimming warnings. After wire/JSON changes run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — must print nothing.
- **JSON:** all new serialization goes through source-gen contexts (`ConsentIpcJsonContext`, `ConsentDecisionJsonContext`). Never `JsonSerializer` without a `JsonTypeInfo`. Use `JsonElementExtensions` instead of checking JSON value kind.
- **DTO convention (positional-required):** trailing nullable members get NO C# default value; nulls are always written on serialize. `ConsentPendingDto` final positional order: `…, RequesterDisplay, PromptId` (spec §4.1).
- **FrameType is append-only:** `ConsentSubscribeV2 = 17`, `ConsentResolveV2 = 18`. No other new values (spec §3).
- **Copy strings verbatim from spec §6:** button labels `Allow once`, `Allow & remember`, `Deny`; tooltip `Saves a rule allowing future launches from this requester. Existing deny rules — including Pause — take precedence until removed.`; terminal texts `Already decided`, `Response time elapsed — unanswered requests are denied by the daemon`, `Expiring…`; disclosure variants in Task 9.
- **Scheduling:** marshal to the UI thread with `.ObserveOn(RxSchedulers.MainThreadScheduler)` / `Dispatcher.UIThread.Post`. Never subscribe `Observable.Interval` off the UI thread (orphan-dispatcher bug — see `MainWindowViewModel._ticker` comment).
- **File sharing:** every read of the daemon-written decision log opens with `FileShare.ReadWrite | FileShare.Delete` (spec §4.4; Windows mandatory sharing — AI-1629 bug class).
- **No Linear IDs in C# comments** (CI lint). Use GitHub issue numbers if needed.
- **TUnit:** run one class with `--treenode-filter "/*/*/ClassName/*"` (bare `"*Name*"` matches zero tests). Unit suites: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`, `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`.
- **Windows CI:** never assert paths built with `/` against `Path.Combine` output; tests touching real daemon dirs must set `DaemonLockPaths` override (`KCAP_DAEMONS_DIR` seam / `SetTestOverride`) so they can't touch the developer's live daemon.
- **README:** this slice adds no CLI surface (no new commands/flags) — README stays untouched; do not "helpfully" edit it.

---

## File Structure (whole slice)

**Core (`src/Capacitor.Cli.Core/LocalIpc/`):**
- `FrameType.cs`, `FrameCodec.cs` — v2 frame values + codec text-group entries (Task 1)
- `ConsentIpc.cs` — trailing DTO fields (Task 1)
- `ConsentDecisionLog.cs` — NEW: `ConsentDecisionRecord` + `ConsentDecisionJsonContext` (Task 1), `ConsentLogReadResult` + `ConsentDecisionLogReader` (Task 6)
- `LocalControlOps.cs` — `ResolveConsentAsync` (Task 4)
- `ConsentSubscription.cs` — NEW: `ConsentStreamEvent` + subscription client (Task 5)

**Daemon (`src/Capacitor.Cli.Daemon/Services/`):**
- `LaunchConsentEngine.cs` (`LaunchConsentInput` + display), `LaunchConsentGate.cs` (mint + threading + Core record), `LaunchConsentBroker.cs` (echo claim), `LaunchConsentDecisionLog.cs` (Core record), `AgentOrchestrator.cs` (pass display) — Task 2
- `LaunchConsentIpc.cs` (rule_saved + echo + v2 validation), `LocalControlServer.cs` (route 17/18), `LocalControlCapabilities.cs` (consent/2) — Task 3

**App (`src/Capacitor.App/`):**
- `Services/UiTicker.cs` — NEW: hoisted app-lifetime ticker (Task 7)
- `Services/ConsentService.cs` + `Services/IConsentService.cs` — NEW (Task 8)
- `ViewModels/ConsentPromptViewModel.cs`, `Views/ConsentPromptWindow.axaml(.cs)`, `Services/ConsentPromptCoordinator.cs` — NEW (Task 9)
- `ViewModels/ActivityViewModel.cs` — NEW; `Views/MainWindow.axaml` tabs (Task 10)
- `ViewModels/TrayViewModel.cs`, `ViewModels/TrayModels.cs`, `Views/TrayMenuBuilder.cs` (Task 11)
- `App.axaml.cs` — composition + shutdown order (Task 9)

Dependency order: Task 1 → {2 → 3, 4, 5, 6} → 7 → 8 → 9 → 10 → 11. Tasks 4/5/6 depend only on Task 1. Task 10 depends on 6 + 7; Task 11 on 8 + 9.

---

### Task 1: Wire contracts — v2 frame types, DTO extensions, hoisted decision record

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` (Encode + Decode switches)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/ConsentIpc.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/ConsentDecisionLog.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/ConsentWireContractsTests.cs` (new)

**Interfaces:**
- Consumes: nothing new.
- Produces (later tasks rely on these EXACT shapes):
  - `FrameType.ConsentSubscribeV2 = 17`, `FrameType.ConsentResolveV2 = 18` (client→daemon)
  - `ConsentPendingDto(string RequestId, string? Requester, string Kind, string RepoPath, string Vendor, string RequestedAt, int TimeoutSeconds, string? RequesterDisplay, string? PromptId)`
  - `ConsentResolveDto(string RequestId, string Decision, ConsentRuleDto? SaveRule, string? PromptId)`
  - `ConsentAckDto(bool Ok, string? Error, bool? RuleSaved)`
  - `ConsentDecisionRecord(string DecidedAt, string AgentId, string? Requester, bool RequesterIsOwner, string Kind, string RepoPath, string Vendor, string Outcome, string Source, string? RequesterDisplay)` + `ConsentDecisionJsonContext`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

/// Wire-shape pins for the AI-1652 consent v2 contracts (spec §4.1, §4.4): trailing nullable
/// members with no C# default, nulls always written, snake_case names byte-compatible with
/// existing daemons' output.
public class ConsentWireContractsTests {
    [Test]
    public async Task Pending_dto_roundtrips_with_trailing_fields() {
        var dto = new ConsentPendingDto("a1", "github:1", "agent", "/r", "codex", "2026-08-08T10:00:00.0000000+00:00", 45, "Mathias", "p1");
        var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPendingDto);
        await Assert.That(json).Contains("\"requester_display\":\"Mathias\"");
        await Assert.That(json).Contains("\"prompt_id\":\"p1\"");
        var back = JsonSerializer.Deserialize(json, ConsentIpcJsonContext.Default.ConsentPendingDto);
        await Assert.That(back).IsEqualTo(dto);
    }

    [Test]
    public async Task Pending_dto_from_v1_daemon_reads_null_display_and_prompt_id() {
        // A pre-AI-1652 daemon's exact serialization (no requester_display, no prompt_id).
        const string v1 = "{\"request_id\":\"a1\",\"requester\":\"github:1\",\"kind\":\"agent\",\"repo_path\":\"/r\",\"vendor\":\"codex\",\"requested_at\":\"t\",\"timeout_seconds\":45}";
        var dto = JsonSerializer.Deserialize(v1, ConsentIpcJsonContext.Default.ConsentPendingDto)!;
        await Assert.That(dto.RequesterDisplay).IsNull();
        await Assert.That(dto.PromptId).IsNull();
    }

    [Test]
    public async Task Resolve_dto_carries_prompt_id_and_ack_carries_rule_saved_with_nulls_written() {
        var resolve = new ConsentResolveDto("a1", "allow", null, "p1");
        var rjson = JsonSerializer.Serialize(resolve, ConsentIpcJsonContext.Default.ConsentResolveDto);
        await Assert.That(rjson).Contains("\"prompt_id\":\"p1\"");

        var ack = new ConsentAckDto(true, null, null);
        var ajson = JsonSerializer.Serialize(ack, ConsentIpcJsonContext.Default.ConsentAckDto);
        await Assert.That(ajson).Contains("\"rule_saved\":null"); // nulls-always-written convention

        // Old-format ack (no rule_saved member) → null, not an error.
        var old = JsonSerializer.Deserialize("{\"ok\":false,\"error\":\"x\"}", ConsentIpcJsonContext.Default.ConsentAckDto)!;
        await Assert.That(old.RuleSaved).IsNull();
    }

    [Test]
    public async Task Decision_record_matches_existing_on_disk_field_names_verbatim() {
        var rec = new ConsentDecisionRecord("t", "a1", "github:1", false, "agent", "/r", "codex", "allowed", "rule[0]", null);
        var json = JsonSerializer.Serialize(rec, ConsentDecisionJsonContext.Default.ConsentDecisionRecord);
        foreach (var name in new[] { "\"decided_at\"", "\"agent_id\"", "\"requester\"", "\"requester_is_owner\"",
                                     "\"kind\"", "\"repo_path\"", "\"vendor\"", "\"outcome\"", "\"source\"", "\"requester_display\"" })
            await Assert.That(json).Contains(name);

        // Old log line (pre-AI-1652, no requester_display) parses; missing bool reads false.
        const string oldLine = "{\"decided_at\":\"t\",\"agent_id\":\"a1\",\"requester\":null,\"requester_is_owner\":true,\"kind\":\"agent\",\"repo_path\":\"/r\",\"vendor\":\"codex\",\"outcome\":\"allowed\",\"source\":\"owner\"}";
        var back = JsonSerializer.Deserialize(oldLine, ConsentDecisionJsonContext.Default.ConsentDecisionRecord)!;
        await Assert.That(back.RequesterDisplay).IsNull();
        await Assert.That(back.RequesterIsOwner).IsTrue();
    }

    [Test]
    public async Task V2_frame_values_are_pinned_and_codec_roundtrips_them() {
        await Assert.That((byte)FrameType.ConsentSubscribeV2).IsEqualTo((byte)17);
        await Assert.That((byte)FrameType.ConsentResolveV2).IsEqualTo((byte)18);

        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.ConsentJson(FrameType.ConsentResolveV2, "{\"x\":1}"), CancellationToken.None);
        await FrameCodec.WriteAsync(ms, new LocalFrame(FrameType.ConsentSubscribeV2), CancellationToken.None);
        ms.Position = 0;
        var f1 = (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
        var f2 = (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
        await Assert.That(f1.Type).IsEqualTo(FrameType.ConsentResolveV2);
        await Assert.That(f1.Text).IsEqualTo("{\"x\":1}");
        await Assert.That(f2.Type).IsEqualTo(FrameType.ConsentSubscribeV2);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ConsentWireContractsTests/*"`
Expected: compile errors (`ConsentSubscribeV2` undefined, ctor arity mismatches).

- [ ] **Step 3: Implement**

`FrameType.cs` — append to the client→daemon block (after `StatusSubscribe = 16`):

```csharp
    // AI-1652 v2 consent frames (append-only). A v1 daemon's codec throws on these bytes
    // before routing — that codec-level rejection IS the down-level fail-closed contract.
    ConsentSubscribeV2 = 17, // long-lived: v2 subscribe (same reply stream as ConsentSubscribe)
    ConsentResolveV2   = 18, // one-shot: resolve requiring the prompt_id identity echo
```

(Keep the "no Linear IDs" rule: the comment says "v2 consent frames", not the issue number — reference the spec file if a pointer is needed.)

`FrameCodec.cs` — add `FrameType.ConsentSubscribeV2 or FrameType.ConsentResolveV2` to the **text-payload group** in BOTH `Encode` (the `or FrameType.ConsentSubscribe or FrameType.ConsentResolve` line) and `Decode` (same group).

`ConsentIpc.cs` — extend the records (no defaults on new members):

```csharp
public sealed record ConsentPendingDto(
    string RequestId, string? Requester, string Kind, string RepoPath, string Vendor,
    string RequestedAt, int TimeoutSeconds, string? RequesterDisplay, string? PromptId);

public sealed record ConsentResolveDto(string RequestId, string Decision, ConsentRuleDto? SaveRule, string? PromptId);

/// Ok = did the resolution apply. RuleSaved: null when the resolve carried no save_rule;
/// true/false = the rule write succeeded/failed — populated on BOTH Ok branches (spec §4.1),
/// because save_rule is deliberately persisted before the resolution is attempted.
public sealed record ConsentAckDto(bool Ok, string? Error, bool? RuleSaved);
```

`ConsentDecisionLog.cs` (new file):

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// The consent decision log's single write/read shape (spec §4.4): the daemon appends one of
/// these per decision to consent-decisions.jsonl; the CLI `log` verb prints raw lines; the app
/// parses them for the Activity feed. Field names are the pre-AI-1652 on-disk names verbatim —
/// existing log files remain readable. Outcome: "allowed"|"denied". Source: "owner"|"rule[i]"|
/// "default"|"prompt_no_ui"|"prompt_user"|"prompt_timeout".
public sealed record ConsentDecisionRecord(
    string DecidedAt, string AgentId, string? Requester, bool RequesterIsOwner,
    string Kind, string RepoPath, string Vendor, string Outcome, string Source,
    string? RequesterDisplay);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ConsentDecisionRecord))]
public partial class ConsentDecisionJsonContext : JsonSerializerContext;
```

Callers of the changed ctors elsewhere in the tree will now fail to compile (daemon's `ToDto`, `LaunchConsentIpc` rule handling, existing tests constructing `ConsentAckDto`/`ConsentPendingDto`). Fix each call site **mechanically** by appending the new arguments (`null` / `null` for pending display+prompt, `null` for resolve PromptId, `null` for ack RuleSaved) — behavioral rewiring is Tasks 2–3, not this task. Search: `rg -n "new ConsentAckDto|new ConsentPendingDto|new ConsentResolveDto|ConsentAckDto\(|ConsentPendingDto\(" src test`.

- [ ] **Step 4: Run to verify pass**

Run: full unit suite `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS (incl. pre-existing consent tests with mechanically-appended args).

- [ ] **Step 5: AOT check + commit**

Run: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` → no output.

```bash
git add -A && git commit -m "feat: consent v2 wire contracts — frame types 17/18, prompt_id/rule_saved/requester_display DTO fields, hoisted decision record"
```

---

### Task 2: Daemon — prompt identity, requester display threading, atomic broker claim

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentEngine.cs` (`LaunchConsentInput`)
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentGate.cs` (`LaunchConsentPromptRequest`, mint, `Done()`)
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentBroker.cs` (`TryResolve` echo overload)
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentDecisionLog.cs` (write Core record; delete internal record+ctx)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (~line 1328: pass `cmd.RequesterDisplay`)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentGateTests.cs`, `LaunchConsentBrokerTests.cs`, `LaunchConsentEngineTests.cs`, `LaunchConsentDecisionLogTests.cs`

**Interfaces:**
- Consumes: Task 1 types (`ConsentDecisionRecord`, `ConsentDecisionJsonContext`).
- Produces:
  - `LaunchConsentInput(string? RequesterUserId, bool RequesterIsOwner, string Kind, string RepoPath, string Vendor, string? RequesterDisplay)` (readonly record struct)
  - `LaunchConsentPromptRequest(string RequestId, string? Requester, string Kind, string RepoPath, string Vendor, string RequestedAt, int TimeoutSeconds, string? RequesterDisplay, string PromptId)` (internal record — PromptId non-nullable here: the gate always mints)
  - `LaunchConsentBroker.TryResolve(string requestId, bool allow, string? promptIdEcho)` — null echo = legacy by-id; non-null must match exactly; lost claim → false, never retries
  - `LaunchConsentDecisionLog.Record(ConsentDecisionRecord rec)`

- [ ] **Step 1: Write the failing tests**

In `LaunchConsentGateTests.cs` add (using that file's existing fake prompter/store/log harness — read it first; reuse its builders):

```csharp
[Test]
public async Task Gate_mints_distinct_prompt_ids_under_a_frozen_clock_and_threads_display() {
    // Frozen TimeProvider: RequestedAt identical for both prompts — PromptId must still differ
    // (the failure mode a timestamp identity would have had, spec §4.1).
    var prompter = new CapturingPrompter(answer: true); // captures every LaunchConsentPromptRequest
    var gate = BuildPromptModeGate(prompter, frozenClock: true);
    var input = new LaunchConsentInput("github:1", false, "agent", "/r", "codex", "Mathias");

    await gate.DecideAsync("agent-1", input, CancellationToken.None);
    await gate.DecideAsync("agent-1", input, CancellationToken.None);

    var (a, b) = (prompter.Requests[0], prompter.Requests[1]);
    await Assert.That(a.RequestedAt).IsEqualTo(b.RequestedAt);        // clock frozen
    await Assert.That(a.PromptId).IsNotEqualTo(b.PromptId);           // identity is not the clock
    await Assert.That(a.PromptId).IsNotEmpty();
    await Assert.That(a.RequesterDisplay).IsEqualTo("Mathias");
}

[Test]
public async Task Done_records_requester_display_in_the_decision_record() {
    // Rule-allowed path (no prompter needed): input display lands in the log record.
    var log = new CapturingLog(); // or read the real file — follow the file's existing pattern
    var gate = BuildAllowByDefaultGate(log);
    await gate.DecideAsync("agent-1", new LaunchConsentInput("github:1", false, "agent", "/r", "codex", "Mathias"), CancellationToken.None);
    await Assert.That(log.Records[0].RequesterDisplay).IsEqualTo("Mathias");
    await Assert.That(log.Records[0].Source).IsEqualTo("default");
}
```

In `LaunchConsentBrokerTests.cs` add:

```csharp
[Test]
public async Task TryResolve_with_matching_echo_resolves_and_mismatch_leaves_pending_untouched() {
    var broker = new LaunchConsentBroker();
    var (id, reader) = broker.Subscribe();
    var promptTask = broker.PromptAsync(ReqWithPromptId("agent-1", "p-A"), TimeSpan.FromMinutes(1), TimeProvider.System, CancellationToken.None);

    await Assert.That(broker.TryResolve("agent-1", true, "WRONG")).IsFalse();   // mismatch: no claim
    await Assert.That(promptTask.IsCompleted).IsFalse();                        // A still pending
    await Assert.That(broker.TryResolve("agent-1", true, "p-A")).IsTrue();      // exact echo claims
    await Assert.That(await promptTask).IsTrue();
    broker.Unsubscribe(id);
}

[Test]
public async Task Stale_echo_for_a_superseded_request_never_decides_the_successor() {
    // A times out; successor B reuses the id; A's late resolve must answer false and leave B live.
    var broker = new LaunchConsentBroker();
    var (id, _) = broker.Subscribe();
    var a = broker.PromptAsync(ReqWithPromptId("agent-1", "p-A"), TimeSpan.Zero, TimeProvider.System, CancellationToken.None);
    await Assert.That(await a).IsNull(); // timeout claimed A
    var b = broker.PromptAsync(ReqWithPromptId("agent-1", "p-B"), TimeSpan.FromMinutes(1), TimeProvider.System, CancellationToken.None);

    await Assert.That(broker.TryResolve("agent-1", true, "p-A")).IsFalse(); // stale identity: refused
    await Assert.That(b.IsCompleted).IsFalse();                             // B untouched
    await Assert.That(broker.TryResolve("agent-1", false, "p-B")).IsTrue(); // B decided on its own terms
    await Assert.That(await b).IsFalse();
    broker.Unsubscribe(id);
}

[Test]
public async Task Null_echo_preserves_legacy_resolve_by_id() {
    var broker = new LaunchConsentBroker();
    var (id, _) = broker.Subscribe();
    var a = broker.PromptAsync(ReqWithPromptId("agent-1", "p-A"), TimeSpan.FromMinutes(1), TimeProvider.System, CancellationToken.None);
    await Assert.That(broker.TryResolve("agent-1", true, null)).IsTrue();
    await Assert.That(await a).IsTrue();
    broker.Unsubscribe(id);
}
```

(`ReqWithPromptId` is a local helper building `LaunchConsentPromptRequest` with all fields; adapt the file's existing request builder.)

In `LaunchConsentEngineTests.cs` add:

```csharp
[Test]
public async Task Earlier_wildcard_deny_shadows_a_later_appended_allow() {
    // First-match-wins pins the "Allow & remember shadowed by pause" contract (spec §4.1).
    var policy = new LaunchConsentPolicy(LaunchConsentDefault.Prompt, 45, [
        new LaunchConsentRule("deny", null, null, null, null),          // pause at rules[0]
        new LaunchConsentRule("allow", "github:1", null, null, null),   // appended by Allow & remember
    ]);
    var d = LaunchConsentEngine.Evaluate(policy, new LaunchConsentInput("github:1", false, "agent", "/r", "codex", null));
    await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Deny);
    await Assert.That(d.Source).IsEqualTo("rule[0]");
}
```

In `LaunchConsentDecisionLogTests.cs`: update record construction to `ConsentDecisionRecord` and add one assertion that a written line contains `"requester_display"`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/LaunchConsent*/*"`
Expected: compile errors (new members/overloads missing).

- [ ] **Step 3: Implement**

`LaunchConsentEngine.cs`:

```csharp
internal readonly record struct LaunchConsentInput(
    string? RequesterUserId,
    bool RequesterIsOwner,
    string Kind,
    string RepoPath,
    string Vendor,
    string? RequesterDisplay);
```

(Matching is untouched — `Matches` never reads `RequesterDisplay`.)

`LaunchConsentGate.cs`:

```csharp
internal sealed record LaunchConsentPromptRequest(
    string RequestId, string? Requester, string Kind, string RepoPath, string Vendor,
    string RequestedAt, int TimeoutSeconds, string? RequesterDisplay, string PromptId);
```

In `DecideAsync`, the request construction becomes (mint the identity where the request is built — no clock involvement, spec §4.1):

```csharp
var req = new LaunchConsentPromptRequest(agentId, input.RequesterUserId, input.Kind,
    input.RepoPath, input.Vendor, requestedAt, policy.PromptTimeoutSeconds,
    input.RequesterDisplay, Guid.NewGuid().ToString("N"));
```

`Done()` writes the Core record:

```csharp
log.Record(new ConsentDecisionRecord(
    DateTimeOffset.UtcNow.ToString("O"), agentId, input.RequesterUserId, input.RequesterIsOwner,
    input.Kind, input.RepoPath, input.Vendor, allowed ? "allowed" : "denied", source,
    input.RequesterDisplay));
```

(add `using Capacitor.Cli.Core.LocalIpc;`).

`LaunchConsentBroker.cs` — replace `TryResolve` with:

```csharp
    public bool TryResolve(string requestId, bool allow) => TryResolve(requestId, allow, null);

    /// Non-null echo: resolve succeeds only for the pending entry whose PromptId matches
    /// exactly, and match+removal is ONE atomic claim — the KeyValuePair-conditional remove is
    /// the same ABA primitive the timeout/cleanup paths use (class doc). A lost claim (the
    /// matched instance replaced between lookup and removal) returns false and NEVER retries
    /// against the successor. Null echo: legacy resolve-by-id (v1 frame callers).
    public bool TryResolve(string requestId, bool allow, string? promptIdEcho) {
        if (!_pending.TryGetValue(requestId, out var p)) return false;
        if (promptIdEcho is not null &&
            !string.Equals(promptIdEcho, p.Request.PromptId, StringComparison.Ordinal)) return false;
        if (!_pending.TryRemove(new KeyValuePair<string, Pending>(requestId, p))) return false;
        return p.Tcs.TrySetResult(allow);
    }
```

`LaunchConsentDecisionLog.cs` — delete `LaunchConsentRecord` and `LaunchConsentDecisionJsonCtx`; `Record` takes `ConsentDecisionRecord` and serializes via `ConsentDecisionJsonContext.Default.ConsentDecisionRecord` (add the `using`). Writer behavior (0600, rotation, append) unchanged.

`AgentOrchestrator.cs` (~line 1328): append the display argument:

```csharp
var consentInput = new LaunchConsentInput(
    cmd.RequesterUserId, cmd.RequesterIsOwner ?? false,
    LaunchConsentEngine.KindToken(cmd.Kind), cmd.RepoPath, cmd.Vendor, cmd.RequesterDisplay);
```

Fix remaining compile errors from the renamed record (`LaunchConsentIpc.ToDto` — stamp the new fields now: `new(r.RequestId, r.Requester, r.Kind, r.RepoPath, r.Vendor, r.RequestedAt, r.TimeoutSeconds, r.RequesterDisplay, r.PromptId)`; existing test builders gain the two new args).

- [ ] **Step 4: Run to verify pass**

Run: full `Capacitor.Cli.Tests.Unit` suite. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: daemon-minted prompt_id identity with atomic broker claim + requester_display threading"
```

---

### Task 3: Daemon — v2 IPC handlers, rule_saved on both branches, consent/2 capability

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentIpc.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` (routing)
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs`
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentIpcTests.cs`, `test/Capacitor.Cli.Tests.Unit/Daemon/LocalControlHelloTests.cs`

**Interfaces:**
- Consumes: Task 2's `TryResolve(id, allow, echo)`, Task 1 DTOs.
- Produces: `LaunchConsentIpc.HandleResolveAsync(string payload, Stream stream, CancellationToken ct, bool requireEcho)`; server routes 17 → subscribe handler, 18 → resolve with `requireEcho: true`, 11/12 unchanged (`requireEcho: false`); `LocalControlCapabilities.Current == ["consent/1", "consent/2", "status/1"]`.

- [ ] **Step 1: Write the failing tests**

`LaunchConsentIpcTests.cs` uses an in-memory stream + real broker/store harness — follow its existing arrange helpers. Add:

```csharp
[Test]
public async Task V2_resolve_without_prompt_id_acks_invalid_payload() {
    // requireEcho: a v2 resolve missing/empty prompt_id never reaches the broker.
    var ack = await ResolveAsync(ipc, "{\"request_id\":\"a1\",\"decision\":\"allow\"}", requireEcho: true);
    await Assert.That(ack.Ok).IsFalse();
    await Assert.That(ack.Error).Contains("prompt_id");
}

[Test]
public async Task Rule_saved_is_populated_on_both_ok_branches() {
    // (1) save_rule + live pending + matching echo → Ok=true, RuleSaved=true.
    // (2) save_rule + NO pending → rule still persisted (save-before-resolve is deliberate),
    //     Ok=false, RuleSaved=true — the disclosure the app renders (spec §4.1).
    // (3) save_rule rejected by the store (drive TryReplace failure the way the file's existing
    //     save_rule-rejection test does) → RuleSaved=false on both branches.
    // (4) no save_rule → RuleSaved=null.
    var okAck = await ResolveAsync(ipc, ResolveJson("a1", "allow", saveRule: true, promptId: livePromptId), requireEcho: true);
    await Assert.That(okAck.Ok).IsTrue();
    await Assert.That(okAck.RuleSaved).IsEqualTo(true);

    var nopAck = await ResolveAsync(ipc, ResolveJson("ghost", "allow", saveRule: true, promptId: "p-x"), requireEcho: true);
    await Assert.That(nopAck.Ok).IsFalse();
    await Assert.That(nopAck.RuleSaved).IsEqualTo(true);
    await Assert.That(StoreRules()).Contains(r => r.Requester == "github:1"); // persisted despite Ok=false

    var plainAck = await ResolveAsync(ipc, ResolveJson("a1", "deny", saveRule: false, promptId: otherLivePromptId), requireEcho: true);
    await Assert.That(plainAck.RuleSaved).IsNull();
}

[Test]
public async Task V2_resolve_with_mismatching_echo_acks_no_pending_and_leaves_the_request_live() {
    var ack = await ResolveAsync(ipc, ResolveJson("a1", "allow", saveRule: false, promptId: "WRONG"), requireEcho: true);
    await Assert.That(ack.Ok).IsFalse();
    await Assert.That(PendingStillLive("a1")).IsTrue();
}

[Test]
public async Task Subscribe_pushes_prompt_id_and_requester_display_on_pending_frames() {
    // ToDto stamping: the pushed ConsentPendingDto carries the request's PromptId + display.
    var dto = await FirstPendingFrom(subscribeStream);
    await Assert.That(dto.PromptId).IsEqualTo(livePromptId);
    await Assert.That(dto.RequesterDisplay).IsEqualTo("Mathias");
}
```

(Write `ResolveAsync`/`ResolveJson`/`StoreRules`/`PendingStillLive`/`FirstPendingFrom` as small local helpers over the file's existing harness — the file already round-trips resolve payloads and subscribe streams; mirror its mechanics, don't invent new plumbing.)

`LocalControlHelloTests.cs`: update the capability assertion to `["consent/1", "consent/2", "status/1"]`.

- [ ] **Step 2: Run to verify failure** — same filter as Task 2 plus `LocalControlHelloTests`. Expected: FAIL/compile errors.

- [ ] **Step 3: Implement**

`LaunchConsentIpc.HandleResolveAsync` — new signature `(string payload, Stream stream, CancellationToken ct, bool requireEcho = false)`; body changes:

```csharp
if (dto is null || string.IsNullOrEmpty(dto.RequestId) || dto.Decision is not ("allow" or "deny")
        || (dto.SaveRule is { } saveRuleShape && saveRuleShape.Action is null)) {
    ack = new ConsentAckDto(false, "invalid resolve payload (decision must be allow|deny)", null);
} else if (requireEcho && string.IsNullOrEmpty(dto.PromptId)) {
    // V2 contract: the identity echo is mandatory — an id-only resolve is exactly the stale-
    // resolve hazard the v2 frame exists to close (spec §4.1).
    ack = new ConsentAckDto(false, "invalid resolve payload (prompt_id required)", null);
} else {
    string? saveError = null;
    bool? ruleSaved = null;
    if (dto.SaveRule is { } r) {
        var current = store.Current;
        var next = current with {
            Rules = [.. current.Rules, new LaunchConsentRule(r.Action, r.Requester, r.Kind, r.Repo, r.Vendor)] };
        if (!store.TryReplace(next, out saveError))
            logger.LogWarning("Consent save_rule rejected: {Error}", saveError);
        ruleSaved = saveError is null;
    }
    var resolved = broker.TryResolve(dto.RequestId, dto.Decision == "allow", dto.PromptId);
    ack = resolved
        ? new ConsentAckDto(true, saveError, ruleSaved)
        : new ConsentAckDto(false, "no pending consent request with that id", ruleSaved);
}
```

(The `JsonException` catch's ack gains the third `null` arg. The pre-existing comment about Ok-vs-Error stays; extend it with one line: RuleSaved reports the save outcome on BOTH branches.)

`LocalControlServer.cs` — add to the switch (after the ConsentRulesPut case):

```csharp
case FrameType.ConsentSubscribeV2: await consentIpc.HandleSubscribeAsync(stream, ct); break;
case FrameType.ConsentResolveV2:   await consentIpc.HandleResolveAsync(first.Text, stream, ct, requireEcho: true); break;
```

and extend the default-arm error text's frame list with `/ConsentSubscribeV2/ConsentResolveV2`.

`LocalControlCapabilities.cs`:

```csharp
public static readonly IReadOnlyList<string> Current = ["consent/1", "consent/2", "status/1"];
```

(Extend the file's existing doc comment: `consent/2` = identity-checked resolution (`prompt_id` echo), `rule_saved` acks, `prompt_id`/`requester_display`-stamped pendings — advertised only because the v2 routes above exist; discovery only, enforcement is the v2 frames themselves.)

- [ ] **Step 4: Run to verify pass** — full `Capacitor.Cli.Tests.Unit` suite. Expected: PASS.

- [ ] **Step 5: AOT check + commit**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'  # no output
git add -A && git commit -m "feat: v2 consent IPC — identity-required resolve, rule_saved disclosure, consent/2 capability"
```

---

### Task 4: Core — `ResolveConsentAsync` one-shot op

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs`
- Test: extend `test/Capacitor.Cli.Tests.Unit/LocalControlOpsTests.cs`

**Interfaces:**
- Consumes: Task 1 DTOs/frames.
- Produces: `Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct)` on `ILocalControlOps` — throws `LocalControlOpsException` with reason `daemon_unreachable | daemon_rejected | unexpected_reply | timed_out`; caller OCE propagates.

- [ ] **Step 1: Write the failing tests**

Add to `LocalControlOpsTests` (reuse `ScriptedOpsServer`, the short-socket-path arrangement, and the existing script helpers — read the whole file first):

```csharp
static ConnScript ConsentResolveV2Ack(string json) => async (_, s, ct) => {
    var f = await FrameCodec.ReadAsync(s, ct);
    if (f?.Type == FrameType.ConsentResolveV2)
        await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentAck, json), ct);
};

/// A faithful v1 daemon: reads the raw 5-byte header, sees a type byte its codec has no case
/// for, and closes without writing ANY frame (FrameCodec.Decode throws InvalidDataException →
/// HandleConnectionAsync catches/logs/closes). NOT a routing-default Error reply — no deployed
/// v1 daemon produces one for byte 18 (spec §4.1).
static ConnScript V1CodecReject() => async (_, s, ct) => {
    var head = new byte[5];
    var read = 0;
    while (read < 5) {
        var n = await s.ReadAsync(head.AsMemory(read), ct);
        if (n == 0) return;
        read += n;
    }
    // v1 FrameCodec would throw here — the server closes the socket, writing nothing.
};

[Test]
public async Task Resolve_sends_v2_frame_with_prompt_id_and_returns_the_ack_shapes() {
    // Ok=true/null-error/null-rule_saved; Ok=true+error+rule_saved=false; Ok=false+rule_saved=true;
    // old-format ack (no rule_saved member) → RuleSaved null. Assert the WRITTEN frame carried
    // prompt_id by echoing the request payload back through the script (capture f.Text).
}

[Test]
public async Task Resolve_maps_error_frame_to_daemon_rejected() {
    // ErrorThen("nope") → LocalControlOpsException with Reason "daemon_rejected", message "nope".
}

[Test]
public async Task Resolve_against_a_v1_codec_observes_eof_as_unexpected_reply_and_nothing_was_resolved() {
    // V1CodecReject() script → LocalControlOpsException Reason "unexpected_reply"
    // ("daemon closed the connection without replying"). The incarnation-swap pin (spec §9/§10):
    // no ack, no resolution — fail closed.
}

[Test]
public async Task Resolve_timeout_eof_malformed_and_cancellation_classify_like_existing_ops() {
    // Mirror the file's existing GetConsentPolicyAsync classification tests for the new op:
    // Eof() → unexpected_reply; no-reply + short ConsentReplyTimeout via fake TimeProvider →
    // timed_out; malformed ack JSON → unexpected_reply; pre-cancelled ct → OCE propagates.
}
```

Flesh these out to real `[Test]` methods following the file's existing per-case shape (each existing classification case already has a sibling to copy the arrangement from — keep one test per failure class, same as the file does for stop/policy ops).

- [ ] **Step 2: Run to verify failure** — `--treenode-filter "/*/*/LocalControlOpsTests/*"`. Expected: compile error (`ResolveConsentAsync` missing).

- [ ] **Step 3: Implement** — add to the interface and class:

```csharp
Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct);
```

```csharp
    public async Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct) {
        var json = JsonSerializer.Serialize(resolve, ConsentIpcJsonContext.Default.ConsentResolveDto);
        // ConsentResolveV2, never the v1 frame: a v1 daemon must fail closed at its codec rather
        // than resolve by id without the identity check (spec §4.1).
        var reply = await ExchangeAsync(LocalFrame.ConsentJson(FrameType.ConsentResolveV2, json), ConsentReplyTimeout, ct);
        switch (reply.Type) {
            case FrameType.ConsentAck:
                var ack = DeserializeOrThrow(reply.Text, ConsentIpcJsonContext.Default.ConsentAckDto, "malformed consent ack reply");
                if (ack is null) throw new LocalControlOpsException(UnexpectedReply, "malformed consent ack reply");
                return ack;
            case FrameType.Error:
                throw new LocalControlOpsException(DaemonRejected, reply.Text);
            default:
                throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to consent resolve ({reply.Type})");
        }
    }
```

Also update `test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs` (the app-test fake implementing `ILocalControlOps`) with a scriptable `ResolveConsentAsync` following its existing per-method scripting shape — Task 8's tests drive it.

- [ ] **Step 4: Run to verify pass** — both unit suites (Cli + App, the latter for the fake's compile). Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: ResolveConsentAsync one-shot op with v2 frame and pinned failure taxonomy"
```

---

### Task 5: Core — consent subscription client

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/ConsentSubscription.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/ConsentSubscriptionTests.cs` (new; copy the socket-path harness conventions from `LocalControlOpsTests` — short paths, Windows guard, `[NotInParallel]`)

**Interfaces:**
- Produces:

```csharp
public abstract record ConsentStreamEvent {
    public sealed record Subscribed : ConsentStreamEvent;
    public sealed record Pending(ConsentPendingDto Request) : ConsentStreamEvent;
}
public static class ConsentSubscription {
    public static IAsyncEnumerable<ConsentStreamEvent> RunAsync(string daemonName, CancellationToken ct);
}
```

- [ ] **Step 1: Write the failing tests** — scripted server (same `ScriptedOpsServer` shape), cases:

1. `Subscribed_is_yielded_after_the_write_and_before_any_frame` — script reads the ConsentSubscribeV2 frame then blocks (await a TCS): first `MoveNextAsync` completes with `Subscribed` even though no reply exists (the empty-replay boundary, spec §4.2).
2. `Replay_and_push_frames_yield_pending_events_in_order` — script writes two valid `ConsentPending` frames; enumeration yields `Subscribed`, `Pending(a)`, `Pending(b)`, then EOF ends it.
3. `Failed_connect_ends_without_subscribed` — no listener at the socket path: enumeration completes empty (SocketException absorbed; `Subscribed` NOT yielded).
4. `Unexpected_frame_type_ends_the_enumeration` — script writes a `ConsentRules` frame → stream ends after `Subscribed` (protocol confusion).
5. `Undecodable_json_ends_the_enumeration` — `ConsentPending` frame with `not-json` text.
6. `Structurally_invalid_pending_is_skipped_and_the_stream_continues` — `{}` frame followed by a valid frame → only the valid one yields.
7. `Prompt_id_requirement_is_isolated` — three v1-shaped frames (every pre-existing field valid; `prompt_id` separately **absent**, **null**, **empty**) each yield nothing, stream continues to a final valid frame (a `{}` frame alone would stay green with the PromptId check forgotten — spec §10).
8. `V1_codec_daemon_yields_subscribed_then_ends` — script reads the raw 5-byte header (type byte 17) and closes without writing (the `V1CodecReject` shape from Task 4): `Subscribed` then end, no `Pending`.
9. `Cancellation_propagates` — cancel mid-read → `OperationCanceledException` from the enumeration.

- [ ] **Step 2: Run to verify failure** — compile error.

- [ ] **Step 3: Implement**

```csharp
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// One consent subscription attempt as a typed stream (spec §4.2). `Subscribed` is a
/// CLIENT-LOCAL boundary — emitted right after the subscribe write flushes, before any read,
/// because an async iterator exposes no other observable point between "dialing" and
/// "subscribed" (with an empty replay the first read never completes). It does not prove the
/// daemon registered the subscription. The enumeration ENDING (for any reason but caller
/// cancellation) means "this attempt is over" — the consumer decides whether to go again.
public abstract record ConsentStreamEvent {
    public sealed record Subscribed : ConsentStreamEvent;
    public sealed record Pending(ConsentPendingDto Request) : ConsentStreamEvent;
}

public static class ConsentSubscription {
    public static async IAsyncEnumerable<ConsentStreamEvent> RunAsync(
            string daemonName, [EnumeratorCancellation] CancellationToken ct = default) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream? stream = null;
        try {
            // Dial + subscribe write. ConsentSubscribeV2: a v1 daemon's codec rejects the byte
            // before routing and closes without replying — we yield Subscribed (the write
            // flushed) and then end on EOF, never registering a subscriber there (spec §4.1).
            try {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(LocalSocketPaths.Socket(daemonName)), ct);
                stream = new NetworkStream(sock, ownsSocket: false);
                await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.ConsentSubscribeV2), ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) when (ex is IOException or SocketException) {
                yield break; // failed dial/write: attempt over, no Subscribed, no clear (spec §5)
            }

            yield return new ConsentStreamEvent.Subscribed();

            while (true) {
                LocalFrame? frame;
                try {
                    frame = await FrameCodec.ReadAsync(stream!, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException) {
                    yield break; // transport death / undecodable frame: attempt over
                }
                if (frame is null || frame.Type != FrameType.ConsentPending) yield break; // EOF / protocol confusion

                ConsentPendingDto? dto;
                try { dto = JsonSerializer.Deserialize(frame.Text, ConsentIpcJsonContext.Default.ConsentPendingDto); }
                catch (JsonException) { yield break; } // undecodable payload: dead connection

                // Structurally invalid (STJ leaves missing members null; `{}` decodes fine) is
                // SKIPPED, not fatal — ending here would thrash: the resubscribe replay would
                // redeliver the same invalid entry forever (spec §4.2).
                if (!IsStructurallyValid(dto)) continue;
                yield return new ConsentStreamEvent.Pending(dto!);
            }
        } finally {
            if (stream is not null) await stream.DisposeAsync();
        }
    }

    static bool IsStructurallyValid(ConsentPendingDto? dto) =>
        dto is not null
        && !string.IsNullOrEmpty(dto.RequestId)
        && !string.IsNullOrEmpty(dto.PromptId)   // consent/2 daemons always stamp it (spec §4.2)
        && !string.IsNullOrEmpty(dto.Kind)
        && !string.IsNullOrEmpty(dto.RepoPath)
        && !string.IsNullOrEmpty(dto.Vendor)
        && !string.IsNullOrEmpty(dto.RequestedAt)
        && dto.TimeoutSeconds > 0;
}
```

(If `yield` placement fights the compiler on the connect block, hoist the dial into a private `static async Task<NetworkStream?> DialAsync(...)` returning null on absorbed failure — keep the classification identical.)

- [ ] **Step 4: Run to verify pass** — full Cli unit suite. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: consent subscription client with typed Subscribed boundary and fail-closed validation"`

---

### Task 6: Core — decision-log reader

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/ConsentDecisionLog.cs` (append reader types)
- Test: `test/Capacitor.Cli.Tests.Unit/ConsentDecisionLogReaderTests.cs` (new)

**Interfaces:**
- Produces:

```csharp
public sealed record ConsentLogReadResult(IReadOnlyList<ConsentDecisionRecord> Records, bool Complete);
public static class ConsentDecisionLogReader {
    public static string PathFor(string daemonName);
    public static ConsentLogReadResult ReadTail(string daemonName, int max);
}
```

- [ ] **Step 1: Write the failing tests** — every test redirects `DaemonLockPaths` to a temp dir (use the existing test override seam other daemon-dir tests use — find it via `rg -n "SetTestOverride|KCAP_DAEMONS_DIR" test/`; never touch the real `~/.config/kcap/daemons`). Cases:

1. `Tail_merges_rotation_pair_newest_first_capped` — write 3 records to `{path}.1` and 3 newer to `{path}`; `ReadTail(name, 4)` → 4 records, newest first (the current file's last record first), `Complete=true`.
2. `Undecodable_and_structurally_invalid_lines_are_skipped` — interleave `not-json`, `{}` (parses, but `decided_at`/`agent_id`/… null → invalid), and valid lines → only valid returned, `Complete=true`.
3. `Absent_files_are_a_complete_empty_read` — no files → `([], Complete=true)` (clean absence — the feed's empty state, spec §4.4).
4. `Unreadable_file_flips_complete_false_with_partial_records` — valid `.1`, then make `{path}` unreadable (open it exclusively with `FileShare.None` on Windows semantics; cross-platform: create `{path}` as a DIRECTORY so the file open throws `UnauthorizedAccessException`/`IOException`) → `.1` records returned, `Complete=false`.
5. `Old_format_lines_parse_with_null_display` — a line without `requester_display` → record with null.
6. `Duplicate_records_across_the_rotation_boundary_are_deduped` — same line present in both files → one record (value equality `Distinct`).
7. `Reader_share_mode_never_blocks_the_writer` — the sharing regression guard (spec §10): hold a reader handle open with the READER's share mode (open via a stream the same way `ReadTail` does — extract `OpenShared(path)` as `internal static FileStream` so the test uses the production open), and while it is open: (a) append via a `FileStream(path, FileMode.Append, FileAccess.Write)` (the daemon writer's mode) — must succeed; (b) rotate via `File.Move(path, path + ".1", overwrite: true)` — must succeed (this is what `FileShare.Delete` buys); (c) a concurrent `ReadTail` succeeds. Trivially green on Unix; load-bearing on the Windows CI leg.

- [ ] **Step 2: Run to verify failure** — compile error.

- [ ] **Step 3: Implement** — append to `ConsentDecisionLog.cs`:

```csharp
/// Complete=false means at least one file existed but could not be read (I/O failure) — the
/// records list may be partial and the consumer must not mistake it for a genuinely shorter
/// log. Clean absence (file not found) is Complete=true (spec §4.4).
public sealed record ConsentLogReadResult(IReadOnlyList<ConsentDecisionRecord> Records, bool Complete);

public static class ConsentDecisionLogReader {
    public static string PathFor(string daemonName) =>
        Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(daemonName), "consent-decisions.jsonl");

    public static ConsentLogReadResult ReadTail(string daemonName, int max) {
        var path = PathFor(daemonName);
        var complete = true;
        var lines = new List<string>();
        foreach (var file in new[] { path + ".1", path }) {         // .1 first: its lines are older
            if (!TryReadLines(file, lines)) complete = false;
        }

        var seen = new HashSet<ConsentDecisionRecord>();            // value equality — rotation-race dedup
        var records = new List<ConsentDecisionRecord>();
        for (var i = lines.Count - 1; i >= 0 && records.Count < max; i--) {
            var rec = ParseValid(lines[i]);
            if (rec is not null && seen.Add(rec)) records.Add(rec); // newest first
        }
        return new(records, complete);
    }

    // The reader's open — ReadWrite so the daemon's live appends are never blocked, Delete so
    // its File.Move rotation is never blocked (Windows mandatory sharing; AI-1629 bug class).
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// True = read cleanly OR cleanly absent; false = existed-but-unreadable (I/O failure).
    static bool TryReadLines(string path, List<string> into) {
        try {
            using var fs = OpenShared(path);
            using var reader = new StreamReader(fs);
            while (reader.ReadLine() is { } line) into.Add(line);
            return true;
        } catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) {
            return true;  // clean absence
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false; // exists (or vanished mid-read) but unreadable — partial/incomplete
        }
    }

    static ConsentDecisionRecord? ParseValid(string line) {
        ConsentDecisionRecord? rec;
        try { rec = JsonSerializer.Deserialize(line, ConsentDecisionJsonContext.Default.ConsentDecisionRecord); }
        catch (JsonException) { return null; }
        if (rec is null || string.IsNullOrEmpty(rec.DecidedAt) || string.IsNullOrEmpty(rec.AgentId)
            || string.IsNullOrEmpty(rec.Kind) || string.IsNullOrEmpty(rec.RepoPath)
            || string.IsNullOrEmpty(rec.Vendor) || string.IsNullOrEmpty(rec.Outcome)
            || string.IsNullOrEmpty(rec.Source)) return null;
        return rec;
    }
}
```

(Add `using System.Text.Json;` and whatever `DaemonLockPaths` needs. If `DaemonLockPaths.Sanitize` has different casing/visibility, mirror exactly what `DaemonConsentCommand.LogAsync` calls — that path derivation must match the CLI verb's byte-for-byte.)

- [ ] **Step 4: Run to verify pass.** Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: consent decision-log reader with Complete flag and writer-safe sharing"`

---

### Task 7: App — hoist the shared ticker into an app-lifetime service

**Files:**
- Create: `src/Capacitor.App/Services/UiTicker.cs`
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` (delete the private `_ticker`, take `ITicker`)
- Modify: `src/Capacitor.App/App.axaml.cs` (construct one `UiTicker` in `StartAsync`, thread it through `BuildAndShowMainWindow`)
- Test: `test/Capacitor.App.Tests.Unit/UiTickerTests.cs` (new); update every `new MainWindowViewModel(` call site in `MainWindowViewModelTests.cs`, `AgentGridTests.cs`, `MainWindowSmokeTests.cs`, `AppStartupTests.cs` (find all: `rg -n "new MainWindowViewModel" test src`)

**Interfaces:**
- Produces:

```csharp
public interface ITicker { IObservable<long> Ticks { get; } }
public sealed class UiTicker : ITicker;   // production; construct ON the UI thread
public sealed class FakeTicker : ITicker; // test helper (Subject-backed), lives in the test project
```
- `MainWindowViewModel(IDaemonClientService service, AgentActionService actions, ITicker ticker, CancellationToken shutdownToken, TimeProvider? time = null)`

- [ ] **Step 1: Write the failing tests**

`UiTickerTests.cs`: port `AppStartupTests`' existing real-ticker seam test (the one that exercises `MainWindowViewModel.Ticker` producing real ticks from a background subscription thread — find it via `rg -n "Ticker" test/Capacitor.App.Tests.Unit/AppStartupTests.cs`) to target `new UiTicker().Ticks` instead; assert ≥1 tick observed within the test's existing timeout when subscribed from a non-UI thread inside the headless session.

- [ ] **Step 2: Run to verify failure** — compile error.

- [ ] **Step 3: Implement**

`UiTicker.cs` — move the exact pipeline (comment included, updated to name this class) from `MainWindowViewModel._ticker`:

```csharp
using System.Reactive.Linq;

namespace Capacitor.App.Services;

public interface ITicker {
    /// Shared 1 Hz heartbeat. HOT via Publish().RefCount(); ticks are delivered on the UI
    /// thread. Construct the production implementation ON the UI thread (App.StartAsync) —
    /// an off-UI-thread Observable.Interval subscription binds an orphan thread-local
    /// dispatcher that never ticks (the AI-1651 uptime bug; see the pipeline comment).
    IObservable<long> Ticks { get; }
}

public sealed class UiTicker : ITicker {
    // [move MainWindowViewModel's full _ticker pipeline comment + construction here verbatim]
    public IObservable<long> Ticks { get; } = Observable
        .Interval(TimeSpan.FromSeconds(1), RxSchedulers.MainThreadScheduler)
        .SubscribeOn(RxSchedulers.MainThreadScheduler)
        .Publish()
        .RefCount();
}
```

`MainWindowViewModel`: delete the `_ticker` field and `internal Ticker` seam (its hazard test moves to `UiTickerTests`); add the `ITicker ticker` ctor param (position 3); the row `Transform` uses `ticker.Ticks`.

`App.axaml.cs` `StartAsync`: `var ticker = new UiTicker();` right after `notifier`; pass through `BuildAndShowMainWindow(service, actions, notifier, ticker, _shutdown.Token)` → `new MainWindowViewModel(service, actions, ticker, shutdownToken)`. Store it in a field `UiTicker? _ticker` for Tasks 8–10 to consume (no disposal needed — RefCount tears down with its subscribers).

Test call sites: add a `FakeTicker` (Subject-backed) to `test/Capacitor.App.Tests.Unit/` and pass it everywhere `MainWindowViewModel` is constructed; tests that previously injected a `Subject<long>` ticker into rows keep doing so via `FakeTicker`.

```csharp
public sealed class FakeTicker : ITicker {
    public readonly System.Reactive.Subjects.Subject<long> Subject = new();
    public IObservable<long> Ticks => Subject;
    public void Tick(long n = 0) => Subject.OnNext(n);
}
```

- [ ] **Step 4: Run to verify pass** — full App unit suite. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "refactor: hoist the shared 1Hz ticker into an app-lifetime UiTicker service"`

---

### Task 8: App — `ConsentService` (pending cache, tombstones, lifecycle, resolve lane, prune)

**Files:**
- Create: `src/Capacitor.App/Services/IConsentService.cs`
- Create: `src/Capacitor.App/Services/ConsentService.cs`
- Test: `test/Capacitor.App.Tests.Unit/ConsentServiceTests.cs` (new)

**Interfaces:**
- Consumes: `ILocalControlOps.ResolveConsentAsync` (Task 4), `ConsentStreamEvent`/`ConsentSubscription` shape (Task 5 — injected as a delegate), `ITicker` (Task 7), `IDaemonClientService.Status`.
- Produces (Tasks 9/11 rely on these exact names):

```csharp
public enum ConsentResolveKind { Applied, AppliedRuleRejected, AlreadyDecided, RuleSkippedNoRequester, TransportFailure }
public enum ConsentRuleOutcome { NotRequested, Saved, Rejected, Unknown, SkippedNoRequester }
public sealed record ConsentResolveOutcome(ConsentResolveKind Kind, ConsentRuleOutcome RuleOutcome, string? Error);

public sealed class PendingConsent {
    public ConsentPendingDto Dto { get; }
    public string RequestId { get; }          // = Dto.RequestId (cache key / per-agent queue identity)
    public string PromptId { get; }           // = Dto.PromptId! (structural validation guarantees it)
    public DateTimeOffset DeadlineHint { get; }
    public DateTimeOffset PruneAfter { get; internal set; }
}

public interface IConsentService : IDisposable {
    IObservable<IChangeSet<PendingConsent, string>> Pending { get; } // background-thread mutations; consumers ObserveOn
    IObservable<int> PendingCount { get; }
    IObservable<Unit> EntryAdded { get; }                            // unconditional; the coordinator filters by visibility
    Task<ConsentResolveOutcome> ResolveAsync(PendingConsent target, bool allow, bool saveRule, CancellationToken ct);
}
```

- Constructor (all seams injectable for tests):

```csharp
public ConsentService(
    IDaemonClientService service,
    ILocalControlOps ops,
    ITicker ticker,
    Func<CancellationToken, IAsyncEnumerable<ConsentStreamEvent>> subscribe, // prod: ct => ConsentSubscription.RunAsync(service.DaemonName, ct)
    TimeProvider time,
    CancellationToken shutdownToken)
```

- [ ] **Step 1: Write the failing tests**

Test harness: a `FakeDaemonClientService`-style status subject (reuse/extend the existing fake), `ScriptedLocalControlOps` for acks, a controllable fake subscription:

```csharp
sealed class FakeConsentStream {
    readonly Channel<ConsentStreamEvent> _ch = Channel.CreateUnbounded<ConsentStreamEvent>();
    public int Attempts; // incremented per RunAsync call — asserts retry cadence
    public async IAsyncEnumerable<ConsentStreamEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
        Interlocked.Increment(ref Attempts);
        await foreach (var e in _ch.Reader.ReadAllAsync(ct)) {
            if (e is EndMarker) yield break;
            yield return e;
        }
    }
    // helpers: EmitSubscribed(), EmitPending(dto), EndAttempt() (yield-breaks the current enumeration)
}
```

Use `Microsoft.Extensions.Time.Testing.FakeTimeProvider` if already referenced (check the csproj; otherwise a manual fake time provider consistent with the repo's existing pattern — `rg -n "FakeTimeProvider|TimeProvider" test/` first).

Test cases (each a `[Test]`; exact expectations):

1. `Subscribes_only_with_consent2_capability` — status Connected + `["consent/1"]` → `Attempts == 0` and cache cleared; Connected + `["consent/1","consent/2"]` → `Attempts == 1`.
2. `Clear_happens_at_subscribed_not_before_dial` — pre-seed cache (via a first subscribe round), end attempt, emit nothing on the next attempt (dial "fails": `EndAttempt` before `Subscribed`) → cache retains entries; then `EmitSubscribed()` on a later attempt → cache empty.
3. `Replay_upserts_by_request_id_and_entryadded_fires_on_new_keys_only` — two pendings with distinct ids → 2 EntryAdded; a re-push of the same id+promptId → no EntryAdded, count stays 2.
4. `Tombstoned_prompt_id_is_dropped_and_survives_resubscribe` — resolve a pending to conclusion (ack Ok=true), then: `EmitPending(same identity)` → dropped; `EndAttempt` + new `Subscribed` + `EmitPending(same identity)` → STILL dropped (service-lifetime, spec §5 — the snapshot-before-ack/Subscribed-after-ack ordering); `EmitPending(different PromptId, same RequestId)` → admitted.
5. `Conclusive_ack_evicts_by_identity_including_a_replayed_fresh_instance` — pending A admitted; a fresh DTO instance with A's identity re-admitted after a resubscribe clear; resolve the ORIGINAL `PendingConsent` object → ack concludes → the replayed instance (same PromptId) is evicted too.
6. `Successor_with_same_request_id_survives_predecessors_ack` — A in cache; upsert B (same RequestId, different PromptId — replaces the cache slot); complete A's in-flight resolve with `Ok=false` → B still cached, 1 entry.
7. `Resolve_outcome_mapping` — table-drive the ack → outcome mapping:
   - `Ok=true, Error=null, RuleSaved=null`, saveRule:false → `(Applied, NotRequested)`
   - `Ok=true, Error=null, RuleSaved=true`, saveRule:true → `(Applied, Saved)`
   - `Ok=true, Error="store full", RuleSaved=false`, saveRule:true → `(AppliedRuleRejected, Rejected)`
   - `Ok=true, Error=null, RuleSaved=null (old-format ack)`, saveRule:true → `(Applied, Saved)` (spec §4.1's Ok=true+Error=null carve-out)
   - `Ok=false, RuleSaved=true`, saveRule:true → `(AlreadyDecided, Saved)`
   - `Ok=false, RuleSaved=null`, saveRule:true → `(AlreadyDecided, Unknown)`
   - `Ok=false, RuleSaved=null`, saveRule:false → `(AlreadyDecided, NotRequested)`
8. `Save_rule_guard_null_and_empty_requester` — target with `Requester: null` and another with `""`, `ResolveAsync(..., saveRule: true)` → the sent `ConsentResolveDto.SaveRule` is NULL (assert via ScriptedLocalControlOps capture), outcome `(RuleSkippedNoRequester, SkippedNoRequester)`; a non-empty requester sends `("allow", requester, null, null, null)`.
9. `Resolve_sends_the_targets_exact_prompt_id` — captured DTO's `PromptId == target.PromptId`, `RequestId == target.RequestId`.
10. `Transport_failure_keeps_the_entry_and_refreshes_prune_after` — scripted `LocalControlOpsException("daemon_unreachable", …)` → outcome `TransportFailure`, entry still cached, `PruneAfter == now + 5s`.
11. `Cancellation_propagates_and_keeps_the_entry` — pre-cancelled ct → `OperationCanceledException` from `ResolveAsync`, entry cached, no tombstone.
12. `Prune_removes_past_prune_after_but_skips_the_inflight_target` — entry with `DeadlineHint` in the past: advance fake time past `PruneAfter`, tick → removed. Second entry mid-resolve (ScriptedLocalControlOps holds the call on a TCS): advance past `PruneAfter`, tick → NOT removed; complete the ack → evicted by conclusion (never double-removed).
13. `Stream_end_while_connected_retries_after_1s` — `EndAttempt()`; fake-time advance 1s → `Attempts == 2` (and NOT before the advance).
14. `Leaving_connected_cancels_the_loop_and_retains_entries` — status → Unreachable: entries stay; `Attempts` stops growing.
15. `Deadline_hint_falls_back_on_unparseable_requested_at` — DTO with `RequestedAt: "garbage"` → `DeadlineHint ≈ now + TimeoutSeconds` (fake clock exact).

- [ ] **Step 2: Run to verify failure** — compile errors.

- [ ] **Step 3: Implement `ConsentService`**

Key mechanics (full file — implementer writes it with these exact behaviors):

```csharp
public sealed class ConsentService : IConsentService {
    readonly SourceCache<PendingConsent, string> _cache = new(p => p.RequestId);
    readonly HashSet<string> _tombstones = [];       // concluded PromptIds — service lifetime, no cap (spec §5)
    readonly Lock _lock = new();                     // guards _tombstones + _inFlightPromptId
    readonly SemaphoreSlim _lane = new(1, 1);        // one resolve at a time (PauseController discipline)
    string? _inFlightPromptId;                       // prune skip (spec §5)
    // + injected fields, a CompositeDisposable, loop CTS management
```

- **Status subscription** (constructor-scoped): on each `AttachStatus`:
  - Connected && caps contains `"consent/2"` → start the loop if not running (new CTS linked to `shutdownToken`).
  - Connected && caps lack `consent/2` → stop the loop AND `_cache.Clear()` (stale incarnation, spec §5).
  - Connecting/Unreachable → stop the loop; entries retained.
- **Loop**: `while (!ct.IsCancellationRequested) { await foreach (evt in subscribe(ct)) { Subscribed → _cache.Clear(); Pending(dto) → Upsert(dto); } await Task.Delay(1s, time, ct); }` — OCE exits; any other exception from the enumeration is contained (log to `Console.Error`) and falls through to the delay.
- **Upsert**: `lock` tombstone check → drop if `_tombstones.Contains(dto.PromptId)`; build `PendingConsent` (`DeadlineHint` = parse `RequestedAt` (`DateTimeOffset.TryParse`, round-trip style) + `TimeoutSeconds`, falling back to `time.GetUtcNow() + TimeoutSeconds`; `PruneAfter = DeadlineHint + 5s`); `var added = !_cache.Lookup(dto.RequestId).HasValue; _cache.AddOrUpdate(pc); if (added) _entryAdded.OnNext(Unit.Default);`
- **ResolveAsync**: `await _lane.WaitAsync(ct)` (OCE propagates); build DTO (guard: `sendRule = saveRule && !string.IsNullOrEmpty(target.Dto.Requester)`); set `_inFlightPromptId = target.PromptId` under lock; call ops; on `LocalControlOpsException` → clear in-flight, `target.PruneAfter = time.GetUtcNow() + 5s`, re-`AddOrUpdate(target)` if still the cached identity, return `TransportFailure`; on ack → `Conclude(target)` then map (mapping per test 7 exactly; `AppliedRuleRejected` when `Ok && ruleOutcome is Rejected or Unknown`; `RuleSkippedNoRequester` when the guard skipped; `AlreadyDecided` when `!Ok`); `finally { _inFlightPromptId = null; _lane.Release(); }`.
- **Conclude(target)**: `lock { _tombstones.Add(target.PromptId); }` then identity-guarded evict: `_cache.Edit(u => { var cur = u.Lookup(target.RequestId); if (cur.HasValue && cur.Value.PromptId == target.PromptId) u.Remove(target.RequestId); });` — the lookup-compare-remove inside ONE `Edit` is the atomicity (spec §5's "atomically records and evicts").
- **Prune** (ticker subscription, constructor-scoped): `var now = time.GetUtcNow(); _cache.Edit(u => { foreach (var p in u.Items.Where(p => now > p.PruneAfter && p.PromptId != Volatile.Read(ref _inFlightPromptId)).ToList()) { var cur = u.Lookup(p.RequestId); if (cur.HasValue && cur.Value.PromptId == p.PromptId) u.Remove(p.RequestId); } });`
- **Exposed**: `Pending => _cache.Connect()`, `PendingCount => _cache.CountChanged`, `EntryAdded`, `Dispose` (cancel loop CTS, dispose cache/subjects/lane).

Add class doc comments carrying the spec's WHY for: service-lifetime tombstones (never retired — a client-local `Subscribed` is not ordered against a concurrent ack; PromptIds are never-reused GUIDs so retention is free), clear-at-`Subscribed` (failed dial must not erase an actionable queue), and the prune's in-flight skip + transport refresh.

- [ ] **Step 4: Run to verify pass** — full App unit suite. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: ConsentService — status-driven subscription, identity-guarded cache, lifetime tombstones, prune hygiene"`

---

### Task 9: App — prompt window, ViewModel, coordinator, composition & shutdown

**Files:**
- Create: `src/Capacitor.App/ViewModels/ConsentPromptViewModel.cs`
- Create: `src/Capacitor.App/Views/ConsentPromptWindow.axaml` + `.axaml.cs`
- Create: `src/Capacitor.App/Services/ConsentPromptCoordinator.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (compose ConsentService + coordinator; shutdown order)
- Test: `test/Capacitor.App.Tests.Unit/ConsentPromptViewModelTests.cs`, `ConsentPromptCoordinatorTests.cs` (new)

**Interfaces:**
- Consumes: `IConsentService` (Task 8), `ITicker`, `IAppNotifier`.
- Produces: `ConsentPromptCoordinator.ShowPromptWindow()` (Task 11's tray menu target); `App` fields `_consent`, `_promptCoordinator` in the disposal lists.

- [ ] **Step 1: Write the failing VM tests** (headless via `AvaloniaSession`, fake ticker, fake `IConsentService` — a small `FakeConsentService` exposing a `SourceCache` + scriptable `ResolveAsync` results):

VM behavior contract (spec §6) the tests pin:

1. `Current_is_oldest_by_requested_at_then_id_and_position_text_reads_1_of_n` — 3 pendings → `Current` = oldest; `PositionText == "1 of 3"`, visible only when N > 1.
2. `Pin_survives_cache_changes_while_resolving_or_in_terminal_hold` — start resolve (scripted hold) → add a new older entry → `Current` unchanged.
3. `Requester_falls_back_display_then_id_then_unknown`; kind labels `agent→Agent, review→Review, review-flow→Review flow`; repo leaf via `RepoLabel.Leaf` with full path tooltip property.
4. `Countdown_ticks_and_expiry_is_not_a_verdict` — fake time past `DeadlineHint`, tick → `CountdownText == "Response time elapsed — unanswered requests are denied by the daemon"` AND buttons remain enabled (spec §6); a subsequent Allow click that acks `Ok=true` → `Applied` (the wall-clock-step case); one acking `Ok=false` → AlreadyDecided path.
5. `Buttons_send_the_pinned_targets_prompt_id` — each of the three commands → `FakeConsentService` captured `(target.PromptId, allow, saveRule)` = (`AllowOnce`: allow=true saveRule=false; `AllowRemember`: true/true; `Deny`: false/false).
6. `Allow_remember_hidden_for_null_and_empty_requester` — `AllowRememberVisible` false for both; true otherwise.
7. `Already_decided_holds_2_ticks_then_advances` — scripted `(AlreadyDecided, NotRequested)` → `PhaseText == "Already decided"`, buttons hidden; 2 fake ticks → advances to next pending.
8. `Already_decided_discloses_rule_outcome_after_allow_remember` — scripted `(AlreadyDecided, Saved)` → text `Already decided — your allow rule for {requester} was still saved.`; `(AlreadyDecided, Rejected)` → `Already decided — no rule was saved.`; `(AlreadyDecided, Unknown)` → `Already decided — this daemon version doesn't report whether your allow rule was saved.`
9. `Applied_advances_immediately_and_rule_warnings_toast` — `(Applied, Saved)` → no toast, advance; `(AppliedRuleRejected, Rejected)` → notifier received `Decision applied — rule not saved: {reason}`; `(Applied→RuleSkippedNoRequester …)` → warning; `(AppliedRuleRejected, Unknown)` → `Decision applied — this daemon version doesn't report whether the rule was saved`.
10. `Transport_failure_reenables_buttons_and_keeps_current` — scripted `TransportFailure` → toast `Daemon unreachable — the request is still pending`, buttons enabled, same `Current`.
11. `Expiry_never_preempts_inflight_resolve` — resolve held; fake time past deadline + tick → `CountdownText == "Expiring…"`, no advance; ack `Ok=true` → Applied.
12. `Advance_on_pinned_removal_in_expired_state` — expired display (no resolve); service prune removes the pinned entry → VM advances/closes-empty.
13. `Resolving_disables_all_buttons` (no double-submit).
14. `Cancellation_is_a_silent_abort` — scripted OCE → no toast, entry kept, buttons re-enabled.

Coordinator tests:

15. `Raise_on_entry_added_while_window_not_visible_marshals_to_ui_thread` — EntryAdded fired from a background thread → window created+shown (headless lifetime); fired again while visible → no second window, no re-activation call.
16. `Close_is_defer_reopen_via_show` — close the window (no decision) → cache untouched; `ShowPromptWindow()` → a fresh window instance shows the same queue.
17. `Dispose_closes_the_window` — for the shutdown path.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

`ConsentPromptViewModel` (constructor: `IConsentService consent, IAppNotifier notifier, ITicker ticker, TimeProvider time, CancellationToken shutdownToken`): activation-scoped (`WhenActivated`) subscription to `consent.Pending` sorted by (`RequestedAt`, `RequestId` ordinal) into a stable `ObservableCollectionExtended` (the AI-1651 stable-collection lesson), OAPH projections for all display text, `enum Phase { Ready, Resolving, AlreadyDecided, Expired }` state machine, 2-tick terminal holds driven by `ticker.Ticks`, commands calling `consent.ResolveAsync(pinned, …)` with the OCE-silent catch. Copy strings exactly as listed in Global Constraints + step 1. Pin logic: `_pinned` field set when `Current` first renders or after each advance; advance = clear pin, take current sorted head.

`ConsentPromptWindow.axaml`: `rxui:ReactiveWindow x:TypeArguments="vm:ConsentPromptViewModel"`, `Width="460" Height="260"`, `CanResize="False"`, `Topmost="True"`, `WindowStartupLocation="CenterScreen"`, `Icon` same as MainWindow. Layout: requester (bold, 16pt), kind · vendor row, repo leaf with `ToolTip.Tip`, countdown line, phase text, button row (`Allow once` / `Allow & remember` with the verbatim tooltip / `Deny`), position text top-right. Code-behind mirrors `MainWindow.axaml.cs`'s notifier attachment: a `WindowNotificationManager` created in `OnLoaded` with `Notifier` property wiring (extend `AppNotifier` usage exactly the way MainWindow does — read `MainWindow.axaml.cs` first and copy its subscription discipline).

`ConsentPromptCoordinator`:

```csharp
/// Owns the single consent prompt window (spec §6): at most one instance at a time; closing
/// releases it (an explicit defer — the queue is untouched) and a later raise re-creates it.
/// The service knows nothing about windows — THIS class filters the unconditional EntryAdded
/// signal by visibility and marshals to the UI thread.
public sealed class ConsentPromptCoordinator : IDisposable {
    readonly Func<ConsentPromptWindow> _windowFactory;
    readonly IDisposable _subscription;
    ConsentPromptWindow? _window;

    public ConsentPromptCoordinator(IConsentService consent, Func<ConsentPromptWindow> windowFactory) {
        _windowFactory = windowFactory;
        _subscription = consent.EntryAdded.Subscribe(_ =>
            Dispatcher.UIThread.Post(() => { if (_window is not { IsVisible: true }) ShowPromptWindow(); }));
    }

    public void ShowPromptWindow() {
        if (_window is null) {
            var w = _windowFactory();
            w.Closed += (_, _) => { if (ReferenceEquals(_window, w)) _window = null; };
            _window = w;
            w.Show();
        }
        _window.Show();
        _window.Activate();
    }

    public void Dispose() {
        _subscription.Dispose();
        _window?.Close();
        _window = null;
    }
}
```

`App.axaml.cs` `StartAsync` additions (after `actions`):

```csharp
_consent = new ConsentService(service, ops, ticker,
    ct => ConsentSubscription.RunAsync(service.DaemonName, ct), TimeProvider.System, _shutdown.Token);
_promptCoordinator = new ConsentPromptCoordinator(_consent,
    () => new ConsentPromptWindow {
        DataContext = new ConsentPromptViewModel(_consent, notifier, ticker, TimeProvider.System, _shutdown.Token),
        Notifier = notifier,
    });
```

Disposal lists (BOTH the shutdown list in `DisposeAndShutdownAsync` and the startup-failure list) become `[_tray, _trayVm, _promptCoordinator, _consent, _pause]` — coordinator before service before the daemon client (spec §5 shutdown order); the corresponding null-out lines on the failure path gain the two new fields. `AppStartupTests`' disposal-order recording tests get the two new entries asserted in order.

- [ ] **Step 4: Run to verify pass** — full App suite (including the updated AppStartupTests). Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: consent prompt window — pinned queue, honest terminal states, coordinator-owned raise"`

---

### Task 10: App — Activity tab

**Files:**
- Create: `src/Capacitor.App/ViewModels/ActivityViewModel.cs`
- Modify: `src/Capacitor.App/Views/MainWindow.axaml` (+ `.axaml.cs` if control lookups change)
- Test: `test/Capacitor.App.Tests.Unit/ActivityViewModelTests.cs` (new); run `MainWindowSmokeTests`/`AgentGridTests` to prove the tab restructure broke nothing

**Interfaces:**
- Consumes: `ConsentDecisionLogReader.ReadTail` (Task 6 — injected as `Func<int, ConsentLogReadResult>` for tests), `ITicker`, stat provider seam `Func<(DateTime? mtime, long? length)[]>` or simpler: inject `Func<string?>` — see below.
- Produces: `ActivityViewModel` with `ReadOnlyObservableCollection<ActivityRow> Rows`, `bool IsEmpty`, `void OnTabVisibleChanged(bool visible)`, `void OnOwnResolution()` (Task 9's VM calls it? No — keep decoupled: MainWindowViewModel wires `consent` acks? Simplest per spec: the prompt VM's conclusive ack path calls `IActivityRefresh.RequestRefresh()`; implement as an interface on ActivityViewModel registered at composition. See Step 3.)

- [ ] **Step 1: Write the failing tests**

`ActivityRow` mapping + refresh semantics (spec §7):

1. `Rows_map_records_with_fallbacks_and_source_labels` — record → row: local time `yyyy-MM-dd HH:mm:ss` (unparseable `decided_at` renders verbatim); requester fallback chain; kind labels; repo leaf + full tooltip; source labels `owner→owner, rule[7]→rule, default→default policy, prompt_user→you, prompt_timeout→timeout, prompt_no_ui→no UI attached, weird→weird` (verbatim); outcome `allowed`/`denied` drives `IsAllowed` for the badge.
2. `Complete_read_replaces_rows_including_to_empty` — first read 2 records; second scripted read `([], Complete=true)` → rows empty, `IsEmpty` true.
3. `Incomplete_read_keeps_last_good_rows` — `(1 record, Complete=false)` after a good 2-record read → still the 2 rows; with NO previous rows → shows the 1 partial row.
4. `Stat_poll_rereads_only_on_change_every_2_ticks_while_visible` — scripted stat function; tick × 2 with unchanged stats → no re-read (count the injected read calls); change stats → re-read on the next 2nd tick; `OnTabVisibleChanged(false)` → no polling.
5. `Tab_visible_triggers_immediate_refresh`.
6. `Own_resolution_refresh_is_eventual` — `RequestRefresh()` → one immediate read; the record "appears no later than the next poll" (second read after stat change on a later tick).
7. `Poll_survives_a_throwing_stat_or_read` — a stat/read that throws once → swallowed, polling continues next tick.

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

`ActivityViewModel` (constructor: `Func<ConsentLogReadResult> read` — prod `() => ConsentDecisionLogReader.ReadTail(daemonName, 200)`; `Func<(DateTime?, long?), (DateTime?, long?)>`? Keep it simple and testable: `Func<string>` **statKey** — prod computes `$"{mtime.Ticks}:{length}"` for both files via `File.GetLastWriteTimeUtc`/`FileInfo.Length` inside try/catch returning `"absent"` on failure; the VM re-reads when the key differs from the last poll's; `ITicker ticker`): every-2nd-tick gate via a counter while `_visible`; `RequestRefresh()` public (immediate read). Rows in a stable `ObservableCollectionExtended<ActivityRow>` replaced only on `Complete || Rows.Count == 0` per spec §7. All mutations marshalled with `ObserveOn(RxSchedulers.MainThreadScheduler)` where sourced from the ticker (already UI-thread) — direct is fine, document it.

`ActivityRow` (plain record): `Time, Outcome ("allowed"/"denied"), IsAllowed, Requester, KindLabel, RepoLeaf, RepoFull, Vendor, SourceLabel`.

`MainWindow.axaml` restructure: keep the header StackPanel (identity/status/buttons) as-is above a `TabControl` occupying the `*` row:

```xml
<TabControl Grid.Row="1" Margin="0,8,0,0">
    <TabItem Header="Agents">
        <!-- the ENTIRE existing agents block (divider, title, empty state, ScrollViewer+grid)
             moves here unchanged — control names preserved so existing smoke tests keep finding
             them in the window's name scope -->
    </TabItem>
    <TabItem Header="Activity">
        <Grid RowDefinitions="Auto,*">
            <TextBlock x:Name="ActivityEmptyText" Text="No decisions yet"
                       IsVisible="{Binding Activity.IsEmpty}" HorizontalAlignment="Center" Margin="0,24,0,0" />
            <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Auto">
                <StackPanel>
                    <Grid x:Name="ActivityHeader" ColumnDefinitions="150,70,120,90,140,90,110" Margin="0,0,0,4"
                          IsVisible="{Binding !Activity.IsEmpty}">
                        <!-- Time / Outcome / Requester / Kind / Repo / Vendor / Source bold headers,
                             same style as AgentsGridHeader -->
                    </Grid>
                    <ItemsControl ItemsSource="{Binding Activity.Rows}">
                        <!-- one Grid row per ActivityRow, same column widths; Outcome TextBlock
                             Foreground green (#2E7D32) when IsAllowed else red (#D32F2F) -->
                    </ItemsControl>
                </StackPanel>
            </ScrollViewer>
        </Grid>
    </TabItem>
</TabControl>
```

`MainWindowViewModel` gains `public ActivityViewModel Activity { get; }` (constructor-injected). Tab visibility: bind `TabControl.SelectionChanged` in `MainWindow.axaml.cs` to `Activity.OnTabVisibleChanged(activityTabSelected && windowVisible)`; also window `IsVisible` changes (`Opened`/`Hidden` — hook the same events the window already handles). Own-resolution refresh: `App.axaml.cs` composition passes the SAME `ActivityViewModel` instance's `RequestRefresh` as an `Action` into `ConsentPromptViewModel` (add an optional `Action? onConcluded = null` ctor param there, invoked after every conclusive ack) — one composition-root wire, no service coupling.

Update `App.BuildAndShowMainWindow` and every VM-construction call site accordingly.

- [ ] **Step 4: Run to verify pass** — full App suite (smoke tests prove the tab move). Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat: Activity tab — decision-log feed with stat-poll refresh and last-good display"`

---

### Task 11: App — tray Attention row + Review menu item; final sweep

**Files:**
- Modify: `src/Capacitor.App/ViewModels/TrayModels.cs`, `TrayViewModel.cs`, `Views/TrayMenuBuilder.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (pass consent + coordinator into `TrayViewModel`)
- Test: extend `test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs`, `TrayAdapterTests.cs`

**Interfaces:**
- Consumes: `IConsentService.PendingCount` (Task 8), `ConsentPromptCoordinator.ShowPromptWindow` (Task 9).
- Produces: `TrayMenuModel(TrayState State, int RunningCount, string Header, IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause, int PendingConsent)`; `TrayViewModel.ReviewPendingCommand`.

- [ ] **Step 1: Write the failing tests**

`TrayViewModelTests` additions (follow the file's existing Build/Project-driving patterns):

1. `Pending_consent_asserts_attention_over_idle_and_running` — Connected+idle with pendingCount 1 → `State == Attention`, header `"{daemonName}: 1 launch awaiting approval"`; Connected+2 agents running with pendingCount 3 → `Attention`, header `"{daemonName}: 3 launches awaiting approval"`, `RunningCount` still 2 (the badge keeps the agent count).
2. `Connection_trouble_rows_keep_precedence` — Unreachable/unreachable-reason with pendingCount 5 → still `Stopped` (row 1); Connected+`reconnecting` with pending → `Attention` with the RECONNECTING header (row 5 wins the copy).
3. `Review_item_visible_only_with_pending` — `MenuModel.PendingConsent == 0` → builder emits no review item; `> 0` → item present between the agents section and the pause item, invoking `ReviewPendingCommand` calls the injected open action (assert via recorded delegate).

`TrayAdapterTests`: extend the menu-shape assertions for the new item (label `Review pending launches…`, placement).

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement**

`TrayModels.cs`: append `int PendingConsent` to `TrayMenuModel`.

`TrayViewModel`: ctor gains `IConsentService consent, Action? openReviewPrompts = null`; `ReviewPendingCommand = ReactiveCommand.Create(openReviewPrompts ?? (() => { }));` combine stream becomes `service.Status.CombineLatest(snapshots, pause.State, actions.StopsInFlight, consent.PendingCount.StartWith(0), (status, snap, pauseState, inFlight, pending) => Build(...))`. In `Build`/`Project`: after the existing ten-row projection, apply the new rule (spec §8):

```csharp
// Row 11 (spec §8): pending consent asserts Attention only while Connected — a launch is
// awaiting the owner. Connection-trouble rows above keep precedence; the running-count badge
// keeps the agent count.
if (status.State == AttachState.Connected && pendingConsent > 0 && state is TrayState.Idle or TrayState.Running)
    return BuildWithAttention(...); // state = Attention, header body $"{pendingConsent} launch{(pendingConsent == 1 ? "" : "es")} awaiting approval"
```

(Restructure `Build`/`HeaderText` minimally: pass `pendingConsent` down; the pending header wins only when the row-11 rule fired.)

`TrayMenuBuilder`: between the agents section and the pause toggle, when `model.PendingConsent > 0`, add a `NativeMenuItem { Header = "Review pending launches…" }` wired to `ReviewPendingCommand` following the file's existing item-construction pattern (mind the AI-1651 lesson comment in that file: `IsEnabled` assigned LAST after `Command`).

`App.axaml.cs`: `_trayVm = new TrayViewModel(service, _pause, actions, _consent, openMainWindow: …, quit: …, openReviewPrompts: _promptCoordinator.ShowPromptWindow);` (adjust parameter order to taste; update `TrayViewModel` test constructions with a `FakeConsentService`).

- [ ] **Step 4: Run to verify pass** — full App suite.

- [ ] **Step 5: Final sweep + commit**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'  # no output
git add -A && git commit -m "feat: tray Attention on pending consent + Review pending launches menu item"
```

Spec-conformance spot-check before hand-off: §6 copy strings verbatim in the built XAML/VM, §9 table rows each traceable to a test, disposal order `[tray, trayVm, promptCoordinator, consent, pause]` asserted.

---

## Self-Review Notes (already applied)

- **Spec coverage:** §4.1 → Tasks 1–4; §4.2 → Task 5; §4.3 → Task 2; §4.4 → Tasks 1+6; §5 → Task 8; §6 → Task 9; §7 → Task 10; §8 → Task 11; §9/§10 rows distributed to their owning tasks' test lists.
- **Deliberate exclusions:** no README change (no CLI surface); no new `kcap daemon consent` verbs (spec §11); dock/notification work stays in AI-1653.
- **Type consistency:** `ConsentResolveOutcome`/`PendingConsent`/`IConsentService` names identical across Tasks 8–11; `ITicker` identical across 7–10; DTO shapes identical across 1–5.
- **Known churn points for implementers:** Task 1 intentionally leaves call sites mechanically null-extended (Tasks 2–3 rewire them); Task 7 touches many test files but only constructor arity; Task 10's XAML move must preserve control names for the existing smoke tests.
