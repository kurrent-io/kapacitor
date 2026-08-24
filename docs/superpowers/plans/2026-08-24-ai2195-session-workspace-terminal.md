# Session Workspace with Terminal (AI-2195) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A workspace pane for one session — header, tab strip, Terminal tab attached to the live PTY over local-control IPC — opened by clicking a session card on Home.

**Architecture:** One additive wire field (`has_terminal`) gives the app the authoritative Terminal gate; a new BCL-only Core client (`AgentAttachClient`) pumps the existing Attach/Stdout/Stdin/Resize frames with an atomic terminal-cause slot; app-side, a `WorkspaceViewModel` + `TerminalTabViewModel` pair owns the attach lifecycle behind a factory seam, `MainWindowViewModel` gains the first navigation seam (top-level surface swap), and a bounded teardown tracker guarantees socket close on every exit path.

**Tech Stack:** .NET 10, Avalonia 12.1.1, ReactiveUI/DynamicData, TUnit; `SvcSystems.UI.Terminal` 1.1.1 + `XTerm.NET` 1.0.16 (direct pinned).

**Spec:** `docs/superpowers/specs/2026-08-24-ai2195-session-workspace-terminal-design.md` — the plan argues from the spec; read both. Where a task compresses a spec rule, the spec wins.

## Global Constraints

- Branch: `alexeyzimarev/ai-2195-desktop-shell-session-workspace-with-terminal` (already checked out in this worktree; spec committed).
- TDD per repo convention: write the failing test, watch it fail, implement, watch it pass. TUnit filters use `--treenode-filter` glob syntax, never `--filter`.
- Core `LocalIpc` surface is BCL-only, NativeAOT-safe: no Rx, no reflection-based JSON, no logging frameworks. Verify with `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` (must print nothing) after Core changes.
- `FrameType` is append-only and untouched by this feature. `AgentStatusDto` fields are always emitted, additive, snake_case.
- Test conventions: `TempDir` from Helpers (`tmp.PathTo/CreateDir/CreateFile`, `GetResolvedPath` for socket paths — macOS `/var`→`/private` symlink eats `sockaddr_un` budget); `EnvScope` for env vars; `[NotInParallel("AvaloniaSession")]` + `AvaloniaSession.WithImmediateRxScheduler` for app VM tests touching Rx-bound state.
- Teardown durations (spec §3): Detach write bound **1 s**; per-teardown budget **3 s**; shutdown drain **5 s** total. All via injected `TimeProvider`.
- Copy rules: non-PTY note is "This session has no terminal" (+ " — runs over ACP" only when the family is reliably known); never a vendor name, never a disabled tab.
- No README change (desktop-app surface, not CLI).
- Commits: small, per task, trailer `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- `FakeTimeProvider`: the repo does not carry one. In the first task that needs it (Task 10), add `Microsoft.Extensions.TimeProvider.Testing` as a test-project `PackageVersion` + `PackageReference` (test projects only, never Core/App), or add a minimal manual-advance `TestTimeProvider` to `test/Capacitor.Tests.Helpers/` if the package pulls an unwanted dependency tree — check `dotnet list package --include-transitive` before choosing.

---

### Task 1: Wire — `has_terminal` on `AgentStatusDto`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs` (record `AgentStatusDto`, ~line 38)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests.cs`

**Interfaces:**
- Consumes: existing `AgentStatusDto`, `StatusIpcJsonContext`.
- Produces: `AgentStatusDto.HasTerminal` — trailing `bool? HasTerminal = null`, serialized `has_terminal`, always emitted. Every later task reads this exact member name.

- [ ] **Step 1: Write the failing tests** (append to `StatusIpcJsonTests`; mirror the file's existing serialize/deserialize helpers — read the top of the file first for its DTO-builder helpers and reuse them):

```csharp
[Test]
public async Task Old_agent_json_without_has_terminal_deserializes_to_null() {
    // Serialize a current DTO, strip the member, deserialize — the exact old-daemon shape.
    var dto = new AgentStatusDto(
        "a1", "agent", "claude", "/repo", "Running",
        null, null, null, DateTime.UtcNow, null, null);
    var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);
    var stripped = System.Text.RegularExpressions.Regex.Replace(json, ",\"has_terminal\":[^,}]+", "");

    var back = JsonSerializer.Deserialize(stripped, StatusIpcJsonContext.Default.AgentStatusDto);

    await Assert.That(back!.HasTerminal).IsNull();
}

[Test]
[Arguments(true)]
[Arguments(false)]
public async Task Has_terminal_serializes_present_and_never_omitted(bool value) {
    var dto = new AgentStatusDto(
        "a1", "agent", "claude", "/repo", "Running",
        null, null, null, DateTime.UtcNow, null, null, HasTerminal: value);

    var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);

    await Assert.That(json).Contains($"\"has_terminal\":{value.ToString().ToLowerInvariant()}");
}

[Test]
public async Task Null_has_terminal_still_emits_the_member() {
    var dto = new AgentStatusDto(
        "a1", "agent", "claude", "/repo", "Running",
        null, null, null, DateTime.UtcNow, null, null);

    var json = JsonSerializer.Serialize(dto, StatusIpcJsonContext.Default.AgentStatusDto);

    await Assert.That(json).Contains("\"has_terminal\":null");
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter '/*/*/StatusIpcJsonTests/*has_terminal*'`
Expected: compile error `'AgentStatusDto' does not contain a definition for 'HasTerminal'` — then, after Step 3's record change but before serializer options are right, possibly an omitted-null failure. Iterate until the failures are the assertions, then proceed.

- [ ] **Step 3: Implement**

In `StatusIpc.cs`, add the trailing member with a why-comment:

```csharp
public sealed record AgentStatusDto(
    string Id, string Kind, string Vendor, string? RepoPath, string Status,
    string? FlowRunId, string? FlowRole, string? Requester, DateTime CreatedAt, string? Model,
    string? RequesterDisplay,
    // Whether the agent's runtime emits a PTY the app can attach to
    // (IHostedAgentRuntime.EmitsTerminalOutput). Trailing + nullable so every
    // existing positional construction stays valid; null = older daemon,
    // unknown — the app falls back to its vendor heuristic. Always emitted:
    // false is a real value, not an absence.
    bool? HasTerminal = null);
```

If the context's `JsonSourceGenerationOptions` sets `DefaultIgnoreCondition`, the null-emission test will fail — this repo's StatusIpc context emits all members by default; do not add an ignore condition.

- [ ] **Step 4: Run to verify pass** — same filter, expect all green, then run the whole Core suite: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj` (expect green; the `NativeTestHost` tests need `dotnet build Capacitor.slnx` first).

- [ ] **Step 5: AOT check** — `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing.

- [ ] **Step 6: Commit** — `git add -A && git commit` message `Wire: additive has_terminal on AgentStatusDto`.

---

### Task 2: Daemon stamps `has_terminal`

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`SnapshotAgentsForStatus`, ~line 44)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/` — add to the file that already exercises `SnapshotAgentsForStatus`/status payloads; if none does, create `AgentStatusHasTerminalTests.cs` reusing the agent-registration harness from `AgentOrchestratorLocalAttachTests.cs` (that file shows how to build an orchestrator with a PTY runtime and how ACP-runtime agents are registered — copy its fixture setup verbatim, do not invent a new harness).

**Interfaces:**
- Consumes: `AgentStatusDto.HasTerminal` (Task 1); `IHostedAgentRuntime.EmitsTerminalOutput` (`src/Capacitor.Cli.Daemon/Services/IHostedAgentRuntime.cs:53`).
- Produces: every status payload carries `has_terminal` = the agent runtime's `EmitsTerminalOutput`.

- [ ] **Step 1: Write the failing test.** Using the borrowed harness, register one agent whose runtime has `EmitsTerminalOutput == true` (the PTY fake the attach tests use) and one with `false` (the ACP fake). Assert on the **serialized** payload, not the DTO:

```csharp
[Test]
public async Task Status_payload_carries_has_terminal_per_runtime() {
    // fixture setup copied from AgentOrchestratorLocalAttachTests (same fakes, same registration calls)
    var snapshot = orchestrator.SnapshotAgentsForStatus();
    var json = JsonSerializer.Serialize(
        snapshot.Single(a => a.Id == ptyAgentId), StatusIpcJsonContext.Default.AgentStatusDto);
    var acpJson = JsonSerializer.Serialize(
        snapshot.Single(a => a.Id == acpAgentId), StatusIpcJsonContext.Default.AgentStatusDto);

    await Assert.That(json).Contains("\"has_terminal\":true");
    await Assert.That(acpJson).Contains("\"has_terminal\":false");
}
```

- [ ] **Step 2: Run to verify failure** — `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter '/*/*/*/Status_payload_carries_has_terminal*'`. Expected: fails with `"has_terminal":null` in both payloads.

- [ ] **Step 3: Implement** — in `SnapshotAgentsForStatus`, add the argument to the `AgentStatusDto` construction:

```csharp
.Select(a => new AgentStatusDto(
    a.Id, KindText(a.Kind), a.Vendor, a.RepoPath, a.Status,
    a.FlowRunId, a.FlowRole, a.RequesterUserId, a.CreatedAt,
    /* existing model expression unchanged */,
    a.RequesterDisplay,
    HasTerminal: a.Runtime.EmitsTerminalOutput))
```

(Keep the existing model-expression lines exactly as they are; only append the named argument.)

- [ ] **Step 4: Run to verify pass**, then the daemon suite: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj`. Known machine-local failures to ignore by name: `Installed_codex_schema_matches_the_vendored_pin`; PTY flood-timing tests if the machine is loaded (re-run filtered to confirm).

- [ ] **Step 5: Commit** — `Daemon: stamp has_terminal from EmitsTerminalOutput`.

---

### Task 3: App gate projection — `HostedHarnessCatalog.FamilyFor` / `ShowsTerminal` / `EffectiveFamily`

**Files:**
- Modify: `src/Capacitor.App/Services/HostedHarnessCatalog.cs`
- Test: `test/Capacitor.App.Tests.Unit/HostedHarnessCatalogTests.cs`

**Interfaces:**
- Produces (all `public static` on `HostedHarnessCatalog`):
  - `string FamilyFor(string vendor)` — `"pty" | "acp" | "rpc"`, case-insensitive, unmapped → `"rpc"` (the map itself stays private).
  - `bool ShowsTerminal(bool? hasTerminal, string vendor)` — `hasTerminal ?? (FamilyFor(vendor) == "pty")`.
  - `string EffectiveFamily(bool? hasTerminal, string vendor)` — `FamilyFor(vendor)`, except an authoritative `hasTerminal == false` with a conflicting `"pty"` guess returns `"rpc"` (generic chat). A non-PTY vendor family is preserved as-is.

- [ ] **Step 1: Failing tests:**

```csharp
[Test]
public async Task FamilyFor_maps_vendors_and_defaults_unknown_to_rpc() {
    await Assert.That(HostedHarnessCatalog.FamilyFor("CLAUDE")).IsEqualTo("pty");
    await Assert.That(HostedHarnessCatalog.FamilyFor("gemini")).IsEqualTo("acp");
    await Assert.That(HostedHarnessCatalog.FamilyFor("neverheardof")).IsEqualTo("rpc");
}

[Test]
public async Task ShowsTerminal_prefers_the_authoritative_flag_and_falls_back_to_family() {
    await Assert.That(HostedHarnessCatalog.ShowsTerminal(true, "gemini")).IsTrue();
    await Assert.That(HostedHarnessCatalog.ShowsTerminal(false, "claude")).IsFalse();
    await Assert.That(HostedHarnessCatalog.ShowsTerminal(null, "claude")).IsTrue();
    await Assert.That(HostedHarnessCatalog.ShowsTerminal(null, "gemini")).IsFalse();
}

[Test]
public async Task EffectiveFamily_overrides_only_a_conflicting_pty_guess() {
    // codex app-server: vendor map says pty, daemon says no terminal → generic chat family.
    await Assert.That(HostedHarnessCatalog.EffectiveFamily(false, "codex")).IsEqualTo("rpc");
    // an already-non-PTY family is preserved, not flattened:
    await Assert.That(HostedHarnessCatalog.EffectiveFamily(false, "gemini")).IsEqualTo("acp");
    await Assert.That(HostedHarnessCatalog.EffectiveFamily(null, "claude")).IsEqualTo("pty");
    await Assert.That(HostedHarnessCatalog.EffectiveFamily(true, "claude")).IsEqualTo("pty");
}
```

- [ ] **Step 2: Run to verify failure** (compile errors → add stubs returning `""`/`false` → assertion failures).

- [ ] **Step 3: Implement:**

```csharp
/// Family for a vendor token, unmapped defaulting to "rpc" — the shared seam so
/// the workspace never duplicates the private map.
public static string FamilyFor(string vendor) =>
    TransportFamilies.TryGetValue(vendor, out var family) ? family : "rpc";

/// The Terminal-tab gate: the daemon's has_terminal when present, the vendor
/// family guess when an older daemon sent null.
public static bool ShowsTerminal(bool? hasTerminal, string vendor) =>
    hasTerminal ?? FamilyFor(vendor) == "pty";

/// Header family, corrected: has_terminal=false cannot distinguish acp/rpc/
/// app-server, so only a CONFLICTING pty guess is overridden (to generic chat);
/// an already-non-PTY family is preserved.
public static string EffectiveFamily(bool? hasTerminal, string vendor) {
    var family = FamilyFor(vendor);
    return hasTerminal == false && family == "pty" ? "rpc" : family;
}
```

- [ ] **Step 4: Run to verify pass** (catalog filter, then full app suite).
- [ ] **Step 5: Commit** — `App: terminal gate projection on HostedHarnessCatalog`.

---

### Task 4: Core `AgentAttachClient` — types, scripted server harness, happy paths

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/AttachOutcome.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/AgentAttachClient.cs`
- Create: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/ScriptedAttachServer.cs`
- Create: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/AgentAttachClientTests.cs`

**Interfaces:**
- Consumes: `FrameCodec` (`WriteAsync`/`ReadAsync`, `Attached`, `AttachedReadOnly` helpers), `FrameType`, `LocalFrame` (all public in Core).
- Produces (the full public API — later tasks fill in behavior, signatures never change):

```csharp
namespace Capacitor.Cli.Core.LocalIpc;

/// Exactly one of these is RunAsync's result. Detached = locally initiated
/// close; Exited = agent process exit; Failed = daemon Error / refusal /
/// protocol failure / pre-attach failure; ConnectionLost = uninitiated
/// transport loss after attach.
public abstract record AttachOutcome {
    public sealed record Detached : AttachOutcome;
    public sealed record Exited(int Code) : AttachOutcome;
    public sealed record Failed(string Message) : AttachOutcome;
    public sealed record ConnectionLost : AttachOutcome;
}

public sealed class AgentAttachClient : IAsyncDisposable {
    public AgentAttachClient(
        string socketPath,
        string agentId,                                   // full 32-hex, never a prefix
        Func<byte[], string?, CancellationToken, Task> onAttachedAsync,  // (snapshot, readOnlyReason: null = read-write, internal token)
        Func<byte[], CancellationToken, Task> onOutputAsync,             // (bytes, internal token)
        Action<string, Exception>? diagnostics = null);   // MUST be fast and non-blocking (runs inline); exception-contained only

    public Task<AttachOutcome> RunAsync(int initialCols, int initialRows, CancellationToken ct);
    public Task SendInputAsync(byte[] bytes);
    public Task ResizeAsync(int cols, int rows);
    public Task DetachAsync();
    public ValueTask DisposeAsync();
}
```

- [ ] **Step 1: Write the scripted server.** A per-test in-proc Unix-socket server that records received frames and plays a script:

```csharp
namespace Capacitor.Cli.Core.Tests.Unit.LocalIpc;

using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;

/// One accepted connection, scripted: records every inbound frame, sends the
/// queued replies when told to. Socket path must come from
/// tmp.GetResolvedPath(...) — sockaddr_un budget.
sealed class ScriptedAttachServer : IAsyncDisposable {
    readonly Socket _listener;
    Socket? _conn;
    NetworkStream? _stream;
    public readonly List<LocalFrame> Received = [];
    public readonly TaskCompletionSource<LocalFrame> FirstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Path { get; }

    public ScriptedAttachServer(string socketPath) {
        Path = socketPath;
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(1);
    }

    public async Task AcceptAndPumpInboundAsync(CancellationToken ct = default) {
        _conn = await _listener.AcceptAsync(ct);
        _stream = new NetworkStream(_conn, ownsSocket: false);
        _ = Task.Run(async () => {
            try {
                while (await FrameCodec.ReadAsync(_stream, ct) is { } f) {
                    lock (Received) Received.Add(f);
                    FirstFrame.TrySetResult(f);
                }
            } catch { /* connection closed by client — fine for a script */ }
        }, ct);
    }

    public Task SendAsync(LocalFrame frame) => FrameCodec.WriteAsync(_stream!, frame);
    public Task SendAttachedAsync(string agentId, byte[] snapshot) => SendAsync(FrameCodec.Attached(agentId, snapshot));
    public Task SendAttachedReadOnlyAsync(string agentId, string reason, byte[] snapshot) => SendAsync(FrameCodec.AttachedReadOnly(agentId, reason, snapshot));
    public Task SendStdoutAsync(byte[] bytes) => SendAsync(LocalFrame.Stdout(bytes));
    public Task SendExitedAsync(int code) => SendAsync(LocalFrame.Exited(code));
    public Task SendErrorAsync(string text) => SendAsync(new LocalFrame(FrameType.Error) { Text = text });
    public void CloseConnection() { _stream?.Dispose(); _conn?.Dispose(); }
    /// For truncation tests: write raw bytes (a partial header/payload), then close.
    public async Task SendRawThenCloseAsync(byte[] raw) { await _stream!.WriteAsync(raw); CloseConnection(); }

    public async ValueTask DisposeAsync() { CloseConnection(); _listener.Dispose(); await Task.CompletedTask; }
}
```

Check `FrameCodec`'s actual helper names/arities against `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs:131-171` before finalizing — `Attached(string, byte[])` and `AttachedReadOnly(string, string, byte[])` exist; `LocalFrame.Stdout/Exited` factories at `LocalFrame.cs:15-20`; `Error` may or may not have a factory (construct with initializer as shown if not).

- [ ] **Step 2: Write the failing happy-path tests:**

```csharp
public class AgentAttachClientTests {
    static readonly string AgentId = new('a', 32);

    static (ScriptedAttachServer Server, TempDir Tmp) NewServer() {
        var tmp = new TempDir("sock");
        var path = tmp.GetResolvedPath("s.sock");
        return (new ScriptedAttachServer(path), tmp);
    }

    sealed class Recorder {
        public readonly List<(byte[] Snapshot, string? Reason)> Attached = [];
        public readonly List<byte[]> Output = [];
        public Func<byte[], string?, CancellationToken, Task> OnAttached => (s, r, _) => { Attached.Add((s, r)); return Task.CompletedTask; };
        public Func<byte[], CancellationToken, Task> OnOutput => (b, _) => { Output.Add(b); return Task.CompletedTask; };
    }

    [Test]
    public async Task Read_write_attach_delivers_snapshot_then_output_then_exit_and_nudges_resize() {
        var (server, tmp) = NewServer();
        await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);

        var run = client.RunAsync(120, 40, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        var first = await server.FirstFrame.Task;              // the opening Attach frame
        await server.SendAttachedAsync(AgentId, [1, 2, 3]);
        await server.SendStdoutAsync([4, 5]);
        await server.SendExitedAsync(7);

        var outcome = await run;

        await Assert.That(first.Type).IsEqualTo(FrameType.Attach);
        await Assert.That(first.Text).IsEqualTo(AgentId);
        await Assert.That(outcome).IsEqualTo(new AttachOutcome.Exited(7));
        await Assert.That(rec.Attached.Single().Snapshot).IsEquivalentTo(new byte[] { 1, 2, 3 });
        await Assert.That(rec.Attached.Single().Reason).IsNull();
        await Assert.That(rec.Output.Single()).IsEquivalentTo(new byte[] { 4, 5 });
        // resize nudge at the initial size, after the read-write Attached:
        var resize = server.Received.Single(f => f.Type == FrameType.Resize);
        await Assert.That(resize.Cols).IsEqualTo((ushort)120);
        await Assert.That(resize.Rows).IsEqualTo((ushort)40);
    }

    [Test]
    public async Task Read_only_attach_carries_the_reason_and_sends_no_resize_nudge() {
        var (server, tmp) = NewServer();
        await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);

        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedReadOnlyAsync(AgentId, "flow participant", [9]);
        await server.SendExitedAsync(0);
        await run;

        await Assert.That(rec.Attached.Single().Reason).IsEqualTo("flow participant");
        await Assert.That(server.Received.Any(f => f.Type == FrameType.Resize)).IsFalse();
    }

    [Test]
    public async Task Error_as_first_reply_settles_failed() {
        var (server, tmp) = NewServer();
        await using var _s = server; using var _t = tmp;
        var rec = new Recorder();
        await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);

        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendErrorAsync("no such agent aaaa…");

        await Assert.That(await run).IsEqualTo(new AttachOutcome.Failed("no such agent aaaa…"));
        await Assert.That(rec.Attached).IsEmpty();
    }

    /// Serial awaited delivery: the pump must not read frame N+1 until the
    /// callback for frame N completed.
    [Test]
    public async Task Output_callbacks_are_awaited_serially() {
        var (server, tmp) = NewServer();
        await using var _s = server; using var _t = tmp;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int concurrent = 0, maxConcurrent = 0, count = 0;
        await using var client = new AgentAttachClient(server.Path, AgentId,
            (_, _, _) => Task.CompletedTask,
            async (_, _) => {
                var c = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, c);
                if (Interlocked.Increment(ref count) == 1) await gate.Task;
                Interlocked.Decrement(ref concurrent);
            });

        var run = client.RunAsync(80, 24, CancellationToken.None);
        await server.AcceptAndPumpInboundAsync();
        await server.SendAttachedAsync(AgentId, []);
        await server.SendStdoutAsync([1]);
        await server.SendStdoutAsync([2]);   // must sit in the socket until the gate opens
        await Task.Delay(100);
        gate.SetResult();
        await server.SendExitedAsync(0);
        await run;

        await Assert.That(maxConcurrent).IsEqualTo(1);
        await Assert.That(count).IsEqualTo(2);
    }
}
```

- [ ] **Step 3: Run to verify failure** — compile errors for the missing types; add the `AttachOutcome` file and an `AgentAttachClient` skeleton whose `RunAsync` returns `new AttachOutcome.ConnectionLost()` unconditionally; re-run; expect assertion failures.

- [ ] **Step 4: Implement the client core.** Full skeleton with the cause slot from day one (later tasks only extend classification):

```csharp
namespace Capacitor.Cli.Core.LocalIpc;

using System.Net.Sockets;

public sealed class AgentAttachClient : IAsyncDisposable {
    // The single linearization point: first CAS winner decides RunAsync's result.
    // Values: AttachOutcome | Exception (callback fault) | _cancelledSentinel.
    object? _cause;
    static readonly object CancelledSentinel = new();

    readonly string _socketPath;
    readonly string _agentId;
    readonly Func<byte[], string?, CancellationToken, Task> _onAttached;
    readonly Func<byte[], CancellationToken, Task> _onOutput;
    readonly Action<string, Exception>? _diagnostics;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly object _sinkLock = new();

    Socket? _socket;
    NetworkStream? _stream;
    CancellationTokenSource? _lifetime;   // linked to the caller token; callbacks get this token
    volatile bool _detachRequested;       // intent, never a cause
    volatile bool _attachedReadWrite;     // true after a read-write Attached
    volatile bool _attachedAny;           // true after either Attached reply
    Task<AttachOutcome>? _run;

    public AgentAttachClient(
            string socketPath, string agentId,
            Func<byte[], string?, CancellationToken, Task> onAttachedAsync,
            Func<byte[], CancellationToken, Task> onOutputAsync,
            Action<string, Exception>? diagnostics = null) {
        _socketPath = socketPath;
        _agentId = agentId;
        _onAttached = onAttachedAsync;
        _onOutput = onOutputAsync;
        _diagnostics = diagnostics;
    }

    bool TryClaim(object cause) => Interlocked.CompareExchange(ref _cause, cause, null) is null;

    // Losing exceptions go here exactly once; expected teardown artifacts are
    // excluded by the callers. Serialized; a throwing sink is contained.
    void Report(string context, Exception ex) {
        if (_diagnostics is null) return;
        lock (_sinkLock) {
            try { _diagnostics(context, ex); } catch { /* contained by contract */ }
        }
    }

    public Task<AttachOutcome> RunAsync(int initialCols, int initialRows, CancellationToken ct) {
        // Dispose/Detach before Run: terminal immediately, no dialing.
        if (_cause is AttachOutcome pre) return Task.FromResult(pre);
        if (_detachRequested) { TryClaim(new AttachOutcome.Detached()); return Task.FromResult((AttachOutcome)_cause!); }
        return _run = RunCoreAsync(initialCols, initialRows, ct);
    }

    async Task<AttachOutcome> RunCoreAsync(int cols, int rows, CancellationToken ct) {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var reg = ct.Register(() => { if (TryClaim(CancelledSentinel)) _lifetime.Cancel(); });
        try {
            _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await _socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct).ConfigureAwait(false);
            _stream = new NetworkStream(_socket, ownsSocket: false);
            await FrameCodec.WriteAsync(_stream, new LocalFrame(FrameType.Attach) { Text = _agentId }).ConfigureAwait(false);

            while (true) {
                var frame = await FrameCodec.ReadAsync(_stream, CancellationToken.None).ConfigureAwait(false);
                if (frame is null) {                                     // clean EOF
                    TryClaim(_detachRequested ? new AttachOutcome.Detached() : new AttachOutcome.ConnectionLost());
                    break;
                }
                switch (frame.Type) {
                    case FrameType.Attached: {
                        var (_, snapshot) = FrameCodec.Attached(frame);
                        _attachedAny = true; _attachedReadWrite = true;
                        await _onAttached(snapshot, null, _lifetime.Token).ConfigureAwait(false);
                        await WriteLockedAsync(SizeFrame(cols, rows)).ConfigureAwait(false);  // repaint nudge
                        break;
                    }
                    case FrameType.AttachedReadOnly: {
                        var (_, reason, snapshot) = FrameCodec.AttachedReadOnly(frame);
                        _attachedAny = true; _attachedReadWrite = false;
                        await _onAttached(snapshot, reason, _lifetime.Token).ConfigureAwait(false);
                        break;                                            // no nudge: never influence the clamp
                    }
                    case FrameType.Stdout:
                        await _onOutput(frame.Bytes!, _lifetime.Token).ConfigureAwait(false);
                        break;
                    case FrameType.Exited:
                        TryClaim(new AttachOutcome.Exited(frame.ExitCode!.Value));
                        goto done;
                    case FrameType.Error:
                        TryClaim(new AttachOutcome.Failed(frame.Text ?? "daemon error"));
                        goto done;
                    default:
                        TryClaim(new AttachOutcome.Failed($"protocol failure: unexpected frame {frame.Type}"));
                        goto done;
                }
            }
            done: ;
        } catch (Exception ex) {
            ClassifyPumpException(ex);      // Task 5 fills this in fully
        } finally {
            CloseTransport();
        }
        return Project();
    }

    // Task 5 completes classification; Task 4 needs only enough for its tests.
    void ClassifyPumpException(Exception ex) {
        if (_detachRequested || _cause is not null) { /* local close or already decided */ }
        else if (!_attachedAny) TryClaim(new AttachOutcome.Failed(ex.Message));
        else TryClaim(new AttachOutcome.ConnectionLost());
        if (_cause is AttachOutcome && _cause is not AttachOutcome.Detached && ex is not OperationCanceledException)
            Report("attach pump", ex);   // refine in Task 7 per loser rules
    }

    AttachOutcome Project() =>
        _cause switch {
            AttachOutcome o => o,
            Exception fault => throw new AttachCallbackException(fault),   // Task 5 refines
            _ when ReferenceEquals(_cause, CancelledSentinel) => throw new OperationCanceledException(),
            _ => new AttachOutcome.ConnectionLost(),
        };

    static LocalFrame SizeFrame(int cols, int rows) => LocalFrame.Resize((ushort)cols, (ushort)rows);

    async Task WriteLockedAsync(LocalFrame frame) {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try { await FrameCodec.WriteAsync(_stream!, frame).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }

    void CloseTransport() { try { _stream?.Dispose(); } catch { } try { _socket?.Dispose(); } catch { } }

    // Tasks 5–7 implement these fully; Task 4 ships minimal versions that keep
    // the signatures compiling.
    public Task SendInputAsync(byte[] bytes) => throw new NotImplementedException("Task 6");
    public Task ResizeAsync(int cols, int rows) => throw new NotImplementedException("Task 6");
    public Task DetachAsync() => throw new NotImplementedException("Task 5");
    public async ValueTask DisposeAsync() {
        _detachRequested = true;
        TryClaim(new AttachOutcome.Detached());
        _lifetime?.Cancel();
        CloseTransport();
        if (_run is { } run) { try { await run.ConfigureAwait(false); } catch { /* Task 5 refines */ } }
    }
}

/// Wraps a callback fault so RunAsync's fault is distinguishable from infrastructure exceptions.
public sealed class AttachCallbackException(Exception inner) : Exception("attach callback failed", inner);
```

Adjust member access against the real `LocalFrame` (`Bytes`, `Text`, `Cols`, `Rows`, `ExitCode` — see `LocalFrame.cs:7-20`) and the tuple shapes of `FrameCodec.Attached/AttachedReadOnly` decode helpers.

- [ ] **Step 5: Run to verify the four tests pass.**
- [ ] **Step 6: AOT check** (Core changed): publish grep prints nothing.
- [ ] **Step 7: Commit** — `Core: AgentAttachClient skeleton — handshake, streaming, outcome slot`.

---

### Task 5: Core client — termination semantics, cause slot completion, lifetime token, `DetachAsync`/`DisposeAsync`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/AgentAttachClient.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/AgentAttachClientTests.cs`

**Interfaces:** unchanged from Task 4. Behavior contract delivered here (spec §2 "termination semantics", "exceptional paths", "cause slot", "lifetime token"):
- `DetachAsync`: records intent (never claims the slot), sends the Detach frame under the write lock; idempotent; a `Detach` write failure resolves `Detached`.
- EOF/close-induced read failure with intent pending → `Detached`; without → `ConnectionLost` (post-attach) / `Failed` (pre-attach, no intent).
- Frame read after intent still wins (`Exited`/`Failed`).
- `DisposeAsync`: claims `Detached` eagerly, cancels the internal token, closes the socket, awaits the pump; never rethrows cancellation, callback faults already reported, or expected close exceptions.
- External cancellation: claims `CancelledSentinel` first, then cancels the internal token; `RunAsync` throws `OperationCanceledException` only when cancellation is the recorded cause.
- Truncation (`EndOfStreamException`) post-attach without intent → `ConnectionLost`; malformed (`InvalidDataException`) → `Failed("protocol failure…")`; connect refusal → `Failed`.

- [ ] **Step 1: Failing tests** (add to `AgentAttachClientTests`):

```csharp
[Test]
public async Task Detach_intent_plus_eof_settles_detached() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);

    await client.DetachAsync();
    server.CloseConnection();                       // daemon closes; no ack

    await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
    await Assert.That(server.Received.Any(f => f.Type == FrameType.Detach)).IsTrue();
}

[Test]
public async Task A_terminal_frame_read_after_detach_intent_still_wins() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);

    await client.DetachAsync();
    await server.SendExitedAsync(3);                // daemon raced: exit after Detach

    await Assert.That(await run).IsEqualTo(new AttachOutcome.Exited(3));
}

[Test]
public async Task Uninitiated_eof_after_attach_is_connection_lost() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    server.CloseConnection();

    await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
}

[Test]
public async Task Mid_header_truncation_after_attach_is_connection_lost() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await server.SendRawThenCloseAsync([0x41, 0x00]);          // stdout type byte + half a length

    await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
}

[Test]
public async Task Connect_refusal_without_intent_is_failed() {
    using var tmp = new TempDir("sock");
    var rec = new Recorder();
    await using var client = new AgentAttachClient(tmp.GetResolvedPath("nobody.sock"), AgentId, rec.OnAttached, rec.OnOutput);

    var outcome = await client.RunAsync(80, 24, CancellationToken.None);

    await Assert.That(outcome).IsAssignableTo<AttachOutcome.Failed>();
}

[Test]
public async Task Dispose_during_blocked_first_reply_settles_detached_without_fault() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.FirstFrame.Task;                   // Attach written, first reply pending

    await client.DisposeAsync();                    // must not throw

    await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
}

[Test]
public async Task Dispose_before_run_makes_a_later_run_return_detached_without_dialing() {
    using var tmp = new TempDir("sock");
    var rec = new Recorder();
    var client = new AgentAttachClient(tmp.GetResolvedPath("nobody.sock"), AgentId, rec.OnAttached, rec.OnOutput);
    await client.DisposeAsync();

    var outcome = await client.RunAsync(80, 24, CancellationToken.None);   // path does not even exist

    await Assert.That(outcome).IsEqualTo(new AttachOutcome.Detached());
}

[Test]
public async Task Caller_cancellation_surfaces_as_oce_and_dispose_does_not_rethrow_it() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    using var cts = new CancellationTokenSource();
    var run = client.RunAsync(80, 24, cts.Token);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);

    cts.Cancel();

    await Assert.ThrowsAsync<OperationCanceledException>(async () => await run);
    await client.DisposeAsync();                    // must complete cleanly
}

[Test]
public async Task Dispose_while_caller_token_uncancelled_exits_a_stuck_callback_via_internal_token() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new AgentAttachClient(server.Path, AgentId,
        (_, _, _) => Task.CompletedTask,
        async (_, ct) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
    var run = client.RunAsync(80, 24, CancellationToken.None);   // external token: none, never cancelled
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await server.SendStdoutAsync([1]);
    await entered.Task;                              // callback is now stuck on the internal token

    await client.DisposeAsync();

    await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
}
```

- [ ] **Step 2: Run to verify failures** (`DetachAsync` NotImplemented, classification gaps).
- [ ] **Step 3: Implement.** Replace the Task-4 placeholders:

```csharp
public async Task DetachAsync() {
    if (_detachRequested) return;                 // idempotent
    _detachRequested = true;
    if (_stream is null) { TryClaim(new AttachOutcome.Detached()); return; }
    try { await WriteLockedAsync(LocalFrame.Detach()).ConfigureAwait(false); }
    catch { TryClaim(new AttachOutcome.Detached()); CloseTransport(); }   // detach write failure = local close
}

public async ValueTask DisposeAsync() {
    _detachRequested = true;
    TryClaim(new AttachOutcome.Detached());       // the one eager local terminalizer
    _lifetime?.Cancel();
    CloseTransport();
    if (_run is { } run) {
        try { await run.ConfigureAwait(false); }
        catch (OperationCanceledException) { }    // retired run's cancellation: never rethrown
        catch (AttachCallbackException) { }       // already reported where it claimed / lost
    }
}
```

And complete `ClassifyPumpException`:

```csharp
void ClassifyPumpException(Exception ex) {
    if (ex is OperationCanceledException && ReferenceEquals(_cause, CancelledSentinel)) return;
    if (_detachRequested || _cause is AttachOutcome.Detached) {
        TryClaim(new AttachOutcome.Detached());   // local close at any phase; expected — not a diagnostic
        return;
    }
    if (ex is InvalidDataException) { TryClaim(new AttachOutcome.Failed($"protocol failure: {ex.Message}")); ReportIfLost("protocol", ex); return; }
    if (!_attachedAny) { TryClaim(new AttachOutcome.Failed(ex.Message)); ReportIfLost("pre-attach", ex); return; }
    TryClaim(new AttachOutcome.ConnectionLost());
    ReportIfLost("transport", ex);
}

// An exception whose cause attempt LOST still gets observed exactly once (Task 7 tests pin this).
void ReportIfLost(string context, Exception ex) {
    if (_cause is AttachOutcome.Detached || ReferenceEquals(_cause, CancelledSentinel)) Report(context, ex);
}
```

`Project()` gains the callback-fault path: when a callback throws, the pump catches it around the `await _onAttached/_onOutput` calls specifically, `TryClaim(ex)`, breaks the loop; `Project` rethrows via `AttachCallbackException` only when the claimed cause **is** that exception; a callback `OperationCanceledException` caused by the internal token is not a fault — it projects the recorded winner (guard: `catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)` → fall through to `Project`).

- [ ] **Step 4: Run all client tests to green.** Then whole Core suite. AOT grep.
- [ ] **Step 5: Commit** — `Core: attach client termination semantics and cause slot`.

---

### Task 6: Core client — outbound methods, semantic invariants, dimension validation

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/AgentAttachClient.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/AgentAttachClientTests.cs`

**Interfaces:** unchanged. Contract (spec §2 outbound): no input/resize before `Attached`; dropped silently after `AttachedReadOnly` and after any terminal cause; no input behind a queued detach; dims `1..=ushort.MaxValue` rejected locally (silently dropped, never sent, never thrown — the client owns invariants, callers are not policed with exceptions); input/resize transport-write failure claims `ConnectionLost`, closes the socket, initiating call does not rethrow.

- [ ] **Step 1: Failing tests:**

```csharp
[Test]
public async Task Input_and_resize_before_attached_are_dropped() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.FirstFrame.Task;

    await client.SendInputAsync([1]);
    await client.ResizeAsync(100, 30);
    await server.SendAttachedAsync(AgentId, []);
    await server.SendExitedAsync(0);
    await run;

    await Assert.That(server.Received.Count(f => f.Type == FrameType.Stdin)).IsEqualTo(0);
    // the only Resize is the post-attach nudge at the run's initial size:
    await Assert.That(server.Received.Count(f => f.Type == FrameType.Resize)).IsEqualTo(1);
    await Assert.That(server.Received.Single(f => f.Type == FrameType.Resize).Cols).IsEqualTo((ushort)80);
}

[Test]
public async Task Explicit_input_and_resize_after_read_only_attach_are_dropped() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedReadOnlyAsync(AgentId, "review", []);
    await Task.Delay(50);

    await client.SendInputAsync([1]);
    await client.ResizeAsync(100, 30);
    await server.SendExitedAsync(0);
    await run;

    await Assert.That(server.Received.Any(f => f.Type is FrameType.Stdin or FrameType.Resize)).IsFalse();
}

[Test]
public async Task No_input_is_written_behind_a_queued_detach() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await Task.Delay(50);

    await client.DetachAsync();
    await client.SendInputAsync([9]);
    server.CloseConnection();
    await run;

    var detachIndex = server.Received.FindIndex(f => f.Type == FrameType.Detach);
    await Assert.That(detachIndex).IsGreaterThanOrEqualTo(0);
    await Assert.That(server.Received.Skip(detachIndex + 1).Any(f => f.Type == FrameType.Stdin)).IsFalse();
}

[Test]
[Arguments(0, 24)]
[Arguments(-1, 24)]
[Arguments(80, 0)]
[Arguments(70000, 24)]
public async Task Invalid_dimensions_are_rejected_locally(int cols, int rows) {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await Task.Delay(50);
    var before = server.Received.Count(f => f.Type == FrameType.Resize);

    await client.ResizeAsync(cols, rows);
    await server.SendExitedAsync(0);
    await run;

    await Assert.That(server.Received.Count(f => f.Type == FrameType.Resize)).IsEqualTo(before);
}

/// Read side held open: the write failure alone must settle the run.
[Test]
public async Task Input_write_failure_settles_connection_lost_without_rethrow_or_hung_pump() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    await using var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await Task.Delay(50);

    server.CloseConnection();                                  // break the transport under the writer
    await client.SendInputAsync([1, 2, 3]);                    // must not throw

    await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
}
```

- [ ] **Step 2: Run to verify failures** (NotImplemented from Task 4 stubs).
- [ ] **Step 3: Implement:**

```csharp
public async Task SendInputAsync(byte[] bytes) {
    if (!_attachedReadWrite || _detachRequested || _cause is not null) return;
    await WriteOutboundAsync(LocalFrame.Stdin(bytes)).ConfigureAwait(false);
}

public async Task ResizeAsync(int cols, int rows) {
    if (!_attachedReadWrite || _detachRequested || _cause is not null) return;
    if (cols is < 1 or > ushort.MaxValue || rows is < 1 or > ushort.MaxValue) return;
    await WriteOutboundAsync(SizeFrame(cols, rows)).ConfigureAwait(false);
}

// Outbound writers claim the slot themselves on transport failure — the read
// side may be blocked; closing the socket completes it and the pump projects.
async Task WriteOutboundAsync(LocalFrame frame) {
    try {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try {
            if (_detachRequested || _cause is not null) return;   // re-check under the lock: nothing behind a queued detach
            await FrameCodec.WriteAsync(_stream!, frame).ConfigureAwait(false);
        } finally { _writeLock.Release(); }
    } catch (Exception ex) {
        if (TryClaim(new AttachOutcome.ConnectionLost())) Report("outbound write", ex);
        else if (_cause is AttachOutcome.Detached || ReferenceEquals(_cause, CancelledSentinel)) Report("outbound write", ex);
        CloseTransport();
    }
}
```

`DetachAsync` from Task 5 already uses `WriteLockedAsync`; change it to take the same under-lock `_cause` re-check style but note: a detach frame IS allowed while `_cause` is null and intent set (it sets intent first), so `DetachAsync` writes via `WriteLockedAsync` directly, not `WriteOutboundAsync`.

- [ ] **Step 4: Run all client tests, Core suite, AOT grep.**
- [ ] **Step 5: Commit** — `Core: attach client outbound invariants`.

---

### Task 7: Core client — diagnostic sink contract and loser matrix

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/AgentAttachClient.cs` (refine `Report`/`ReportIfLost` call sites per the tests)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/AgentAttachClientTests.cs`

**Interfaces:** unchanged. Contract (spec §2 sink + loser matrix): every actual losing exception hits the sink exactly once; cooperative-cancellation and local-close artifacts never reach it; a throwing sink is swallowed; concurrent losers are serialized; callback-fault-vs-Dispose both orderings; input-write-failure-vs-detach both orderings.

- [ ] **Step 1: Failing tests:**

```csharp
sealed class RecordingSink {
    public readonly List<(string Context, Exception Ex)> Entries = [];
    readonly object _gate = new();
    public int MaxConcurrent; int _current;
    public Action<string, Exception> Callback => (c, e) => {
        var now = Interlocked.Increment(ref _current);
        MaxConcurrent = Math.Max(MaxConcurrent, now);
        lock (_gate) Entries.Add((c, e));
        Interlocked.Decrement(ref _current);
    };
}

[Test]
public async Task Routine_dispose_produces_zero_diagnostics() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var sink = new RecordingSink();
    var rec = new Recorder();
    var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput, sink.Callback);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await Task.Delay(50);

    await client.DisposeAsync();
    await run;

    await Assert.That(sink.Entries).IsEmpty();
}

[Test]
public async Task Callback_fault_losing_to_dispose_is_logged_once_and_run_settles_detached() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var sink = new RecordingSink();
    var boom = new InvalidOperationException("render exploded");
    var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    AgentAttachClient client = null!;
    client = new AgentAttachClient(server.Path, AgentId,
        (_, _, _) => Task.CompletedTask,
        async (_, _) => { await disposeStarted.Task; throw boom; },   // fault AFTER dispose claimed
        sink.Callback);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await server.SendStdoutAsync([1]);
    await Task.Delay(50);

    var dispose = client.DisposeAsync();
    disposeStarted.SetResult();
    await dispose;                                             // completes normally, no rethrow

    await Assert.That(await run).IsEqualTo(new AttachOutcome.Detached());
    await Assert.That(sink.Entries.Count(e => ReferenceEquals(e.Ex, boom))).IsEqualTo(1);
}

[Test]
public async Task Callback_fault_claiming_first_faults_run_and_dispose_does_not_rethrow() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var sink = new RecordingSink();
    var boom = new InvalidOperationException("render exploded");
    var client = new AgentAttachClient(server.Path, AgentId,
        (_, _, _) => Task.CompletedTask,
        (_, _) => throw boom,
        sink.Callback);
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await server.SendStdoutAsync([1]);

    var thrown = await Assert.ThrowsAsync<AttachCallbackException>(async () => await run);
    await client.DisposeAsync();                               // completes normally

    await Assert.That(thrown!.InnerException).IsSameReferenceAs(boom);
    // the fault WON — it is the result, not a losing diagnostic:
    await Assert.That(sink.Entries.Any(e => ReferenceEquals(e.Ex, boom))).IsFalse();
}

[Test]
public async Task A_throwing_sink_is_swallowed_and_alters_nothing() {
    var (server, tmp) = NewServer(); await using var _s = server; using var _t = tmp;
    var rec = new Recorder();
    var client = new AgentAttachClient(server.Path, AgentId, rec.OnAttached, rec.OnOutput,
        (_, _) => throw new Exception("sink bug"));
    var run = client.RunAsync(80, 24, CancellationToken.None);
    await server.AcceptAndPumpInboundAsync();
    await server.SendAttachedAsync(AgentId, []);
    await Task.Delay(50);

    server.CloseConnection();
    await client.SendInputAsync([1]);                          // write failure → loser or winner, sink throws either way

    await Assert.That(await run).IsEqualTo(new AttachOutcome.ConnectionLost());
    await client.DisposeAsync();                               // still clean
}
```

- [ ] **Step 2: Run to verify failures.** The Task-5 `ReportIfLost` skeleton likely over- or under-reports; the tests force the exact rule.
- [ ] **Step 3: Implement** by refining the call sites until the matrix holds:
  - `ClassifyPumpException`: local-close/cancellation artifacts (`_detachRequested` true, or the exception is the socket-close `IOException`/`ObjectDisposedException` following a claimed `Detached`) → **no** report. A genuine exception whose claim lost → one report.
  - Callback catch: if `TryClaim(ex)` succeeded, the fault is the result — no report; if it lost (Dispose already claimed) → one report.
  - Keep `Report` serialized under `_sinkLock`, wrapped in try/catch (already so from Task 4).
- [ ] **Step 4: Run all client tests, full Core suite, AOT grep.**
- [ ] **Step 5: Commit** — `Core: attach client diagnostic sink and loser matrix`.

---

### Task 8: Terminal packages, UTF-8 assembler, recorded-transcript test

**Files:**
- Modify: `Directory.Packages.props` (add `SvcSystems.UI.Terminal` 1.1.1, `XTerm.NET` 1.0.16 — both `PackageVersion` entries)
- Modify: `src/Capacitor.App/Capacitor.App.csproj` (add **direct** `PackageReference` to BOTH — the repo has no transitive pinning, a `PackageVersion` line alone pins nothing)
- Create: `src/Capacitor.App/Services/Utf8StreamDecoder.cs`
- Test: `test/Capacitor.App.Tests.Unit/Utf8StreamDecoderTests.cs`, `test/Capacitor.App.Tests.Unit/TerminalTranscriptTests.cs`

**Interfaces:**
- Produces: `sealed class Utf8StreamDecoder` — `string Decode(ReadOnlySpan<byte> bytes)` (incremental, `Decoder`-backed), `string Flush()`. One instance spans snapshot + all frames of one attach attempt.

- [ ] **Step 1: Failing decoder tests:**

```csharp
public class Utf8StreamDecoderTests {
    [Test]
    [Arguments("é")]      // 2-byte
    [Arguments("€")]      // 3-byte
    [Arguments("𝄞")]      // 4-byte
    public async Task Multibyte_characters_split_at_every_boundary_reassemble(string ch) {
        var bytes = System.Text.Encoding.UTF8.GetBytes($"a{ch}b");
        for (var split = 1; split < bytes.Length; split++) {
            var decoder = new Utf8StreamDecoder();
            var text = decoder.Decode(bytes.AsSpan(0, split)) + decoder.Decode(bytes.AsSpan(split)) + decoder.Flush();
            await Assert.That(text).IsEqualTo($"a{ch}b");
        }
    }

    [Test]
    public async Task The_snapshot_live_seam_is_one_stream() {
        var bytes = System.Text.Encoding.UTF8.GetBytes("€");
        var decoder = new Utf8StreamDecoder();
        var snapshotPart = decoder.Decode(bytes.AsSpan(0, 1));   // snapshot ends mid-character
        var livePart = decoder.Decode(bytes.AsSpan(1));          // first live frame completes it
        await Assert.That(snapshotPart + livePart).IsEqualTo("€");
    }

    [Test]
    public async Task Flush_emits_a_replacement_for_a_dangling_partial() {
        var decoder = new Utf8StreamDecoder();
        decoder.Decode(System.Text.Encoding.UTF8.GetBytes("€").AsSpan(0, 1));
        await Assert.That(decoder.Flush()).IsEqualTo("�");
    }
}
```

- [ ] **Step 2: Verify failure, then implement:**

```csharp
namespace Capacitor.App.Services;

using System.Text;

/// One incremental UTF-8 stream per attach attempt: PTY frames split multibyte
/// code points at arbitrary boundaries, and the terminal control's Feed does a
/// fresh GetString per call — this is the single decoder spanning the snapshot
/// and every live frame, flushed only at terminal completion.
public sealed class Utf8StreamDecoder {
    readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public string Decode(ReadOnlySpan<byte> bytes) {
        if (bytes.IsEmpty) return "";
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        var n = _decoder.GetChars(bytes, chars, flush: false);
        return new string(chars, 0, n);
    }

    public string Flush() {
        var chars = new char[4];
        var n = _decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
        return new string(chars, 0, n);
    }
}
```

- [ ] **Step 3: Add the packages.** `Directory.Packages.props`: `<PackageVersion Include="SvcSystems.UI.Terminal" Version="1.1.1" />` and `<PackageVersion Include="XTerm.NET" Version="1.0.16" />`; csproj: `<PackageReference Include="SvcSystems.UI.Terminal" />` and `<PackageReference Include="XTerm.NET" />`. Build the app project. **Then discover the actual control surface** — dump the API before writing the transcript test: `dotnet run` a scratch or use `ilspy`/completion to confirm: the control model type name (`TerminalControlModel`), its `Feed(string)`/`Feed(byte[])` members, the `TerminalOptions.ReflowOnResize` option, the user-input event (keyboard → bytes), and whether the engine's terminal-reply path (`Terminal.Engine.DataReceived` or equivalent) is reachable. Record findings as comments in `TerminalTranscriptTests.cs` — Task 10/11 consume them.
- [ ] **Step 4: Recorded-transcript test** (headless — the model is engine state, not a rendered window):

```csharp
/// The acceptance gate for emulation fidelity: a captured ANSI/TUI stream
/// (colors, cursor addressing, alternate screen) through the decode-and-feed
/// path, ReflowOnResize=false per upstream's own TUI guidance.
[NotInParallel("AvaloniaSession")]
public class TerminalTranscriptTests {
    [Test]
    public async Task A_recorded_tui_transcript_feeds_without_faulting_and_lands_expected_cells() {
        await AvaloniaSession.DispatchAsync(() => {
            var model = /* construct the control model with ReflowOnResize = false, 80x24 */;
            var decoder = new Utf8StreamDecoder();
            // transcript: SGR color, cursor addressing, alt-screen enter/leave, text
            var transcript = "\x1b[2J\x1b[H\x1b[1;31mRED\x1b[0m\x1b[10;5Hmid\x1b[?1049h alt \x1b[?1049l back"u8.ToArray();
            foreach (var chunk in Chunk(transcript, 7))           // deliberately ugly boundaries
                model.Feed(decoder.Decode(chunk));
            model.Feed(decoder.Flush());
            // assert on the engine buffer: "RED" at row 0, "mid" at row 9 col 4, "back" present post alt-screen
            // (exact accessor per the API discovered in Step 3 — buffer/GetLine/Cells)
            return true;
        });
    }
    static IEnumerable<byte[]> Chunk(byte[] data, int size) {
        for (var i = 0; i < data.Length; i += size) yield return data[i..Math.Min(i + size, data.Length)];
    }
}
```

Fill the two commented holes with the real API found in Step 3 — that discovery is part of this task's deliverable, not optional.

- [ ] **Step 5: Run both test files to green; run the full app suite; commit** — `App: terminal packages (direct-pinned), UTF-8 assembler, transcript gate`. Include the license note in the commit body: SvcSystems.UI.Terminal MIT, XTerm.NET MIT, verify with `dotnet list src/Capacitor.App/Capacitor.App.csproj package --include-transitive` and record the resolved graph in the PR description later.

---

### Task 9: App seam — `ITerminalAttachClient`, factory, `TerminalSessionState`

**Files:**
- Create: `src/Capacitor.App/Services/TerminalAttach.cs`
- Test: none of its own (types only, exercised from Task 10 onward) — but it must compile and the real adapter is trivial enough to include here.

**Interfaces:**
- Produces:

```csharp
namespace Capacitor.App.Services;

using Capacitor.Cli.Core.LocalIpc;

/// App-side seam over Core's AgentAttachClient so VM tests script attachment.
public interface ITerminalAttachClient : IAsyncDisposable {
    Task<AttachOutcome> RunAsync(int initialCols, int initialRows, CancellationToken ct);
    Task SendInputAsync(byte[] bytes);
    Task ResizeAsync(int cols, int rows);
    Task DetachAsync();
}

/// One client per attach attempt — the factory is the unit tests' scripting seam.
public delegate ITerminalAttachClient TerminalAttachClientFactory(
    string agentId,
    Func<byte[], string?, CancellationToken, Task> onAttached,
    Func<byte[], CancellationToken, Task> onOutput);

/// Production adapter: Core client, diagnostics to Console.Error (the app's
/// teardown-diagnostic convention — never AppNotifier toasts).
public sealed class CoreTerminalAttachClient(AgentAttachClient inner) : ITerminalAttachClient {
    public static TerminalAttachClientFactory Factory(Func<string> socketPath) =>
        (agentId, onAttached, onOutput) => new CoreTerminalAttachClient(new AgentAttachClient(
            socketPath(), agentId, onAttached, onOutput,
            (ctx, ex) => Console.Error.WriteLine($"kcap: terminal attach {ctx}: {ex.Message}")));
    public Task<AttachOutcome> RunAsync(int c, int r, CancellationToken ct) => inner.RunAsync(c, r, ct);
    public Task SendInputAsync(byte[] b) => inner.SendInputAsync(b);
    public Task ResizeAsync(int c, int r) => inner.ResizeAsync(c, r);
    public Task DetachAsync() => inner.DetachAsync();
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// Deliberately NOT named *Attach*State — AttachStatus/AttachState already
/// describe the daemon status subscription (spec naming note).
public enum TerminalSessionPhase { Resolving, NoTerminal, NotFound, Connecting, Attached, Detached, Exited, Failed, SessionEnded }

public sealed record TerminalSessionState(TerminalSessionPhase Phase, string? Detail = null, bool ReadOnly = false, int? ExitCode = null) {
    public static readonly TerminalSessionState Resolving = new(TerminalSessionPhase.Resolving);
    public static TerminalSessionState NoTerminal(string? familyNote) => new(TerminalSessionPhase.NoTerminal, familyNote);
    public static readonly TerminalSessionState NotFound = new(TerminalSessionPhase.NotFound, "Session not found");
    public static readonly TerminalSessionState Connecting = new(TerminalSessionPhase.Connecting);
    public static TerminalSessionState Attached(string? readOnlyReason) => new(TerminalSessionPhase.Attached, readOnlyReason, ReadOnly: readOnlyReason is not null);
    public static readonly TerminalSessionState DetachedState = new(TerminalSessionPhase.Detached);
    public static TerminalSessionState Exited(int code) => new(TerminalSessionPhase.Exited, ExitCode: code);
    public static TerminalSessionState Failed(string message) => new(TerminalSessionPhase.Failed, message);
    public static readonly TerminalSessionState SessionEnded = new(TerminalSessionPhase.SessionEnded);
}
```

- [ ] **Step 1: Write the file, build the app project, commit** — `App: terminal attach seam and session states`. (No standalone tests — every member is pinned by Tasks 10–12; a task reviewer verifies compilation only.)

---

### Task 10: `TerminalTabViewModel` — Resolving gate, attempt lifecycle, outcome mapping

**Files:**
- Create: `src/Capacitor.App/ViewModels/TerminalTabViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/TerminalTabViewModelTests.cs`
- Test helper: `test/Capacitor.App.Tests.Unit/FakeTerminalAttachClient.cs`

**Interfaces:**
- Consumes: `ITerminalAttachClient`/factory/`TerminalSessionState` (Task 9), `Utf8StreamDecoder` (Task 8), `IDaemonClientService.Agents`, `HostedHarnessCatalog.ShowsTerminal/EffectiveFamily` (Task 3), `RxSchedulers.MainThreadScheduler`, `TimeProvider`.
- Produces:

```csharp
public sealed class TerminalTabViewModel : ReactiveObject {
    public TerminalTabViewModel(
        string agentId, IDaemonClientService daemon, TerminalAttachClientFactory factory,
        Func<ITerminalSurface> surfaceFactory,     // fresh terminal model per attempt (Task 12 provides the real one)
        TimeProvider time);

    public TerminalSessionState State { get; }      // bound; mutated only on the UI scheduler
    public ITerminalSurface? Surface { get; }       // the VM-owned model handle the view binds
    public ReactiveCommand<Unit, Unit> ReattachCommand { get; }   // single-flight
    public ReactiveCommand<Unit, Unit> DetachCommand { get; }
    public ReactiveCommand<Unit, Unit> RetryResolveCommand { get; }
    public Task TeardownAsync();                    // bounded per spec (1s detach / 3s budget) — Task 13's tracker calls this
}

/// Minimal surface the VM drives; the production implementation (Task 12) wraps
/// the SvcSystems control model. Kept App-local so VM tests don't touch Avalonia.
public interface ITerminalSurface {
    void Feed(string text);
    event Action<byte[]>? InputProduced;           // keyboard AND terminal-generated protocol replies
    event Action<int, int>? Resized;
}
```

Resolve budget: **10 s** via the injected `TimeProvider`.

- [ ] **Step 1: Write `FakeTerminalAttachClient`** — scriptable: `TaskCompletionSource<AttachOutcome> Result`, recorded `SentInput`/`Resizes`/`DetachCalls`/`DisposeCalls`, exposed `TriggerAttached(snapshot, reason)` / `TriggerOutput(bytes)` that invoke the captured callbacks, `RunStarted` TCS. The factory fake records every created client.

- [ ] **Step 2: Failing tests, first batch — the Resolving gate** (all `[NotInParallel("AvaloniaSession")]`, inside `AvaloniaSession.WithImmediateRxScheduler`, agents pushed through `FakeDaemonClientService.Agents` with the `Agent(...)` helper pattern from `HomeViewModelTests`, extended with a `hasTerminal` argument):

```csharp
[Test] public async Task Opens_resolving_and_no_client_exists_before_the_first_dto() { /* construct VM for an id absent from cache; assert State.Phase == Resolving && factory.Created.Count == 0 */ }
[Test] public async Task Has_terminal_false_renders_the_note_with_zero_attempts() { /* push DTO hasTerminal:false vendor gemini; assert Phase == NoTerminal, Detail contains "runs over ACP" (from EffectiveFamily "acp"), factory.Created.Count == 0 */ }
[Test] public async Task Has_terminal_true_and_null_pty_fallback_proceed_to_attach() { /* two cases: hasTerminal:true vendor gemini; hasTerminal:null vendor claude — both create exactly one client and enter Connecting */ }
[Test] public async Task Resolve_timeout_is_not_found_and_a_late_dto_is_ignored_until_retry() { /* FakeTimeProvider advance 10s; Phase == NotFound; push DTO afterwards → still NotFound, zero clients; execute RetryResolveCommand → resolves against the now-present DTO */ }
[Test] public async Task Removal_after_first_observation_is_session_ended_not_resolving() { /* push DTO, then remove from cache; Phase == SessionEnded */ }
```

- [ ] **Step 3: Failing tests, second batch — outcome mapping and attempt lifecycle:**

```csharp
[Test] public async Task Read_only_attach_shows_the_reason_and_suppresses_input_and_resize() { /* TriggerAttached(reason:"review"); raise surface.InputProduced + Resized; assert client.SentInput empty, client.Resizes empty, State.ReadOnly true */ }
[Test] public async Task Exited_maps_to_exited_banner_and_connection_lost_maps_to_failed() { /* complete Result with Exited(3) → Phase Exited, ExitCode 3; new VM, complete with ConnectionLost → Phase Failed, Detail mentions lost connection */ }
[Test] public async Task A_background_run_fault_renders_failed_with_reattach() { /* fail Result task with AttachCallbackException(new Exception("boom")) from a background thread; Phase Failed */ }
[Test] public async Task Explicit_detach_stays_in_place_with_single_flight_reattach() { /* DetachCommand → client.DetachCalls == 1; complete Result Detached → Phase Detached (no navigation concept here); ReattachCommand twice quickly → factory.Created grows by exactly 1 */ }
[Test] public async Task Reattach_swaps_in_a_fresh_surface_and_decoder_before_the_snapshot() { /* first attempt: TriggerOutput with "AB"; fail with ConnectionLost; Reattach; second client TriggerAttached(snapshot:"AB") — assert the NEW surface received "AB" exactly once and it is a different ITerminalSurface instance than the first */ }
[Test] public async Task Exited_is_not_overwritten_by_session_ended() { /* complete Exited(0); then remove agent from cache; Phase stays Exited */ }
[Test] public async Task A_retired_attempts_cancellation_mutates_nothing() { /* start attempt 1; begin Reattach (retires it: cancel+dispose); fail attempt 1's Result with OperationCanceledException afterwards; assert State reflects attempt 2 (Connecting), not Failed */ }
[Test] public async Task Utf8_split_across_snapshot_and_frames_renders_whole_characters() { /* TriggerAttached(snapshot: first byte of "€"), TriggerOutput(remaining bytes); surface received "€" */ }
[Test] public async Task Surface_swap_and_state_mutations_happen_on_the_ui_thread() { /* drive Reattach completion from Task.Run; record thread via surfaceFactory + State observer; assert UI-thread per the An_agent_arriving_off_the_UI_thread pattern in HomeViewSmokeTests (thread identity, not absence of throw) */ }
[Test] public async Task Cancel_dispose_orderings_each_yield_one_recorded_state() { /* three cases with the fake client: cancel-before-dispose, dispose-before-cancel, simultaneous (start both from two Task.Run and Task.WhenAll) — each ends in exactly one terminal State transition on the VM, and a retired generation's late completion mutates nothing */ }
[Test] public async Task A_never_completing_detach_write_is_force_closed_within_the_bound() { /* fake client whose DetachAsync never completes; TeardownAsync under FakeTimeProvider: advance 1s → DisposeAsync called (socket close); advance to 3s → TeardownAsync returns; the abandoned detach task later faulting is observed once (recording diagnostic), no further State mutation */ }
[Test] public async Task A_never_completing_awaited_callback_is_released_by_run_token_cancellation() { /* fake client that captures the onOutput callback and invokes it with a token; block the callback on Task.Delay(Infinite, ct); TeardownAsync cancels the attempt → callback task completes cancelled; assert the VM's Surface reference is released (weak-reference goes dead after GC.Collect) or at minimum the callback task completed */ }
```

- [ ] **Step 4: Run to verify failures; implement the VM.** Key internals (write in full):
  - Resolve: subscribe `daemon.Agents.Connect().ObserveOn(RxSchedulers.MainThreadScheduler)`, filter by id; an atomic `int _resolveState` (0 pending / 1 dto-won / 2 timeout-won / 3 disposed) compare-exchanged by the DTO handler, the `TimeProvider` timer callback, and `Dispose` — the loser no-ops. Timeout disposes the subscription.
  - Attempt: `int _attemptGeneration`; each attempt captures its generation; every completion handler checks generation before mutating (`if (gen != _attemptGeneration) return;` — a cancelled retired attempt is silent). One `SemaphoreSlim(1,1)` single-flights attach/reattach; the previous client is `cts.Cancel()` + `await DisposeAsync()` before the next `factory(...)` call.
  - Fresh per attempt: `surface = _surfaceFactory()` and `decoder = new Utf8StreamDecoder()` assigned on the UI scheduler **before** `RunAsync` is started; `OnAttached`/`OnOutput` decode then `await Dispatcher.UIThread.InvokeAsync(() => surface.Feed(text), DispatcherPriority.Default, ct)` — awaited with the callback's ct (the internal token), giving backpressure and cancellable dispatch.
  - Input wiring: `surface.InputProduced += b => _ = client.SendInputAsync(b)` only when not read-only; `Resized` likewise (client also guards — belt and braces).
  - `TeardownAsync()`: generation bump; detach with a 1 s bound (`Task.WhenAny(client.DetachAsync(), Task.Delay(1s, time))`), then `DisposeAsync` immediately, remainder of 3 s awaiting the run task; abandoned run gets `.ContinueWith` observer that writes to `Console.Error` on fault.
- [ ] **Step 5: Green on the VM test file, then full app suite.**
- [ ] **Step 6: Commit** — `App: TerminalTabViewModel — resolve gate, attempt lifecycle, outcome mapping`.

---### Task 11: Terminal surface adapter + input/replies wiring (`ITerminalSurface` production impl)

**Files:**
- Create: `src/Capacitor.App/Services/XtermTerminalSurface.cs`
- Test: extend `test/Capacitor.App.Tests.Unit/TerminalTranscriptTests.cs`

**Interfaces:**
- Consumes: the control-model API discovered in Task 8 Step 3; `ITerminalSurface` (Task 9).
- Produces: `sealed class XtermTerminalSurface : ITerminalSurface` wrapping the SvcSystems control model with `ReflowOnResize = false`; `InputProduced` raised for BOTH keyboard input and terminal-generated protocol replies (the engine data-received path found in Task 8 — if the wrapper hides it, subscribe on the underlying XTerm.NET engine object; this reachability is a spec acceptance item and must not be silently skipped); `Resized` from the control's resize event.

- [ ] **Step 1: Failing test — query/response through the surface:**

```csharp
[Test]
[NotInParallel("AvaloniaSession")]
public async Task A_device_status_query_produces_a_terminal_reply_through_InputProduced() {
    await AvaloniaSession.DispatchAsync(() => {
        var surface = new XtermTerminalSurface(cols: 80, rows: 24);
        var replies = new List<byte[]>();
        surface.InputProduced += replies.Add;

        surface.Feed("\x1b[6n");                          // DSR: report cursor position

        // the engine answers ESC[row;colR on its own:
        return replies.Any(r => System.Text.Encoding.ASCII.GetString(r).Contains(";1R"));
    }).ContinueWith(async t => await Assert.That(t.Result).IsTrue()).Unwrap();
}
```

(Adapt the assertion plumbing to the file's existing `DispatchAsync` style; the substance is: feed DSR, observe the reply bytes.)

- [ ] **Step 2: Verify failure, implement the adapter, verify pass.** The VM's read-only suppression from Task 10 already covers "suppressed in read-only mode" (InputProduced is not forwarded); add one VM-level test there if Task 10 didn't already assert replies specifically.
- [ ] **Step 3: Full app suite; commit** — `App: xterm surface adapter with terminal-reply lane`.

---

### Task 12: `WorkspaceViewModel` + `WorkspaceView` (header, tabs)

**Files:**
- Create: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs`
- Create: `src/Capacitor.App/Views/WorkspaceView.axaml` + `.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/WorkspaceViewModelTests.cs`

**Interfaces:**
- Consumes: `TerminalTabViewModel` (Task 10), `AgentActionService` (`RequestStop(agentId, label, kind)` / `OpenInWeb(agentId)`), `RepoLabel.Leaf`, `HostedHarnessCatalog` (Task 3), `IDaemonClientService.Agents`.
- Produces:

```csharp
public sealed class WorkspaceViewModel : ReactiveObject {
    public WorkspaceViewModel(
        string agentId, IDaemonClientService daemon, AgentActionService actions,
        TerminalAttachClientFactory factory, Func<ITerminalSurface> surfaceFactory, TimeProvider time);
    public string AgentId { get; }
    public string Title { get; }            // "RepoLabel.Leaf(repo) · vendor", live from the cache
    public string RepoLabelText { get; }    // repo leaf; NO branch this slice (spec)
    public string VendorChip { get; }       // vendor + model
    public string FamilyDot { get; }        // EffectiveFamily(hasTerminal, vendor): "pty"|"acp"|"rpc"
    public bool ShowsTerminalTab { get; }   // ShowsTerminal(hasTerminal, vendor)
    public string NoTerminalNote { get; }   // "This session has no terminal" (+ " — runs over ACP"/family when known); no vendor names
    public TerminalTabViewModel Terminal { get; }
    public ReactiveCommand<Unit, Unit> OpenInWebCommand { get; }
    public ReactiveCommand<Unit, Unit> StopCommand { get; }
    public bool SessionEnded { get; }
    public Task TeardownAsync();            // delegates to Terminal.TeardownAsync()
}
```

- [ ] **Step 1: Failing VM tests:** title/repo/chip projection from a pushed DTO; `ShowsTerminalTab` false + note text for `hasTerminal:false` vendor `gemini` (note says "runs over ACP", never "Gemini"); `SessionEnded` when the agent leaves the cache; Stop routes through `AgentActionService.RequestStop` with the DTO's kind (reuse the service's existing test fake pattern from `AgentActionServiceTests`).
- [ ] **Step 2: Implement VM (OAPH projections over `daemon.Agents.Connect()` with `ObserveOn` before binding, per the codified rule).**
- [ ] **Step 3: `WorkspaceView.axaml`** — canvas structure: 56px header row (title over repo label; spacer; vendor chip with family dot; icon buttons bound to `OpenInWebCommand`/`StopCommand`), 42px tab strip (`Terminal` tab when `ShowsTerminalTab`, else the muted note), content area hosting the terminal surface + the state banners (attach banner with Detach button; read-only reason; exited/failed/detached banners with Reattach). Named controls for smoke: `WorkspaceTitle`, `WorkspaceRepo`, `WorkspaceVendorChip`, `TerminalTabButton`, `NoTerminalNote`, `TerminalHost`, `DetachButton`, `ReattachButton`, `BackButton`. Use `KcapCanvasBrush`/`KcapSurfaceBrush`/`KcapBorderBrush`/`KcapMutedBrush`/`KcapTextBrush` resources; DataContext supplied externally per the app convention (header comment saying so).
- [ ] **Step 4: Green, full suite, commit** — `App: WorkspaceViewModel and WorkspaceView`.

---

### Task 13: Workspace teardown tracker

**Files:**
- Create: `src/Capacitor.App/Services/WorkspaceTeardownTracker.cs`
- Test: `test/Capacitor.App.Tests.Unit/WorkspaceTeardownTrackerTests.cs`

**Interfaces:**
- Produces:

```csharp
/// App-lifetime registry of asynchronous workspace teardowns — the piece the
/// synchronous disposal pass cannot express. Sealed at shutdown drain.
public sealed class WorkspaceTeardownTracker(TimeProvider time) {
    /// Registers and starts observing a teardown. Post-seal: the teardown is
    /// executed and observed immediately rather than refused (belt-and-braces —
    /// a path that slips past the shutdown latch must still not hold a socket).
    public void Track(Func<Task> teardown);
    /// Seals atomically (no registration races past the snapshot), awaits all
    /// pending teardowns bounded by 5 seconds total, then returns. Idempotent.
    public Task DrainAsync();
}
```

- [ ] **Step 1: Failing tests:** tracked teardown observed (fault logged once via an injected diagnostic callback — give the ctor an optional `Action<string, Exception>` like the Core sink, `Console.Error` in production); a teardown faulting does not poison the drain (drain still completes; other teardowns run); drain bounded at 5 s under `FakeTimeProvider` with a never-completing teardown (drain returns; the straggler task still gets its observer — complete it later, assert its fault is consumed exactly once and nothing throws); seal atomicity (a `Track` racing `DrainAsync` either joins the drained set or executes immediately post-seal — assert by tracking from another thread in a loop while draining and verifying every teardown ran exactly once); idempotent double-drain.
- [ ] **Step 2: Implement** (lock + `List<Task>` + sealed flag; post-seal `Track` runs the func and attaches the same observer; `DrainAsync` = `Task.WhenAny(Task.WhenAll(snapshot), Delay(5s, time))`).
- [ ] **Step 3: Green, commit** — `App: workspace teardown tracker`.

---

### Task 14: Navigation — `MainWindowViewModel` surface swap, entry points, shutdown latch, exit paths

**Files:**
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Capacitor.App/Views/MainWindow.axaml` + `.axaml.cs`
- Modify: `src/Capacitor.App/ViewModels/HomeViewModel.cs` + `src/Capacitor.App/Views/HomeView.axaml` + `.axaml.cs` (card click)
- Modify: `src/Capacitor.App/Services/MainWindowCoordinator.cs` (close paths start tracked teardown)
- Modify: `src/Capacitor.App/App.axaml.cs` (composition: factory wiring, tracker, shutdown first pass, launch consumer)
- Test: `test/Capacitor.App.Tests.Unit/MainWindowViewModelTests.cs` (extend), `test/Capacitor.App.Tests.Unit/WorkspaceNavigationTests.cs` (new)

**Interfaces:**
- Produces on `MainWindowViewModel`:

```csharp
public WorkspaceViewModel? CurrentWorkspace { get; }        // null = tabbed shell
public int NavigationGeneration { get; }                    // bumped by every navigation, close-to-hide, disposal; latched by shutdown
public void OpenSession(string agentId);                    // rejects when shutdown-latched; bumps generation
public void OpenSessionIfCurrent(string agentId, int generation);  // launch auto-open: no-ops on stale generation
public void CloseWorkspace();                               // starts tracked teardown, swaps to shell
public void LatchShutdown();                                // first shutdown pass: unhook workspace, register teardown, reject future opens
```

- On `HomeViewModel`: ctor gains `Action<string>? openSession = null` (invoked on card click) and the Start path calls the injected `Func<int>? navigationGeneration` + `Action<string, int>? openSessionIfCurrent` — concretely: capture `gen = navigationGeneration()` before `StartAsync`'s launch call; on `outcome.Started` with a well-formed 32-hex `AgentId`, call `openSessionIfCurrent(id, gen)`; a `Started` outcome with null/malformed id sets `StartError = "Launched, but the session id was unusable — open it from the session list."` and opens nothing.
- On `SessionCardViewModel`: add `Id`-based click plumbing in `HomeView.axaml` — wrap the card `Border` in a `Button` (transparent style) with `Click` handler in code-behind calling `vm.OpenSessionRequested(card.Id)`.
- Coordinator: `OnWindowClosing` additionally invokes an injected `Action` (`onCloseToHide`) that the composition root wires to `vm.CloseWorkspace()`; real close path (`QuitInProgress` or discard) triggers the same teardown before discarding.

- [ ] **Step 1: Failing tests** (in `WorkspaceNavigationTests`, `AvaloniaSession` cohort, using fakes for daemon/factory/tracker):

```csharp
[Test] public async Task Open_session_swaps_to_a_workspace_and_close_returns_to_shell() { }
[Test] public async Task Opening_another_session_tears_down_the_previous_workspace_tracked() { /* tracker fake records exactly one teardown; previous workspace's client got DetachAsync+DisposeAsync */ }
[Test] public async Task Intercepted_close_to_hide_tears_down_and_resets_navigation() { /* drive MainWindowCoordinator.OnWindowClosing() itself (construct the coordinator with the wiring, not a direct CloseWorkspace call); assert Detach frame path via the fake client and CurrentWorkspace == null */ }
[Test] public async Task A_stale_generation_launch_success_opens_nothing() { /* capture gen; bump via CloseWorkspace or OpenSession of another id; OpenSessionIfCurrent(staleGen) → CurrentWorkspace unchanged */ }
[Test] public async Task Launch_success_after_close_to_hide_opens_nothing() { }
[Test] public async Task Open_session_after_shutdown_latch_creates_nothing() { /* LatchShutdown(); OpenSession → CurrentWorkspace null, factory.Created empty */ }
[Test] public async Task Shutdown_first_pass_registers_the_live_workspace_before_drain() { /* open workspace; LatchShutdown(); assert tracker received its teardown; DrainAsync completes with client disposed */ }
[Test] public async Task A_window_built_after_shutdown_began_cannot_open_a_workspace() { /* new MainWindowViewModel constructed with the latched shared state → OpenSession no-ops */ }
[Test] public async Task Malformed_launch_agent_id_surfaces_an_error_and_opens_nothing() { /* HomeViewModel path: Started with AgentId null → StartError set, callback not invoked */ }
```

The shutdown latch must be shared state (the tracker or a small `NavigationGate` class owned by the composition root and passed to each `MainWindowViewModel`) so a rebuilt window sees it — implement `NavigationGate` (`int Generation`, `bool ShutdownLatched`, `int Bump()`, `void Latch()`, thread-safe) in `src/Capacitor.App/Services/NavigationGate.cs` as part of this task, with the generation/latch tests above exercising it through the VM.

- [ ] **Step 2: Implement** across the files listed; `MainWindow.axaml` top-level becomes:

```xml
<Panel>
    <ContentControl IsVisible="{Binding CurrentWorkspace, Converter={x:Static ObjectConverters.IsNull}}"> <!-- existing TabControl moves inside --> </ContentControl>
    <views:WorkspaceView DataContext="{Binding CurrentWorkspace}"
                         IsVisible="{Binding CurrentWorkspace, Converter={x:Static ObjectConverters.IsNotNull}}" />
</Panel>
```

(The Back button in `WorkspaceView` raises an event the window code-behind routes to `vm.CloseWorkspace()` — or bind a command injected by `MainWindowViewModel`; prefer the command.) App shutdown sequence in `App.axaml.cs`: at the point where `QuitInProgress` is set (the first pass), call `mainVm?.LatchShutdown()` then `await tracker.DrainAsync()` before the async service disposal continues.

- [ ] **Step 3: Green on the navigation tests, then the FULL app suite** (regressions in MainWindow/Home tests are likely — every existing `MainWindowViewModel` construction gains the new ctor parameters; update the test fixtures by giving the parameters defaults or updating call sites, whichever the existing style prefers — look at how `home:` was added as nullable-with-default and follow it).
- [ ] **Step 4: Commit** — `App: workspace navigation, entry points, shutdown latch`.

---

### Task 15: Smoke tests, suite-wide verification, manual QA

**Files:**
- Create: `test/Capacitor.App.Tests.Unit/WorkspaceViewSmokeTests.cs`
- Modify: none expected

- [ ] **Step 1: Smoke test** per `HomeViewSmokeTests` conventions: host `WorkspaceView` in a `Window` with a scripted `WorkspaceViewModel` (fake factory), resolve every named control from Task 12 (`WorkspaceTitle`, `WorkspaceRepo`, `WorkspaceVendorChip`, `TerminalTabButton`, `NoTerminalNote`, `TerminalHost`, `DetachButton`, `ReattachButton`, `BackButton`); assert the tab/note visibility flip on `ShowsTerminalTab`; assert the detached-banner presentation appears when the state is `Detached`.
- [ ] **Step 2: Full verification battery:**
  - `dotnet build Capacitor.slnx` — 0 errors/warnings.
  - `dotnet test --solution Capacitor.slnx` — only the documented machine-local failures (session-start nudge set; codex schema pin; load-flaky PTY floods, re-run filtered).
  - `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — nothing.
  - `dotnet list src/Capacitor.App/Capacitor.App.csproj package --include-transitive` — record the resolved terminal-package graph for the PR body; verify licenses (MIT expected throughout the SvcSystems/XTerm.NET subtree).
- [ ] **Step 3: Manual QA on this machine (macOS — no CI leg):** `dotnet run --project src/Capacitor.App/Capacitor.App.csproj`; launch a claude session from Home; click its card; verify: terminal renders live output, typing reaches the agent, resize follows the window, Detach/Reattach round-trips with scrollback restored, an ACP session (e.g. gemini) shows the no-terminal note, Back returns Home, close-to-hide then daemon-side `kcap agent ls` shows the agent still running.
- [ ] **Step 4: Commit** — `App: workspace smoke tests`; then push the branch and open the PR (reference AI-2195; spec + plan ride the PR; include the dependency-graph/license note and the manual-QA checklist results in the body).

---

## Self-review notes (kept for executors)

- Spec §2's cause-slot enumeration maps to Tasks 4–7; §3's Resolving/attempt/precedence to Task 10; ownership/exit paths to Tasks 13–14; every §4 test bullet has a home in a task's test list — if you find one missing while executing, add it to the nearest task rather than skipping it.
- Names locked across tasks: `AttachOutcome{.Detached/.Exited/.Failed/.ConnectionLost}`, `AgentAttachClient`, `AttachCallbackException`, `ITerminalAttachClient`, `TerminalAttachClientFactory`, `TerminalSessionState`/`TerminalSessionPhase`, `Utf8StreamDecoder`, `ITerminalSurface`, `XtermTerminalSurface`, `WorkspaceViewModel`, `TerminalTabViewModel`, `WorkspaceTeardownTracker`, `NavigationGate`, `HostedHarnessCatalog.FamilyFor/ShowsTerminal/EffectiveFamily`, `AgentStatusDto.HasTerminal`.
- Task 8 Step 3's API discovery is load-bearing for Tasks 10–12: if the real control API differs from the assumed `Feed(string)`/model shape, adjust `ITerminalSurface`'s production adapter (Task 11), not the VM contract.
