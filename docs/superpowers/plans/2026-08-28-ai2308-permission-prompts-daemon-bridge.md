# Permission prompts through the daemon bridge (AI-2308) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A PTY-hosted Claude/Codex session's permission prompt reaches the desktop app as a card on the Chat tab (plus rail pip and tray Attention), is answered there or on the web — whichever claims first — and the answer reaches the vendor hook, with every settlement pushed back so no indicator is ever left lit.

**Architecture:** A new append-only frame family on the local control socket (`PermissionSubscribe`/`PermissionResolve` → `PermissionPending`/`PermissionResolved`/`PermissionAck`). In the daemon, a `PermissionPromptBroker` is the single claim point: the bridge registers each attributed hook request there first, runs a detached server leg that feeds the server's decision into the same claim, and answers the hook from whichever settlement won. In the app, a `PermissionService` mirrors `ConsentService`; the Chat tab, rail and tray derive from its cache.

**Tech Stack:** .NET 10 NativeAOT, System.Text.Json source-gen, SignalR client, Avalonia + ReactiveUI + DynamicData, TUnit on Microsoft Testing Platform.

**Spec:** `docs/superpowers/specs/2026-08-28-ai2308-permission-prompts-daemon-bridge-design.md` — the plan argues from it; executors read both. Section numbers below (§1, §2.3.1 …) refer to the spec.

## Global Constraints

- `FrameType` values are never reused or renumbered; new values are exactly `20, 21` (client→daemon) and `77, 78, 79` (daemon→client).
- `LocalControlCapabilities.Current` gains `"permission/1"` in the same commit as the `LocalControlServer` routing arms — never advertised without a live handler.
- Every JSON payload on the local wire is snake_case via a source-generated context; no reflection serialization (NativeAOT: `dotnet publish -c Release` must show no `IL2026`/`IL3050`).
- Bounds on the wire (§1): `MaxToolNameBytes = 512`, `MaxElementBytes = 64 * 1024`, `MaxAgentIdBytes = 128`; `session_id`/`agent_id` canonicalized by `Guid.TryParse` → `ToString("N")`.
- Vocabulary (§1): `Outcome ∈ allow|deny|withdrawn`; `Source ∈ app|server|agent_gone|no_ui|daemon_shutdown`; `RespondOutcome ∈ Applied|NotPending|Failed`.
- Comments: scarce, no ticket ids, no change narration (CLAUDE.md "Comments"). Commit subjects ≤ 80 chars, imperative, no reference (AI-2308 has no GitHub issue yet).
- Tests: TUnit; a single suite runs via `dotnet run --project test/<Suite>/<Suite>.csproj -- --treenode-filter "/*/*/<Class>/*"`; `[NotInParallel]` rules per CLAUDE.md; `TempDir`/`TempDaemonStore` from Helpers; `EnvScope.Exclusive` for env vars.
- Agent-owned files are never opened with `File.ReadAllText` (irrelevant here — no transcript reads are added).
- `docs/CHANGES.md` gets a section; `README.md` is untouched (no user-facing CLI surface changes).

---

## File map

| Area | Create | Modify |
|---|---|---|
| Core wire | `src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs`, `…/PermissionSubscription.cs`, `…/PermissionDecisionLog.cs`, `src/Capacitor.Cli.Core/ClaudePermissions.cs` | `FrameType.cs`, `FrameCodec.cs`, `LocalFrame.cs`, `LocalControlOps.cs` |
| Daemon | `src/Capacitor.Cli.Daemon/Services/OwnerOnlyJsonlLog.cs`, `…/PermissionDecisionLog.cs`, `…/PermissionPromptBroker.cs`, `…/PermissionIpc.cs`, `…/PermissionRequestAbandonedException.cs` | `LaunchConsentDecisionLog.cs`, `LocalControlCapabilities.cs`, `LocalControlServer.cs`, `ServerConnection.cs`, `ConnectionRetry.cs` (no change — verified), `LocalPermissionBridge.cs`, `AgentOrchestrator.cs`, `DaemonRunner.cs` |
| CLI | `src/Capacitor.Cli/Commands/HookAgentId.cs` | `PermissionRequestCommand.cs`, `Harness/CodexHookCommand.cs` |
| App | `src/Capacitor.App/Services/IPermissionService.cs`, `…/PermissionService.cs`, `src/Capacitor.App/ViewModels/PermissionCardViewModel.cs` | `ChatTabViewModel.cs`, `Views/ChatTabView.axaml`, `WorkspaceViewModel.cs`, `RailSessionViewModel.cs`, `RailWorktreeViewModel.cs`, `RailRepoViewModel.cs`, `SessionRailViewModel.cs`, `TrayViewModel.cs`, `App.axaml.cs` |
| Tests | one new test class per new type, named `<Type>Tests`, in the suite mirroring the prod project | the constructor call sites listed per task |

---

### Task 1: Frame values and codec arms

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` (`MaxPayload` → `internal`; the two text arms)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs` (`PermissionJson`)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecPermissionTests.cs`

**Interfaces:**
- Produces: `FrameType.PermissionSubscribe = 20`, `PermissionResolve = 21`, `PermissionPending = 77`, `PermissionResolved = 78`, `PermissionAck = 79`; `LocalFrame.PermissionJson(FrameType type, string json)`; `internal const int FrameCodec.MaxPayload`.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class FrameCodecPermissionTests {
    static async Task<LocalFrame> RoundTrip(LocalFrame f) {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, f, CancellationToken.None);
        ms.Position = 0;
        return (await FrameCodec.ReadAsync(ms, CancellationToken.None))!;
    }

    [Test]
    [Arguments(FrameType.PermissionSubscribe)]
    [Arguments(FrameType.PermissionResolve)]
    [Arguments(FrameType.PermissionPending)]
    [Arguments(FrameType.PermissionResolved)]
    [Arguments(FrameType.PermissionAck)]
    public async Task Permission_frames_roundtrip_with_text_payload(FrameType type) {
        var f = await RoundTrip(LocalFrame.PermissionJson(type, """{"k":"v"}"""));
        await Assert.That(f.Type).IsEqualTo(type);
        await Assert.That(f.Text).IsEqualTo("""{"k":"v"}""");
    }

    [Test]
    public async Task Subscribe_frame_roundtrips_with_empty_payload() {
        var f = await RoundTrip(new LocalFrame(FrameType.PermissionSubscribe));
        await Assert.That(f.Type).IsEqualTo(FrameType.PermissionSubscribe);
        await Assert.That(f.Text).IsEqualTo("");
    }

    [Test]
    public async Task Permission_frame_values_are_stable_wire_bytes() {
#pragma warning disable TUnitAssertions0005
        await Assert.That((byte)FrameType.PermissionSubscribe).IsEqualTo((byte)20);
        await Assert.That((byte)FrameType.PermissionResolve).IsEqualTo((byte)21);
        await Assert.That((byte)FrameType.PermissionPending).IsEqualTo((byte)77);
        await Assert.That((byte)FrameType.PermissionResolved).IsEqualTo((byte)78);
        await Assert.That((byte)FrameType.PermissionAck).IsEqualTo((byte)79);
#pragma warning restore TUnitAssertions0005
    }

    [Test]
    public async Task Max_payload_is_eight_mebibytes() {
        await Assert.That(FrameCodec.MaxPayload).IsEqualTo(8 * 1024 * 1024);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/FrameCodecPermissionTests/*"`
Expected: build error — `PermissionSubscribe` does not exist.

- [ ] **Step 3: Add the values, the codec arms and the constructor**

`FrameType.cs` — append after `ConsentRulesPut = 14,` in the client→daemon block:

```csharp
    // Permission control frames — values append-only
    PermissionSubscribe = 20, // long-lived: replay pending + push PermissionPending/PermissionResolved
    PermissionResolve   = 21, // one-shot: settle a pending request (Text = PermissionResolveDto JSON)
```

and after `ConsentAck = 74,` in the daemon→client block:

```csharp
    PermissionPending  = 77, // Text = PermissionPendingDto JSON, pushed on PermissionSubscribe
    PermissionResolved = 78, // Text = PermissionResolvedDto JSON, pushed on every settlement
    PermissionAck      = 79, // Text = PermissionAckDto JSON, reply to PermissionResolve
```

`FrameCodec.cs` — change `const int MaxPayload` to `internal const int MaxPayload = 8 * 1024 * 1024;` and add to BOTH the `Encode` and `Decode` text arms (the `or FrameType.ConsentAck or FrameType.DaemonStatus` list):

```csharp
            or FrameType.PermissionSubscribe or FrameType.PermissionResolve
            or FrameType.PermissionPending or FrameType.PermissionResolved or FrameType.PermissionAck
```

`LocalFrame.cs` — beside `ConsentJson`:

```csharp
    /// Constructs any of the permission control frames, whose payload is UTF-8 JSON
    /// (snake_case via PermissionIpcJsonContext) carried in Text — see PermissionIpc.cs.
    public static LocalFrame PermissionJson(FrameType type, string json) => new(type) { Text = json };
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command. Expected: 8 passed (5 parametrized + 3).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/FrameType.cs src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/FrameCodecPermissionTests.cs
git commit -m "Reserve the permission frame family on the local control socket"
```

---

### Task 2: Wire DTOs, bounds and the decision record

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/PermissionDecisionLog.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionWireContractsTests.cs`

**Interfaces:**
- Produces:
  - `PermissionPendingDto(string RequestId, string AgentId, string SessionId, string Vendor, string ToolName, JsonElement? ToolInput, JsonElement? Suggestions, bool ToolInputOmitted, bool SuggestionsOmitted, string RequestedAt)`
  - `PermissionResolveDto(string RequestId, string Decision, JsonElement? ApplyPermissions, JsonElement? UpdatedInput)`
  - `PermissionResolvedDto(string RequestId, string Outcome, string Source)`
  - `PermissionAckDto(bool Ok, string? Error)`
  - `PermissionIpcJsonContext` (snake_case, all four)
  - `static class PermissionWire { const int MaxToolNameBytes = 512; const int MaxElementBytes = 64 * 1024; const int MaxAgentIdBytes = 128; static string? Canonical(string? id); static bool IsPendingStructurallyValid(PermissionPendingDto? dto) }`
  - `PermissionDecisionRecord(string DecidedAt, string AgentId, string SessionId, string Vendor, string ToolName, string Outcome, string Source)` + `PermissionDecisionJsonContext`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class PermissionWireContractsTests {
    static JsonElement El(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }

    [Test]
    public async Task Pending_dto_roundtrips_and_writes_snake_case_with_nulls_and_flags() {
        var dto = new PermissionPendingDto("r1", "a1", "s1", "claude", "Bash", El("""{"command":"ls"}"""), null, false, true, "2026-08-28T10:00:00.0000000+00:00");
        var json = JsonSerializer.Serialize(dto, PermissionIpcJsonContext.Default.PermissionPendingDto);
        await Assert.That(json).Contains("\"request_id\":\"r1\"");
        await Assert.That(json).Contains("\"tool_input\":{\"command\":\"ls\"}");
        await Assert.That(json).Contains("\"suggestions\":null");
        await Assert.That(json).Contains("\"suggestions_omitted\":true");
        var back = JsonSerializer.Deserialize(json, PermissionIpcJsonContext.Default.PermissionPendingDto)!;
        await Assert.That(back.RequestId).IsEqualTo("r1");
        await Assert.That(back.ToolInput!.Value.GetProperty("command").GetString()).IsEqualTo("ls");
        await Assert.That(back.SuggestionsOmitted).IsTrue();
    }

    [Test]
    public async Task Empty_object_decodes_to_nulls_and_false_flags() {
        var dto = JsonSerializer.Deserialize("{}", PermissionIpcJsonContext.Default.PermissionPendingDto)!;
        await Assert.That(dto.RequestId).IsNull();
        await Assert.That(dto.ToolInput).IsNull();
        await Assert.That(dto.ToolInputOmitted).IsFalse();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(dto)).IsFalse();
    }

    [Test]
    public async Task Structural_validity_requires_ids_vendor_and_time_but_not_tool_name() {
        var ok = new PermissionPendingDto("r1", "a1", "s1", "codex", "", null, null, false, false, "t");
        await Assert.That(PermissionWire.IsPendingStructurallyValid(ok)).IsTrue();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(ok with { AgentId = "" })).IsFalse();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(ok with { SessionId = "" })).IsFalse();
        await Assert.That(PermissionWire.IsPendingStructurallyValid(ok with { RequestedAt = "" })).IsFalse();
    }

    [Test]
    public async Task Resolve_resolved_and_ack_dtos_roundtrip() {
        var resolve = new PermissionResolveDto("r1", "allow", El("""[{"type":"toolAlwaysAllow","tool":"Bash"}]"""), null);
        var rjson = JsonSerializer.Serialize(resolve, PermissionIpcJsonContext.Default.PermissionResolveDto);
        await Assert.That(rjson).Contains("\"apply_permissions\":[{\"type\":\"toolAlwaysAllow\",\"tool\":\"Bash\"}]");
        await Assert.That(rjson).Contains("\"updated_input\":null");

        var resolved = new PermissionResolvedDto("r1", "deny", "agent_gone");
        var sjson = JsonSerializer.Serialize(resolved, PermissionIpcJsonContext.Default.PermissionResolvedDto);
        await Assert.That(sjson).IsEqualTo("{\"request_id\":\"r1\",\"outcome\":\"deny\",\"source\":\"agent_gone\"}");

        var ack = JsonSerializer.Deserialize("{\"ok\":false,\"error\":\"x\"}", PermissionIpcJsonContext.Default.PermissionAckDto)!;
        await Assert.That(ack.Ok).IsFalse();
        await Assert.That(ack.Error).IsEqualTo("x");
    }

    [Test]
    [Arguments("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", "6ba7b8109dad11d180b400c04fd430c8")]
    [Arguments("6ba7b8109dad11d180b400c04fd430c8", "6ba7b8109dad11d180b400c04fd430c8")]
    [Arguments("6BA7B8109DAD11D180B400C04FD430C8", "6ba7b8109dad11d180b400c04fd430c8")]
    [Arguments("not-a-guid", null)]
    [Arguments("", null)]
    [Arguments(null, null)]
    public async Task Canonical_parses_any_guid_shape_to_n_form(string? input, string? expected) {
        await Assert.That(PermissionWire.Canonical(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task Worst_case_pending_frame_writes_and_reads_under_the_codec_cap() {
        var name = new string('"', PermissionWire.MaxToolNameBytes);               // every byte escapes to \"
        var key  = new string('\\', PermissionWire.MaxAgentIdBytes);               // every byte escapes to \\
        var big  = "\"" + new string('x', PermissionWire.MaxElementBytes - 2) + "\"";
        var dto  = new PermissionPendingDto("r1", key, "s1", "claude", name, El(big), El(big), false, false, "t");
        var json = JsonSerializer.Serialize(dto, PermissionIpcJsonContext.Default.PermissionPendingDto);
        await Assert.That(Encoding.UTF8.GetByteCount(json) < FrameCodec.MaxPayload).IsTrue();

        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, LocalFrame.PermissionJson(FrameType.PermissionPending, json), CancellationToken.None);
        ms.Position = 0;
        var back = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(back!.Text).IsEqualTo(json);
    }

    [Test]
    public async Task Decision_record_writes_snake_case() {
        var rec = new PermissionDecisionRecord("t", "a1", "s1", "claude", "Bash", "allow", "app");
        var json = JsonSerializer.Serialize(rec, PermissionDecisionJsonContext.Default.PermissionDecisionRecord);
        await Assert.That(json).IsEqualTo("{\"decided_at\":\"t\",\"agent_id\":\"a1\",\"session_id\":\"s1\",\"vendor\":\"claude\",\"tool_name\":\"Bash\",\"outcome\":\"allow\",\"source\":\"app\"}");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionWireContractsTests/*"`
Expected: build error — `PermissionPendingDto` missing.

- [ ] **Step 3: Create `PermissionIpc.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the permission control frames. snake_case on the wire; shared verbatim
/// by the daemon, the CLI, and the desktop app. Every member is always emitted (nulls written).
public sealed record PermissionPendingDto(
    string RequestId, string AgentId, string SessionId, string Vendor, string ToolName,
    JsonElement? ToolInput, JsonElement? Suggestions, bool ToolInputOmitted, bool SuggestionsOmitted,
    string RequestedAt);

public sealed record PermissionResolveDto(
    string RequestId, string Decision, JsonElement? ApplyPermissions, JsonElement? UpdatedInput);

/// Outcome: allow|deny|withdrawn. Source: app|server|agent_gone|no_ui|daemon_shutdown.
public sealed record PermissionResolvedDto(string RequestId, string Outcome, string Source);

public sealed record PermissionAckDto(bool Ok, string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PermissionPendingDto))]
[JsonSerializable(typeof(PermissionResolveDto))]
[JsonSerializable(typeof(PermissionResolvedDto))]
[JsonSerializable(typeof(PermissionAckDto))]
public partial class PermissionIpcJsonContext : JsonSerializerContext;

/// The bounds every caller-controlled value must satisfy before it rides a frame: the codec
/// rejects a frame over its cap and a rejected replay would kill every subscription forever.
public static class PermissionWire {
    public const int MaxToolNameBytes = 512;
    public const int MaxElementBytes  = 64 * 1024;
    public const int MaxAgentIdBytes  = 128;

    /// A GUID in any case, with or without dashes, as "N"; null when the value is not a GUID.
    public static string? Canonical(string? id) =>
        !string.IsNullOrEmpty(id) && Guid.TryParse(id, out var g) ? g.ToString("N") : null;

    /// STJ source-gen leaves a missing member null and `{}` decodes fine; tool_name may be empty.
    public static bool IsPendingStructurallyValid(PermissionPendingDto? dto) =>
        dto is not null
        && !string.IsNullOrEmpty(dto.RequestId)
        && !string.IsNullOrEmpty(dto.AgentId)
        && !string.IsNullOrEmpty(dto.SessionId)
        && !string.IsNullOrEmpty(dto.Vendor)
        && dto.ToolName is not null
        && !string.IsNullOrEmpty(dto.RequestedAt);
}
```

- [ ] **Step 4: Create `PermissionDecisionLog.cs` (Core: the record only)**

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// One line of permission-decisions.jsonl. Outcome: allow|deny|withdrawn.
/// Source: app|server|agent_gone|no_ui.
public sealed record PermissionDecisionRecord(
    string DecidedAt, string AgentId, string SessionId, string Vendor,
    string ToolName, string Outcome, string Source);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PermissionDecisionRecord))]
public partial class PermissionDecisionJsonContext : JsonSerializerContext;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: the Step 2 command. Expected: all pass (the worst-case frame test proves the bound through the codec).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/PermissionIpc.cs src/Capacitor.Cli.Core/LocalIpc/PermissionDecisionLog.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionWireContractsTests.cs
git commit -m "Add the permission wire DTOs, their bounds and the decision record"
```

---

### Task 3: `PermissionSubscription` (client-side stream)

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/PermissionSubscription.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionSubscriptionTests.cs`

**Interfaces:**
- Consumes: Task 1 frames, Task 2 DTOs, `DaemonStore.SocketPath(name)`.
- Produces: `abstract record PermissionStreamEvent { Subscribed; Pending(PermissionPendingDto Request); Resolved(PermissionResolvedDto Settlement) }`; `PermissionSubscription.RunAsync(DaemonStore store, string daemonName, CancellationToken ct) → IAsyncEnumerable<PermissionStreamEvent>`.

- [ ] **Step 1: Write the failing tests** (the `ScriptedOpsServer` harness is copied verbatim from `ConsentSubscriptionTests.cs` — same class, same `ConnScript` delegate, same Windows guard; only the scripts differ)

```csharp
using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

public class PermissionSubscriptionTests {
    delegate Task ConnScript(Socket raw, NetworkStream s, CancellationToken ct);

    // ScriptedOpsServer: copy the class from ConsentSubscriptionTests.cs unchanged.

    static ConnScript SubscribePush(params LocalFrame[] frames) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.PermissionSubscribe) return;
        foreach (var frame in frames) await FrameCodec.WriteAsync(s, frame, ct);
    };

    static ConnScript SubscribeThenWrongFrameType() => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type != FrameType.PermissionSubscribe) return;
        await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRules, "{}"), ct);
    };

    static string Pending(string id, string toolName = "Bash") =>
        $$"""{"request_id":"{{id}}","agent_id":"a1","session_id":"s1","vendor":"claude","tool_name":"{{toolName}}","tool_input":null,"suggestions":null,"tool_input_omitted":false,"suggestions_omitted":false,"requested_at":"t"}""";

    static string Resolved(string id) => $$"""{"request_id":"{{id}}","outcome":"allow","source":"server"}""";

    async Task<List<PermissionStreamEvent>> CollectAsync(string sockPath, int expected) {
        var events = new List<PermissionStreamEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = new DaemonStore(Path.GetDirectoryName(sockPath)!);
        await foreach (var e in PermissionSubscription.RunAsync(store, Path.GetFileNameWithoutExtension(sockPath), cts.Token)) {
            events.Add(e);
            if (events.Count == expected) break;
        }
        return events;
    }

    [Test]
    public async Task Subscribed_then_pending_then_resolved_in_order() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir("psub");
        var store = new DaemonStore(tmp.GetResolvedPath());
        var sock = store.SocketPath("d");
        await using var server = new ScriptedOpsServer(sock, SubscribePush(
            LocalFrame.PermissionJson(FrameType.PermissionPending, Pending("r1")),
            LocalFrame.PermissionJson(FrameType.PermissionResolved, Resolved("r1"))));

        var events = await CollectAsync(sock, 3);
        await Assert.That(events[0]).IsTypeOf<PermissionStreamEvent.Subscribed>();
        await Assert.That(((PermissionStreamEvent.Pending)events[1]).Request.RequestId).IsEqualTo("r1");
        await Assert.That(((PermissionStreamEvent.Resolved)events[2]).Settlement.Source).IsEqualTo("server");
    }

    [Test]
    public async Task Invalid_pending_is_skipped_and_empty_tool_name_is_delivered() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir("psub");
        var store = new DaemonStore(tmp.GetResolvedPath());
        var sock = store.SocketPath("d");
        await using var server = new ScriptedOpsServer(sock, SubscribePush(
            LocalFrame.PermissionJson(FrameType.PermissionPending, "{}"),
            LocalFrame.PermissionJson(FrameType.PermissionPending, Pending("r2", toolName: ""))));

        var events = await CollectAsync(sock, 2);
        await Assert.That(((PermissionStreamEvent.Pending)events[1]).Request.RequestId).IsEqualTo("r2");
        await Assert.That(((PermissionStreamEvent.Pending)events[1]).Request.ToolName).IsEqualTo("");
    }

    [Test]
    public async Task Wrong_frame_type_ends_the_attempt_after_subscribed() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir("psub");
        var store = new DaemonStore(tmp.GetResolvedPath());
        var sock = store.SocketPath("d");
        await using var server = new ScriptedOpsServer(sock, SubscribeThenWrongFrameType());

        var events = await CollectAsync(sock, 99);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(events[0]).IsTypeOf<PermissionStreamEvent.Subscribed>();
    }

    [Test]
    public async Task Failed_dial_yields_nothing() {
        if (OperatingSystem.IsWindows()) return;
        using var tmp = new TempDir("psub");
        var store = new DaemonStore(tmp.GetResolvedPath());
        var events = await CollectAsync(store.SocketPath("nobody"), 99);
        await Assert.That(events.Count).IsEqualTo(0);
    }
}
```

`DaemonStore`'s constructor and `SocketPath` are the ones `ConsentSubscriptionTests` uses; match its construction exactly (copy the `TempDir` + `DaemonStore` lines from that file if they differ from the above).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionSubscriptionTests/*"`
Expected: build error — `PermissionSubscription` missing.

- [ ] **Step 3: Create `PermissionSubscription.cs`**

```csharp
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Capacitor.Cli.Core.LocalIpc;

/// One permission subscription attempt as a typed stream. `Subscribed` is a client-local
/// boundary emitted after the subscribe write flushes; it does not prove the daemon registered
/// the subscription. The enumeration ending for any reason but caller cancellation means "this
/// attempt is over" — the consumer decides whether to go again.
public abstract record PermissionStreamEvent {
    public sealed record Subscribed : PermissionStreamEvent;
    public sealed record Pending(PermissionPendingDto Request) : PermissionStreamEvent;
    public sealed record Resolved(PermissionResolvedDto Settlement) : PermissionStreamEvent;
}

public static class PermissionSubscription {
    public static async IAsyncEnumerable<PermissionStreamEvent> RunAsync(
            DaemonStore store, string daemonName, [EnumeratorCancellation] CancellationToken ct = default) {
        using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        NetworkStream? stream = null;
        try {
            try {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(store.SocketPath(daemonName)), ct);
                stream = new NetworkStream(sock, ownsSocket: false);
                await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.PermissionSubscribe), ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) when (ex is IOException or SocketException) {
                yield break;
            }

            yield return new PermissionStreamEvent.Subscribed();

            while (true) {
                LocalFrame? frame;
                try {
                    frame = await FrameCodec.ReadAsync(stream!, ct);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    throw;
                } catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException) {
                    yield break;
                }
                if (frame is null) yield break;

                switch (frame.Type) {
                    case FrameType.PermissionPending: {
                        PermissionPendingDto? dto;
                        try { dto = JsonSerializer.Deserialize(frame.Text, PermissionIpcJsonContext.Default.PermissionPendingDto); }
                        catch (JsonException) { yield break; }
                        // Skipped, not fatal: ending here would make the resubscribe replay redeliver it forever.
                        if (!PermissionWire.IsPendingStructurallyValid(dto)) continue;
                        yield return new PermissionStreamEvent.Pending(dto!);
                        break;
                    }
                    case FrameType.PermissionResolved: {
                        PermissionResolvedDto? dto;
                        try { dto = JsonSerializer.Deserialize(frame.Text, PermissionIpcJsonContext.Default.PermissionResolvedDto); }
                        catch (JsonException) { yield break; }
                        if (dto is null || string.IsNullOrEmpty(dto.RequestId)) continue;
                        yield return new PermissionStreamEvent.Resolved(dto);
                        break;
                    }
                    default:
                        yield break; // protocol confusion
                }
            }
        } finally {
            if (stream is not null) await stream.DisposeAsync();
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command. Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/PermissionSubscription.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/PermissionSubscriptionTests.cs
git commit -m "Add the client-side permission subscription stream"
```

---

### Task 4: `ResolvePermissionAsync` on the ops seam and `ClaudePermissions.AlwaysAllow`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs` (interface + implementation)
- Create: `src/Capacitor.Cli.Core/ClaudePermissions.cs`
- Modify: `test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs` (the interface gains a member; this fake must implement it or the App test suite stops building)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/LocalControlOpsTests.cs` (append), `test/Capacitor.Cli.Core.Tests.Unit/ClaudePermissionsTests.cs`

**Interfaces:**
- Produces: `Task<PermissionAckDto> ILocalControlOps.ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct)`; `static JsonElement ClaudePermissions.AlwaysAllow(string toolName)`.

- [ ] **Step 1: Write the failing tests**

Append to `LocalControlOpsTests.cs` (it already has `WithOpsAsync`, `ErrorThen`, `V1CodecReject`; add one script beside `ConsentResolveV2Ack`):

```csharp
    static ConnScript PermissionAckThen(string json, Action<string>? capture = null) => async (_, s, ct) => {
        var f = await FrameCodec.ReadAsync(s, ct);
        if (f?.Type == FrameType.PermissionResolve) {
            capture?.Invoke(f.Text);
            await FrameCodec.WriteAsync(s, LocalFrame.PermissionJson(FrameType.PermissionAck, json), ct);
        }
    };

    // ---- ResolvePermissionAsync ----

    [Test]
    [Arguments("""{"ok":true,"error":null}""", true, null)]
    [Arguments("""{"ok":false,"error":"no pending permission request with that id"}""", false, "no pending permission request with that id")]
    public async Task Permission_resolve_ack_shapes(string json, bool ok, string? error) {
        if (OperatingSystem.IsWindows()) return;
        string? sent = null;
        await WithOpsAsync([PermissionAckThen(json, t => sent = t)], async ops => {
            var ack = await ops.ResolvePermissionAsync(new PermissionResolveDto("r1", "allow", null, null), CancellationToken.None);
            await Assert.That(ack.Ok).IsEqualTo(ok);
            await Assert.That(ack.Error).IsEqualTo(error);
        });
        await Assert.That(sent).Contains("\"request_id\":\"r1\"");
    }

    [Test]
    public async Task Permission_resolve_maps_error_frame_to_daemon_rejected() {
        if (OperatingSystem.IsWindows()) return;
        await WithOpsAsync([ErrorThen("nope")], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.ResolvePermissionAsync(new PermissionResolveDto("r1", "deny", null, null), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("daemon_rejected");
        });
    }

    [Test]
    public async Task Permission_resolve_against_a_down_level_codec_is_unexpected_reply() {
        if (OperatingSystem.IsWindows()) return;
        await WithOpsAsync([V1CodecReject()], async ops => {
            var ex = await Assert.ThrowsAsync<LocalControlOpsException>(
                async () => await ops.ResolvePermissionAsync(new PermissionResolveDto("r1", "allow", null, null), CancellationToken.None));
            await Assert.That(ex!.Reason).IsEqualTo("unexpected_reply");
        });
    }
```

New `ClaudePermissionsTests.cs`:

```csharp
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Core.Tests.Unit;

public class ClaudePermissionsTests {
    [Test]
    public async Task Always_allow_is_the_web_ui_shape() {
        var el = ClaudePermissions.AlwaysAllow("Bash");
        await Assert.That(el.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalControlOpsTests/Permission*"` and `… --treenode-filter "/*/*/ClaudePermissionsTests/*"`
Expected: build errors — no `ResolvePermissionAsync`, no `ClaudePermissions`.

- [ ] **Step 3: Implement**

`LocalControlOps.cs` — add to `ILocalControlOps`:

```csharp
    Task<PermissionAckDto>  ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct);
```

and to `LocalControlOps`, beside `ResolveConsentAsync`:

```csharp
    public async Task<PermissionAckDto> ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct) {
        var json  = JsonSerializer.Serialize(resolve, PermissionIpcJsonContext.Default.PermissionResolveDto);
        var reply = await ExchangeAsync(LocalFrame.PermissionJson(FrameType.PermissionResolve, json), ConsentReplyTimeout, ct);
        switch (reply.Type) {
            case FrameType.PermissionAck:
                var ack = DeserializeOrThrow(reply.Text, PermissionIpcJsonContext.Default.PermissionAckDto, "malformed permission ack reply");
                if (ack is null) throw new LocalControlOpsException(UnexpectedReply, "malformed permission ack reply");
                return ack;
            case FrameType.Error:
                throw new LocalControlOpsException(DaemonRejected, reply.Text);
            default:
                throw new LocalControlOpsException(UnexpectedReply, $"unexpected daemon response to permission resolve ({reply.Type})");
        }
    }
```

`ClaudePermissions.cs` (Core root namespace `Capacitor.Cli.Core`):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core;

/// The `applyPermissions` payload the web UI sends for "Always allow": Claude persists the rule
/// itself, so the client composes it rather than relaying the hook's permission_suggestions.
public static class ClaudePermissions {
    public static JsonElement AlwaysAllow(string toolName) {
        var json = JsonSerializer.Serialize(new[] { new AlwaysAllowEntry("toolAlwaysAllow", toolName) },
            ClaudePermissionsJsonContext.Default.AlwaysAllowEntryArray);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal sealed record AlwaysAllowEntry(string Type, string Tool);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClaudePermissions.AlwaysAllowEntry[]))]
internal partial class ClaudePermissionsJsonContext : JsonSerializerContext;
```

`ScriptedLocalControlOps.cs` (App tests) — add a queue, an `Arm`/`Queue` pair and the member, mirroring the consent resolve members exactly:

```csharp
    readonly Queue<TaskCompletionSource<PermissionAckDto>> _permissionResolves = new();
    public int PermissionResolveCalls;
    public readonly List<PermissionResolveDto> PermissionResolvePayloads = [];

    public TaskCompletionSource<PermissionAckDto> ArmPermissionResolve() {
        var tcs = new TaskCompletionSource<PermissionAckDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _permissionResolves.Enqueue(tcs);
        return tcs;
    }

    public void QueuePermissionResolve(bool ok, string? error = null) => ArmPermissionResolve().SetResult(new PermissionAckDto(ok, error));
    public void QueuePermissionResolveFailure(string reason) => ArmPermissionResolve().SetException(new LocalControlOpsException(reason, reason));

    public Task<PermissionAckDto> ResolvePermissionAsync(PermissionResolveDto resolve, CancellationToken ct) {
        Interlocked.Increment(ref PermissionResolveCalls);
        PermissionResolvePayloads.Add(resolve);
        if (ct.IsCancellationRequested) return Task.FromCanceled<PermissionAckDto>(ct);
        if (_permissionResolves.Count == 0) throw new InvalidOperationException("ScriptedLocalControlOps: unscripted permission resolve call");
        var tcs = _permissionResolves.Dequeue();
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: the two Step 2 commands, then build the App tests: `dotnet build test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`. Expected: pass, build green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs src/Capacitor.Cli.Core/ClaudePermissions.cs test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/LocalControlOpsTests.cs test/Capacitor.Cli.Core.Tests.Unit/ClaudePermissionsTests.cs
git commit -m "Add the permission resolve op and the always-allow payload helper"
```

---

### Task 5: `OwnerOnlyJsonlLog` and `PermissionDecisionLog` (daemon)

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/OwnerOnlyJsonlLog.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentDecisionLog.cs` (becomes a wrapper)
- Create: `src/Capacitor.Cli.Daemon/Services/PermissionDecisionLog.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/PermissionDecisionLogTests.cs`; the existing `LaunchConsentDecisionLogTests` must stay green unchanged.

**Interfaces:**
- Produces: `OwnerOnlyJsonlLog(string path, ILogger logger, long maxBytes)` with `void Append(string line, string subjectForLog)`; `PermissionDecisionLog(string stateDir, ILogger logger, long maxBytes = 1_048_576)` with `void Record(PermissionDecisionRecord rec)`; `LaunchConsentDecisionLog` keeps its public surface.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionDecisionLogTests {
    static PermissionDecisionRecord Rec(string agent = "a1") =>
        new(DateTimeOffset.UtcNow.ToString("O"), agent, "s1", "claude", "Bash", "allow", "app");

    [Test]
    public async Task Records_append_as_parseable_snake_case_jsonl_owner_only() {
        using var tmp = new TempDir();
        var log = new PermissionDecisionLog(tmp.Path, NullLogger.Instance);
        log.Record(Rec("a1"));
        log.Record(Rec("a2"));
        var path = tmp.PathTo("permission-decisions.jsonl");
        var lines = File.ReadAllLines(path);
        await Assert.That(lines.Length).IsEqualTo(2);
        using var parsed = JsonDocument.Parse(lines[1]);
        await Assert.That(parsed.RootElement.GetProperty("agent_id").GetString()).IsEqualTo("a2");
        await Assert.That(parsed.RootElement.GetProperty("source").GetString()).IsEqualTo("app");
        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Rotates_to_backup_at_cap() {
        using var tmp = new TempDir();
        var log = new PermissionDecisionLog(tmp.Path, NullLogger.Instance, maxBytes: 512);
        for (var i = 0; i < 20; i++) log.Record(Rec($"agent-{i}"));
        await Assert.That(File.Exists(tmp.PathTo("permission-decisions.jsonl.1"))).IsTrue();
        await Assert.That(new FileInfo(tmp.PathTo("permission-decisions.jsonl")).Length <= 512).IsTrue();
    }

    [Test]
    public async Task Unwritable_directory_never_throws() {
        var log = new PermissionDecisionLog("/nonexistent/deeply/nested", NullLogger.Instance);
        await Assert.That(() => log.Record(Rec())).ThrowsNothing();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionDecisionLogTests/*"`
Expected: build error — `PermissionDecisionLog` missing.

- [ ] **Step 3: Extract the writer, wrap the consent log, add the permission log**

`OwnerOnlyJsonlLog.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Append-only JSONL audit file: created 0600 from the first byte (UnixCreateMode) under an
/// owner-only directory, rotated once to `.1` at maxBytes. Best-effort — an I/O fault is logged
/// and swallowed, because audit must never fail the decision it records.
internal sealed class OwnerOnlyJsonlLog(string path, ILogger logger, long maxBytes) {
    readonly object _gate = new();
    bool _dirCreated;

    public void Append(string line, string subjectForLog) {
        lock (_gate) {
            try {
                if (!_dirCreated) {
                    var dir = Path.GetDirectoryName(path)!;
                    Directory.CreateDirectory(dir);
                    if (!OperatingSystem.IsWindows())
                        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    _dirCreated = true;
                }

                var incoming = Encoding.UTF8.GetByteCount(line) + 1;
                if (File.Exists(path) && new FileInfo(path).Length + incoming > maxBytes)
                    File.Move(path, path + ".1", overwrite: true);

                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

                using var fs = new FileStream(path, options);
                fs.Write(Encoding.UTF8.GetBytes(line + "\n"));
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to append audit record for {Subject}", subjectForLog);
            }
        }
    }
}
```

`LaunchConsentDecisionLog.cs` — replace the body with:

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Append-only JSONL audit of every consent decision (rule-matched and human), rendered by the
/// desktop app as the Activity feed and by `kcap daemon consent log`.
internal sealed class LaunchConsentDecisionLog(string stateDir, ILogger logger, long maxBytes = 1_048_576) {
    readonly OwnerOnlyJsonlLog _log = new(Path.Combine(stateDir, "consent-decisions.jsonl"), logger, maxBytes);

    public void Record(ConsentDecisionRecord rec) =>
        _log.Append(JsonSerializer.Serialize(rec, ConsentDecisionJsonContext.Default.ConsentDecisionRecord), rec.AgentId);
}
```

`PermissionDecisionLog.cs` (daemon):

```csharp
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Append-only JSONL audit of every settled, attributed permission request.
internal sealed class PermissionDecisionLog(string stateDir, ILogger logger, long maxBytes = 1_048_576) {
    readonly OwnerOnlyJsonlLog _log = new(Path.Combine(stateDir, "permission-decisions.jsonl"), logger, maxBytes);

    public void Record(PermissionDecisionRecord rec) =>
        _log.Append(JsonSerializer.Serialize(rec, PermissionDecisionJsonContext.Default.PermissionDecisionRecord), rec.AgentId);
}
```

- [ ] **Step 4: Run both log suites**

Run: `… --treenode-filter "/*/*/PermissionDecisionLogTests/*"` and `… --treenode-filter "/*/*/LaunchConsentDecisionLogTests/*"`. Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/OwnerOnlyJsonlLog.cs src/Capacitor.Cli.Daemon/Services/LaunchConsentDecisionLog.cs src/Capacitor.Cli.Daemon/Services/PermissionDecisionLog.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/PermissionDecisionLogTests.cs
git commit -m "Share the owner-only JSONL writer between the consent and permission logs"
```

---

### Task 6: `PermissionPromptBroker` — the one claim point

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/PermissionPromptBroker.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/PermissionPromptBrokerTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct PermissionSettlement(PermissionDecision Decision, string Outcome, string Source)`
  - `abstract record PermissionStreamItem { Pending(PermissionPendingDto Dto); Resolved(PermissionResolvedDto Dto) }`
  - `PermissionPromptBroker`: `Task<PermissionSettlement> Register(PermissionPendingDto dto)`; `bool TrySettle(string requestId, PermissionDecision decision, string outcome, string source)`; `bool TrySettleIfNoSubscriber(string requestId, PermissionDecision decision, string outcome, string source)`; `(Guid id, ChannelReader<PermissionStreamItem> reader) Subscribe()`; `void Unsubscribe(Guid id)`; `void WithdrawForAgent(string agentId)`; `bool HasSubscriber`; `IReadOnlyList<PermissionPendingDto> PendingSnapshot()`.
  - `static class PermissionSettlements { const string Allow = "allow", Deny = "deny", Withdrawn = "withdrawn"; const string SourceApp = "app", SourceServer = "server", SourceAgentGone = "agent_gone", SourceNoUi = "no_ui", SourceDaemonShutdown = "daemon_shutdown"; static readonly PermissionDecision DenyDecision = new("deny", null, null) }`

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionPromptBrokerTests {
    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(10);

    static PermissionPendingDto Dto(string id = "r1", string agent = "a1") =>
        new(id, agent, "s1", "claude", "Bash", null, null, false, false, DateTimeOffset.UtcNow.ToString("O"));

    static PermissionDecision Allow => new("allow", null, null);

    static async Task<T> WaitBounded<T>(Task<T> task, string because) {
        var finished = await Task.WhenAny(task, Task.Delay(Bounded));
        await Assert.That(finished == task).IsTrue().Because(because);
        return await task;
    }

    [Test]
    public async Task Register_broadcasts_pending_and_settle_broadcasts_resolved_and_completes_the_task() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var settlement = broker.Register(Dto());

        var first = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(((PermissionStreamItem.Pending)first).Dto.RequestId).IsEqualTo("r1");

        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsTrue();
        var second = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        var resolved = ((PermissionStreamItem.Resolved)second).Dto;
        await Assert.That(resolved.Outcome).IsEqualTo("allow");
        await Assert.That(resolved.Source).IsEqualTo("app");

        var s = await WaitBounded(settlement, "the claim completes the registration");
        await Assert.That(s.Decision.Behavior).IsEqualTo("allow");
        await Assert.That(s.Source).IsEqualTo("app");
    }

    [Test]
    public async Task Second_claim_loses_and_the_task_carries_the_first() {
        var broker = new PermissionPromptBroker();
        var settlement = broker.Register(Dto());
        await Assert.That(broker.TrySettle("r1", new("deny", null, null), "deny", "server")).IsTrue();
        await Assert.That(broker.TrySettle("r1", Allow, "allow", "app")).IsFalse();
        var s = await WaitBounded(settlement, "first claim");
        await Assert.That(s.Source).IsEqualTo("server");
        await Assert.That(s.Decision.Behavior).IsEqualTo("deny");
    }

    [Test]
    public async Task Subscribe_replays_each_pending_exactly_once() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1"));
        _ = broker.Register(Dto("r2"));
        var (_, reader) = broker.Subscribe();
        var a = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        var b = await reader.ReadAsync(new CancellationTokenSource(5000).Token);
        await Assert.That(new[] { ((PermissionStreamItem.Pending)a).Dto.RequestId, ((PermissionStreamItem.Pending)b).Dto.RequestId })
            .IsEquivalentTo(new[] { "r1", "r2" });
        await Assert.That(reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task Withdraw_settles_the_agents_entries_and_a_later_register_for_it_settles_at_once_without_broadcast() {
        var broker = new PermissionPromptBroker();
        var (_, reader) = broker.Subscribe();
        var s1 = broker.Register(Dto("r1", "a1"));
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // the Pending

        broker.WithdrawForAgent("a1");
        var resolved = ((PermissionStreamItem.Resolved)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(resolved.Outcome).IsEqualTo("withdrawn");
        await Assert.That(resolved.Source).IsEqualTo("agent_gone");
        await Assert.That((await WaitBounded(s1, "withdrawn")).Decision.Behavior).IsEqualTo("deny");

        var s2 = broker.Register(Dto("r2", "a1"));
        await Assert.That(s2.IsCompletedSuccessfully).IsTrue();
        await Assert.That(s2.Result.Source).IsEqualTo("agent_gone");
        await Assert.That(reader.TryRead(out _)).IsFalse(); // nothing broadcast for r2
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Settle_if_no_subscriber_is_refused_while_a_subscriber_is_registered() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto());
        var (id, _) = broker.Subscribe();
        await Assert.That(broker.TrySettleIfNoSubscriber("r1", new("deny", null, null), "deny", "no_ui")).IsFalse();
        broker.Unsubscribe(id);
        await Assert.That(broker.TrySettleIfNoSubscriber("r1", new("deny", null, null), "deny", "no_ui")).IsTrue();
    }

    /// The gate invariant: a subscriber that dials during a settlement sees either nothing or
    /// Pending then Resolved — never Pending alone. Driven from many interleavings.
    [Test]
    public async Task Subscribe_racing_settle_never_yields_pending_alone() {
        for (var round = 0; round < 200; round++) {
            var broker = new PermissionPromptBroker();
            _ = broker.Register(Dto());
            var subscribe = Task.Run(() => broker.Subscribe());
            var settle    = Task.Run(() => broker.TrySettle("r1", Allow, "allow", "app"));
            var (id, reader) = await subscribe;
            await settle;
            broker.Unsubscribe(id);

            var items = new List<PermissionStreamItem>();
            while (reader.TryRead(out var item)) items.Add(item);
            var pendings  = items.Count(i => i is PermissionStreamItem.Pending);
            var resolveds = items.Count(i => i is PermissionStreamItem.Resolved);
            await Assert.That(pendings == 0 || resolveds == 1).IsTrue().Because($"round {round}: {pendings} pending, {resolveds} resolved");
        }
    }

    [Test]
    public async Task Unsubscribe_completes_the_channel() {
        var broker = new PermissionPromptBroker();
        var (id, reader) = broker.Subscribe();
        broker.Unsubscribe(id);
        await reader.Completion.WaitAsync(Bounded);
        await Assert.That(broker.HasSubscriber).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionPromptBrokerTests/*"`
Expected: build error — `PermissionPromptBroker` missing.

- [ ] **Step 3: Create `PermissionPromptBroker.cs`**

```csharp
using System.Collections.Concurrent;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Daemon.Services;

internal readonly record struct PermissionSettlement(PermissionDecision Decision, string Outcome, string Source);

internal abstract record PermissionStreamItem {
    public sealed record Pending(PermissionPendingDto Dto) : PermissionStreamItem;
    public sealed record Resolved(PermissionResolvedDto Dto) : PermissionStreamItem;
}

internal static class PermissionSettlements {
    public const string Allow = "allow", Deny = "deny", Withdrawn = "withdrawn";
    public const string SourceApp = "app", SourceServer = "server", SourceAgentGone = "agent_gone",
                        SourceNoUi = "no_ui", SourceDaemonShutdown = "daemon_shutdown";
    public static readonly PermissionDecision DenyDecision = new("deny", null, null);
}

/// The single claim point for a hosted permission request. Every settlement — the app, the
/// server's push, an agent's withdrawal, the no-UI deny, the shutdown claim — goes through
/// TrySettle under one gate that replay/registration also takes, so a subscriber observes a
/// request as nothing, or Pending then Resolved, never Pending alone. The withdrawn set is
/// service-lifetime: agent ids are never reused, so it can never suppress a future agent.
internal sealed class PermissionPromptBroker {
    sealed record Entry(PermissionPendingDto Dto, TaskCompletionSource<PermissionSettlement> Tcs);

    readonly ConcurrentDictionary<string, Entry> _pending = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<Guid, Channel<PermissionStreamItem>> _subscribers = new();
    readonly HashSet<string> _withdrawnAgents = new(StringComparer.Ordinal);
    readonly object _gate = new();

    public bool HasSubscriber => !_subscribers.IsEmpty;

    public Task<PermissionSettlement> Register(PermissionPendingDto dto) {
        lock (_gate) {
            if (_withdrawnAgents.Contains(dto.AgentId))
                return Task.FromResult(new PermissionSettlement(
                    PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceAgentGone));

            // Completed while the gate is held: a continuation running inline would re-enter it.
            var entry = new Entry(dto, new(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending[dto.RequestId] = entry;
            Broadcast(new PermissionStreamItem.Pending(dto));
            return entry.Tcs.Task;
        }
    }

    public bool TrySettle(string requestId, PermissionDecision decision, string outcome, string source) {
        lock (_gate) return SettleLocked(requestId, decision, outcome, source);
    }

    public bool TrySettleIfNoSubscriber(string requestId, PermissionDecision decision, string outcome, string source) {
        lock (_gate) return _subscribers.IsEmpty && SettleLocked(requestId, decision, outcome, source);
    }

    public (Guid id, ChannelReader<PermissionStreamItem> reader) Subscribe() {
        var id = Guid.NewGuid();
        var ch = Channel.CreateUnbounded<PermissionStreamItem>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate) {
            foreach (var e in _pending.Values) ch.Writer.TryWrite(new PermissionStreamItem.Pending(e.Dto));
            _subscribers[id] = ch;
        }
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id) {
        Channel<PermissionStreamItem>? ch;
        lock (_gate) _subscribers.TryRemove(id, out ch);
        ch?.Writer.TryComplete();
    }

    public void WithdrawForAgent(string agentId) {
        lock (_gate) {
            _withdrawnAgents.Add(agentId);
            foreach (var e in _pending.Values.Where(e => e.Dto.AgentId == agentId).ToList())
                SettleLocked(e.Dto.RequestId, PermissionSettlements.DenyDecision, PermissionSettlements.Withdrawn, PermissionSettlements.SourceAgentGone);
        }
    }

    public IReadOnlyList<PermissionPendingDto> PendingSnapshot() {
        lock (_gate) return _pending.Values.Select(e => e.Dto).ToList();
    }

    // Caller holds _gate. Instance-scoped removal, the consent broker's discipline.
    bool SettleLocked(string requestId, PermissionDecision decision, string outcome, string source) {
        if (!_pending.TryGetValue(requestId, out var entry)) return false;
        if (!_pending.TryRemove(new KeyValuePair<string, Entry>(requestId, entry))) return false;
        Broadcast(new PermissionStreamItem.Resolved(new PermissionResolvedDto(requestId, outcome, source)));
        entry.Tcs.TrySetResult(new PermissionSettlement(decision, outcome, source));
        return true;
    }

    void Broadcast(PermissionStreamItem item) {
        foreach (var ch in _subscribers.Values) ch.Writer.TryWrite(item);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command. Expected: 7 passed (the race test runs 200 rounds).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/PermissionPromptBroker.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/PermissionPromptBrokerTests.cs
git commit -m "Add the permission prompt broker as the single claim point"
```

---

### Task 7: `PermissionIpc` handler, routing arms and the `permission/1` capability

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/PermissionIpc.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` (ctor + two `switch` arms + the `default` arm's list)
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (register `PermissionPromptBroker`, `PermissionIpc`, `PermissionDecisionLog`)
- Modify (construction sites — `LocalControlServer` gains a required `PermissionIpc permissionIpc` parameter after `consentIpc`): `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorLocalAttachTests.cs` (3 sites), `ConsentRulesPutV2Tests.cs`, `DaemonStatusIpcTests.cs`, `LaunchConsentIpcTests.cs`, `LocalControlHelloTests.cs`, `LocalControlOpsV2PutTests.cs`, `LocalControlProbeTests.cs`; and the capability arrays in `LocalControlHelloTests.cs` (three `IsEquivalentTo` lines).
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/PermissionIpcTests.cs`

**Interfaces:**
- Consumes: Task 6 broker.
- Produces: `PermissionIpc(PermissionPromptBroker broker, ILogger<PermissionIpc> logger)` with `Task HandleSubscribeAsync(Stream stream, CancellationToken ct)` and `Task HandleResolveAsync(string payload, Stream stream, CancellationToken ct)`; `LocalControlServer(DaemonConfig, AgentOrchestrator, RestartCoordinator, LaunchConsentIpc, PermissionIpc, DaemonStatusIpc, ILogger<LocalControlServer>)`; capability list `["consent/1", "consent/2", "consent/3", "status/1", "permission/1"]`.

- [ ] **Step 1: Write the failing tests**

`PermissionIpcTests.cs` drives the handler directly over an in-memory duplex stream — no socket needed for the handler's own contract (the routing switch is covered by the updated hello test):

```csharp
using System.IO.Pipes;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class PermissionIpcTests {
    static PermissionPendingDto Dto(string id = "r1") =>
        new(id, "a1", "s1", "claude", "Bash", null, null, false, false, "t");

    /// An anonymous pipe pair: the handler writes to `server`, the test reads from `client`.
    static (Stream server, Stream client) Duplex() {
        var toClient = new AnonymousPipeServerStream(PipeDirection.Out);
        var fromServer = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        return (toClient, fromServer);
    }

    [Test]
    public async Task Subscribe_replays_pending_then_pushes_resolved() {
        var broker = new PermissionPromptBroker();
        _ = broker.Register(Dto("r1"));
        var ipc = new PermissionIpc(broker, NullLogger<PermissionIpc>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (server, client) = Duplex();
        var handler = ipc.HandleSubscribeAsync(server, cts.Token);

        var first = await FrameCodec.ReadAsync(client, cts.Token);
        await Assert.That(first!.Type).IsEqualTo(FrameType.PermissionPending);
        await Assert.That(JsonSerializer.Deserialize(first.Text, PermissionIpcJsonContext.Default.PermissionPendingDto)!.RequestId).IsEqualTo("r1");

        broker.TrySettle("r1", new PermissionDecision("allow", null, null), "allow", "server");
        var second = await FrameCodec.ReadAsync(client, cts.Token);
        await Assert.That(second!.Type).IsEqualTo(FrameType.PermissionResolved);
        await Assert.That(second.Text).Contains("\"source\":\"server\"");

        cts.Cancel();
        await handler;
        await Assert.That(broker.HasSubscriber).IsFalse();
    }

    [Test]
    [Arguments("""{"request_id":"r1","decision":"allow","apply_permissions":null,"updated_input":null}""", true, null)]
    [Arguments("""{"request_id":"nope","decision":"allow","apply_permissions":null,"updated_input":null}""", false, "no pending permission request with that id")]
    [Arguments("""{"request_id":"r1","decision":"maybe","apply_permissions":null,"updated_input":null}""", false, "invalid resolve payload (decision must be allow|deny)")]
    [Arguments("""{"decision":"allow"}""", false, "invalid resolve payload (decision must be allow|deny)")]
    [Arguments("""{ not json""", false, "malformed resolve payload")]
    public async Task Resolve_acks(string payload, bool ok, string? error) {
        var broker = new PermissionPromptBroker();
        var settlement = broker.Register(Dto("r1"));
        var ipc = new PermissionIpc(broker, NullLogger<PermissionIpc>.Instance);
        var (server, client) = Duplex();

        await ipc.HandleResolveAsync(payload, server, CancellationToken.None);
        var reply = await FrameCodec.ReadAsync(client, CancellationToken.None);
        await Assert.That(reply!.Type).IsEqualTo(FrameType.PermissionAck);
        var ack = JsonSerializer.Deserialize(reply.Text, PermissionIpcJsonContext.Default.PermissionAckDto)!;
        await Assert.That(ack.Ok).IsEqualTo(ok);
        await Assert.That(ack.Error).IsEqualTo(error);
        if (ok) {
            var s = await settlement;
            await Assert.That(s.Source).IsEqualTo("app");
            await Assert.That(s.Decision.Behavior).IsEqualTo("allow");
        }
    }

    [Test]
    public async Task Resolve_relays_apply_permissions_verbatim() {
        var broker = new PermissionPromptBroker();
        var settlement = broker.Register(Dto("r1"));
        var ipc = new PermissionIpc(broker, NullLogger<PermissionIpc>.Instance);
        var (server, _) = Duplex();
        await ipc.HandleResolveAsync("""{"request_id":"r1","decision":"allow","apply_permissions":[{"type":"toolAlwaysAllow","tool":"Bash"}],"updated_input":null}""", server, CancellationToken.None);
        var s = await settlement;
        await Assert.That(s.Decision.ApplyPermissions!.Value.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
    }
}
```

`LocalControlHelloTests.cs` — change the three capability assertions to:

```csharp
            await Assert.That(dto.Capabilities).IsEquivalentTo(new[] { "consent/1", "consent/2", "consent/3", "status/1", "permission/1" });
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionIpcTests/*"`
Expected: build error — `PermissionIpc` missing.

- [ ] **Step 3: Create `PermissionIpc.cs`**

```csharp
using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket handlers for the permission frames. Trust model: anything on the daemon's own
/// 0600 socket is the owner — no further auth.
internal sealed class PermissionIpc(PermissionPromptBroker broker, ILogger<PermissionIpc> logger) {
    public async Task HandleSubscribeAsync(Stream stream, CancellationToken ct) {
        var (id, reader) = broker.Subscribe();
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // EOF watcher: a vanished subscriber must be reaped promptly, or the broker keeps
            // broadcasting into a channel nobody drains.
            _ = Task.Run(async () => {
                try { while (await FrameCodec.ReadAsync(stream, cts.Token) is not null) { } }
                catch { }
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }, cts.Token);

            await foreach (var item in reader.ReadAllAsync(cts.Token)) {
                var frame = item switch {
                    PermissionStreamItem.Pending p => LocalFrame.PermissionJson(FrameType.PermissionPending,
                        JsonSerializer.Serialize(p.Dto, PermissionIpcJsonContext.Default.PermissionPendingDto)),
                    PermissionStreamItem.Resolved r => LocalFrame.PermissionJson(FrameType.PermissionResolved,
                        JsonSerializer.Serialize(r.Dto, PermissionIpcJsonContext.Default.PermissionResolvedDto)),
                    _ => throw new InvalidOperationException("unknown stream item"),
                };
                await FrameCodec.WriteAsync(stream, frame, cts.Token);
            }
        } catch (OperationCanceledException) {
        } catch (IOException) {
            // A vanished subscriber is normal lifecycle for a long-lived subscription, not a fault.
        } catch (SocketException) {
        } finally {
            broker.Unsubscribe(id);
        }
    }

    public async Task HandleResolveAsync(string payload, Stream stream, CancellationToken ct) {
        PermissionAckDto ack;
        try {
            var dto = JsonSerializer.Deserialize(payload, PermissionIpcJsonContext.Default.PermissionResolveDto);
            if (dto is null || string.IsNullOrEmpty(dto.RequestId) || dto.Decision is not ("allow" or "deny")) {
                ack = new PermissionAckDto(false, "invalid resolve payload (decision must be allow|deny)");
            } else {
                var decision = new PermissionDecision(dto.Decision, dto.ApplyPermissions, dto.UpdatedInput);
                var settled  = broker.TrySettle(dto.RequestId, decision, dto.Decision, PermissionSettlements.SourceApp);
                ack = settled
                    ? new PermissionAckDto(true, null)
                    : new PermissionAckDto(false, "no pending permission request with that id");
                if (!settled) logger.LogDebug("Permission resolve for {RequestId} lost the claim", dto.RequestId);
            }
        } catch (JsonException) {
            ack = new PermissionAckDto(false, "malformed resolve payload");
        }
        var json = JsonSerializer.Serialize(ack, PermissionIpcJsonContext.Default.PermissionAckDto);
        await FrameCodec.WriteAsync(stream, LocalFrame.PermissionJson(FrameType.PermissionAck, json), ct);
    }
}
```

- [ ] **Step 4: Route, advertise, register**

`LocalControlServer.cs` — constructor becomes `(DaemonConfig config, AgentOrchestrator orchestrator, RestartCoordinator restart, LaunchConsentIpc consentIpc, PermissionIpc permissionIpc, DaemonStatusIpc statusIpc, ILogger<LocalControlServer> logger)`; add two arms after the `ConsentRulesPutV2` arm:

```csharp
                case FrameType.PermissionSubscribe: await permissionIpc.HandleSubscribeAsync(stream, ct); break;
                case FrameType.PermissionResolve:   await permissionIpc.HandleResolveAsync(first.Text, stream, ct); break;
```

and extend the `default` arm's text to end `…/ConsentRulesPutV2/PermissionSubscribe/PermissionResolve/Hello/StatusSubscribe`.

`LocalControlCapabilities.cs`:

```csharp
    public static readonly IReadOnlyList<string> Current = ["consent/1", "consent/2", "consent/3", "status/1", "permission/1"];
```

and add one sentence to its summary: `"permission/1"` routes `PermissionSubscribe`/`PermissionResolve` to `PermissionIpc`.

`DaemonRunner.cs` — after `builder.Services.AddSingleton<LaunchConsentIpc>();`:

```csharp
        builder.Services.AddSingleton<PermissionPromptBroker>();
        builder.Services.AddSingleton<PermissionIpc>();
        builder.Services.AddSingleton(sp => new PermissionDecisionLog(
            coverageStateDir, sp.GetRequiredService<ILogger<PermissionDecisionLog>>()));
```

Update every `new LocalControlServer(…)` test site to pass `new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance)` (or the harness's shared broker where one exists) as the new argument after `consentIpc`.

- [ ] **Step 5: Run the daemon suite**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`. Expected: green, including `LocalControlHelloTests` with the five capabilities and `PermissionIpcTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon test/Capacitor.Cli.Daemon.Tests.Unit
git commit -m "Route the permission frames and advertise permission/1"
```

---

### Task 8: `ServerConnection` split — `Begin`, `Await`, `RespondToPermission`

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/PermissionRequestAbandonedException.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (around `RequestPermissionAsync`, ~L941)
- Modify: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeTests.cs` (`FakeServerConnection` gains the three overrides — the bridge tests in Task 9 script them)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ConnectionRetryTests.cs` (append) and `test/Capacitor.Cli.Daemon.Tests.Unit/Services/ServerConnectionPermissionSplitTests.cs`

**Interfaces:**
- Produces on `ServerConnection` (all `public virtual`):
  - `Task<string> BeginPermissionRequestAsync(string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct, Func<bool> abandoned)`
  - `Task<PermissionDecision> AwaitPermissionDecisionAsync(string serverRequestId, CancellationToken ct)`
  - `Task<RespondOutcome> RespondToPermissionAsync(string sessionId, string serverRequestId, PermissionDecision decision)`
  - `RequestPermissionAsync` unchanged in signature, now `Begin(…, ct, () => false)` then `Await`.
  - `enum RespondOutcomeKind { Applied, NotPending, Failed }`; `readonly record struct RespondOutcome(RespondOutcomeKind Kind, string? Reason)`
  - `sealed class PermissionRequestAbandonedException : Exception`

- [ ] **Step 1: Write the failing tests**

Append to `ConnectionRetryTests.cs` (find the existing class; add):

```csharp
    [Test]
    public async Task An_abandonment_exception_propagates_on_the_first_attempt_without_cancellation() {
        var attempts = 0;
        await Assert.ThrowsAsync<PermissionRequestAbandonedException>(async () =>
            await ConnectionRetry.InvokeWithConnectionRetryAsync<string>(
                () => { attempts++; throw new PermissionRequestAbandonedException(); },
                isReady: () => true, TimeSpan.FromMilliseconds(1), _ => { }, CancellationToken.None));
        await Assert.That(attempts).IsEqualTo(1);
    }
```

New `ServerConnectionPermissionSplitTests.cs` — the split is a pure refactor of the invoke path, which cannot be exercised without a hub; what IS testable is the exception type's classification and the `RespondOutcome` mapping, so pin those:

```csharp
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class ServerConnectionPermissionSplitTests {
    [Test]
    public async Task Abandonment_is_neither_cancellation_nor_invalid_operation() {
        var ex = new PermissionRequestAbandonedException();
        await Assert.That(ex is OperationCanceledException).IsFalse();
        await Assert.That(ex is InvalidOperationException).IsFalse();
    }

    [Test]
    [Arguments("Permission request is no longer pending.", RespondOutcomeKind.NotPending)]
    [Arguments("Caller is not the daemon owning session", RespondOutcomeKind.Failed)]
    public async Task Respond_classifies_hub_exceptions_by_message(string message, RespondOutcomeKind expected) {
        await Assert.That(ServerConnection.ClassifyRespondFailure(new HubException(message)).Kind).IsEqualTo(expected);
    }

    [Test]
    public async Task Respond_classifies_a_dropped_connection_as_failed() {
        var outcome = ServerConnection.ClassifyRespondFailure(new InvalidOperationException("The 'InvokeCoreAsync' method cannot be called if the connection is not active"));
        await Assert.That(outcome.Kind).IsEqualTo(RespondOutcomeKind.Failed);
        await Assert.That(outcome.Reason).IsNotNull();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `… --treenode-filter "/*/*/ConnectionRetryTests/*"` and `… --treenode-filter "/*/*/ServerConnectionPermissionSplitTests/*"`. Expected: build errors.

- [ ] **Step 3: Implement**

`PermissionRequestAbandonedException.cs`:

```csharp
namespace Capacitor.Cli.Daemon.Services;

/// Thrown from the permission invoke lambda when the request settled before the hub call went
/// out. Its own type on purpose: ConnectionRetry retries OperationCanceledException and
/// InvalidOperationException as transient, and this must leave the loop at once.
internal sealed class PermissionRequestAbandonedException() : Exception("permission request settled before the server invoke");
```

`ServerConnection.cs` — add beside `PermissionDecision`-related members:

```csharp
    public enum RespondOutcomeKind { Applied, NotPending, Failed }
    public readonly record struct RespondOutcome(RespondOutcomeKind Kind, string? Reason);

    public virtual async Task<PermissionDecision> RequestPermissionAsync(
            string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct = default) {
        var requestId = await BeginPermissionRequestAsync(sessionId, toolName, toolInput, suggestions, ct, static () => false);
        return await AwaitPermissionDecisionAsync(requestId, ct);
    }

    /// The RequestPermission2 invoke under ConnectionRetry. `abandoned` is evaluated synchronously
    /// immediately before every hub invoke: a token cancelled from a task continuation is not
    /// synchronous with the settlement that requested it, so the predicate is what keeps a settled
    /// request's invoke off the wire when readiness returns.
    public virtual Task<string> BeginPermissionRequestAsync(
            string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions,
            CancellationToken ct, Func<bool> abandoned) =>
        ConnectionRetry.InvokeWithConnectionRetryAsync(
            () => {
                if (abandoned()) throw new PermissionRequestAbandonedException();
                return _hub.InvokeAsync<string>("RequestPermission2",
                    new HostedPermissionRequest(sessionId, toolName, toolInput, suggestions), ct);
            },
            () => IsReady,
            PermissionRetryPollInterval,
            attempt => LogPermissionRetry(sessionId, attempt),
            ct,
            isRetriableServerError: IsOwnershipNotReady,
            maxServerErrorRetries: OwnershipNotReadyMaxRetries);

    public virtual Task<PermissionDecision> AwaitPermissionDecisionAsync(string serverRequestId, CancellationToken ct) =>
        _pendingPermissions.AwaitDecisionAsync(serverRequestId, ct);

    /// The hub method the web UI answers through, invoked as the owner so the web card clears
    /// after a local settlement. Never throws; runs on the daemon-lifetime token.
    public virtual async Task<RespondOutcome> RespondToPermissionAsync(string sessionId, string serverRequestId, PermissionDecision decision) {
        try {
            await _hub.InvokeAsync("RespondToPermission", sessionId, serverRequestId, decision.Behavior,
                decision.ApplyPermissions, decision.UpdatedInput, _ct);
            return new RespondOutcome(RespondOutcomeKind.Applied, null);
        } catch (Exception ex) {
            return ClassifyRespondFailure(ex);
        }
    }

    internal static RespondOutcome ClassifyRespondFailure(Exception ex) =>
        ex is Microsoft.AspNetCore.SignalR.HubException he && he.Message.Contains("no longer pending", StringComparison.Ordinal)
            ? new RespondOutcome(RespondOutcomeKind.NotPending, he.Message)
            : new RespondOutcome(RespondOutcomeKind.Failed, ex.Message);
```

Delete the old body of `RequestPermissionAsync` (keep its doc comment, trimmed to what still holds).

`LocalPermissionBridgeTests.cs` — extend `FakeServerConnection` so Task 9 can script each leg. Replace the class with:

```csharp
sealed class FakeServerConnection(Func<string, string?, JsonElement?, JsonElement?, CancellationToken, Task<PermissionDecision>>? respond)
    : ServerConnection(new() { Name = "test", ServerUrl = "http://127.0.0.1:1" }, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance) {
    public List<Call> Calls { get; } = [];
    public List<(string SessionId, string RequestId, PermissionDecision Decision)> Responds { get; } = [];

    /// Scripted legs. Null = the legacy composition (RequestPermissionAsync via `respond`).
    public Func<CancellationToken, Func<bool>, Task<string>>? BeginScript;
    public Func<string, CancellationToken, Task<PermissionDecision>>? AwaitScript;
    public Func<RespondOutcome> RespondScript = () => new RespondOutcome(RespondOutcomeKind.Applied, null);

    public override Task<PermissionDecision> RequestPermissionAsync(string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct = default) {
        Calls.Add(new Call(sessionId, toolName, toolInput, suggestions));
        return respond is null ? Task.FromResult(new PermissionDecision("allow", null, null)) : respond(sessionId, toolName, toolInput, suggestions, ct);
    }

    public override Task<string> BeginPermissionRequestAsync(string sessionId, string? toolName, JsonElement? toolInput, JsonElement? suggestions, CancellationToken ct, Func<bool> abandoned) {
        Calls.Add(new Call(sessionId, toolName, toolInput, suggestions));
        if (BeginScript is not null) return BeginScript(ct, abandoned);
        if (abandoned()) throw new PermissionRequestAbandonedException();
        return Task.FromResult("srv-1");
    }

    public override Task<PermissionDecision> AwaitPermissionDecisionAsync(string serverRequestId, CancellationToken ct) =>
        AwaitScript is not null ? AwaitScript(serverRequestId, ct)
            : respond is not null ? respond("", null, null, null, ct)
            : Task.FromResult(new PermissionDecision("allow", null, null));

    public override Task<RespondOutcome> RespondToPermissionAsync(string sessionId, string serverRequestId, PermissionDecision decision) {
        Responds.Add((sessionId, serverRequestId, decision));
        return Task.FromResult(RespondScript());
    }

    public sealed record Call(string SessionId, string? ToolName, JsonElement? ToolInput, JsonElement? Suggestions);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: the two Step 2 commands plus `… --treenode-filter "/*/*/LocalPermissionBridgeTests/*"` (the existing bridge tests must still pass — they go through the reviewer/unattributed paths, which Task 9 keeps byte-for-byte). Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/PermissionRequestAbandonedException.cs src/Capacitor.Cli.Daemon/Services/ServerConnection.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/ConnectionRetryTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/ServerConnectionPermissionSplitTests.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeTests.cs
git commit -m "Split the permission request into begin, await and respond legs"
```

---

### Task 9: The bridge's interactive branch — register first, one claim, detached server leg

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` (constructor, the shared-token branch of `HandleAsync`, new `RunServerLegAsync`, `AttributeHandler`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeInteractiveTests.cs` (new class; the existing `LocalPermissionBridgeTests` stay untouched)

**Interfaces:**
- Consumes: Task 6 broker, Task 5 log, Task 8 `Begin`/`Await`/`RespondToPermissionAsync`, Task 2 `PermissionWire`.
- Produces:
  - `LocalPermissionBridge(ServerConnection server, ILogger<LocalPermissionBridge> logger, PermissionPromptBroker? broker = null, PermissionDecisionLog? decisionLog = null)` — optional trailing so every existing construction compiles; DI supplies the singletons (bare `AddSingleton<LocalPermissionBridge>()` resolves optional parameters from the container).
  - `internal readonly record struct PermissionAttribution(string? AgentId, string SessionId, string? Cwd)`
  - `internal readonly record struct AttributedAgent(string AgentId)` — what the handler returns (the bridge needs only the registry key; it never touches `AgentInstance`).
  - `internal Func<PermissionAttribution, AttributedAgent?>? AttributeHandler { get; set; }` on the bridge.
  - `internal static PermissionPendingDto? BuildPending(string requestId, string agentId, string sessionId, string vendor, string? toolName, JsonElement? toolInput, JsonElement? suggestions, string requestedAt)` — returns null when `tool_name` exceeds `MaxToolNameBytes` or `agentId` exceeds `MaxAgentIdBytes` (→ unattributed); omits oversized elements with the flags.
  - `internal static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(2)`.

The reviewer-token branch and the unattributed path stay byte-for-byte as today: the new code is reached only when `isShared` is true AND `AttributeHandler` returns an agent.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The shared-token branch with an attributed agent: the broker is the one claim point, the
/// server leg feeds it, and the hook receives whichever settlement won.
public class LocalPermissionBridgeInteractiveTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    sealed class Harness : IAsyncDisposable {
        public FakeServerConnection Server { get; } = new(respond: null);
        public PermissionPromptBroker Broker { get; } = new();
        public TempDir Tmp { get; } = new();
        public PermissionDecisionLog Log { get; }
        public LocalPermissionBridge Bridge { get; }
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(10) };

        public Harness(string? attributeTo = "agent-1") {
            Log    = new PermissionDecisionLog(Tmp.Path, NullLogger.Instance);
            Bridge = new LocalPermissionBridge(Server, NullLogger<LocalPermissionBridge>.Instance, Broker, Log) {
                AttributeHandler = attributeTo is null ? _ => null : _ => new AttributedAgent(attributeTo),
            };
        }

        public async Task StartAsync() => await Bridge.StartAsync(CancellationToken.None);

        /// Posts a Claude hook payload; the returned task completes when the hook is answered.
        public Task<HttpResponseMessage> PostAsync(string toolName = "Bash", string? agentId = "agent-1") =>
            Client.PostAsync($"{Bridge.BaseUrl}/claude/permission-request",
                JsonContent.Create(new { session_id = Session, tool_name = toolName, tool_input = new { command = "ls" }, agent_id = agentId, cwd = "/repo" }));

        public async Task<string> BehaviorOf(HttpResponseMessage response) {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString()!;
        }

        public string[] LogLines() {
            var path = Tmp.PathTo("permission-decisions.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }

        public async Task<PermissionPendingDto> WaitPendingAsync() {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Broker.PendingSnapshot().Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            return Broker.PendingSnapshot().Single();
        }

        public async ValueTask DisposeAsync() { await Bridge.DisposeAsync(); Client.Dispose(); Tmp.Dispose(); }
    }

    static PermissionDecision Allow => new("allow", null, null);
    static PermissionDecision Deny  => new("deny", null, null);

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task App_claim_first_answers_the_hook_cancels_the_server_await_responds_to_the_server_and_logs_app() {
        await using var h = new Harness();
        var awaitCts = new TaskCompletionSource<CancellationToken>();
        h.Server.AwaitScript = (_, ct) => { awaitCts.SetResult(ct); return new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct); };
        await h.StartAsync();

        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        await Assert.That(pending.SessionId).IsEqualTo(Session);
        await Assert.That(pending.AgentId).IsEqualTo("agent-1");

        await Assert.That(h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app")).IsTrue();
        await Assert.That(await h.BehaviorOf(await response)).IsEqualTo("allow");

        var ct = await awaitCts.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntil(() => ct.IsCancellationRequested, "the server await is cancelled");
        await WaitUntil(() => h.Server.Responds.Count == 1, "RespondToPermission is invoked");
        await Assert.That(h.Server.Responds[0].RequestId).IsEqualTo("srv-1");
        await Assert.That(h.Server.Responds[0].Decision.Behavior).IsEqualTo("allow");

        var lines = h.LogLines();
        await Assert.That(lines.Length).IsEqualTo(1);
        await Assert.That(lines[0]).Contains("\"source\":\"app\"");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Server_claim_first_answers_the_hook_pushes_resolved_server_and_a_later_app_claim_loses() {
        await using var h = new Harness();
        var serverDecision = new TaskCompletionSource<PermissionDecision>();
        h.Server.AwaitScript = (_, ct) => serverDecision.Task.WaitAsync(ct);
        await h.StartAsync();
        var (_, reader) = h.Broker.Subscribe();

        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        _ = await reader.ReadAsync(new CancellationTokenSource(5000).Token); // Pending

        serverDecision.SetResult(Deny);
        await Assert.That(await h.BehaviorOf(await response)).IsEqualTo("deny");
        var resolved = ((PermissionStreamItem.Resolved)await reader.ReadAsync(new CancellationTokenSource(5000).Token)).Dto;
        await Assert.That(resolved.Source).IsEqualTo("server");
        await Assert.That(h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app")).IsFalse();
        await Assert.That(h.Server.Responds.Count).IsEqualTo(0);
        await Assert.That(h.LogLines()[0]).Contains("\"source\":\"server\"");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Respond_reporting_not_pending_is_logged_not_treated_as_a_conflict() {
        await using var h = new Harness();
        h.Server.AwaitScript = (_, ct) => new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);
        h.Server.RespondScript = () => new ServerConnection.RespondOutcome(ServerConnection.RespondOutcomeKind.NotPending, "Permission request is no longer pending.");
        await h.StartAsync();
        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");
        await Assert.That(await h.BehaviorOf(await response)).IsEqualTo("allow");
        await WaitUntil(() => h.Server.Responds.Count == 1, "respond attempted");
        await Assert.That(h.LogLines()[0]).Contains("\"outcome\":\"allow\"");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Begin_fault_with_no_subscriber_denies_as_no_ui_and_logs_it() {
        await using var h = new Harness();
        h.Server.BeginScript = (_, _) => throw new Microsoft.AspNetCore.SignalR.HubException("boom");
        await h.StartAsync();
        var response = await h.PostAsync();
        await Assert.That(await h.BehaviorOf(response)).IsEqualTo("deny");
        await Assert.That(h.LogLines()[0]).Contains("\"source\":\"no_ui\"");
        await Assert.That(h.Server.Responds.Count).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Begin_fault_with_a_subscriber_keeps_the_request_answerable() {
        await using var h = new Harness();
        var (id, _) = h.Broker.Subscribe();
        h.Server.BeginScript = (_, _) => throw new Microsoft.AspNetCore.SignalR.HubException("boom");
        await h.StartAsync();
        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        await Task.Delay(100);
        await Assert.That(response.IsCompleted).IsFalse();
        h.Broker.Unsubscribe(id);
        await Task.Delay(100);
        await Assert.That(response.IsCompleted).IsFalse().Because("a subscriber leaving never denies");
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");
        await Assert.That(await h.BehaviorOf(await response)).IsEqualTo("allow");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Begin_held_in_readiness_wait_is_abandoned_by_the_predicate_with_the_cancellation_held() {
        await using var h = new Harness();
        var release = new TaskCompletionSource();
        Func<bool>? seen = null;
        var invoked = 0;
        // Models ConnectionRetry: wait for "readiness" (the release), then check the predicate
        // immediately before the invoke. The token is deliberately IGNORED so the queued
        // cancellation cannot be what ends the leg.
        h.Server.BeginScript = async (_, abandoned) => {
            seen = abandoned;
            await release.Task;
            if (abandoned()) throw new PermissionRequestAbandonedException();
            invoked++;
            return "srv-1";
        };
        await h.StartAsync();
        var response = h.PostAsync();
        var pending = await h.WaitPendingAsync();
        await WaitUntil(() => seen is not null, "Begin entered");

        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");
        await Assert.That(await h.BehaviorOf(await response)).IsEqualTo("allow");
        release.SetResult();
        await WaitUntil(() => h.Bridge.ServerLegsInFlightForTest == 0, "the leg completes");
        await Assert.That(invoked).IsEqualTo(0);
        await Assert.That(h.Server.Responds.Count).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Withdrawal_during_a_held_begin_settles_withdrawn_and_the_leg_completes() {
        await using var h = new Harness();
        var release = new TaskCompletionSource();
        h.Server.BeginScript = async (ct, abandoned) => { await release.Task.WaitAsync(ct); if (abandoned()) throw new PermissionRequestAbandonedException(); return "srv-1"; };
        await h.StartAsync();
        var response = h.PostAsync();
        _ = await h.WaitPendingAsync();
        h.Broker.WithdrawForAgent("agent-1");
        await Assert.That(await h.BehaviorOf(await response)).IsEqualTo("deny");
        await Assert.That(h.LogLines()[0]).Contains("\"source\":\"agent_gone\"");
        await WaitUntil(() => h.Bridge.ServerLegsInFlightForTest == 0, "the leg completes");
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Unattributed_request_takes_the_server_only_path() {
        await using var h = new Harness(attributeTo: null);
        await h.StartAsync();
        var response = await h.PostAsync();
        await Assert.That(await h.BehaviorOf(response)).IsEqualTo("allow"); // the fake's legacy composition
        await Assert.That(h.Broker.PendingSnapshot().Count).IsEqualTo(0);
        await Assert.That(h.LogLines().Length).IsEqualTo(0);
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeInteractiveTests))]
    public async Task Oversized_tool_input_is_omitted_on_the_wire_with_the_flag() {
        await using var h = new Harness();
        h.Server.AwaitScript = (_, ct) => new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);
        await h.StartAsync();
        var big = new string('x', PermissionWire.MaxElementBytes);
        var response = h.Client.PostAsync($"{h.Bridge.BaseUrl}/claude/permission-request",
            JsonContent.Create(new { session_id = Session, tool_name = "Bash", tool_input = new { command = big }, agent_id = "agent-1" }));
        var pending = await h.WaitPendingAsync();
        await Assert.That(pending.ToolInput).IsNull();
        await Assert.That(pending.ToolInputOmitted).IsTrue();
        h.Broker.TrySettle(pending.RequestId, Allow, "allow", "app");
        await Assert.That(await h.BehaviorOf(await response)).IsEqualTo("allow");
    }

    [Test]
    public async Task Build_pending_bounds() {
        var ok = LocalPermissionBridge.BuildPending("r", "a1", Session, "claude", "Bash", null, null, "t");
        await Assert.That(ok).IsNotNull();
        await Assert.That(ok!.ToolName).IsEqualTo("Bash");
        await Assert.That(LocalPermissionBridge.BuildPending("r", "a1", Session, "claude", new string('n', PermissionWire.MaxToolNameBytes + 1), null, null, "t")).IsNull();
        await Assert.That(LocalPermissionBridge.BuildPending("r", new string('k', PermissionWire.MaxAgentIdBytes + 1), Session, "claude", "Bash", null, null, "t")).IsNull();
        await Assert.That(LocalPermissionBridge.BuildPending("r", "a1", Session, "codex", null, null, null, "t")!.ToolName).IsEqualTo("");
    }

    static async Task WaitUntil(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalPermissionBridgeInteractiveTests/*"`
Expected: build errors — the bridge has no broker parameter, `AttributeHandler`, `BuildPending`, `ServerLegsInFlightForTest`.

- [ ] **Step 3: Implement in `LocalPermissionBridge.cs`**

Constructor and new members (replace the primary constructor's parameter list and add fields):

```csharp
internal sealed partial class LocalPermissionBridge(
        ServerConnection               server,
        ILogger<LocalPermissionBridge> logger,
        PermissionPromptBroker?        broker      = null,
        PermissionDecisionLog?         decisionLog = null
    ) : IHostedService, IAsyncDisposable {
    internal static readonly TimeSpan ResponseWriteTimeout = TimeSpan.FromSeconds(2);

    readonly PermissionPromptBroker _broker      = broker ?? new();
    readonly PermissionDecisionLog? _decisionLog = decisionLog;
    int _serverLegsInFlight;

    /// Assigned by the orchestrator after construction (it takes this bridge in its own
    /// constructor, so the dependency cannot point the other way). Null = every request is
    /// unattributed and takes the server-only path.
    internal Func<PermissionAttribution, AttributedAgent?>? AttributeHandler { get; set; }

    internal int ServerLegsInFlightForTest => Volatile.Read(ref _serverLegsInFlight);
```

Add the records (same file, outside the class):

```csharp
internal readonly record struct PermissionAttribution(string? AgentId, string SessionId, string? Cwd);
internal readonly record struct AttributedAgent(string AgentId);
```

In `HandleAsync`, replace the `else` branch of `if (isReviewer) { … } else { … }` (the shared-token path) with:

```csharp
            } else {
                var attributed = AttributeHandler?.Invoke(new PermissionAttribution(
                    node["agent_id"]?.GetValue<string>(), sessionId, node["cwd"]?.GetValue<string>()));
                var pending = attributed is { } a
                    ? BuildPending(Guid.NewGuid().ToString("N"), a.AgentId, sessionId, vendor, toolName, toolInput, suggestions,
                        DateTimeOffset.UtcNow.ToString("O"))
                    : null;

                if (pending is null) {
                    // Server-only path, exactly as before attribution existed.
                    if (attributed is not null) LogUnattributable(logger, sessionId);
                    try {
                        decision = await server.RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct);
                    } catch (Exception ex) {
                        LogRequestPermissionFailed(logger, ex, sessionId);
                        decision = new PermissionDecision("deny", null, null);
                    }
                } else {
                    var settlementTask = _broker.Register(pending);
                    _ = RunServerLegAsync(pending, toolName, toolInput, suggestions, settlementTask, ct);

                    PermissionSettlement settlement;
                    try {
                        settlement = await settlementTask.WaitAsync(ct);
                    } catch (OperationCanceledException) {
                        // Shutdown: claim rather than inspect. Losing means another party settled first.
                        if (_broker.TrySettle(pending.RequestId, PermissionSettlements.DenyDecision,
                                PermissionSettlements.Deny, PermissionSettlements.SourceDaemonShutdown)) {
                            await WriteResponseAsync(context, BuildHookResponseJson(PermissionSettlements.DenyDecision, vendor));
                            return;
                        }
                        settlement = await settlementTask;
                    }

                    _decisionLog?.Record(new PermissionDecisionRecord(
                        DateTimeOffset.UtcNow.ToString("O"), pending.AgentId, pending.SessionId, pending.Vendor,
                        pending.ToolName, settlement.Outcome, settlement.Source));
                    await WriteResponseAsync(context, BuildHookResponseJson(settlement.Decision, vendor));
                    return;
                }
            }
```

Replace the tail of `HandleAsync` (the four lines that build `responseJson`, set headers and write) with a call to the same helper so both paths share it:

```csharp
            await WriteResponseAsync(context, BuildHookResponseJson(decision, vendor));
```

and add:

```csharp
    /// Written under a bounded token of its own, never the bridge token: shutdown cancels that
    /// token before the drain, and a claimed answer must still reach the hook.
    static async Task WriteResponseAsync(HttpListenerContext context, string responseJson) {
        using var writeCts = new CancellationTokenSource(ResponseWriteTimeout);
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        context.Response.ContentType     = "application/json";
        context.Response.StatusCode      = 200;
        context.Response.ContentLength64 = bytes.LongLength;
        await context.Response.OutputStream.WriteAsync(bytes, writeCts.Token);
        context.Response.Close();
    }

    internal static PermissionPendingDto? BuildPending(
            string requestId, string agentId, string sessionId, string vendor, string? toolName,
            JsonElement? toolInput, JsonElement? suggestions, string requestedAt) {
        var name = toolName ?? "";
        if (Encoding.UTF8.GetByteCount(name) > PermissionWire.MaxToolNameBytes) return null;
        if (Encoding.UTF8.GetByteCount(agentId) > PermissionWire.MaxAgentIdBytes) return null;
        var (input, inputOmitted)   = Bound(toolInput);
        var (sugg,  suggOmitted)    = Bound(suggestions);
        return new PermissionPendingDto(requestId, agentId, sessionId, vendor, name, input, sugg, inputOmitted, suggOmitted, requestedAt);

        static (JsonElement?, bool) Bound(JsonElement? el) =>
            el is { } e && Encoding.UTF8.GetByteCount(e.GetRawText()) > PermissionWire.MaxElementBytes ? (null, true) : (el, false);
    }

    /// Everything that touches the server for one request. Total: every exit returns normally
    /// and the bridge never awaits it. The settlement continuation only WAKES a wait — the
    /// broker's TCS runs continuations asynchronously — while the `abandoned` predicate, read
    /// synchronously before the hub invoke, is what keeps a settled request off the wire.
    async Task RunServerLegAsync(
            PermissionPendingDto pending, string? toolName, JsonElement? toolInput, JsonElement? suggestions,
            Task<PermissionSettlement> settlement, CancellationToken daemonToken) {
        Interlocked.Increment(ref _serverLegsInFlight);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(daemonToken);
        var wake = settlement.ContinueWith(_ => { try { cts.Cancel(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
        try {
            string serverRequestId;
            try {
                serverRequestId = await server.BeginPermissionRequestAsync(
                    pending.SessionId, toolName, toolInput, suggestions, cts.Token, () => settlement.IsCompleted);
            } catch (OperationCanceledException) {
                return; // shutdown, or the settlement woke the readiness wait: no server request exists
            } catch (PermissionRequestAbandonedException) {
                return;
            } catch (Exception ex) {
                LogServerLegBeginFailed(logger, ex, pending.RequestId);
                _broker.TrySettleIfNoSubscriber(pending.RequestId, PermissionSettlements.DenyDecision,
                    PermissionSettlements.Deny, PermissionSettlements.SourceNoUi);
                return;
            }

            if (settlement.IsCompleted) {
                await RelaySettlementAsync(pending, serverRequestId, settlement.Result);
                return;
            }

            PermissionDecision decision;
            try {
                decision = await server.AwaitPermissionDecisionAsync(serverRequestId, cts.Token);
            } catch (OperationCanceledException) {
                if (daemonToken.IsCancellationRequested) return;
                await RelaySettlementAsync(pending, serverRequestId, await settlement);
                return;
            }

            if (!_broker.TrySettle(pending.RequestId, decision, decision.Behavior, PermissionSettlements.SourceServer))
                LogServerDecisionArrivedLate(logger, pending.RequestId, (await settlement).Decision.Behavior);
        } catch (Exception ex) {
            LogServerLegFaulted(logger, ex, pending.RequestId);
        } finally {
            _ = wake;
            Interlocked.Decrement(ref _serverLegsInFlight);
        }
    }

    async Task RelaySettlementAsync(PermissionPendingDto pending, string serverRequestId, PermissionSettlement settlement) {
        if (settlement.Source == PermissionSettlements.SourceServer) return;
        var outcome = await server.RespondToPermissionAsync(pending.SessionId, serverRequestId, settlement.Decision);
        switch (outcome.Kind) {
            case ServerConnection.RespondOutcomeKind.NotPending:
                LogServerNoLongerHeld(logger, pending.RequestId, settlement.Decision.Behavior);
                break;
            case ServerConnection.RespondOutcomeKind.Failed:
                LogRespondFailed(logger, pending.RequestId, outcome.Reason ?? "");
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Permission request for session {SessionId} could not be attributed to a live agent; server-only path")]
    static partial void LogUnattributable(ILogger logger, string sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server leg for permission request {RequestId} could not begin")]
    static partial void LogServerLegBeginFailed(ILogger logger, Exception exception, string requestId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Server leg for permission request {RequestId} faulted")]
    static partial void LogServerLegFaulted(ILogger logger, Exception exception, string requestId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Server decision for permission request {RequestId} arrived after it was settled locally; the hook received {Behavior}")]
    static partial void LogServerDecisionArrivedLate(ILogger logger, string requestId, string behavior);

    [LoggerMessage(Level = LogLevel.Information, Message = "The server no longer held permission request {RequestId} when the local decision was relayed; the hook received {Behavior}")]
    static partial void LogServerNoLongerHeld(ILogger logger, string requestId, string behavior);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relaying the local decision for permission request {RequestId} to the server failed: {Reason}")]
    static partial void LogRespondFailed(ILogger logger, string requestId, string reason);
```

Two details the executor must keep: `sessionId` in `HandleAsync` is already the dashless form; canonicalize it once more with `PermissionWire.Canonical(sessionId)` and treat `null` as unattributed (a non-GUID session id never reaches the broker). The `cts` in the leg is disposed by `using` after the `finally`; the guarded `Cancel` in the continuation is what makes a late wake harmless.

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command, then the whole `LocalPermissionBridgeTests` class. Expected: green — the legacy tests still exercise the reviewer and unattributed (no `AttributeHandler`) paths.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeInteractiveTests.cs
git commit -m "Race the app and the server through one claim in the permission bridge"
```

---

### Task 10: Bridge shutdown — admission gate and drain

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs` (`AcceptLoopAsync`, `StopAsync`, new gate fields)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeShutdownTests.cs`

**Interfaces:**
- Produces: `internal static readonly TimeSpan ShutdownDrain = TimeSpan.FromSeconds(2)`; `internal int InFlightHandlersForTest`; `internal Func<Task>? BeforeHandlerRunsForTest` (a seam the tests use to hold a handler between admission and entry).

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// Shutdown through the real StopAsync: a claimed answer is delivered inside the drain, an
/// admitted-but-unstarted handler is still counted, and a context arriving after admission
/// closed is rejected without ever being tracked.
public class LocalPermissionBridgeShutdownTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    static (LocalPermissionBridge bridge, FakeServerConnection server, PermissionPromptBroker broker) Build() {
        var server = new FakeServerConnection(respond: null);
        var broker = new PermissionPromptBroker();
        var bridge = new LocalPermissionBridge(server, NullLogger<LocalPermissionBridge>.Instance, broker) {
            AttributeHandler = _ => new AttributedAgent("agent-1"),
        };
        server.AwaitScript = (_, ct) => new TaskCompletionSource<PermissionDecision>().Task.WaitAsync(ct);
        return (bridge, server, broker);
    }

    static Task<HttpResponseMessage> Post(HttpClient client, LocalPermissionBridge bridge) =>
        client.PostAsync($"{bridge.BaseUrl}/claude/permission-request",
            JsonContent.Create(new { session_id = Session, tool_name = "Bash", agent_id = "agent-1" }));

    static async Task<string> BehaviorOf(HttpResponseMessage r) {
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision").GetProperty("behavior").GetString()!;
    }

    static async Task WaitUntil(Func<bool> c, string what) {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!c()) { if (DateTime.UtcNow > deadline) throw new TimeoutException(what); await Task.Delay(10); }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeShutdownTests))]
    public async Task Shutdown_with_no_other_claim_answers_deny_with_no_record_and_the_leg_completes() {
        var (bridge, _, broker) = Build();
        await bridge.StartAsync(CancellationToken.None);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = Post(client, bridge);
        await WaitUntil(() => broker.PendingSnapshot().Count == 1, "pending");

        var stop = bridge.StopAsync(CancellationToken.None);
        await Assert.That(await BehaviorOf(await response)).IsEqualTo("deny");
        await stop;
        await Assert.That(bridge.ServerLegsInFlightForTest).IsEqualTo(0);
        await Assert.That(broker.PendingSnapshot().Count).IsEqualTo(0);
        await bridge.DisposeAsync();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeShutdownTests))]
    public async Task App_claim_landing_as_the_token_fires_is_delivered_inside_the_drain() {
        var (bridge, _, broker) = Build();
        await bridge.StartAsync(CancellationToken.None);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = Post(client, bridge);
        await WaitUntil(() => broker.PendingSnapshot().Count == 1, "pending");
        var requestId = broker.PendingSnapshot()[0].RequestId;

        // Claim first, then stop: the token fires after the claim; the bridge's shutdown claim must lose.
        await Assert.That(broker.TrySettle(requestId, new PermissionDecision("allow", null, null), "allow", "app")).IsTrue();
        var stop = bridge.StopAsync(CancellationToken.None);
        await Assert.That(await BehaviorOf(await response)).IsEqualTo("allow");
        await stop;
        await bridge.DisposeAsync();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeShutdownTests))]
    public async Task Admitted_handler_held_before_entry_is_drained_with_the_token_already_cancelled() {
        var (bridge, _, broker) = Build();
        var hold = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        bridge.BeforeHandlerRunsForTest = async () => { entered.TrySetResult(); await hold.Task; };
        await bridge.StartAsync(CancellationToken.None);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var response = Post(client, bridge);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(bridge.InFlightHandlersForTest).IsEqualTo(1);

        var started = DateTime.UtcNow;
        var stop = bridge.StopAsync(CancellationToken.None);
        hold.SetResult();                       // the delegate runs despite the cancelled token
        await Assert.That(await BehaviorOf(await response)).IsEqualTo("deny");
        await stop;
        await Assert.That(bridge.InFlightHandlersForTest).IsEqualTo(0);
        await Assert.That(DateTime.UtcNow - started < LocalPermissionBridge.ShutdownDrain).IsTrue().Because("the drain must not expire");
        await bridge.DisposeAsync();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeShutdownTests))]
    public async Task Context_arriving_after_admission_closed_is_rejected_untracked() {
        var (bridge, _, _) = Build();
        var hold = new TaskCompletionSource();
        bridge.BeforeHandlerRunsForTest = () => hold.Task;
        await bridge.StartAsync(CancellationToken.None);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var first = Post(client, bridge);
        await WaitUntil(() => bridge.InFlightHandlersForTest == 1, "first admitted");

        var stop = bridge.StopAsync(CancellationToken.None);   // closes admission, drains the first
        await WaitUntil(() => !bridge.AdmittingForTest, "admission closed");
        var second = await Post(client, bridge);
        await Assert.That((int)second.StatusCode).IsEqualTo(503);
        await Assert.That(bridge.InFlightHandlersForTest).IsEqualTo(1);
        hold.SetResult();
        await first;
        await stop;
        await bridge.DisposeAsync();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `… --treenode-filter "/*/*/LocalPermissionBridgeShutdownTests/*"`. Expected: build errors (`ShutdownDrain`, `InFlightHandlersForTest`, `AdmittingForTest`, `BeforeHandlerRunsForTest`).

- [ ] **Step 3: Implement the gate and the drain**

Fields:

```csharp
    internal static readonly TimeSpan ShutdownDrain = TimeSpan.FromSeconds(2);

    // One gate owns admission and the in-flight count together, so a snapshot taken under it
    // is exact: nothing admitted after it, nothing admitted before it invisible.
    readonly object _admission = new();
    bool _admitting = true;
    int  _inFlight;

    internal int  InFlightHandlersForTest => Volatile.Read(ref _inFlight);
    internal bool AdmittingForTest { get { lock (_admission) return _admitting; } }
    internal Func<Task>? BeforeHandlerRunsForTest { get; set; }
```

`StartAsync` — reset `_admitting = true` beside `_listenerClosed = 0`.

`AcceptLoopAsync` — replace the fire-and-forget line with:

```csharp
            bool admitted;
            lock (_admission) {
                admitted = _admitting;
                if (admitted) _inFlight++;
            }
            if (!admitted) {
                try { context.Response.StatusCode = 503; context.Response.Close(); } catch { /* peer gone */ }
                continue;
            }
            // No scheduling token: a delegate cancelled before it starts never runs its finally,
            // and the count would never reach zero.
            _ = Task.Run(() => RunTrackedAsync(context, ct));
```

and add:

```csharp
    async Task RunTrackedAsync(HttpListenerContext context, CancellationToken ct) {
        try {
            if (BeforeHandlerRunsForTest is { } hold) await hold();
            await HandleAsync(context, ct);
        } finally {
            lock (_admission) _inFlight--;
        }
    }
```

`StopAsync` — between the token cancellation and `listener.Close()`, insert the drain:

```csharp
        lock (_admission) _admitting = false;
        var drainDeadline = DateTime.UtcNow + ShutdownDrain;
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < drainDeadline)
            await Task.Delay(10, CancellationToken.None);
```

(Closing the listener before the drain would abort the very responses the claims promised.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command plus `LocalPermissionBridgeTests` and `LocalPermissionBridgeInteractiveTests`. Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LocalPermissionBridge.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalPermissionBridgeShutdownTests.cs
git commit -m "Drain admitted permission handlers before the bridge closes its listener"
```

---

### Task 11: Orchestrator — attribution ladder and withdrawal

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (ctor: `PermissionPromptBroker? permissionBroker = null` trailing param; handler assignment beside `_server.FindRepoForRemoteHandler`; `HandleAttributePermission`; `WithdrawForAgent` before `UnpublishAgent` in the teardown path ~L4622)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` — no change needed: `AddSingleton<AgentOrchestrator>()` is bare, so DI supplies the registered broker to the optional parameter (the `DaemonStatusNotifier` note in `DaemonRunner` documents this mechanism).
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorPermissionAttributionTests.cs`

**Interfaces:**
- Consumes: Task 9 `PermissionAttribution`/`AttributedAgent`/`AttributeHandler`, Task 6 broker.
- Produces: `internal AttributedAgent? HandleAttributePermission(PermissionAttribution query)`; `internal PermissionPromptBroker PermissionBrokerForTest`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Capacitor.Cli.Daemon.Services;
using static Capacitor.Cli.Daemon.Tests.Unit.Services.AgentOrchestratorHarness;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorPermissionAttributionTests {
    const string S1 = "6ba7b8109dad11d180b400c04fd430c8";

    static AgentInstance Agent(string id, string worktree, string? sessionId = null) =>
        new(id, null, "", null, "/repo", "claude", new FakeHostedAgentRuntime("claude", true),
            new WorktreeInfo(worktree, "b", "/repo"), new CancellationTokenSource()) { SessionId = sessionId };

    static AgentOrchestrator Build() =>
        BuildOrchestrator(new FakeServerConnectionForAttribution(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task Agent_id_rung_matches_raw_then_canonical_and_needs_exactly_one() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", "/w1")); // a non-"N" key
        orch.RegisterAgentForTest(Agent("not-a-guid-key", "/w2"));

        await Assert.That(orch.HandleAttributePermission(new("6BA7B810-9DAD-11D1-80B4-00C04FD430C8", S1, null))!.Value.AgentId)
            .IsEqualTo("6BA7B810-9DAD-11D1-80B4-00C04FD430C8");                                   // raw
        await Assert.That(orch.HandleAttributePermission(new("6ba7b8109dad11d180b400c04fd430c8", S1, null))!.Value.AgentId)
            .IsEqualTo("6BA7B810-9DAD-11D1-80B4-00C04FD430C8");                                   // canonical
        await Assert.That(orch.HandleAttributePermission(new("not-a-guid-key", S1, null))!.Value.AgentId)
            .IsEqualTo("not-a-guid-key");                                                          // raw, non-GUID
        await Assert.That(orch.HandleAttributePermission(new("unknown", "ffffffffffffffffffffffffffffffff", null))).IsNull();
    }

    [Test]
    public async Task Session_rung_matches_any_guid_shape_and_falls_through_on_two_matches() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/w1", sessionId: "6BA7B810-9DAD-11D1-80B4-00C04FD430C8"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, null))!.Value.AgentId).IsEqualTo("a1");

        orch.RegisterAgentForTest(Agent("a2", "/w2", sessionId: S1));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, null))).IsNull();
    }

    [Test]
    public async Task Cwd_rung_matches_one_worktree_and_falls_through_on_a_shared_checkout() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/repo/.capacitor/worktrees/agent-a1"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, "/repo/.capacitor/worktrees/agent-a1/"))!.Value.AgentId).IsEqualTo("a1");

        orch.RegisterAgentForTest(Agent("b1", "/shared"));
        orch.RegisterAgentForTest(Agent("b2", "/shared"));
        await Assert.That(orch.HandleAttributePermission(new(null, S1, "/shared"))).IsNull();
    }

    [Test]
    public async Task Malformed_session_id_is_unattributed() {
        await using var orch = Build();
        orch.RegisterAgentForTest(Agent("a1", "/w1", sessionId: S1));
        await Assert.That(orch.HandleAttributePermission(new("a1", "nope", "/w1"))).IsNull();
    }

    [Test]
    public async Task Teardown_withdraws_the_agents_pending_permissions_before_unpublishing() {
        await using var orch = Build();
        var agent = Agent("a1", "/w1");
        orch.RegisterAgentForTest(agent);
        var settlement = orch.PermissionBrokerForTest.Register(
            new("r1", "a1", S1, "claude", "Bash", null, null, false, false, "t"));

        orch.UnpublishAgentForTest("a1");
        var s = await settlement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(s.Source).IsEqualTo("agent_gone");
    }

    sealed class FakeServerConnectionForAttribution() : ServerConnection(
        new() { Name = "test", ServerUrl = "http://127.0.0.1:1" },
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ServerConnection>.Instance);
}
```

`SpyPtyProcessFactory` is the harness's existing PTY spy (used throughout the orchestrator suites). If `UnpublishAgentForTest` does not exist, add `internal void UnpublishAgentForTest(string agentId) => WithdrawAndUnpublish(agentId);` where `WithdrawAndUnpublish` is the two-line sequence the teardown path calls (Step 3).

- [ ] **Step 2: Run to verify they fail**

Run: `… --treenode-filter "/*/*/AgentOrchestratorPermissionAttributionTests/*"`. Expected: build errors.

- [ ] **Step 3: Implement in `AgentOrchestrator.cs`**

Constructor: append `PermissionPromptBroker? permissionBroker = null` after `statusNotifier`, store `_permissionBroker = permissionBroker ?? new();` and, beside `_server.FindRepoForRemoteHandler = HandleFindRepoForRemote;`, add:

```csharp
        _permissionBridge.AttributeHandler = HandleAttributePermission;
```

The handler:

```csharp
    /// The attribution ladder: the payload's agent id (raw, then canonical GUID), the resolved
    /// vendor session id, the worktree path — each rung only on exactly one live match. Live is
    /// "present in _agents"; teardown withdraws whatever was attributed during that window.
    internal AttributedAgent? HandleAttributePermission(PermissionAttribution query) {
        var canonicalSession = PermissionWire.Canonical(query.SessionId);
        if (canonicalSession is null) return null;

        var live = _agents.Values.ToList();

        if (query.AgentId is { Length: > 0 } rawId) {
            var raw = live.Where(a => string.Equals(a.Id, rawId, StringComparison.Ordinal)).ToList();
            if (raw.Count == 1) return new AttributedAgent(raw[0].Id);
            if (PermissionWire.Canonical(rawId) is { } canonicalId) {
                var canon = live.Where(a => PermissionWire.Canonical(a.Id) == canonicalId).ToList();
                if (canon.Count == 1) return new AttributedAgent(canon[0].Id);
            }
        }

        var bySession = live.Where(a => a.SessionId is { } s && PermissionWire.Canonical(s) == canonicalSession).ToList();
        if (bySession.Count == 1) return new AttributedAgent(bySession[0].Id);

        if (query.Cwd is { Length: > 0 } cwd) {
            var wanted = Path.TrimEndingDirectorySeparator(cwd);
            var byCwd = live.Where(a => string.Equals(
                Path.TrimEndingDirectorySeparator(a.Worktree.Path), wanted, RepoPathStore.PathComparison)).ToList();
            if (byCwd.Count == 1) return new AttributedAgent(byCwd[0].Id);
        }

        return null;
    }

    internal PermissionPromptBroker PermissionBrokerForTest => _permissionBroker;
    internal void UnpublishAgentForTest(string agentId) => WithdrawAndUnpublish(agentId);

    void WithdrawAndUnpublish(string agentId) {
        _permissionBroker.WithdrawForAgent(agentId);
        UnpublishAgent(agentId);
    }
```

In the teardown path, replace the `UnpublishAgent(agentId);` call that follows the quarantine/PID-record block (~L4622) with `WithdrawAndUnpublish(agentId);`. `RepoPathStore` is `Capacitor.Cli.Core.Config.RepoPathStore`; `PermissionWire` is `Capacitor.Cli.Core.LocalIpc`.

- [ ] **Step 4: Run the daemon suite**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`. Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentOrchestratorPermissionAttributionTests.cs
git commit -m "Attribute permission prompts to one live agent and withdraw them on teardown"
```

---

### Task 12: Hook payloads carry `agent_id` (and `cwd` for Claude) on the bridge branch only

**Files:**
- Create: `src/Capacitor.Cli/Commands/HookAgentId.cs`
- Modify: `src/Capacitor.Cli/Commands/PermissionRequestCommand.cs` (`HandleRenderedAgent`)
- Modify: `src/Capacitor.Cli/Commands/Harness/CodexHookCommand.cs` (`HandlePermissionRequestViaBridge`)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/HookAgentIdTests.cs`, `test/Capacitor.Cli.Tests.Unit/Commands/PermissionRequestCommandTests.cs` (append), `test/Capacitor.Cli.Tests.Unit/Commands/Harness/CodexHookCommandTests.cs` (append)

**Interfaces:**
- Produces: `internal static class HookAgentId { static string? FromEnvironment() }` (reads `KCAP_AGENT_ID`, null when unset/empty); `internal static JsonObject PermissionRequestCommand.BuildBridgePayload(JsonNode node, string sessionId, string? agentId)`; `internal static JsonObject CodexHookCommand.BuildBridgePayload(JsonNode node, string? agentId)`.

- [ ] **Step 1: Write the failing tests**

`HookAgentIdTests.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Bare: KCAP_AGENT_ID is read by two hook commands and inherited by spawned children.
[NotInParallel]
public class HookAgentIdTests {
    [Test]
    public async Task Unset_and_empty_are_null() {
        using (EnvScope.Exclusive("KCAP_AGENT_ID", null)) await Assert.That(HookAgentId.FromEnvironment()).IsNull();
        using (EnvScope.Exclusive("KCAP_AGENT_ID", "")) await Assert.That(HookAgentId.FromEnvironment()).IsNull();
    }

    [Test]
    public async Task Set_is_returned_verbatim() {
        using var _ = EnvScope.Exclusive("KCAP_AGENT_ID", "6ba7b8109dad11d180b400c04fd430c8");
        await Assert.That(HookAgentId.FromEnvironment()).IsEqualTo("6ba7b8109dad11d180b400c04fd430c8");
    }
}
```

Append to `PermissionRequestCommandTests.cs`:

```csharp
    [Test]
    public async Task Bridge_payload_adds_agent_id_and_cwd_and_leaves_the_server_shape_alone() {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"session_id":"abc","tool_name":"Bash","tool_input":{"command":"ls"},"permission_suggestions":null,"cwd":"/repo","transcript_path":"/t"}""")!;
        var bridge = PermissionRequestCommand.BuildBridgePayload(node, "abc", "agent-1");
        await Assert.That(bridge["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(bridge["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(bridge["tool_name"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(bridge["transcript_path"]).IsNull();

        var withoutAgent = PermissionRequestCommand.BuildBridgePayload(node, "abc", null);
        await Assert.That(withoutAgent["agent_id"]).IsNull();
        await Assert.That(withoutAgent["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
    }
```

Append to `CodexHookCommandTests.cs` (inside the class):

```csharp
    [Test]
    public async Task Bridge_payload_adds_agent_id_beside_the_hooks_own_cwd() {
        var node = System.Text.Json.Nodes.JsonNode.Parse("""{"session_id":"abc","cwd":"/repo","tool_name":"shell","tool_input":{"command":"ls"}}""")!;
        var bridge = CodexHookCommand.BuildBridgePayload(node, "agent-1");
        await Assert.That(bridge["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(bridge["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(node["agent_id"]).IsNull().Because("the hook's own node is not mutated");
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/HookAgentIdTests/*"` (and the two appended tests by name). Expected: build errors.

- [ ] **Step 3: Implement**

`HookAgentId.cs`:

```csharp
namespace Capacitor.Cli.Commands;

/// The hosted agent id every daemon-spawned agent exports; null outside one.
internal static class HookAgentId {
    public static string? FromEnvironment() {
        var id = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
```

`PermissionRequestCommand.cs` — in `HandleRenderedAgent`, keep `payload` for the server path unchanged and build the bridge copy on the bridge branch:

```csharp
        if (TryGetLoopbackDaemonUrl(out var daemonUrl)) {
            var bridgePayload = BuildBridgePayload(node, sessionId, HookAgentId.FromEnvironment());
            return await PostAsync(daemonUrl + "/claude/permission-request", bridgePayload, authenticatedBase: null, stdout);
        }
```

and add:

```csharp
    /// The server payload plus what the daemon's attribution ladder reads: agent_id when this
    /// process runs inside a hosted agent, and the hook's cwd. The server-bound payload never
    /// carries either.
    internal static JsonObject BuildBridgePayload(JsonNode node, string sessionId, string? agentId) {
        var payload = new JsonObject {
            ["session_id"]             = sessionId,
            ["tool_name"]              = node["tool_name"]?.GetValue<string>() ?? "Unknown",
            ["tool_input"]             = node["tool_input"]?.DeepClone(),
            ["permission_suggestions"] = node["permission_suggestions"]?.DeepClone(),
        };
        if (agentId is not null) payload["agent_id"] = agentId;
        if (node["cwd"] is JsonValue cwd && cwd.TryGetValue<string>(out var c)) payload["cwd"] = c;
        return payload;
    }
```

`CodexHookCommand.cs` — in `HandlePermissionRequestViaBridge`, post `BuildBridgePayload(node, HookAgentId.FromEnvironment()).ToJsonString()` instead of `node.ToJsonString()`, and add:

```csharp
    internal static JsonObject BuildBridgePayload(JsonNode node, string? agentId) {
        var payload = (JsonObject)node.DeepClone();
        if (agentId is not null) payload["agent_id"] = agentId;
        return payload;
    }
```

- [ ] **Step 4: Run the CLI suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`. Expected: green (the pre-existing nudge failures noted in memory are not a regression signal).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/HookAgentId.cs src/Capacitor.Cli/Commands/PermissionRequestCommand.cs src/Capacitor.Cli/Commands/Harness/CodexHookCommand.cs test/Capacitor.Cli.Tests.Unit/Commands
git commit -m "Stamp the hosted agent id on the hooks' bridge payloads"
```

---

### Task 13: `PermissionService` (app)

**Files:**
- Create: `src/Capacitor.App/Services/IPermissionService.cs`
- Create: `src/Capacitor.App/Services/PermissionService.cs`
- Create: `test/Capacitor.App.Tests.Unit/FakePermissionService.cs`
- Test: `test/Capacitor.App.Tests.Unit/PermissionServiceTests.cs`

**Interfaces:**
- Produces (`IPermissionService.cs`):

```csharp
public enum PermissionAnswer { Allow, AllowAlways, Deny }
public enum PermissionResolveKind { Applied, AlreadyDecided, TransportFailure }
public sealed record PermissionResolveOutcome(PermissionResolveKind Kind, string? Error);

public sealed class PendingPermission {
    internal PendingPermission(PermissionPendingDto dto);
    public PermissionPendingDto Dto { get; }
    public string RequestId { get; }   public string AgentId { get; }   public string Vendor { get; }
    public string ToolName { get; }    public string? ToolInputJson { get; }   public bool ToolInputOmitted { get; }
    public DateTimeOffset RequestedAt { get; }
}

public interface IPermissionService : IDisposable {
    IObservable<IChangeSet<PendingPermission, string>> Pending { get; }
    IObservable<int> PendingCount { get; }
    IObservable<IReadOnlySet<string>> AgentsWithPending { get; }
    Task<PermissionResolveOutcome> ResolveAsync(PendingPermission target, PermissionAnswer answer, CancellationToken ct);
}
```

- `PermissionService(IDaemonClientService service, ILocalControlOps ops, Func<CancellationToken, IAsyncEnumerable<PermissionStreamEvent>> subscribe, TimeProvider time, CancellationToken shutdownToken)`; capability `"permission/1"`.

- [ ] **Step 1: Write the fake and the failing tests**

`FakePermissionService.cs`:

```csharp
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Tests.Unit;

static class PermissionEntries {
    public static PendingPermission Entry(
            string requestId = "r1", string agentId = "a1", string vendor = "claude", string toolName = "Bash",
            string? toolInputJson = """{"command":"ls"}""", bool omitted = false, string requestedAt = "2026-08-28T10:00:00.0000000+00:00") {
        System.Text.Json.JsonElement? input = null;
        if (toolInputJson is not null) { using var d = System.Text.Json.JsonDocument.Parse(toolInputJson); input = d.RootElement.Clone(); }
        return new PendingPermission(new PermissionPendingDto(requestId, agentId, "s1", vendor, toolName, input, null, omitted, false, requestedAt));
    }
}

/// Scripted IPermissionService: a real SourceCache plus a per-call outcome queue, like
/// FakeConsentService. A conclusive outcome evicts its target before the caller resumes; a
/// transport failure keeps it.
sealed class FakePermissionService : IPermissionService {
    public readonly SourceCache<PendingPermission, string> Cache = new(p => p.RequestId);
    readonly Queue<TaskCompletionSource<PermissionResolveOutcome>> _outcomes = new();
    public readonly List<(string RequestId, PermissionAnswer Answer)> Resolved = [];

    public IObservable<IChangeSet<PendingPermission, string>> Pending => Cache.Connect();
    public IObservable<int> PendingCount => Cache.CountChanged;
    public IObservable<IReadOnlySet<string>> AgentsWithPending =>
        Cache.Connect().QueryWhenChanged(q => (IReadOnlySet<string>)q.Items.Select(p => p.AgentId).ToHashSet(StringComparer.Ordinal))
            .StartWith((IReadOnlySet<string>)new HashSet<string>());

    public void Add(PendingPermission entry) => Cache.AddOrUpdate(entry);
    public void Remove(string requestId) => Cache.Remove(requestId);

    public TaskCompletionSource<PermissionResolveOutcome> Arm() {
        var tcs = new TaskCompletionSource<PermissionResolveOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _outcomes.Enqueue(tcs);
        return tcs;
    }
    public void Queue(PermissionResolveKind kind, string? error = null) => Arm().SetResult(new PermissionResolveOutcome(kind, error));

    public async Task<PermissionResolveOutcome> ResolveAsync(PendingPermission target, PermissionAnswer answer, CancellationToken ct) {
        Resolved.Add((target.RequestId, answer));
        if (_outcomes.Count == 0) throw new InvalidOperationException("FakePermissionService: unscripted resolve call");
        var outcome = await _outcomes.Dequeue().Task;
        if (outcome.Kind != PermissionResolveKind.TransportFailure) Cache.Remove(target.RequestId);
        return outcome;
    }

    public void Dispose() => Cache.Dispose();
}
```

`PermissionServiceTests.cs` (the harness mirrors `ConsentHarness`; `FakeDaemonClientService`, `ScriptedLocalControlOps`, `WaitUntilAsync` exist):

```csharp
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class PermissionServiceTests {
    static PermissionPendingDto Dto(string id = "r1", string agent = "a1") =>
        new(id, agent, "s1", "claude", "Bash", null, null, false, false, "2026-08-28T10:00:00.0000000+00:00");

    sealed class FakePermissionStream {
        readonly Channel<PermissionStreamEvent?> _channel = Channel.CreateUnbounded<PermissionStreamEvent?>();
        int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);

        public async IAsyncEnumerable<PermissionStreamEvent> RunAsync([EnumeratorCancellation] CancellationToken ct) {
            Interlocked.Increment(ref _attempts);
            await foreach (var evt in _channel.Reader.ReadAllAsync(ct)) {
                if (evt is null) yield break;
                yield return evt;
            }
        }

        public void EmitSubscribed() => _channel.Writer.TryWrite(new PermissionStreamEvent.Subscribed());
        public void EmitPending(PermissionPendingDto dto) => _channel.Writer.TryWrite(new PermissionStreamEvent.Pending(dto));
        public void EmitResolved(string id, string source) => _channel.Writer.TryWrite(new PermissionStreamEvent.Resolved(new PermissionResolvedDto(id, "allow", source)));
        public void EndAttempt() => _channel.Writer.TryWrite(null);
    }

    sealed class Harness : IDisposable {
        public readonly FakeDaemonClientService Daemon = new();
        public readonly ScriptedLocalControlOps Ops = new();
        public readonly FakePermissionStream Stream = new();
        public readonly PermissionService Service;
        public readonly IObservableCache<PendingPermission, string> View;
        public IReadOnlySet<string> Agents = new HashSet<string>();
        public int Count;

        public Harness() {
            Service = new PermissionService(Daemon, Ops, Stream.RunAsync, new FakeTimeProvider(), CancellationToken.None);
            View = Service.Pending.AsObservableCache();
            Service.AgentsWithPending.Subscribe(s => Agents = s);
            Service.PendingCount.Subscribe(c => Count = c);
        }

        public void Connect(params string[] caps) => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, caps));

        public async Task StartAsync() {
            Connect("consent/1", "permission/1");
            await WaitUntilAsync(() => Stream.Attempts == 1, what: "the subscribe attempt");
            Stream.EmitSubscribed();
        }

        public async Task<PendingPermission> EmitAsync(PermissionPendingDto dto) {
            Stream.EmitPending(dto);
            await WaitUntilAsync(() => View.Lookup(dto.RequestId).HasValue, $"pending {dto.RequestId} cached");
            return View.Lookup(dto.RequestId).Value;
        }

        public void Dispose() { Service.Dispose(); View.Dispose(); }
    }

    [Test]
    public async Task Subscribes_only_with_the_permission_capability_and_clears_on_a_down_level_daemon() {
        using var h = new Harness();
        h.Connect("consent/1");
        await Task.Delay(50);
        await Assert.That(h.Stream.Attempts).IsEqualTo(0);

        await h.StartAsync();
        await h.EmitAsync(Dto());
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Connect("consent/1");
        await WaitUntilAsync(() => h.View.Count == 0, what: "cleared on a down-level daemon");
    }

    [Test]
    public async Task Resolved_push_from_the_server_clears_entry_agent_set_and_count_together() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1", "a1"));
        await WaitUntilAsync(() => h.Agents.Contains("a1") && h.Count == 1, what: "derivations lit");

        h.Stream.EmitResolved("r1", "server");
        await WaitUntilAsync(() => h.View.Count == 0 && !h.Agents.Contains("a1") && h.Count == 0, what: "every derivation cleared");
    }

    [Test]
    public async Task A_replayed_ghost_of_a_resolved_request_is_dropped() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1"));
        h.Stream.EmitResolved("r1", "app");
        await WaitUntilAsync(() => h.View.Count == 0, what: "removed");
        h.Stream.EmitPending(Dto("r1"));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_outcomes_and_the_always_allow_payload() {
        using var h = new Harness();
        await h.StartAsync();
        var entry = await h.EmitAsync(Dto("r1"));

        h.Ops.QueuePermissionResolve(true);
        var applied = await h.Service.ResolveAsync(entry, PermissionAnswer.AllowAlways, CancellationToken.None);
        await Assert.That(applied.Kind).IsEqualTo(PermissionResolveKind.Applied);
        await Assert.That(h.Ops.PermissionResolvePayloads[0].Decision).IsEqualTo("allow");
        await Assert.That(h.Ops.PermissionResolvePayloads[0].ApplyPermissions!.Value.GetRawText()).IsEqualTo("""[{"type":"toolAlwaysAllow","tool":"Bash"}]""");
        await Assert.That(h.View.Count).IsEqualTo(0);

        var second = await h.EmitAsync(Dto("r2"));
        h.Ops.QueuePermissionResolve(false, "no pending permission request with that id");
        var already = await h.Service.ResolveAsync(second, PermissionAnswer.Deny, CancellationToken.None);
        await Assert.That(already.Kind).IsEqualTo(PermissionResolveKind.AlreadyDecided);
        await Assert.That(h.View.Count).IsEqualTo(0);

        var third = await h.EmitAsync(Dto("r3"));
        h.Ops.QueuePermissionResolveFailure("daemon_unreachable");
        var failed = await h.Service.ResolveAsync(third, PermissionAnswer.Allow, CancellationToken.None);
        await Assert.That(failed.Kind).IsEqualTo(PermissionResolveKind.TransportFailure);
        await Assert.That(failed.Error).IsEqualTo("daemon_unreachable");
        await Assert.That(h.View.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Subscribed_clears_at_the_boundary_and_disconnect_retains() {
        using var h = new Harness();
        await h.StartAsync();
        await h.EmitAsync(Dto("r1"));
        h.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Unreachable, "daemon_unreachable", null));
        await Task.Delay(50);
        await Assert.That(h.View.Count).IsEqualTo(1);

        h.Connect("permission/1");
        await WaitUntilAsync(() => h.Stream.Attempts == 2, what: "resubscribe");
        await Assert.That(h.View.Count).IsEqualTo(1);
        h.Stream.EmitSubscribed();
        await WaitUntilAsync(() => h.View.Count == 0, what: "cleared at Subscribed");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PermissionServiceTests/*"`. Expected: build errors.

- [ ] **Step 3: Implement**

`IPermissionService.cs`:

```csharp
using System.Globalization;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

public enum PermissionAnswer { Allow, AllowAlways, Deny }

/// Only TransportFailure leaves the request pending; the other two are conclusive.
public enum PermissionResolveKind { Applied, AlreadyDecided, TransportFailure }

public sealed record PermissionResolveOutcome(PermissionResolveKind Kind, string? Error);

public sealed class PendingPermission {
    internal PendingPermission(PermissionPendingDto dto) {
        Dto = dto;
        RequestedAt = DateTimeOffset.TryParse(dto.RequestedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t : DateTimeOffset.MinValue;
    }

    public PermissionPendingDto Dto { get; }
    public string RequestId => Dto.RequestId;
    public string AgentId => Dto.AgentId;
    public string Vendor => Dto.Vendor;
    public string ToolName => Dto.ToolName;
    public string? ToolInputJson => Dto.ToolInput?.GetRawText();
    public bool ToolInputOmitted => Dto.ToolInputOmitted;
    public DateTimeOffset RequestedAt { get; }
}

public interface IPermissionService : IDisposable {
    /// Mutated on background continuations — consumers ObserveOn(RxSchedulers.MainThreadScheduler).
    IObservable<IChangeSet<PendingPermission, string>> Pending { get; }
    /// Replays the current count on subscribe (DynamicData's CountChanged).
    IObservable<int> PendingCount { get; }
    /// The distinct agent ids in the cache; replays the current set on subscribe.
    IObservable<IReadOnlySet<string>> AgentsWithPending { get; }
    Task<PermissionResolveOutcome> ResolveAsync(PendingPermission target, PermissionAnswer answer, CancellationToken ct);
}
```

`PermissionService.cs`:

```csharp
using System.Reactive.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;

namespace Capacitor.App.Services;

/// Sole owner of the pending-permission cache. One lock guards the tombstone set and every
/// cache mutation: the tombstone test + upsert, the tombstone add + evict (on an ack and on a
/// Resolved push), the Connected-without-capability clear, the Subscribed clear and the disposed
/// flag. The stream loop, the status subscription and ResolveAsync run on different
/// continuations, and this lock is what makes the ordering hold. Tombstones live for the
/// service lifetime: request ids are never reused, so one can never suppress a future request.
public sealed class PermissionService : IPermissionService {
    const string Capability = "permission/1";
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    readonly SourceCache<PendingPermission, string> _cache = new(p => p.RequestId);
    readonly HashSet<string> _tombstones = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    readonly ILocalControlOps _ops;
    readonly Func<CancellationToken, IAsyncEnumerable<PermissionStreamEvent>> _subscribe;
    readonly TimeProvider _time;
    readonly CancellationToken _shutdownToken;
    readonly IDisposable _statusSub;
    CancellationTokenSource? _loopCts;
    bool _disposed;

    public PermissionService(
            IDaemonClientService service, ILocalControlOps ops,
            Func<CancellationToken, IAsyncEnumerable<PermissionStreamEvent>> subscribe,
            TimeProvider time, CancellationToken shutdownToken) {
        _ops = ops; _subscribe = subscribe; _time = time; _shutdownToken = shutdownToken;
        _statusSub = service.Status.Subscribe(OnStatus);
    }

    public IObservable<IChangeSet<PendingPermission, string>> Pending => _cache.Connect();
    public IObservable<int> PendingCount => _cache.CountChanged;
    public IObservable<IReadOnlySet<string>> AgentsWithPending =>
        _cache.Connect()
            .QueryWhenChanged(q => (IReadOnlySet<string>)q.Items.Select(p => p.AgentId).ToHashSet(StringComparer.Ordinal))
            .StartWith((IReadOnlySet<string>)_cache.Items.Select(p => p.AgentId).ToHashSet(StringComparer.Ordinal));

    public async Task<PermissionResolveOutcome> ResolveAsync(PendingPermission target, PermissionAnswer answer, CancellationToken ct) {
        var decision = answer == PermissionAnswer.Deny ? "deny" : "allow";
        var apply = answer == PermissionAnswer.AllowAlways ? ClaudePermissions.AlwaysAllow(target.ToolName) : (System.Text.Json.JsonElement?)null;
        var dto = new PermissionResolveDto(target.RequestId, decision, apply, null);

        PermissionAckDto ack;
        try {
            ack = await _ops.ResolvePermissionAsync(dto, ct).ConfigureAwait(false);
        } catch (LocalControlOpsException ex) {
            return new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, ex.Reason);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: permission resolve failed unexpectedly: {ex.Message}");
            return new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, ex.Message);
        }

        Conclude(target.RequestId);
        return new PermissionResolveOutcome(ack.Ok ? PermissionResolveKind.Applied : PermissionResolveKind.AlreadyDecided, ack.Error);
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _statusSub.Dispose();
        StopLoop();
        _cache.Dispose();
    }

    void OnStatus(AttachStatus status) {
        if (status is { State: AttachState.Connected, Capabilities: not null } && status.Capabilities.Contains(Capability)) {
            StartLoop();
            return;
        }
        StopLoop();
        // A Connected daemon without the capability is a different incarnation; disconnected retains.
        if (status.State == AttachState.Connected) lock (_lock) { if (!_disposed) _cache.Clear(); }
    }

    void StartLoop() {
        CancellationTokenSource cts;
        lock (_lock) {
            if (_disposed || _loopCts is not null) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            _loopCts = cts;
        }
        _ = Task.Run(() => RunLoopAsync(cts));
    }

    void StopLoop() {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _loopCts; _loopCts = null; }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    async Task RunLoopAsync(CancellationTokenSource cts) {
        var ct = cts.Token;
        try {
            while (!ct.IsCancellationRequested) {
                try {
                    await foreach (var evt in _subscribe(ct).WithCancellation(ct).ConfigureAwait(false)) {
                        ct.ThrowIfCancellationRequested();
                        switch (evt) {
                            case PermissionStreamEvent.Subscribed: lock (_lock) { if (!_disposed) _cache.Clear(); } break;
                            case PermissionStreamEvent.Pending p:  Upsert(p.Request); break;
                            case PermissionStreamEvent.Resolved r: Conclude(r.Settlement.RequestId); break;
                        }
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"kcap: permission subscription attempt failed: {ex.Message}");
                }
                try { await Task.Delay(RetryDelay, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        } finally {
            cts.Dispose();
        }
    }

    void Upsert(PermissionPendingDto dto) {
        lock (_lock) {
            if (_disposed || _tombstones.Contains(dto.RequestId)) return;
            _cache.AddOrUpdate(new PendingPermission(dto));
        }
    }

    void Conclude(string requestId) {
        lock (_lock) {
            if (_disposed) return;
            _tombstones.Add(requestId);
            _cache.Remove(requestId);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: the Step 2 command. Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/IPermissionService.cs src/Capacitor.App/Services/PermissionService.cs test/Capacitor.App.Tests.Unit/FakePermissionService.cs test/Capacitor.App.Tests.Unit/PermissionServiceTests.cs
git commit -m "Add the app's permission service over the local control socket"
```

---

### Task 14: The Chat tab card

**Files:**
- Create: `src/Capacitor.App/ViewModels/PermissionCardViewModel.cs`
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs` (ctor gains `IPermissionService permissions`; `PendingPermissions`; `Root` observable)
- Modify: `src/Capacitor.App/Views/ChatTabView.axaml` (the NEEDS YOU row)
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs` (ctor gains `IPermissionService permissions`, passed to the chat)
- Modify (construction sites): `test/Capacitor.App.Tests.Unit/ChatTabViewModelTests.cs`, `ChatTabViewSmokeTests.cs`, `ChatComposerTests.cs` (`new ChatTabViewModel(…, Opener, Time, new FakePermissionService())`); `MainWindowSmokeTests.cs` (2), `MainWindowViewModelTests.cs`, `WorkspaceNavigationTests.cs`, `WorkspaceViewSmokeTests.cs` (`new WorkspaceViewModel(…, opener, new FakePermissionService())`)
- Test: `test/Capacitor.App.Tests.Unit/PermissionCardViewModelTests.cs`; append to `ChatTabViewModelTests.cs` and `ChatTabViewSmokeTests.cs`

**Interfaces:**
- Produces: `PermissionCardViewModel(PendingPermission entry, IPermissionService permissions, IObservable<string?> root)` with `string RequestId`, `string ToolName`, `string Detail` (OAPH), `bool ShowsAllowAlways`, `bool IsBusy`, `string? ErrorText`, `ReactiveCommand<Unit, Unit> AllowCommand / AllowAlwaysCommand / DenyCommand`, `IDisposable`; `ChatTabViewModel.PendingPermissions : IAvaloniaReadOnlyList<PermissionCardViewModel>`; `ChatTabViewModel.Root : IObservable<string?>` (replay-1); `ChatTabViewModel.HasPendingPermissions : bool`.

- [ ] **Step 1: Write the failing tests**

`PermissionCardViewModelTests.cs`:

```csharp
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

public class PermissionCardViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Detail_is_relative_to_the_root_and_re_renders_when_the_root_arrives_late() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var root = new BehaviorSubject<string?>(null);
            using var svc = new FakePermissionService();
            using var card = new PermissionCardViewModel(
                PermissionEntries.Entry(toolName: "Read", toolInputJson: """{"file_path":"/repo/x/src/a.cs"}"""), svc, root);
            await Assert.That(card.Detail).IsEqualTo("/repo/x/src/a.cs");
            root.OnNext("/repo/x");
            await Assert.That(card.Detail).IsEqualTo("src/a.cs");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Omitted_input_and_empty_tool_name_have_their_own_text() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var svc = new FakePermissionService();
            using var card = new PermissionCardViewModel(PermissionEntries.Entry(toolName: "", toolInputJson: null, omitted: true), svc, new BehaviorSubject<string?>(null));
            await Assert.That(card.ToolName).IsEqualTo("Tool call");
            await Assert.That(card.Detail).IsEqualTo("Input too large to show");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Allow_always_shows_for_claude_only() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var svc = new FakePermissionService();
            using var claude = new PermissionCardViewModel(PermissionEntries.Entry(vendor: "claude"), svc, new BehaviorSubject<string?>(null));
            using var codex  = new PermissionCardViewModel(PermissionEntries.Entry(vendor: "codex"), svc, new BehaviorSubject<string?>(null));
            await Assert.That(claude.ShowsAllowAlways).IsTrue();
            await Assert.That(codex.ShowsAllowAlways).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Commands_resolve_with_the_answer_and_a_transport_failure_re_enables_with_an_error_line() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var svc = new FakePermissionService();
            var entry = PermissionEntries.Entry();
            svc.Add(entry);
            using var card = new PermissionCardViewModel(entry, svc, new BehaviorSubject<string?>(null));

            var gate = svc.Arm();
            var run = card.AllowAlwaysCommand.Execute().ToTask();
            await WaitUntilAsync(() => card.IsBusy, what: "busy while in flight");
            gate.SetResult(new PermissionResolveOutcome(PermissionResolveKind.TransportFailure, "daemon_unreachable"));
            await run;
            await Assert.That(card.IsBusy).IsFalse();
            await Assert.That(card.ErrorText).IsEqualTo("Daemon unreachable — try again");
            await Assert.That(svc.Resolved[0].Answer).IsEqualTo(PermissionAnswer.AllowAlways);

            svc.Queue(PermissionResolveKind.Applied);
            await card.DenyCommand.Execute().ToTask();
            await Assert.That(svc.Resolved[1].Answer).IsEqualTo(PermissionAnswer.Deny);
            await Assert.That(svc.Cache.Count).IsEqualTo(0);
        });
    }
}
```

Append to `ChatTabViewModelTests.cs` (the `Harness` there must now construct the chat with a `FakePermissionService Permissions { get; } = new();` — add the property and pass it):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cards_are_filtered_to_the_agent_ordered_by_request_time_and_removed_on_resolve() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r2", "a1", requestedAt: "2026-08-28T10:00:02.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", requestedAt: "2026-08-28T10:00:01.0000000+00:00"));
            h.Permissions.Add(PermissionEntries.Entry("rX", "other", requestedAt: "2026-08-28T10:00:00.0000000+00:00"));
            await WaitUntilAsync(() => h.Chat.PendingPermissions.Count == 2, what: "two cards");
            await Assert.That(h.Chat.PendingPermissions.Select(c => c.RequestId).ToArray()).IsEquivalentTo(new[] { "r1", "r2" }, CollectionOrdering.Matching);
            await Assert.That(h.Chat.HasPendingPermissions).IsTrue();

            h.Permissions.Remove("r1");
            await WaitUntilAsync(() => h.Chat.PendingPermissions.Count == 1, what: "one card left");
            await Assert.That(h.Chat.PendingPermissions[0].RequestId).IsEqualTo("r2");
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_permission_replayed_before_the_agent_dto_ends_up_with_a_relative_detail() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            h.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Read", toolInputJson: """{"file_path":"/repo/x/src/a.cs"}"""));
            await WaitUntilAsync(() => h.Chat.PendingPermissions.Count == 1, what: "the card");
            await Assert.That(h.Chat.PendingPermissions[0].Detail).IsEqualTo("/repo/x/src/a.cs");
            await h.PushAsync(Dto(transcriptPath: null));
            await WaitUntilAsync(() => h.Chat.PendingPermissions[0].Detail == "src/a.cs", what: "relative once the root lands");
            await h.TeardownAsync();
        });
    }
```

Append to `ChatTabViewSmokeTests.cs` (its `Host` gains `FakePermissionService Permissions { get; } = new();` passed to the chat):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Card_renders_with_its_buttons_and_the_row_collapses_when_empty() {
        await RunOnUiAsync(async () => {
            var host = new Host();
            var row = host.View.FindControl<Border>("PermissionRow")!;
            await Assert.That(row.IsVisible).IsFalse();

            host.Permissions.Add(PermissionEntries.Entry("r1", "a1", toolName: "Bash"));
            await WaitUntilAsync(() => host.Chat.PendingPermissions.Count == 1, what: "the card");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(row.IsVisible).IsTrue();
            var buttons = row.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToArray();
            await Assert.That(buttons).IsEquivalentTo(new[] { "Deny", "Allow always", "Allow" });

            host.Permissions.Remove("r1");
            await WaitUntilAsync(() => host.Chat.PendingPermissions.Count == 0, what: "cleared");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(row.IsVisible).IsFalse();
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `… --treenode-filter "/*/*/PermissionCardViewModelTests/*"`. Expected: build errors.

- [ ] **Step 3: Implement**

`PermissionCardViewModel.cs`:

```csharp
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using ReactiveUI;

namespace Capacitor.App.ViewModels;

/// One NEEDS YOU card. Detail follows the chat tab's root, which the agent stream delivers
/// independently of the permission replay, so a card built first re-renders relative later.
public sealed class PermissionCardViewModel : ReactiveObject, IDisposable {
    readonly PendingPermission _entry;
    readonly IPermissionService _permissions;
    readonly CompositeDisposable _disposables = new();
    readonly ObservableAsPropertyHelper<string> _detail;

    public string RequestId => _entry.RequestId;
    public string ToolName { get; }
    public string Detail => _detail.Value;
    public bool ShowsAllowAlways { get; }

    bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    string? _errorText;
    public string? ErrorText { get => _errorText; private set => this.RaiseAndSetIfChanged(ref _errorText, value); }

    public ReactiveCommand<Unit, Unit> AllowCommand { get; }
    public ReactiveCommand<Unit, Unit> AllowAlwaysCommand { get; }
    public ReactiveCommand<Unit, Unit> DenyCommand { get; }

    public PermissionCardViewModel(PendingPermission entry, IPermissionService permissions, IObservable<string?> root) {
        _entry = entry;
        _permissions = permissions;
        ToolName = entry.ToolName.Length == 0 ? "Tool call" : entry.ToolName;
        ShowsAllowAlways = entry.Vendor == "claude";

        _detail = root
            .Select(r => entry.ToolInputOmitted ? "Input too large to show" : ToolDetail.From(entry.ToolInputJson, r))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .ToProperty(this, x => x.Detail, entry.ToolInputOmitted ? "Input too large to show" : ToolDetail.From(entry.ToolInputJson, null))
            .DisposeWith(_disposables);

        var idle = this.WhenAnyValue(x => x.IsBusy).Select(b => !b);
        AllowCommand       = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.Allow), idle);
        AllowAlwaysCommand = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.AllowAlways), idle);
        DenyCommand        = ReactiveCommand.CreateFromTask(() => AnswerAsync(PermissionAnswer.Deny), idle);
        _disposables.Add(AllowCommand);
        _disposables.Add(AllowAlwaysCommand);
        _disposables.Add(DenyCommand);
    }

    async Task AnswerAsync(PermissionAnswer answer) {
        IsBusy = true;
        ErrorText = null;
        try {
            var outcome = await _permissions.ResolveAsync(_entry, answer, CancellationToken.None);
            if (outcome.Kind == PermissionResolveKind.TransportFailure)
                ErrorText = outcome.Error == "daemon_unreachable" ? "Daemon unreachable — try again" : $"Could not answer ({outcome.Error}) — try again";
        } finally {
            IsBusy = false;
        }
    }

    public void Dispose() => _disposables.Dispose();
}
```

`ChatTabViewModel.cs` — add the constructor parameter `IPermissionService permissions` (after `TimeProvider time`), a `BehaviorSubject<string?> _rootSubject = new(null)` set in `OnDto` (`_rootSubject.OnNext(dto.RepoPath)` beside `_root = dto.RepoPath`), and:

```csharp
    readonly AvaloniaList<PermissionCardViewModel> _pendingPermissions = new();
    public IAvaloniaReadOnlyList<PermissionCardViewModel> PendingPermissions => _pendingPermissions;
    public IObservable<string?> Root => _rootSubject;

    readonly ObservableAsPropertyHelper<bool> _hasPendingPermissions;
    public bool HasPendingPermissions => _hasPendingPermissions.Value;
```

with, in the constructor:

```csharp
        permissions.Pending
            .Filter(p => p.AgentId == agentId)
            .Transform(p => new PermissionCardViewModel(p, permissions, _rootSubject))
            .DisposeMany()
            .Sort(Comparer<PermissionCardViewModel>.Create((a, b) => {
                var byTime = string.CompareOrdinal(a.RequestedAtKey, b.RequestedAtKey);
                return byTime != 0 ? byTime : string.CompareOrdinal(a.RequestId, b.RequestId);
            }))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Bind(_pendingPermissions)
            .Subscribe()
            .DisposeWith(_disposables);

        _hasPendingPermissions = Observable.FromEventPattern(_pendingPermissions, nameof(_pendingPermissions.CollectionChanged))
            .Select(_ => _pendingPermissions.Count > 0)
            .ToProperty(this, x => x.HasPendingPermissions, initialValue: false)
            .DisposeWith(_disposables);
```

`RequestedAtKey` is `internal string RequestedAtKey => _entry.RequestedAt.ToString("O")` on the card (sortable ISO text). `TeardownAsync` disposes `_rootSubject` after `_disposables`.

`ChatTabView.axaml` — change the root grid to `RowDefinitions="*,Auto,Auto"`, move the composer `Border` to `Grid.Row="2"`, and insert between them:

```xml
        <Border x:Name="PermissionRow" Grid.Row="1" Margin="22,0,22,4" IsVisible="{Binding HasPendingPermissions}">
            <StackPanel Spacing="6">
                <TextBlock Text="NEEDS YOU" FontSize="10" FontWeight="SemiBold" Foreground="{StaticResource KcapMutedBrush}" />
                <ItemsControl ItemsSource="{Binding PendingPermissions}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate x:DataType="vm:PermissionCardViewModel">
                            <Border Background="{StaticResource KcapSurfaceRaisedBrush}" BorderBrush="{StaticResource KcapAccentBrush}"
                                    BorderThickness="1" CornerRadius="10" Padding="13,10" Margin="0,0,0,6">
                                <StackPanel Spacing="6">
                                    <TextBlock Text="{Binding ToolName}" FontWeight="SemiBold" FontSize="13" Foreground="{StaticResource KcapTextBrush}" />
                                    <TextBlock Text="{Binding Detail}" FontSize="11.5" Foreground="{StaticResource KcapMutedBrush}" TextTrimming="CharacterEllipsis" />
                                    <TextBlock Text="{Binding ErrorText}" FontSize="11" Foreground="{StaticResource KcapDangerBrush}"
                                               IsVisible="{Binding ErrorText, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" />
                                    <StackPanel Orientation="Horizontal" Spacing="6" HorizontalAlignment="Right">
                                        <Button Content="Deny" Command="{Binding DenyCommand}" Background="Transparent" BorderBrush="Transparent"
                                                Foreground="{StaticResource KcapDangerBrush}" Padding="10,4" FontSize="12" CornerRadius="7" />
                                        <Button Content="Allow always" Command="{Binding AllowAlwaysCommand}" IsVisible="{Binding ShowsAllowAlways}"
                                                Background="Transparent" BorderBrush="Transparent" Foreground="{StaticResource KcapMutedBrush}" Padding="10,4" FontSize="12" CornerRadius="7" />
                                        <Button Content="Allow" Command="{Binding AllowCommand}" Padding="12,4" FontSize="12" FontWeight="SemiBold" CornerRadius="7"
                                                Background="{StaticResource KcapAccentBrush}" Foreground="#07120E" />
                                    </StackPanel>
                                </StackPanel>
                            </Border>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </StackPanel>
        </Border>
```

`WorkspaceViewModel.cs` — add `IPermissionService permissions` after `IUrlOpener opener` and pass it as the chat's last argument. Update every construction site listed under **Files**.

- [ ] **Step 4: Run the App suite**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`. Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App test/Capacitor.App.Tests.Unit
git commit -m "Show pending permission cards above the Chat composer"
```

---

### Task 15: Rail pips from the pending set

**Files:**
- Modify: `src/Capacitor.App/ViewModels/RailSessionViewModel.cs`, `RailWorktreeViewModel.cs`, `RailRepoViewModel.cs`, `SessionRailViewModel.cs`
- Modify (construction sites): `test/Capacitor.App.Tests.Unit/RailSessionViewModelTests.cs` (8 sites — add a `NoPending` helper), `RailWorktreeViewModelTests.cs` (`Build` helper)
- Test: append to `RailSessionViewModelTests.cs` and `RailWorktreeViewModelTests.cs`

**Interfaces:**
- `RailSessionViewModel(AgentStatusDto dto, IObservable<string?> selectedAgentId, IObservable<IReadOnlySet<string>> agentsWithPending, Action<string> open)`; `NeedsYou` becomes an OAPH.
- `RailWorktreeViewModel(string path, string repoRoot, bool showHeader, IObservableCache<AgentStatusDto,string> sessionsCache, RailCollapseState collapse, IObservable<string?> selectedAgentId, IObservable<IReadOnlySet<string>> agentsWithPending, Action<string> open)`.
- `RailRepoViewModel(IGroup<…> group, RailCollapseState collapse, IObservable<string?> selectedAgentId, IObservable<IReadOnlySet<string>> agentsWithPending, Action<string> open)`.
- `SessionRailViewModel(IDaemonClientService daemon, Action<string> openSession, Func<string,string>? resolveRepoRoot = null, IObservable<IReadOnlySet<string>>? agentsWithPending = null)` — null defaults to an empty set, so existing tests compile.

- [ ] **Step 1: Write the failing tests**

Append to `RailSessionViewModelTests.cs` (and change its existing constructions to pass `NoPending` — `static readonly IObservable<IReadOnlySet<string>> NoPending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());` — as the third argument):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Needs_you_follows_the_pending_set_and_the_status() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            using var row = new RailSessionViewModel(Dto(status: "Running"), new BehaviorSubject<string?>(null), pending, _ => { });
            await Assert.That(row.NeedsYou).IsFalse();
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(row.NeedsYou).IsTrue();
            pending.OnNext(new HashSet<string>());
            await Assert.That(row.NeedsYou).IsFalse();

            using var failed = new RailSessionViewModel(Dto(status: "Failed"), new BehaviorSubject<string?>(null), pending, _ => { });
            await Assert.That(failed.NeedsYou).IsTrue();
        });
    }
```

Append to `RailWorktreeViewModelTests.cs` (its `Build` helper gains `IObservable<IReadOnlySet<string>>? pending = null` and passes `pending ?? new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>())` before `open`):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Collapsed_worktree_shows_a_permission_only_alert() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var cache = new SourceCache<AgentStatusDto, string>(a => a.Id);
            var pending = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
            var collapse = new RailCollapseState();
            collapse.Set("/repo/.claude/worktrees/wt-a", collapsed: true);
            using var wt = Build(cache, collapse, pending: pending);
            cache.AddOrUpdate(Dto("a1"));
            await Assert.That(wt.NeedsYou).IsFalse();
            pending.OnNext(new HashSet<string> { "a1" });
            await Assert.That(wt.NeedsYou).IsTrue();
            pending.OnNext(new HashSet<string> { "somebody-else" });
            await Assert.That(wt.NeedsYou).IsFalse();
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `… --treenode-filter "/*/*/RailSessionViewModelTests/*"` and `… --treenode-filter "/*/*/RailWorktreeViewModelTests/*"`. Expected: build errors.

- [ ] **Step 3: Implement**

`RailSessionViewModel.cs` — replace `public bool NeedsYou { get; }` and its assignment with:

```csharp
    readonly ObservableAsPropertyHelper<bool> _needsYou;
    public bool NeedsYou => _needsYou.Value;
```

```csharp
        var byStatus = SessionStatusDots.NeedsAttention(dto.Status);
        _needsYou = agentsWithPending.Select(set => byStatus || set.Contains(dto.Id))
            .ToProperty(this, x => x.NeedsYou, initialValue: byStatus)
            .DisposeWith(_disposables);
```

`RailWorktreeViewModel.cs` — replace the `_needsYou` projection with:

```csharp
        _needsYou = sessionsCache.Connect().QueryWhenChanged()
            .CombineLatest(agentsWithPending, (q, set) =>
                q.Items.Any(d => SessionStatusDots.NeedsAttention(d.Status)) || q.Keys.Any(set.Contains))
            .ToProperty(this, x => x.NeedsYou, initialValue: false)
            .DisposeWith(_disposables);
```

and pass `agentsWithPending` into each `new RailSessionViewModel(dto, selectedAgentId, agentsWithPending, open)`.

`RailRepoViewModel.cs` — thread the parameter into `new RailWorktreeViewModel(…, selectedAgentId, agentsWithPending, open)`.

`SessionRailViewModel.cs` — add the optional parameter, `var pending = agentsWithPending ?? Observable.Return((IReadOnlySet<string>)new HashSet<string>());`, and pass `pending` into `new RailRepoViewModel(g, _collapse, selected, pending, openSession)`.

- [ ] **Step 4: Run the App suite**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`. Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels test/Capacitor.App.Tests.Unit
git commit -m "Light the rail pips from pending permission requests"
```

---

### Task 16: Tray Attention from pending permissions

**Files:**
- Modify: `src/Capacitor.App/ViewModels/TrayViewModel.cs` (optional trailing `IPermissionService? permissions = null`; `Build`/`HeaderText` gain `pendingPermissions`)
- Test: append to `test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs`

**Interfaces:**
- `TrayViewModel(…, Func<Task>? installShim = null, IPermissionService? permissions = null)`; `Build(…, int pendingConsent, string? lifecycleAttention, int pendingPermissions)`; header body `"{n} permission request(s) waiting"` precedes the consent text when both apply.

- [ ] **Step 1: Write the failing test** (append; the existing tests build the VM with four positional args and a `consent` fake — follow whichever helper they use for `service`/`pause`/`actions`/`consent`):

```csharp
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Pending_permissions_assert_attention_while_connected_with_their_own_header() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            using var permissions = new FakePermissionService();
            using var vm = new TrayViewModel(service, pause, actions, consent, permissions: permissions);

            service.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["permission/1"]));
            service.SnapshotsSubject.OnNext(FakeDaemonClientService.Snap(active: 1));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Running);

            permissions.Add(PermissionEntries.Entry("r1"));
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Attention);
            await Assert.That(vm.MenuModel.Header).IsEqualTo("daemon-a: 1 permission request waiting");

            permissions.Remove("r1");
            await Assert.That(vm.MenuModel.State).IsEqualTo(TrayState.Running);
        });
    }
```

(`FakePauseController`, `NewActions` and `MenuModel.Header` are what the neighbouring tray tests already use — match their names if they differ.)

- [ ] **Step 2: Run to verify it fails**

Run: `… --treenode-filter "/*/*/TrayViewModelTests/Pending_permissions*"`. Expected: build error — no `permissions` parameter.

- [ ] **Step 3: Implement**

In the constructor: `var permissionCount = permissions?.PendingCount ?? Observable.Return(0);` and extend the `CombineLatest` to seven sources, passing `permissionCount` through to `Build(…, pending, lifecycleMsg, permissionPending)`. In `Build`:

```csharp
        var pendingAttention = status.State == AttachState.Connected && (pendingConsent > 0 || pendingPermissions > 0)
            && baseState is TrayState.Idle or TrayState.Running;
```

and pass `pendingPermissions` to `HeaderText`, whose body becomes:

```csharp
        var body = pendingAttention
            ? PendingBody(pendingPermissions, pendingConsent)
            : state switch { … unchanged … };
```

```csharp
    static string PendingBody(int permissions, int consent) {
        var parts = new List<string>(2);
        if (permissions > 0) parts.Add($"{permissions} permission request{(permissions == 1 ? "" : "s")} waiting");
        if (consent > 0) parts.Add($"{consent} launch{(consent == 1 ? "" : "es")} awaiting approval");
        return string.Join(", ", parts);
    }
```

Update the seed-assertion message to name `IPermissionService.PendingCount` too.

- [ ] **Step 4: Run the tray suite**

Run: `… --treenode-filter "/*/*/TrayViewModelTests/*"`. Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/TrayViewModel.cs test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs
git commit -m "Raise the tray to Attention while a permission request waits"
```

---

### Task 17: Wire the app, document, verify end to end

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (construct `PermissionService` beside `ConsentService`; pass it to `BuildWorkspace`, the rail and the tray; dispose it in the same list as `_consent`)
- Modify: `docs/CHANGES.md` (new section after "## Session chat")
- Verify: full test run, AOT publish

**Interfaces:** consumes everything above; produces nothing new.

- [ ] **Step 1: Wire `App.axaml.cs`**

Beside `_consent`:

```csharp
    PermissionService? _permissions;
```

After the `ConsentService` construction:

```csharp
        var permissions = new PermissionService(
            service, ops, ct => PermissionSubscription.RunAsync(_daemonStore, service.DaemonName, ct),
            TimeProvider.System, _shutdown.Token);
        _permissions = permissions;
```

`BuildWorkspace`: `new(agentId, service, actions, attachFactory, () => new XtermTerminalSurface(80, 24, PtyDumpPath), TimeProvider.System, opener, permissions)`.
The rail: `new SessionRailViewModel(service, openSession: …, agentsWithPending: permissions.AgentsWithPending)` — `BuildAndShowMainWindow` takes `permissions` as an extra parameter to reach that line.
The tray: add `permissions: permissions` to the `new TrayViewModel(…)` call.
Disposal: add `_permissions` to the disposal list beside `_consent` (line ~200) and null it where `_consent` is nulled.

- [ ] **Step 2: Add the CHANGES.md section**

After the "## Session chat" section:

```markdown
## Permission prompts in the desktop app

**AI-2308** (spec: `docs/superpowers/specs/2026-08-28-ai2308-permission-prompts-daemon-bridge-design.md`)
surfaces a PTY-hosted Claude/Codex session's permission prompt as a card on the Chat tab, with the
rail pip and tray Attention derived from the same cache. The local control socket gains the
append-only frames `PermissionSubscribe = 20` / `PermissionResolve = 21` and `PermissionPending = 77` /
`PermissionResolved = 78` / `PermissionAck = 79`, advertised as `permission/1`. **The daemon's
`PermissionPromptBroker` is the one claim point**: the app's resolve, the server's push, an agent's
withdrawal, the no-UI deny and the shutdown claim all settle a request through `TrySettle`, and the
hook's answer, the ack, the log record and the `Resolved` push all derive from the claimed
settlement — so `Ok=true` is the decision the hook receives. The bridge registers the request
locally BEFORE the server leg dials; the leg feeds the server's decision into the same claim, and a
local win is relayed through the hub's own `RespondToPermission` so the web card clears. A settled
request's server invoke is kept off the wire by a predicate the invoke lambda reads synchronously
(`PermissionRequestAbandonedException`, deliberately not one of the exception types
`ConnectionRetry` retries). The bridge drains admitted handlers before closing its listener; the
tracked wrapper is scheduled with no cancellation token, because a delegate cancelled before it
starts never runs its `finally`. Every caller-controlled wire string is bounded (`PermissionWire`),
ids are canonicalized by GUID parse, and a request kept for a subscriber that then leaves has no
clock — it lives until the agent exits, the same stale card a TUI answer leaves.
```

- [ ] **Step 3: Full verification**

Run, in order:
1. `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
2. `dotnet test --solution Capacitor.slnx` — expected green (memory: 7 unit + 1 integration nudge tests fail on this machine on `main` and are not a regression signal).
3. `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — expected: no output.

- [ ] **Step 4: Commit**

```bash
git add src/Capacitor.App/App.axaml.cs docs/CHANGES.md
git commit -m "Wire the permission service into the desktop shell"
```

---

## Self-review against the spec

- §1 wire, bounds, canonical ids, capability → Tasks 1, 2, 7 (`PermissionWire`, `BuildPending`, `Canonical`). Worst-case frame through the codec → Task 2.
- §2.1 broker, gate invariant, withdrawn set → Task 6. `TrySettleIfNoSubscriber` → Tasks 6, 9.
- §2.2 split, abandonment type, `RespondOutcome`/`NotPending` → Task 8.
- §2.3 register-first, shutdown claim, log-before-write, response token → Task 9.
- §2.3.1 leg state machine (continuation wakes, predicate abandons, both OCE exits, late-decision log, `NotPending` log) → Task 9.
- §2.4 withdrawal before `UnpublishAgent`, register-after-withdraw → Tasks 6, 11.
- §2.5 log writer extraction → Task 5.
- §2.6 IPC handler acks → Task 7.
- §2.7 hook payloads bridge-branch only → Task 12.
- §2.8 composition seam, ladder exactly-one, raw-then-canonical agent id, path comparison → Tasks 9, 11.
- §2.9 admission gate, drain, no scheduling token → Task 10.
- §3.1 subscription, ops, `AlwaysAllow` → Tasks 3, 4.
- §3.2 service with one lock, gate on capability, retain on disconnect, tombstones → Task 13.
- §3.3 card, root observable, view row → Task 14.
- §3.4 session + worktree pips, tray → Tasks 15, 16.
- §3.5 older daemon → Task 13 (gate test).
- §4 edge cases → Tasks 9, 10, 13 tests; the TUI-answer and kept-request limitations need no code.
- §5 tests → distributed as above; the server-push-clears-everything case → Task 13.
- Decision 3's "every indicator clears from one push" → Task 13 test + Tasks 15/16 derivations.
- Names used consistently: `PermissionPromptBroker.TrySettle/TrySettleIfNoSubscriber/Register/WithdrawForAgent`, `PermissionSettlement`, `PermissionSettlements.*`, `AttributedAgent`, `PermissionAttribution`, `AttributeHandler`, `BuildPending`, `ServerLegsInFlightForTest`, `InFlightHandlersForTest`, `AdmittingForTest`, `BeforeHandlerRunsForTest`, `RespondOutcome/RespondOutcomeKind`, `PermissionRequestAbandonedException`, `IPermissionService.{Pending,PendingCount,AgentsWithPending,ResolveAsync}`, `PermissionAnswer`, `PermissionResolveKind`, `PendingPermission`, `PermissionCardViewModel`, `ChatTabViewModel.{PendingPermissions,HasPendingPermissions,Root}`.
