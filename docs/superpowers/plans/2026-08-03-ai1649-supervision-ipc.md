# Supervision IPC (AI-1649) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the daemon's local control socket a v1 supervision surface — `StatusSubscribe` → pushed full `DaemonStatus` snapshots (daemon block + live agent list) driven by a monotonic change generation — plus a `RequesterUserId` field on the agent record, per the approved spec `docs/superpowers/specs/2026-08-01-slice2-prework-control-ipc-design.md` §4 (PR 2, AI-1649).

**Architecture:** A new frame pair (`StatusSubscribe = 16` client→daemon, `DaemonStatus = 76` daemon→client) rides the existing length-prefixed codec. A `DaemonStatusNotifier` holds a monotonic generation counter + one shared rearm `TaskCompletionSource` under a single lock; the orchestrator and `ServerConnection` pulse it strictly *after* each state mutation via centralized helpers. A `DaemonStatusIpc` handler (long-lived connection, same EOF-watcher discipline as `ConsentSubscribe`) pushes a full snapshot immediately on subscribe, then a debounced re-push whenever the generation advances past the subscriber's per-connection cursor. Stop needs nothing new — the app reuses the shipped `StopV2` frame (spec §4.4; zero work in this plan).

**Tech Stack:** .NET 10 NativeAOT, System.Text.Json source generation (snake_case, no reflection), TUnit on Microsoft Testing Platform, real Unix-domain-socket integration tests.

## Global Constraints

- **Append-only wire values:** `StatusSubscribe = 16`, `DaemonStatus = 76`. No other `FrameType` value may move (spec §1.1, §7).
- **JSON:** snake_case via a new source-gen `JsonSerializerContext` in Core; **every field always emitted, absent values are JSON `null`** — do NOT set `DefaultIgnoreCondition` (spec §4.1); deserialization must ignore unmapped members (STJ default — never opt into `Disallow`) (spec §5).
- **Wire semantics pinned (spec §4.1):** `connection` ∈ `connected|connecting|reconnecting|disconnected` (lowercase); `agent.status` verbatim PascalCase internal string; `kind` uses the existing `KindText` spellings; agents ordered `created_at` asc, tie-broken by `id` (ordinal); `active_agents` computed from the materialized array with the `Status is "Starting" or "Running"` predicate.
- **Mutation first, `Pulse()` second — always** (spec §4.2). Pulse call sites live in centralized helpers only.
- **Capability list:** `LocalControlCapabilities.Current` becomes `["consent/1", "status/1"]` in the same PR that wires the `StatusSubscribe` handler — never before (invariant documented in that file).
- **AOT:** `dotnet build` does NOT surface IL3050/IL2026 — run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` after changes and expect no output.
- **Tests:** TUnit on MTP — run one class with `--treenode-filter "/*/*/ClassName/*"` (the bare `"*ClassName*"` glob silently matches nothing). Real-socket tests need: `if (OperatingSystem.IsWindows()) return;` guard, short daemon names (macOS `sockaddr_un` ~104-byte path limit), and `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]`.
- **No README change:** this PR adds no CLI command/flag/behavior — the surface is app-facing IPC only. State this in the PR description so the README-sync check is visibly considered.
- **Comments:** no Linear issue numbers in code comments; keep comments to constraints code can't show.

**Branch:** `alexeyzimarev/ai-1649-supervision-ipc-daemon-state-live-agent-list-stop-agent` off current `main`.

```bash
git checkout -b alexeyzimarev/ai-1649-supervision-ipc-daemon-state-live-agent-list-stop-agent
```

---

### Task 1: Wire contract — frame values, codec arms, LocalFrame helper

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` (Encode/Decode switches)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs` (StatusJson helper)
- Test: `test/Capacitor.Cli.Tests.Unit/FrameCodecStatusTests.cs` (create)

**Interfaces:**
- Consumes: existing `FrameCodec.WriteAsync/ReadAsync`, `LocalFrame`.
- Produces: `FrameType.StatusSubscribe = 16` (no payload), `FrameType.DaemonStatus = 76` (Text = JSON), `LocalFrame.StatusJson(FrameType type, string json)`.

- [ ] **Step 1: Write the failing round-trip tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/FrameCodecStatusTests.cs
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Round-trips for the supervision frame pair. StatusSubscribe carries no payload
/// (Detach/List shape); DaemonStatus carries UTF-8 JSON in Text (consent-frame shape).
/// </summary>
public class FrameCodecStatusTests {
    [Test]
    public async Task StatusSubscribe_round_trips_with_an_empty_payload() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, new LocalFrame(FrameType.StatusSubscribe), CancellationToken.None);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(f!.Type).IsEqualTo(FrameType.StatusSubscribe);
        await Assert.That(f.Text).IsEmpty();
        await Assert.That(f.Bytes).IsEmpty();
    }

    [Test]
    public async Task DaemonStatus_round_trips_its_json_text_payload() {
        using var ms = new MemoryStream();
        await FrameCodec.WriteAsync(
            ms, LocalFrame.StatusJson(FrameType.DaemonStatus, """{"daemon":{"name":"x"}}"""), CancellationToken.None);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, CancellationToken.None);
        await Assert.That(f!.Type).IsEqualTo(FrameType.DaemonStatus);
        await Assert.That(f.Text).IsEqualTo("""{"daemon":{"name":"x"}}""");
    }

    [Test]
    public async Task Frame_values_are_pinned_16_and_76() {
        // Append-only wire contract: these bytes are claimed by the spec and must never move.
        await Assert.That((byte)FrameType.StatusSubscribe).IsEqualTo((byte)16);
        await Assert.That((byte)FrameType.DaemonStatus).IsEqualTo((byte)76);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/FrameCodecStatusTests/*"
```

Expected: compile error — `StatusSubscribe` is not a member of `FrameType`.

- [ ] **Step 3: Implement the wire contract**

In `FrameType.cs`, append to the client→daemon block (after `Hello = 15`):

```csharp
    StatusSubscribe = 16, // long-lived: push DaemonStatus snapshots (immediate + on change)
```

and to the daemon→client block (after `HelloReply = 75`):

```csharp
    DaemonStatus = 76, // Text = DaemonStatusDto JSON: daemon block + full agent list snapshot
```

In `FrameCodec.cs` `Encode`, change the empty-payload arm and the Text arm:

```csharp
        FrameType.Detach or FrameType.List or FrameType.StatusSubscribe => [],
```

and add `or FrameType.DaemonStatus` to the long Text arm (after `or FrameType.ConsentAck`):

```csharp
            or FrameType.ConsentAck or FrameType.DaemonStatus => Encoding.UTF8.GetBytes(f.Text),
```

Mirror both changes in `Decode` (`new(t)` for the empty arm, `new(t) { Text = ... }` for the Text arm).

In `LocalFrame.cs`, after the `HelloJson` helper:

```csharp
    /// Constructs a DaemonStatus frame, whose payload is UTF-8 JSON
    /// (snake_case via StatusIpcJsonContext) carried in Text — see StatusIpc.cs.
    public static LocalFrame StatusJson(FrameType type, string json) => new(type) { Text = json };
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/FrameCodecStatusTests/*"
```

Expected: 3 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/FrameType.cs src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs test/Capacitor.Cli.Tests.Unit/FrameCodecStatusTests.cs
git commit -m "Add StatusSubscribe/DaemonStatus frames to the local IPC wire contract"
```

---

### Task 2: Status DTOs + snake_case JSON context (Core) with exact-JSON contract tests

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/StatusIpcJsonTests.cs` (create)

**Interfaces:**
- Produces (all public, in `Capacitor.Cli.Core.LocalIpc`):
  - `sealed record DaemonStatusDto(DaemonInfoDto Daemon, List<AgentStatusDto> Agents)`
  - `sealed record DaemonInfoDto(string Name, string Version, string ServerUrl, string Connection, int MaxAgents, int ActiveAgents)`
  - `sealed record AgentStatusDto(string Id, string Kind, string Vendor, string? RepoPath, string Status, string? FlowRunId, string? FlowRole, string? Requester, DateTime CreatedAt, string? Model)`
  - `partial class StatusIpcJsonContext : JsonSerializerContext` (snake_case; nulls always written)
- Property declaration order matches the spec §4.1 example — source-gen serializes in declaration order and the exact-JSON tests pin it.

- [ ] **Step 1: Write the failing contract tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/StatusIpcJsonTests.cs
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Exact-JSON pins for the DaemonStatus payload (spec §4.1): snake_case, every field always
/// emitted (absent = null, never omitted), field order as declared, ISO-8601 UTC created_at.
/// Plus the §5 forward-compat pin: unknown members deserialize without error.
/// </summary>
public class StatusIpcJsonTests {
    [Test]
    public async Task DaemonStatus_serializes_exactly_with_nulls_present_and_pinned_field_order() {
        var dto = new DaemonStatusDto(
            new DaemonInfoDto("main", "0.12.3", "https://tenant.example.com", "connected", 5, 1),
            [
                new AgentStatusDto(
                    "agent-abc123", "review-flow", "codex", "/Users/x/dev/repo", "Live",
                    "flow_1", "reviewer", "github:12345",
                    new DateTime(2026, 8, 1, 12, 34, 56, 789, DateTimeKind.Utc), "gpt-5-codex"),
                new AgentStatusDto(
                    "agent-b", "agent", "claude", null, "Starting",
                    null, null, null,
                    new DateTime(2026, 8, 1, 12, 35, 0, DateTimeKind.Utc), null),
            ]);

        var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);

        await Assert.That(json).IsEqualTo(
            """{"daemon":{"name":"main","version":"0.12.3","server_url":"https://tenant.example.com","connection":"connected","max_agents":5,"active_agents":1},"agents":[{"id":"agent-abc123","kind":"review-flow","vendor":"codex","repo_path":"/Users/x/dev/repo","status":"Live","flow_run_id":"flow_1","flow_role":"reviewer","requester":"github:12345","created_at":"2026-08-01T12:34:56.789Z","model":"gpt-5-codex"},{"id":"agent-b","kind":"agent","vendor":"claude","repo_path":null,"status":"Starting","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T12:35:00Z","model":null}]}""");
    }

    [Test]
    public async Task Unknown_members_in_a_payload_deserialize_without_error() {
        // §5 forward compat: an older client must survive a future additive field.
        var json =
            """{"daemon":{"name":"m","version":"1","server_url":"u","connection":"connected","max_agents":5,"active_agents":0,"future_field":42},"agents":[{"id":"a","kind":"agent","vendor":"codex","repo_path":null,"status":"Live","flow_run_id":null,"flow_role":null,"requester":null,"created_at":"2026-08-01T00:00:00Z","model":null,"future_field":{"x":1}}],"future_top":true}""";

        var dto = JsonSerializer.Deserialize(json, StatusIpcJsonContext.Default.DaemonStatusDto);

        await Assert.That(dto!.Daemon.Name).IsEqualTo("m");
        await Assert.That(dto.Agents[0].Id).IsEqualTo("a");
        await Assert.That(dto.Agents[0].Status).IsEqualTo("Live");
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/StatusIpcJsonTests/*"
```

Expected: compile error — `DaemonStatusDto` does not exist.

- [ ] **Step 3: Implement the DTOs + context**

```csharp
// src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payload for the DaemonStatus frame. snake_case on the wire; shared verbatim by the
/// daemon, the CLI, and the desktop app. Every field is ALWAYS emitted — absent values are
/// JSON null, never omitted (one wire shape, exact-JSON testable), so this context must never
/// gain a DefaultIgnoreCondition. Deserialization ignores unmapped members (STJ default) —
/// additive fields must never break an older client.
public sealed record DaemonStatusDto(DaemonInfoDto Daemon, List<AgentStatusDto> Agents);

/// <summary>
/// <see cref="Connection"/> ∈ connected|connecting|reconnecting|disconnected (lowercase).
/// <see cref="ActiveAgents"/> is derived from the SAME materialized agents array it ships
/// with (Status is "Starting" or "Running"), so count and array can never disagree within
/// one payload.
/// </summary>
public sealed record DaemonInfoDto(
    string Name, string Version, string ServerUrl, string Connection, int MaxAgents, int ActiveAgents);

/// <summary>
/// <see cref="Status"/> is the daemon's internal status string VERBATIM (PascalCase, open
/// vocabulary — clients treat unknown values as opaque display text). <see cref="Kind"/>
/// uses the KindText wire spellings (agent/review/review-flow, unknown enum names pass
/// through) — one vocabulary across AgentList and this payload. <see cref="Requester"/> is
/// null when unknown (old servers, local spawns); rendering "unknown" is presentation.
/// </summary>
public sealed record AgentStatusDto(
    string Id, string Kind, string Vendor, string? RepoPath, string Status,
    string? FlowRunId, string? FlowRole, string? Requester, DateTime CreatedAt, string? Model);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DaemonStatusDto))]
public partial class StatusIpcJsonContext : JsonSerializerContext;
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/StatusIpcJsonTests/*"
```

Expected: 2 PASS. If the exact-JSON assertion fails on `created_at` formatting, fix the *test's* expected string to STJ's actual ISO-8601 output — the DTO shape and null emission are the contract, and STJ's UTC DateTime format is stable.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs test/Capacitor.Cli.Tests.Unit/StatusIpcJsonTests.cs
git commit -m "Add DaemonStatus DTOs with pinned snake_case wire shape"
```

---

### Task 3: DaemonStatusNotifier — monotonic generation, broadcast-safe

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/DaemonStatusNotifier.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/DaemonStatusNotifierTests.cs` (create)

**Interfaces:**
- Produces (`internal sealed class DaemonStatusNotifier`, namespace `Capacitor.Cli.Daemon.Services`):
  - `long Version { get; }` — monotonic change generation.
  - `void Pulse()` — increments Version and wakes every current waiter; never blocks on consumers.
  - `Task WaitBeyondAsync(long seen, CancellationToken ct)` — completed synchronously when `Version > seen`; otherwise completes on the next `Pulse()`. Broadcast: N waiters each hold their own cursor and can never consume each other's signal.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/DaemonStatusNotifierTests.cs
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// The generation counter behind DaemonStatus pushes (spec §4.2): version check and source
/// capture are atomic, Pulse is a broadcast, and a stale cursor returns synchronously —
/// a missed pulse can never strand a subscriber.
/// </summary>
public class DaemonStatusNotifierTests {
    [Test]
    public async Task Wait_returns_synchronously_when_version_is_already_beyond_seen() {
        var n = new DaemonStatusNotifier();
        n.Pulse();
        var t = n.WaitBeyondAsync(0, CancellationToken.None);
        await Assert.That(t.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task Pulse_wakes_a_waiter_captured_at_the_current_version() {
        var n = new DaemonStatusNotifier();
        var t = n.WaitBeyondAsync(n.Version, CancellationToken.None);
        await Assert.That(t.IsCompleted).IsFalse();
        n.Pulse();
        await t.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Pulse_is_a_broadcast_every_waiter_in_the_generation_wakes() {
        var n = new DaemonStatusNotifier();
        var seen = n.Version;
        var a = n.WaitBeyondAsync(seen, CancellationToken.None);
        var b = n.WaitBeyondAsync(seen, CancellationToken.None);
        n.Pulse();
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task A_fresh_wait_after_a_pulse_blocks_until_the_next_pulse() {
        var n = new DaemonStatusNotifier();
        n.Pulse();
        var t = n.WaitBeyondAsync(n.Version, CancellationToken.None);
        await Assert.That(t.IsCompleted).IsFalse();
        n.Pulse();
        await t.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Cancellation_aborts_one_wait_without_disturbing_other_waiters() {
        var n = new DaemonStatusNotifier();
        var seen = n.Version;
        using var cts = new CancellationTokenSource();
        var cancelled = n.WaitBeyondAsync(seen, cts.Token);
        var live      = n.WaitBeyondAsync(seen, CancellationToken.None);

        cts.Cancel();
        var threw = false;
        try { await cancelled; } catch (OperationCanceledException) { threw = true; }
        await Assert.That(threw).IsTrue();

        n.Pulse();
        await live.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusNotifierTests/*"
```

Expected: compile error — `DaemonStatusNotifier` does not exist.

- [ ] **Step 3: Implement the notifier**

```csharp
// src/Capacitor.Cli.Daemon/Services/DaemonStatusNotifier.cs
namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Monotonic change generation behind DaemonStatus pushes. Version and the shared rearm
/// source live under ONE lock: Pulse() increments and completes-and-rearms in the same
/// critical section; WaitBeyondAsync reads Version and captures the current source in that
/// same critical section — no torn interleaving between the check and the capture. This is
/// a broadcast: N subscribers each hold their own cursor (the `seen` they pass in) and can
/// never consume each other's signal. Call sites must mutate state FIRST and Pulse() second,
/// or a subscriber could snapshot old state at the new version and then wait forever.
/// </summary>
internal sealed class DaemonStatusNotifier {
    readonly Lock _lock = new();
    long _version;
    // RunContinuationsAsynchronously: completed under _lock — a waiter's continuation must
    // not run inline while the lock is held.
    TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public long Version {
        get { lock (_lock) return _version; }
    }

    public void Pulse() {
        lock (_lock) {
            _version++;
            var done = _next;
            _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
            done.TrySetResult();
        }
    }

    public Task WaitBeyondAsync(long seen, CancellationToken ct) {
        TaskCompletionSource wait;
        lock (_lock) {
            if (_version > seen) return Task.CompletedTask;
            wait = _next;
        }
        // A timeout/cancellation here only stops THIS waiter's wait — the shared source is
        // never completed or replaced by a consumer.
        return wait.Task.WaitAsync(ct);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusNotifierTests/*"
```

Expected: 5 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/DaemonStatusNotifier.cs test/Capacitor.Cli.Tests.Unit/Daemon/DaemonStatusNotifierTests.cs
git commit -m "Add DaemonStatusNotifier generation counter for supervision pushes"
```

---

### Task 4: RequesterUserId on AgentInstance, stamped from the launch command

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (AgentInstance record ~line 44; launch-core `new AgentInstance(...)` initializer ~line 1467; new test accessor; `SeedAgentForTest` gains a `requester` parameter ~line 956)
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorRequesterTests.cs` (create)

**Interfaces:**
- Consumes: `LaunchAgentCommand.RequesterUserId` (already on the wire, `src/Capacitor.Cli.Core/Models.cs` — null for old servers), the vendor-test harness `AgentOrchestratorVendorTests.BuildOrchestrator(...)` and its fakes (`SpyPtyProcessFactory`, fake `IHostedAgentLauncher`, fake `ServerConnection`, `CreateGitRepo`) — reuse them exactly as the existing vendor routing tests do.
- Produces:
  - `AgentInstance.RequesterUserId` — `public string? RequesterUserId { get; init; }`
  - `internal AgentInstance? GetAgentForTest(string id)` on `AgentOrchestrator`
  - `SeedAgentForTest(..., string? requester = null)` stamps `RequesterUserId = requester`

- [ ] **Step 1: Write the failing test**

Model the harness usage on an existing launch test in `AgentOrchestratorVendorTests.cs` (it is a `partial class` — if `BuildOrchestrator`/fakes are `static` members there, declare this new class `public partial class AgentOrchestratorVendorTests` in the new file so they're in scope; otherwise mirror the harness construction inline).

```csharp
// test/Capacitor.Cli.Tests.Unit/AgentOrchestratorRequesterTests.cs
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Requester stamping (spec §4.3): AgentInstance.RequesterUserId is captured from
/// LaunchAgentCommand at construction — non-null when a new server sends it, null for
/// old servers (field absent) — so the supervision payload can show who asked.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task Launch_stamps_RequesterUserId_from_the_command() {
        var (repoPath, cleanup) = CreateGitRepo();
        try {
            var server       = new FakeServerConnection();          // same fake the routing tests use
            var ptyFactory   = new SpyPtyProcessFactory();
            var orchestrator = BuildOrchestrator(
                server, ptyFactory, ClaudeOnlyLaunchers(), allowedRepoPath: repoPath);

            await orchestrator.HandleLaunchAgent(new LaunchAgentCommand(
                AgentId: "req-1", Prompt: "p", Model: "default", Effort: null,
                RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude",
                RequesterUserId: "github:12345"));

            await Assert.That(orchestrator.GetAgentForTest("req-1")!.RequesterUserId)
                .IsEqualTo("github:12345");
        } finally { cleanup(); }
    }

    [Test]
    public async Task Launch_without_a_requester_leaves_RequesterUserId_null() {
        var (repoPath, cleanup) = CreateGitRepo();
        try {
            var server       = new FakeServerConnection();
            var ptyFactory   = new SpyPtyProcessFactory();
            var orchestrator = BuildOrchestrator(
                server, ptyFactory, ClaudeOnlyLaunchers(), allowedRepoPath: repoPath);

            await orchestrator.HandleLaunchAgent(new LaunchAgentCommand(
                AgentId: "req-2", Prompt: "p", Model: "default", Effort: null,
                RepoPath: repoPath, Tools: null, AttachmentIds: null, Vendor: "claude"));

            await Assert.That(orchestrator.GetAgentForTest("req-2")!.RequesterUserId).IsNull();
        } finally { cleanup(); }
    }
}
```

Adjust the fake names (`FakeServerConnection`, `SpyPtyProcessFactory`, the launcher-dictionary helper) to whatever the existing vendor tests actually define — copy the exact construction lines from a passing launch test in that file rather than inventing new fakes. If `HandleLaunchAgent` requires awaiting agent registration (it registers before the read loop), assert with a short poll: `GetAgentForTest` non-null within 5 s.

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"
```

Expected: compile error — `RequesterUserId` / `GetAgentForTest` not defined.

- [ ] **Step 3: Implement**

In the `AgentInstance` record body (next to `FlowRunId`/`FlowRole`, ~line 46):

```csharp
    /// <summary>Who asked for this launch (server-stamped requester user id). Null for old
    /// servers and local spawns — the supervision payload renders null as unknown.</summary>
    public string?              RequesterUserId   { get; init; }
```

In the launch core's `new AgentInstance(...)` initializer (~line 1467, next to `Kind = cmd.Kind`):

```csharp
                RequesterUserId     = cmd.RequesterUserId,
```

Add the test accessor next to `RegisterAgentForTest` (~line 3341):

```csharp
    internal AgentInstance? GetAgentForTest(string id) => _agents.GetValueOrDefault(id);
```

In `SeedAgentForTest` (~line 956): add parameter `string? requester = null` and `RequesterUserId = requester` in the object initializer.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"
```

Expected: new tests PASS, existing vendor tests still PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorRequesterTests.cs
git commit -m "Stamp RequesterUserId onto AgentInstance from the launch command"
```

---

### Task 5: Orchestrator snapshot + centralized mutate-then-pulse helpers

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (ctor param, helpers, replace mutation sites)
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (snapshot builder; local-spawn registry write)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/AgentStatusSnapshotTests.cs` (create)

**Interfaces:**
- Consumes: `DaemonStatusNotifier` (Task 3), `AgentStatusDto` (Task 2), `AgentInstance.RequesterUserId` (Task 4), private static `KindText(LaunchKind)` (same partial class).
- Produces on `AgentOrchestrator`:
  - ctor gains trailing optional param `DaemonStatusNotifier? statusNotifier = null`; field `readonly DaemonStatusNotifier _statusNotifier;` = `statusNotifier ?? new()`. (Optional so the existing 3 direct construction sites and DI keep compiling; MS DI injects the registered singleton into an optional parameter when one is registered.)
  - `internal void SetAgentStatus(AgentInstance agent, string status)` — writes `agent.Status`, then pulses.
  - `internal void PublishAgent(AgentInstance agent)` — `_agents[agent.Id] = agent`, then pulses.
  - `internal void UnpublishAgent(string agentId)` — `_agents.TryRemove(agentId, out _)`, then pulses.
  - `internal List<AgentStatusDto> SnapshotAgentsForStatus()` — one enumeration pass, pinned order.

- [ ] **Step 1: Write the failing tests**

Use the existing `SeedAgentForTest` seam (plus Task 4's `requester` param). Build a bare orchestrator exactly the way `LocalControlHelloTests.StartAsync` does (Noop factories, empty launcher dicts), passing a shared notifier: `new AgentOrchestrator(config, connection, worktreeManager, repoMatcher, new NoopPtyProcessFactory(), new NoopHttpClientFactory(), permissionBridge, new Dictionary<string, IHostedAgentLauncher>(), new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(), NullLogger<AgentOrchestrator>.Instance, gate, statusNotifier: notifier)` — copy the support types from that file into a small shared local helper in this test class.

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/AgentStatusSnapshotTests.cs  (test bodies)
    [Test]
    public async Task Snapshot_orders_by_created_at_then_id_ordinal_and_includes_all_statuses() {
        var (orch, _) = Build();
        var t0 = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc);
        orch.SeedAgentForTest("b-second", status: "Quarantined", createdAt: t0.AddMinutes(1));
        orch.SeedAgentForTest("z-first",  status: "Starting",    createdAt: t0);
        orch.SeedAgentForTest("a-tie",    status: "Completed",   createdAt: t0.AddMinutes(1));

        var agents = orch.SnapshotAgentsForStatus();

        await Assert.That(agents.Select(a => a.Id)).IsEquivalentTo(
            new[] { "z-first", "a-tie", "b-second" }, CollectionOrdering.Matching);
        // All statuses ride along verbatim — the vocabulary is open, PascalCase as stored.
        await Assert.That(agents.Select(a => a.Status)).IsEquivalentTo(
            new[] { "Starting", "Completed", "Quarantined" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Snapshot_maps_kind_spellings_requester_and_nullables() {
        var (orch, _) = Build();
        orch.SeedAgentForTest("r1", kind: LaunchKind.ReviewFlow, flowRunId: "flow_1",
            flowRole: "reviewer", requester: "github:12345");
        orch.SeedAgentForTest("d1"); // defaults: LaunchKind.Default, no flow identity, no requester

        var byId = orch.SnapshotAgentsForStatus().ToDictionary(a => a.Id);

        await Assert.That(byId["r1"].Kind).IsEqualTo("review-flow");
        await Assert.That(byId["r1"].Requester).IsEqualTo("github:12345");
        await Assert.That(byId["r1"].FlowRunId).IsEqualTo("flow_1");
        await Assert.That(byId["d1"].Kind).IsEqualTo("agent");
        await Assert.That(byId["d1"].Requester).IsNull();
        await Assert.That(byId["d1"].FlowRunId).IsNull();
    }

    [Test]
    public async Task Publish_status_change_and_unpublish_each_advance_the_generation() {
        var (orch, notifier) = Build();

        var v0 = notifier.Version;
        var agent = orch.SeedAgentForTest("gen-1"); // registers via PublishAgent
        await Assert.That(notifier.Version).IsGreaterThan(v0);

        var v1 = notifier.Version;
        orch.SetAgentStatus(agent, "Completed");
        await Assert.That(notifier.Version).IsGreaterThan(v1);

        var v2 = notifier.Version;
        orch.UnpublishAgent("gen-1");
        await Assert.That(notifier.Version).IsGreaterThan(v2);
        await Assert.That(orch.SnapshotAgentsForStatus()).IsEmpty();
    }
```

(`Build()` is the local helper returning `(AgentOrchestrator, DaemonStatusNotifier)`. If TUnit's ordered-collection assertion spelling differs, assert element-by-element on the indexed list instead — the *order* is the contract.)

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentStatusSnapshotTests/*"
```

Expected: compile error — `SnapshotAgentsForStatus` / `statusNotifier:` not defined.

- [ ] **Step 3: Implement**

Ctor (~line 366): append `DaemonStatusNotifier? statusNotifier = null` after `deferProcessorPublication` and in the body set `_statusNotifier = statusNotifier ?? new();` (field declared near `_agents`).

Helpers (place near `ActiveCount`, ~line 568):

```csharp
    /// Mutation FIRST, Pulse() second — always (a pulse published before its mutation lets a
    /// subscriber read the new version, snapshot the OLD state, and wait forever). These
    /// helpers are the only writers of agent status and registry membership, so the ordering
    /// cannot be forgotten at a call site.
    internal void SetAgentStatus(AgentInstance agent, string status) {
        agent.Status = status;
        _statusNotifier.Pulse();
    }

    internal void PublishAgent(AgentInstance agent) {
        _agents[agent.Id] = agent;
        _statusNotifier.Pulse();
    }

    internal void UnpublishAgent(string agentId) {
        _agents.TryRemove(agentId, out _);
        _statusNotifier.Pulse();
    }
```

Replace the mutation sites (verify each with `grep -n '\.Status = \|_agents\[' src/Capacitor.Cli.Daemon/Services/AgentOrchestrator*.cs` — line numbers may have drifted):

| Site (approx.) | Old | New |
|---|---|---|
| `AgentOrchestrator.cs` ~1750 (read loop Starting→Running) | `agent.Status = "Running";` | `SetAgentStatus(agent, "Running");` |
| `AgentOrchestrator.cs` ~1911 (launch failure status) | `agent.Status = status;` | `SetAgentStatus(agent, status);` |
| `AgentOrchestrator.cs` ~2102 (stop path) | `agent.Status = "Completed";` | `SetAgentStatus(agent, "Completed");` |
| `AgentOrchestrator.cs` ~1479 (launch core) | `_agents[agentId] = agent;` | `PublishAgent(agent);` |
| `AgentOrchestrator.cs` ~972 (`SeedAgentForTest`) | `_agents[id] = agent;` | `PublishAgent(agent);` |
| `AgentOrchestrator.cs` ~3341 (`RegisterAgentForTest`) | `_agents[agent.Id] = agent;` | `PublishAgent(agent);` |
| `AgentOrchestrator.cs` ~3058 (cleanup removal) | `_agents.TryRemove(agentId, out _);` | `UnpublishAgent(agentId);` (keep the surrounding comment) |
| `AgentOrchestrator.LocalIpc.cs` ~185 (local spawn) | `_agents[agentId] = agent;` | `PublishAgent(agent);` |

Do NOT pulse on `LastOutputAt`/`HasReceivedOutput` writes — they are not in the payload. `SeedAgentForTest`'s pre-publication `agent.Status = status;` (~line 970) stays a plain write: the instance is not yet visible, and `PublishAgent` pulses right after.

Snapshot builder in `AgentOrchestrator.LocalIpc.cs` (next to `HandleLocalListAsync`, where `KindText` lives):

```csharp
    /// <summary>
    /// The supervision payload's agent rows: every entry in _agents (all statuses — same
    /// visibility as `kcap agent ls`; quarantined-but-removed children are gone from _agents
    /// already). Order is a wire contract: created_at ascending, id-ordinal tie-break —
    /// ConcurrentDictionary enumeration order must never leak into the payload.
    /// </summary>
    internal List<AgentStatusDto> SnapshotAgentsForStatus() =>
        [.. _agents.Values
            .OrderBy(a => a.CreatedAt)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new AgentStatusDto(
                a.Id, KindText(a.Kind), a.Vendor, a.RepoPath, a.Status,
                a.FlowRunId, a.FlowRole, a.RequesterUserId, a.CreatedAt, a.Model))];
```

- [ ] **Step 4: Run the new tests, then the full unit suite (the mutation-site swap touches launch/stop/cleanup paths)**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/AgentStatusSnapshotTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
```

Expected: new tests PASS; zero regressions in the full suite.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs test/Capacitor.Cli.Tests.Unit/Daemon/AgentStatusSnapshotTests.cs
git commit -m "Centralize agent mutations behind pulse helpers and add the status snapshot"
```

---

### Task 6: DaemonStatusIpc handler, routing, capability, ServerConnection pulses, DI

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` (ctor + route + default-arm message)
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/ServerConnection.cs` (optional ctor param + 4 pulse sites)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (DI registrations)
- Modify: `test/Capacitor.Cli.Tests.Unit/Daemon/LocalControlHelloTests.cs` (capability expectations ×3; harness gains the status pieces)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/DaemonStatusIpcTests.cs` (create — first test only; the full matrix is Task 7)

**Interfaces:**
- Consumes: everything from Tasks 1–5; `LaunchConsentIpc.HandleSubscribeAsync` EOF-watcher shape; `DaemonRunner.ResolveDaemonVersion()`; `ServerConnection.HubState` (`Microsoft.AspNetCore.SignalR.Client.HubConnectionState`).
- Produces:
  - `internal sealed class DaemonStatusIpc(DaemonConfig config, AgentOrchestrator orchestrator, ServerConnection connection, DaemonStatusNotifier notifier)` with `Task HandleSubscribeAsync(Stream stream, CancellationToken ct)`, `internal TimeSpan Debounce { get; set; }` (default 250 ms), `internal int ActiveSubscribersForTest { get; }`, `internal Action? AfterSnapshotForTest { get; set; }`.
  - `LocalControlServer` ctor gains `DaemonStatusIpc statusIpc`; routes `FrameType.StatusSubscribe`.
  - `LocalControlCapabilities.Current == ["consent/1", "status/1"]`.
  - `ServerConnection` ctor gains trailing optional `DaemonStatusNotifier? statusNotifier = null` (subclass test doubles keep compiling).

- [ ] **Step 1: Write the failing first integration test**

Create `DaemonStatusIpcTests.cs` with a harness copied from `LocalControlHelloTests` (`StartAsync`/`StopAsync`/`RunAsync`/`ConnectAsync`, Noop support types), extended with the status pieces — this harness is reused verbatim by Task 7:

```csharp
// Harness deltas vs LocalControlHelloTests.StartAsync (everything else copied as-is):
        var notifier   = new DaemonStatusNotifier();
        var connection = new ServerConnection(
            config, NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, notifier);
        // orchestrator: same arguments as the hello harness, plus `statusNotifier: notifier`
        var statusIpc  = new DaemonStatusIpc(config, orchestrator, connection, notifier) {
            Debounce = TimeSpan.FromMilliseconds(25), // fast tests; 250ms is the production default
        };
        var server = new LocalControlServer(
            config, orchestrator, restart, consentIpc, statusIpc, NullLogger<LocalControlServer>.Instance);
// Harness record carries: Server, Orchestrator, Connection, Config, SockPath, Notifier, StatusIpc.

    static async Task<DaemonStatusDto> ReadStatusAsync(Stream s, CancellationToken ct) {
        var f = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(f!.Type).IsEqualTo(FrameType.DaemonStatus);
        return JsonSerializer.Deserialize(f.Text, StatusIpcJsonContext.Default.DaemonStatusDto)!;
    }

    [Test]
    [NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
    public async Task Subscribe_pushes_an_immediate_snapshot_with_daemon_block_and_agents() {
        if (OperatingSystem.IsWindows()) return; // Unix-domain socket path

        await RunAsync("st-a", async (h, ct) => {
            h.Orchestrator.SeedAgentForTest("s1", kind: LaunchKind.ReviewFlow,
                flowRunId: "flow_1", flowRole: "reviewer", requester: "github:12345");
            h.Orchestrator.SeedAgentForTest("s2", status: "Starting");

            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);

            var dto = await ReadStatusAsync(s, ct);
            await Assert.That(dto.Daemon.Name).IsEqualTo(h.Config.Name);
            await Assert.That(dto.Daemon.Version).IsNotEmpty();
            await Assert.That(dto.Daemon.ServerUrl).IsEqualTo(h.Config.ServerUrl);
            await Assert.That(dto.Daemon.Connection).IsEqualTo("disconnected"); // no live hub in tests
            await Assert.That(dto.Daemon.MaxAgents).IsEqualTo(h.Config.MaxConcurrentAgents);
            await Assert.That(dto.Daemon.ActiveAgents).IsEqualTo(2); // Running + Starting
            await Assert.That(dto.Agents.Count).IsEqualTo(2);
            var r1 = dto.Agents.Single(a => a.Id == "s1");
            await Assert.That(r1.Kind).IsEqualTo("review-flow");
            await Assert.That(r1.Requester).IsEqualTo("github:12345");
        });
    }
```

Also update the three `LocalControlHelloTests` capability assertions from `new[] { "consent/1" }` to `new[] { "consent/1", "status/1" }`, and add `statusIpc` to that harness's `LocalControlServer` construction (a minimal `new DaemonStatusIpc(config, orchestrator, connection, new DaemonStatusNotifier())` is fine there).

- [ ] **Step 2: Run to verify failure**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusIpcTests/*"
```

Expected: compile error — `DaemonStatusIpc` does not exist.

- [ ] **Step 3: Implement the handler**

```csharp
// src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs
using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Daemon.Services;

/// Local-socket handler for StatusSubscribe: one full DaemonStatusDto snapshot immediately,
/// then a debounced re-push whenever the change generation advances past this connection's
/// cursor. Full snapshots + per-connection cursors mean a missed pulse can never desync a
/// client, and a slow subscriber delays only itself. Trust model: same 0600-socket owner
/// trust as every other local frame.
internal sealed class DaemonStatusIpc(
    DaemonConfig config, AgentOrchestrator orchestrator, ServerConnection connection,
    DaemonStatusNotifier notifier) {

    /// Coalesces a pulse burst into one trailing snapshot. A tuning constant, not a wire
    /// contract; tests shrink it.
    internal TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(250);

    int _subscribers;
    internal int ActiveSubscribersForTest => Volatile.Read(ref _subscribers);

    /// Test seam: runs between materializing a snapshot and pushing it, so a test can land a
    /// mutation exactly at the cursor/snapshot boundary deterministically.
    internal Action? AfterSnapshotForTest { get; set; }

    public async Task HandleSubscribeAsync(Stream stream, CancellationToken ct) {
        Interlocked.Increment(ref _subscribers);
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // EOF watcher: a vanished subscriber must be reaped promptly (same discipline as
            // ConsentSubscribe), or the daemon would keep serializing snapshots for nobody.
            _ = Task.Run(async () => {
                try { while (await FrameCodec.ReadAsync(stream, cts.Token) is not null) { } }
                catch { }
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }, cts.Token);

            while (true) {
                var seen = notifier.Version; // cursor BEFORE snapshotting: a mutation during
                var json = Snapshot();       // the snapshot/push advances Version past `seen`
                AfterSnapshotForTest?.Invoke();
                await FrameCodec.WriteAsync(stream, LocalFrame.StatusJson(FrameType.DaemonStatus, json), cts.Token);
                await notifier.WaitBeyondAsync(seen, cts.Token);
                await Task.Delay(Debounce, cts.Token);
            }
        } catch (OperationCanceledException) {
            // subscriber EOF or daemon shutdown — either way the connection just closes
        } finally {
            Interlocked.Decrement(ref _subscribers);
        }
    }

    string Snapshot() {
        var agents = orchestrator.SnapshotAgentsForStatus();
        // Same predicate as the orchestrator's ActiveCount, applied to the SAME materialized
        // array — the count and the array can never disagree within one payload.
        var active = agents.Count(a => a.Status is "Starting" or "Running");
        var dto = new DaemonStatusDto(
            new DaemonInfoDto(
                config.Name, DaemonRunner.ResolveDaemonVersion(), config.ServerUrl,
                ConnectionText(connection.HubState), config.MaxConcurrentAgents, active),
            agents);
        return JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.DaemonStatusDto);
    }

    static string ConnectionText(HubConnectionState s) => s switch {
        HubConnectionState.Connected    => "connected",
        HubConnectionState.Connecting   => "connecting",
        HubConnectionState.Reconnecting => "reconnecting",
        _                               => "disconnected",
    };
}
```

`LocalControlServer.cs`: add `DaemonStatusIpc statusIpc` to the primary constructor (after `consentIpc`); add the route after the `Hello` case:

```csharp
                case FrameType.StatusSubscribe: await statusIpc.HandleSubscribeAsync(stream, ct); break;
```

and extend the default-arm message's frame list with `/StatusSubscribe` (before `, got {first.Type}`).

`LocalControlCapabilities.cs`: set `Current = ["consent/1", "status/1"];` and rewrite the last doc sentence to state both entries' handlers (`ConsentSubscribe`… and `StatusSubscribe`) are live in this build — keep the invariant sentence, drop the "a later change appends" sentence.

`ServerConnection.cs`: append optional ctor param `DaemonStatusNotifier? statusNotifier = null` (~line 170), store `_statusNotifier = statusNotifier ?? new();`. Pulse at the four HubState transition points — the hub mutates `State` before invoking these, so pulse-inside-handler is mutation-first:
- `OnReconnecting` (~line 649): after `_gate.MarkUnregistered();` add `_statusNotifier.Pulse();`
- `OnReconnected` (~line 669): first statement `_statusNotifier.Pulse();` (before `RegisterDaemonAsync`, which can throw)
- `OnClosed` (locate with `grep -n "OnClosed" src/Capacitor.Cli.Daemon/Services/ServerConnection.cs`): first statement `_statusNotifier.Pulse();`
- `ConnectWithRetryAsync` success path (~line 456): after `LogConnected(_config.Name);` add `_statusNotifier.Pulse();`

`DaemonRunner.cs`: next to the consent registrations (~line 241):

```csharp
        builder.Services.AddSingleton<DaemonStatusNotifier>();
        builder.Services.AddSingleton<DaemonStatusIpc>();
```

(The registered notifier satisfies the optional ctor params of `AgentOrchestrator` and `ServerConnection` — MS DI resolves a registered service for an optional parameter and only falls back to the default when unregistered.)

- [ ] **Step 4: Run the new test, the hello tests, and the full unit suite**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusIpcTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/LocalControlHelloTests/*"
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
```

Expected: all PASS (hello tests now assert both capabilities).

- [ ] **Step 5: Commit**

```bash
git add -A src test
git commit -m "Serve StatusSubscribe with pushed DaemonStatus snapshots and advertise status/1"
```

---

### Task 7: Real-socket behavior matrix (spec §6 PR 2)

**Files:**
- Modify: `test/Capacitor.Cli.Tests.Unit/Daemon/DaemonStatusIpcTests.cs` (extend with the matrix; harness from Task 6)

**Interfaces:**
- Consumes: Task 6 harness (`RunAsync`, `ConnectAsync`, `ReadStatusAsync`), `SeedAgentForTest`, `SetAgentStatus`, `UnpublishAgent`, `Debounce`, `AfterSnapshotForTest`, `ActiveSubscribersForTest`.
- Produces: the pinned behavior tests below. Add one helper:

```csharp
    /// Reads one frame or returns null when none arrives within the window — for asserting
    /// "no further push" without hanging the suite.
    static async Task<LocalFrame?> ReadOrNullAsync(Stream s, TimeSpan window, CancellationToken ct) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(window);
        try { return await FrameCodec.ReadAsync(s, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
```

- [ ] **Step 1: Write the matrix tests** (each `[Test]` carries the Windows guard + `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]`; distinct short daemon names `st-b`…`st-h`)

```csharp
    [Test] // add / status-change / removal each trigger a re-push
    public async Task Each_mutation_triggers_a_re_push() {
        // subscribe → drain immediate snapshot
        // SeedAgentForTest("m1") → next frame contains m1
        // SetAgentStatus(m1, "Completed") → next frame shows m1 Completed and active_agents dropped
        // UnpublishAgent("m1") → next frame has empty agents
        // Assert each step via ReadStatusAsync; internal consistency: dto.Daemon.ActiveAgents ==
        // dto.Agents.Count(a => a.Status is "Starting" or "Running") on every payload.
    }

    [Test] // burst coalescing: at most one trailing snapshot after the in-flight push
    public async Task A_pulse_burst_coalesces_into_one_trailing_snapshot() {
        // h.StatusIpc.Debounce = TimeSpan.FromMilliseconds(150) (set in harness before subscribe
        // via a RunAsync overload or by seeding Debounce per-test through the harness record).
        // subscribe → drain immediate snapshot
        // seed 5 agents back-to-back (5 pulses, no reads in between)
        // next frame: converged (all 5 present)
        // ReadOrNullAsync(s, 400ms): null — no second trailing push for the same burst
    }

    [Test] // two-subscriber convergence + slow subscriber doesn't stall the fast one
    public async Task Both_subscribers_converge_after_a_change_and_a_slow_one_stalls_only_itself() {
        // open two subscriptions, drain both immediate snapshots
        // seed one agent
        // subscriber A: read until a frame contains the agent (converged)
        // subscriber B: never read until now — then read: its buffered/next frame also converges
        // (per-connection cursors: B's snapshot is at least as new as the final generation)
    }

    [Test] // cursor-before-snapshot + pulse-after-mutation regressions, deterministic via the hook
    public async Task A_mutation_at_the_snapshot_boundary_still_converges() {
        // BEFORE subscribing:
        //   h.StatusIpc.AfterSnapshotForTest = () => {
        //       h.StatusIpc.AfterSnapshotForTest = null;              // fire once
        //       h.Orchestrator.SeedAgentForTest("boundary");          // mutation + pulse land
        //   };                                                        // between snapshot and wait
        // subscribe → first frame does NOT contain "boundary" (snapshot was taken first)
        // next frame (no further external pulses!) DOES contain "boundary":
        //   the loop's WaitBeyondAsync(seen) completes synchronously because the seed advanced
        //   the generation past the pre-snapshot cursor — pinning cursor-before-snapshot AND
        //   that a final mutation with no later pulse converges.
    }

    [Test] // subscriber EOF reaps the handler promptly
    public async Task Subscriber_eof_reaps_the_handler_promptly() {
        // subscribe, drain immediate snapshot, assert ActiveSubscribersForTest == 1
        // dispose the client stream
        // poll (20ms steps, 5s deadline) until h.StatusIpc.ActiveSubscribersForTest == 0
    }

    [Test] // snapshot stress: no exceptions, every payload internally consistent, converges
    public async Task Concurrent_mutations_never_produce_an_inconsistent_payload() {
        // subscribe with Debounce = 25ms; reader task: collect DaemonStatusDto frames,
        //   asserting per-payload ActiveAgents == Count(Starting|Running) as they arrive
        // mutator task: 50 iterations of seed("x{i}") / SetAgentStatus / UnpublishAgent mix
        // after mutations settle on a known final registry, read until a frame matches the
        //   final agent-id set (5s deadline) — convergence, not per-generation delivery
    }

    [Test] // §5: StatusSubscribe on a shutting-down daemon — the connection just closes
    public async Task Daemon_shutdown_closes_the_subscription() {
        // subscribe, drain immediate snapshot
        // await h.Server.StopAsync(CancellationToken.None)  (harness re-entrancy: skip StopAsync
        //   in the finally for this test, or make StopAsync idempotent-safe)
        // FrameCodec.ReadAsync returns null (clean EOF) or throws EndOfStream/IOException —
        //   assert the read does NOT return another DaemonStatus frame
    }
```

Write these as real test bodies (the comments above are the specification of each body — expand them into code following Task 6's first test's style; every read uses the harness `ct`).

- [ ] **Step 2: Run to verify the new tests fail** (before any needed seams exist — e.g. the harness `Debounce` plumbing)

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusIpcTests/*"
```

- [ ] **Step 3: Make them pass** — this is test-plumbing work only (harness knobs, timing); production changes should not be needed. If a test exposes a real defect (e.g. a missed pulse site), fix production and note it in the commit message.

- [ ] **Step 4: Run the status tests 3× to shake out flake, then the full unit suite**

```bash
for i in 1 2 3; do dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonStatusIpcTests/*" || break; done
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
```

Expected: PASS ×3, no regressions. Timing assertions must be one-sided (deadlines for things that must happen; generous quiet-windows for things that must not) — never assert an exact push count under load.

- [ ] **Step 5: Commit**

```bash
git add test/Capacitor.Cli.Tests.Unit/Daemon/DaemonStatusIpcTests.cs
git commit -m "Pin supervision IPC behavior: re-push, coalescing, convergence, EOF, stress"
```

---

### Task 8: Verification, AOT check, PR

**Files:**
- No new code. Verification + PR only.

- [ ] **Step 1: Full unit + integration suites**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```

Expected: all PASS. (Windows CI note: the new socket tests self-skip on Windows via the `OperatingSystem.IsWindows()` guard; no `Path.Combine` assertions were introduced.)

- [ ] **Step 2: AOT publish — zero IL warnings**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output. (The new `StatusIpcJsonContext` is source-gen; nothing reflects.)

- [ ] **Step 3: README check** — confirmed no user-facing CLI surface changed (no new command/flag/default/prereq), so no README edit; say so in the PR description.

- [ ] **Step 4: Push and open the PR**

```bash
git push -u origin alexeyzimarev/ai-1649-supervision-ipc-daemon-state-live-agent-list-stop-agent
gh pr create --title "Supervision IPC: daemon state + live agent list over the control socket" --body "$(cat <<'EOF'
Implements the supervision surface from the slice-2 pre-work spec (§4, docs/superpowers/specs/2026-08-01-slice2-prework-control-ipc-design.md): `StatusSubscribe = 16` opens a long-lived connection that receives full `DaemonStatus = 76` snapshots — immediately on subscribe, then debounced re-pushes driven by a monotonic change generation (`DaemonStatusNotifier`, mutation-first/pulse-second via centralized orchestrator helpers). `AgentInstance` gains `RequesterUserId` stamped from the launch command. The hello capability list now advertises `status/1` alongside `consent/1`. Stop reuses the existing `StopV2` frame — no new stop machinery.

No CLI surface changes — this is app-facing IPC only, so no README update is needed.

AI-1649

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 5: Watch CI to green** — poll `gh pr checks` (do NOT rely on `gh pr merge --auto`: only `license/cla` is a required check, so auto-merge fires before build/test/AOT legs finish). Mind the Windows leg.

---

## Self-Review (performed while writing)

- **Spec coverage:** §4.1 frames/wire semantics → Tasks 1, 2, 5, 6; §4.2 notifier + pulse sites + subscriber loop → Tasks 3, 5, 6; §4.3 requester → Task 4; §4.4 stop → no work by design; §5 error handling (nulls-written context, forward compat, shutdown close, extended default-arm message) → Tasks 2, 6, 7; §6 PR-2 test list → Tasks 1, 2, 4, 5, 7 (round-trips; exact JSON incl. order/nulls/active-count consistency; unmapped-member compat; immediate snapshot; convergence ×2 subscribers incl. boundary-mutation regressions; per-mutation re-push; burst coalescing; stress; EOF reap; requester stamping).
- **Deliberate deviations:** none from the spec. Test-only seams added beyond it: `AfterSnapshotForTest`, `ActiveSubscribersForTest`, settable `Debounce`, `GetAgentForTest` — all internal, all needed to make §6's pinned behaviors deterministic.
- **Type consistency:** `SnapshotAgentsForStatus() → List<AgentStatusDto>` consumed by `DaemonStatusIpc.Snapshot()`; `WaitBeyondAsync(long, CancellationToken) → Task` consumed by the subscriber loop; helper names (`SetAgentStatus`/`PublishAgent`/`UnpublishAgent`) used identically in Tasks 5 and 7.
- **Known drift risks for the implementer:** line numbers are approximate — re-grep before editing; TUnit assertion spellings for ordered collections may differ — the pinned *order* is the contract, not the assertion API; the Task 4 fakes must be copied from the existing vendor tests, not invented.
