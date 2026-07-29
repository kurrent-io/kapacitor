# Flow-Participant-Aware `kcap agent` Commands Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `kcap agent ls|attach|stop` recognise review and review-flow agents, show them, attach to them read-only, and refuse to stop them without `--force`.

**Architecture:** The daemon already knows each agent's `LaunchKind` and flow identity; it just never tells the CLI. This carries that across the local socket (three new `AgentList` columns) and enforces protection **daemon-side** for both attach (a new `AttachedReadOnly` frame plus a no-input attach loop) and stop (a new `StopV2` frame carrying a force flag, plus a `skipped` status in `StopAck`).

**Tech Stack:** .NET 10, NativeAOT, TUnit on Microsoft Testing Platform, custom length-prefixed binary IPC over a Unix domain socket.

**Spec:** `docs/superpowers/specs/2026-07-29-ai1557-flow-participant-aware-agent-commands-design.md`

## Global Constraints

- **The protected set is `Kind != LaunchKind.Default`** — `Review` and `ReviewFlow`. Plain web-UI-launched agents (`Default`) are deliberately NOT protected.
- **Enforcement is daemon-side.** The CLI may mirror a check for a better message, but the daemon must refuse independently — a stale or hand-rolled client cannot bypass protection.
- **`FrameType` is append-only.** Never renumber or reuse. New values: `StopV2 = 10`, `AttachedReadOnly = 71`.
- **A read-only viewer never resizes the PTY.** It must not be entered into `AgentInstance.ClientDims`, and its `Resize` frames are ignored.
- **Do not touch** the `if (agent.IsPrivate) return;` guard in `HandleStopAgent`, or any server-origin stop behaviour.
- **`AgentRow` parsing accepts 3 or more columns**, defaulting the new ones — a running older daemon must still list correctly.
- Comment style: self-explanatory code over prose; no Linear issue numbers in comments (GitHub numbers like #379 are fine).
- **AOT:** verify with `dotnet publish -c Release`, not `dotnet build`. Note `[]` has no natural type, so `var x = cond ? [] : arr;` will not compile — use an explicit type.
- **Docs land in the same PR**: `README.md` *and* `help-agent.txt`.
- Run tests as executables: `dotnet run --project test/<proj>/<proj>.csproj`. Filter with `--treenode-filter` (glob syntax), **never** `--filter`.
- **Known test baseline:** the unit suite has ~42 pre-existing failures in `CodexHookCommandTests` and the uninstall/`config.toml` area, confirmed at the branch's merge base. The gate is **no NEW failures**; the count wobbles 42-43.

---

### Task 1: Carry and show the agent's kind

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`HandleLocalListAsync`)
- Modify: `src/Capacitor.Cli/Commands/AgentCommand.cs` (`AgentRow`, `FetchAgentsAsync`, `ListAsync`)
- Test: `test/Capacitor.Cli.Tests.Unit/AgentIdResolutionTests.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Produces: `internal readonly record struct AgentRow(string Id, string Status, string Repo, string Kind, string FlowRunId, string FlowRole)`; `internal static AgentRow ParseAgentRow(string line)` on `AgentCommand`; `internal static bool IsProtectedKind(string kind)` on `AgentCommand`; `static string KindText(LaunchKind kind)` on `AgentOrchestrator`.
- Consumes: existing `AgentInstance.Kind` / `.FlowRunId` / `.FlowRole`, and `LaunchKind { Default, Review, ReviewFlow }` from `Capacitor.Cli.Core.Models`.

- [ ] **Step 1: Write the failing CLI parse tests**

Append to `test/Capacitor.Cli.Tests.Unit/AgentIdResolutionTests.cs`, inside the class:

```csharp
    [Test]
    public async Task Row_from_a_current_daemon_carries_kind_and_flow() {
        var row = AgentCommand.ParseAgentRow("ab12\tRunning\t/repo\treview-flow\tflow-7f3a\treviewer");
        await Assert.That(row.Id).IsEqualTo("ab12");
        await Assert.That(row.Status).IsEqualTo("Running");
        await Assert.That(row.Repo).IsEqualTo("/repo");
        await Assert.That(row.Kind).IsEqualTo("review-flow");
        await Assert.That(row.FlowRunId).IsEqualTo("flow-7f3a");
        await Assert.That(row.FlowRole).IsEqualTo("reviewer");
    }

    [Test]
    public async Task Row_from_an_older_daemon_defaults_to_an_unprotected_agent() {
        // An older daemon sends three columns. Treating the missing kind as `agent` is what
        // makes the group keep working against it — protection simply does not engage.
        var row = AgentCommand.ParseAgentRow("ab12\tRunning\t/repo");
        await Assert.That(row.Kind).IsEqualTo("agent");
        await Assert.That(row.FlowRunId).IsEqualTo("");
        await Assert.That(row.FlowRole).IsEqualTo("");
        await Assert.That(AgentCommand.IsProtectedKind(row.Kind)).IsFalse();
    }

    [Test]
    public async Task Review_and_review_flow_are_protected_and_agent_is_not() {
        await Assert.That(AgentCommand.IsProtectedKind("review")).IsTrue();
        await Assert.That(AgentCommand.IsProtectedKind("review-flow")).IsTrue();
        await Assert.That(AgentCommand.IsProtectedKind("agent")).IsFalse();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentIdResolutionTests/*"`
Expected: FAIL — compile error, `ParseAgentRow` and `IsProtectedKind` do not exist.

- [ ] **Step 3: Widen `AgentRow` and add the parser**

In `src/Capacitor.Cli/Commands/AgentCommand.cs`, replace the `AgentRow` declaration:

```csharp
/// One row of the daemon's agent table (`id\tstatus\trepo\tkind\tflowRunId\tflowRole` on the
/// wire). A daemon older than #379 sends only the first three; the rest default.
internal readonly record struct AgentRow(
    string Id, string Status, string Repo, string Kind, string FlowRunId, string FlowRole);
```

Then add to the `AgentCommand` class, next to `ResolveAgentId`:

```csharp
    /// Kinds the CLI refuses to mutate by accident: a reviewer mid-round is not the user's to
    /// type at or stop. Mirrors LaunchKind — anything that is not a plain agent is protected.
    internal static bool IsProtectedKind(string kind) => kind is "review" or "review-flow";

    /// <summary>Tolerates a short row from an older daemon by defaulting the newer columns.</summary>
    internal static AgentRow ParseAgentRow(string line) {
        var p = line.Split('\t');

        return new AgentRow(
            p[0],
            p.Length > 1 ? p[1] : "",
            p.Length > 2 ? p[2] : "",
            p.Length > 3 && p[3].Length > 0 ? p[3] : "agent",
            p.Length > 4 ? p[4] : "",
            p.Length > 5 ? p[5] : "");
    }
```

- [ ] **Step 4: Route `FetchAgentsAsync` through the parser**

In `FetchAgentsAsync`, replace the projection:

```csharp
            return [.. resp.Text.Split('\n')
                .Where(l => l.Length > 0)
                .Select(ParseAgentRow)];
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentIdResolutionTests/*"`
Expected: PASS (10 tests).

- [ ] **Step 6: Write the failing daemon-side list test**

Append to `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`, inside the `AgentOrchestratorVendorTests` partial class:

```csharp
    [Test]
    public async Task Local_list_reports_each_agent_kind_and_flow_identity() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("plain-1");
        orch.SeedAgentForTest("rev-1", kind: LaunchKind.Review);
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        using var client = new DuplexTestStream(new MemoryStream(), new MemoryStream());
        await orch.HandleLocalListAsync(client, default);
        client.WrittenStream.Position = 0;
        var reply = await FrameCodec.ReadAsync(client.WrittenStream, default);

        var rows = reply!.Text.Split('\n').Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p);

        await Assert.That(rows["plain-1"][3]).IsEqualTo("agent");
        await Assert.That(rows["rev-1"][3]).IsEqualTo("review");
        await Assert.That(rows["flow-1"][3]).IsEqualTo("review-flow");
        await Assert.That(rows["flow-1"][4]).IsEqualTo("flow-7f3a");
        await Assert.That(rows["flow-1"][5]).IsEqualTo("reviewer");
    }
```

- [ ] **Step 7: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Local_list_reports*"`
Expected: FAIL — the reply has only three columns, so `rows["plain-1"][3]` throws `IndexOutOfRangeException`.

- [ ] **Step 8: Emit the new columns**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs`, replace `HandleLocalListAsync` and add the mapper:

```csharp
    /// <summary>Reply to a <c>kcap agent ls</c> request with a tab-separated agent table.</summary>
    public Task HandleLocalListAsync(Stream stream, CancellationToken ct) {
        var lines = _agents.Values.Select(a =>
            $"{a.Id}\t{a.Status}\t{a.RepoPath}\t{KindText(a.Kind)}\t{a.FlowRunId ?? ""}\t{a.FlowRole ?? ""}");

        return FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.AgentList) { Text = string.Join('\n', lines) }, ct);
    }

    /// Wire spelling of <see cref="LaunchKind"/>. Kept separate from the enum name so the table
    /// reads as a CLI column rather than a .NET identifier.
    static string KindText(LaunchKind kind) => kind switch {
        LaunchKind.Review     => "review",
        LaunchKind.ReviewFlow => "review-flow",
        _                     => "agent",
    };
```

- [ ] **Step 9: Run it to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Local_list_reports*"`
Expected: PASS.

- [ ] **Step 10: Render the KIND column**

In `AgentCommand.ListAsync`, replace the two output lines:

```csharp
        Console.WriteLine($"{"AGENT",-34} {"STATUS",-10} {"KIND",-12} REPO");
        foreach (var a in agents) {
            var role = a.FlowRole.Length > 0 ? $"  [{a.FlowRole}]" : "";
            Console.WriteLine($"{a.Id,-34} {a.Status,-10} {a.Kind,-12} {a.Repo}{role}");
        }
```

- [ ] **Step 11: Verify the build and the full unit suite**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
```

Expected: build succeeds; no new failures beyond the ~42 baseline.

- [ ] **Step 12: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs src/Capacitor.Cli/Commands/AgentCommand.cs test/Capacitor.Cli.Tests.Unit/AgentIdResolutionTests.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs
git commit -m "feat(agent): carry and show each agent's launch kind"
```

---

### Task 2: `StopV2` and `AttachedReadOnly` frames

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs`

**Interfaces:**
- Produces: `FrameType.StopV2 = 10`, `FrameType.AttachedReadOnly = 71`; `LocalFrame.StopV2(bool force, string agentId)`; `FrameCodec.StopV2(LocalFrame f)` returning `(bool force, string agentId)`; `FrameCodec.AttachedReadOnly(string agentId, string reason, byte[] snapshot)` and its decoder `FrameCodec.AttachedReadOnly(LocalFrame f)` returning `(string agentId, string reason, byte[] snapshot)`.

- [ ] **Step 1: Write the failing round-trip tests**

Append to `test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs`, inside the class:

```csharp
    [Test]
    [Arguments(true,  "ab12")]
    [Arguments(false, "ab12")]
    [Arguments(false, "")]
    public async Task StopV2_round_trips_mode_and_id(bool force, string agentId) {
        var r = await RoundTrip(LocalFrame.StopV2(force, agentId));
        await Assert.That(r.Type).IsEqualTo(FrameType.StopV2);

        var (gotForce, gotId) = FrameCodec.StopV2(r);
        await Assert.That(gotForce).IsEqualTo(force);
        await Assert.That(gotId).IsEqualTo(agentId);
    }

    [Test]
    public async Task AttachedReadOnly_round_trips_id_reason_and_snapshot() {
        var snapshot = new byte[] { 0x1b, 0x5b, 0x41, 0x00, 0xff };
        var r = await RoundTrip(FrameCodec.AttachedReadOnly("ab12", "review-flow reviewer", snapshot));
        await Assert.That(r.Type).IsEqualTo(FrameType.AttachedReadOnly);

        var (id, reason, got) = FrameCodec.AttachedReadOnly(r);
        await Assert.That(id).IsEqualTo("ab12");
        await Assert.That(reason).IsEqualTo("review-flow reviewer");
        await Assert.That(got).IsEquivalentTo(snapshot);
    }

    [Test]
    public async Task AttachedReadOnly_round_trips_an_empty_snapshot() {
        var r = await RoundTrip(FrameCodec.AttachedReadOnly("ab12", "review agent", []));
        var (_, _, got) = FrameCodec.AttachedReadOnly(r);
        await Assert.That(got).IsEmpty();
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/FrameCodecTests/*"`
Expected: FAIL — compile error, `LocalFrame.StopV2` and `FrameCodec.AttachedReadOnly` do not exist.

- [ ] **Step 3: Add the frame types**

In `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, add `StopV2` after `Stop` and `AttachedReadOnly` after `StopAck`:

```csharp
    Stop    = 8,   // stop an agent (Text = agent id; empty = every agent this daemon hosts)
    StopV2  = 10,  // stop with a force flag (see FrameCodec.StopV2); supersedes Stop
```

```csharp
    StopAck    = 70, // acknowledgement for Stop (Text = `id\tstatus` per line)
    AttachedReadOnly = 71, // Attached for a protected agent: id + reason + snapshot, no input accepted
```

- [ ] **Step 4: Teach the codec both frames**

In `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs`, add `StopV2` and `AttachedReadOnly` to the pre-encoded arms of `Encode` and `Decode`, alongside `Attached`/`Spawn`:

```csharp
        FrameType.Attached or FrameType.Spawn
            or FrameType.StopV2 or FrameType.AttachedReadOnly => f.Bytes, // pre-encoded by the helpers below
```

```csharp
        FrameType.Attached or FrameType.Spawn
            or FrameType.StopV2 or FrameType.AttachedReadOnly => new(t) { Bytes = p },
```

Then add the structured helpers next to the existing `Attached` pair:

```csharp
    // --- StopV2 structured payload ---
    public static LocalFrame StopV2(bool force, string agentId) {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(force ? 1 : 0));
        WriteLp(ms, agentId);
        return new(FrameType.StopV2) { Bytes = ms.ToArray(), Text = agentId };
    }
    public static (bool force, string agentId) StopV2(LocalFrame f) {
        var o = 1;
        return (f.Bytes[0] == 1, ReadLp(f.Bytes, ref o));
    }

    // --- AttachedReadOnly structured payload ---
    // Length-prefixed id AND reason before the snapshot: the snapshot is the unbounded tail, so
    // anything appended after it would be painted onto the user's terminal instead of parsed.
    public static LocalFrame AttachedReadOnly(string agentId, string reason, byte[] snapshot) {
        using var ms = new MemoryStream();
        WriteLp(ms, agentId);
        WriteLp(ms, reason);
        ms.Write(snapshot);
        return new(FrameType.AttachedReadOnly) { Bytes = ms.ToArray(), Text = agentId };
    }
    public static (string agentId, string reason, byte[] snapshot) AttachedReadOnly(LocalFrame f) {
        var o = 0;
        var id = ReadLp(f.Bytes, ref o);
        var reason = ReadLp(f.Bytes, ref o);
        return (id, reason, f.Bytes[o..]);
    }
```

- [ ] **Step 5: Add the `LocalFrame` factory**

In `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs`, after the `StopAck` factory:

```csharp
    public static LocalFrame StopV2(bool force, string agentId) => FrameCodec.StopV2(force, agentId);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/FrameCodecTests/*"`
Expected: PASS, including the five new cases.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/ test/Capacitor.Cli.Tests.Unit/FrameCodecTests.cs
git commit -m "feat(ipc): add StopV2 and AttachedReadOnly frames"
```

---

### Task 3: Daemon-side read-only attach

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs` (`HandleLocalAttachAsync`, `AttachClientLoopAsync`)
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Consumes: `FrameCodec.AttachedReadOnly(string, string, byte[])` (Task 2); `AgentInstance.Kind` / `.FlowRunId` / `.FlowRole`.
- Produces: `AttachClientLoopAsync(AgentInstance agent, Stream stream, CancellationToken ct, bool readOnly = false)`; `static string ProtectionReason(AgentInstance agent)` on `AgentOrchestrator`.

- [ ] **Step 1: Write the failing tests**

Append to `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`, inside the `AgentOrchestratorVendorTests` partial class:

```csharp
    [Test]
    public async Task Attaching_to_a_flow_participant_is_read_only() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        // Client sends input, then a resize, then detaches. None of the first two may land.
        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Stdin("hello"u8.ToArray()), default);
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Resize(40, 10), default);
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());

        await orch.HandleLocalAttachAsync("flow-1", client, default);

        client.WrittenStream.Position = 0;
        var first = await FrameCodec.ReadAsync(client.WrittenStream, default);

        await Assert.That(first!.Type).IsEqualTo(FrameType.AttachedReadOnly);
        var (_, reason, _) = FrameCodec.AttachedReadOnly(first);
        await Assert.That(reason).Contains("review-flow");
        await Assert.That(reason).Contains("reviewer");

        // The resize must not have been recorded, so the PTY is never clamped to the viewer.
        await Assert.That(agent.ClientDims).IsEmpty();
    }

    [Test]
    public async Task Attaching_to_a_plain_agent_stays_read_write() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("plain-1");

        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());

        await orch.HandleLocalAttachAsync("plain-1", client, default);

        client.WrittenStream.Position = 0;
        var first = await FrameCodec.ReadAsync(client.WrittenStream, default);
        await Assert.That(first!.Type).IsEqualTo(FrameType.Attached);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Attaching_to*"`
Expected: FAIL — the flow case gets `FrameType.Attached`, not `AttachedReadOnly`.

- [ ] **Step 3: Add the protection reason and route the attach**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs`, replace `HandleLocalAttachAsync`:

```csharp
    /// <summary>Attach an existing agent to a local client (used by <c>kcap agent attach</c>).</summary>
    public Task HandleLocalAttachAsync(string agentId, Stream stream, CancellationToken ct) {
        if (!_agents.TryGetValue(agentId, out var agent))
            return FrameCodec.WriteAsync(stream, LocalFrame.Error($"no such agent {agentId}"), ct);

        // A review or flow agent is addressed through the flow protocol, never by typing at it,
        // so the daemon — not the client — decides this attach carries no input.
        return AttachClientLoopAsync(agent, stream, ct, readOnly: agent.Kind != LaunchKind.Default);
    }

    /// Human-readable "why is this read-only", carried on the AttachedReadOnly frame.
    static string ProtectionReason(AgentInstance agent) {
        var kind = agent.Kind == LaunchKind.ReviewFlow ? "review-flow" : "review";
        var role = string.IsNullOrEmpty(agent.FlowRole) ? "" : $", role {agent.FlowRole}";
        var flow = string.IsNullOrEmpty(agent.FlowRunId) ? "" : $" (flow {agent.FlowRunId}{role})";

        return $"{kind} agent{flow}";
    }
```

- [ ] **Step 4: Make the attach loop honour read-only**

In the same file, change `AttachClientLoopAsync`'s signature and three points inside it.

Signature:

```csharp
    internal async Task AttachClientLoopAsync(
            AgentInstance agent, Stream stream, CancellationToken ct, bool readOnly = false) {
```

Replace the `Attached` send with a conditional one (the line currently reading `await Send(FrameCodec.Attached(agent.Id, snapshot));`):

```csharp
            await Send(readOnly
                ? FrameCodec.AttachedReadOnly(agent.Id, ProtectionReason(agent), snapshot)
                : FrameCodec.Attached(agent.Id, snapshot));
```

Guard the two input arms in the read loop:

```csharp
                    if (f.Type == FrameType.Stdin) {
                        if (readOnly) continue; // protected agent: input is never delivered

                        try {
```

```csharp
                    } else if (f.Type == FrameType.Resize) {
                        // A read-only viewer must not enter ClientDims, or the min-clamp would
                        // let an observer shrink the participant's terminal.
                        if (!readOnly) ApplyResizeClamp(agent, sink, f.Cols, f.Rows);
                    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Attaching_to*"`
Expected: PASS (2 tests).

- [ ] **Step 6: Run the whole local-attach family for regressions**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/*"`
Expected: PASS. The existing spawn-then-attach tests go through `AttachClientLoopAsync` with the new default `readOnly: false` and must be unaffected.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs
git commit -m "feat(daemon): attach to review and flow agents read-only"
```

---

### Task 4: CLI read-only attach banner

**Files:**
- Modify: `src/Capacitor.Cli/Local/LocalAgentClient.cs`

**Interfaces:**
- Consumes: `FrameType.AttachedReadOnly` and `FrameCodec.AttachedReadOnly(LocalFrame)` (Task 2).

- [ ] **Step 1: Handle the frame in the output pump**

In `src/Capacitor.Cli/Local/LocalAgentClient.cs`, add a `readOnly` flag beside the other loop state (next to `var detached = false;`):

```csharp
        var       readOnly   = false; // daemon attached us to a protected agent; input is dropped
```

Then add a case to the `switch (f.Type)` in `outPump`, directly after the `FrameType.Attached` case:

```csharp
                        case FrameType.AttachedReadOnly:
                            readOnly = true;
                            var (_, reason, roSnapshot) = FrameCodec.AttachedReadOnly(f);
                            if (roSnapshot.Length > 0) TerminalRawMode.WriteStdout(roSnapshot, roSnapshot.Length);

                            var banner = "\r\n— read-only: " + reason + ".\r\n"
                                       + "  Input is not delivered — address it with the flow tools."
                                       + " Ctrl-Q d to detach. —\r\n";
                            var bannerBytes = System.Text.Encoding.UTF8.GetBytes(banner);
                            TerminalRawMode.WriteStdout(bannerBytes, bannerBytes.Length);

                            // No SizeFrame nudge: the daemon ignores our size for a protected
                            // agent, so asking for a repaint at our dimensions would mislead.
                            break;
```

- [ ] **Step 2: Stop the input and resize pumps from sending**

In the `resizePump` loop, skip sending while read-only — replace its body's send line:

```csharp
                    if (cur != last) { last = cur; if (!readOnly) await Send(SizeFrame()); }
```

In the `stdinPump` loop, forward nothing but keep the detach sequence working — replace the forward line:

```csharp
                    var (forward, detach) = scanner.Process(buf.AsSpan(0, n));
                    if (forward.Length > 0 && !readOnly) await Send(LocalFrame.Stdin(forward));
```

- [ ] **Step 3: Verify the build**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
```

Expected: builds with no errors.

- [ ] **Step 4: Verify end to end against a live daemon**

```bash
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- daemon start -d
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent start claude --detach
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent attach <first-4-chars>
```

Expected: `agent` lists the agent with KIND `agent`; attaching is read-write as before (typing works, `Ctrl-Q d` detaches). A protected agent cannot be produced locally — `agent start` always creates `LaunchKind.Default` — so the read-only banner is covered by the daemon unit tests rather than this smoke check. Say so in your report rather than fabricating a flow.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Local/LocalAgentClient.cs
git commit -m "feat(cli): print the read-only banner and stop sending input"
```

---

### Task 5: Daemon-side stop protection

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`

**Interfaces:**
- Consumes: `FrameCodec.StopV2(LocalFrame)` → `(bool force, string agentId)` (Task 2); `ProtectionReason(AgentInstance)` (Task 3).
- Produces: `public Task HandleLocalStopV2Async(bool force, string agentId, Stream stream, CancellationToken ct)`.

- [ ] **Step 1: Write the failing tests**

Append to `test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs`, inside the `AgentOrchestratorVendorTests` partial class:

```csharp
    static async Task<LocalFrame?> StopV2AndReadReply(AgentOrchestrator orch, bool force, string agentId) {
        using var client = new DuplexTestStream(new MemoryStream(), new MemoryStream());
        await orch.HandleLocalStopV2Async(force, agentId, client, default);
        client.WrittenStream.Position = 0;

        return await FrameCodec.ReadAsync(client.WrittenStream, default);
    }

    [Test]
    public async Task Stopping_a_flow_participant_without_force_is_refused() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        var reply = await StopV2AndReadReply(orch, force: false, "flow-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).Contains("review-flow");
        await Assert.That(reply.Text).Contains("--force");
        await Assert.That(orch.GetAgentForTest("flow-1")!.Status).IsNotEqualTo("Completed");
    }

    [Test]
    public async Task Stopping_a_flow_participant_with_force_succeeds() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        var reply = await StopV2AndReadReply(orch, force: true, "flow-1");

        await Assert.That(reply!.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(reply.Text).IsEqualTo("flow-1\tstopped");
        await Assert.That(orch.GetAgentForTest("flow-1")!.Status).IsEqualTo("Completed");
    }

    [Test]
    public async Task Stop_all_without_force_skips_protected_agents_and_says_so() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("plain-1");
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        var reply = await StopV2AndReadReply(orch, force: false, "");

        var rows = reply!.Text.Split('\n').Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1]);
        await Assert.That(rows["plain-1"]).IsEqualTo("stopped");
        await Assert.That(rows["flow-1"]).IsEqualTo("skipped");
        await Assert.That(orch.GetAgentForTest("flow-1")!.Status).IsNotEqualTo("Completed");
    }

    [Test]
    public async Task Stop_all_with_force_includes_protected_agents() {
        var server = new TripwireServerConnection();
        await using var orch = BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("plain-1");
        orch.SeedAgentForTest("flow-1", kind: LaunchKind.ReviewFlow, flowRunId: "flow-7f3a", flowRole: "reviewer");

        var reply = await StopV2AndReadReply(orch, force: true, "");

        var rows = reply!.Text.Split('\n').Select(l => l.Split('\t')).ToDictionary(p => p[0], p => p[1]);
        await Assert.That(rows["plain-1"]).IsEqualTo("stopped");
        await Assert.That(rows["flow-1"]).IsEqualTo("stopped");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Stop*"`
Expected: FAIL — compile error, `HandleLocalStopV2Async` does not exist.

- [ ] **Step 3: Add the V2 handler**

In `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs`, add after `HandleLocalStopAsync`:

```csharp
    /// <summary>
    /// `kcap agent stop` with protection. A review or flow agent is refused unless the user
    /// passed --force; a stop-all reports them as `skipped` rather than silently omitting them.
    /// </summary>
    public async Task HandleLocalStopV2Async(bool force, string agentId, Stream stream, CancellationToken ct) {
        if (agentId.Length == 0) {
            var all       = _agents.Values.ToList();
            var eligible  = all.Where(a => force || a.Kind == LaunchKind.Default).ToList();
            var results   = await Task.WhenAll(eligible.Select(StopAgentCoreAsync));
            var stopped   = eligible.Zip(results, (a, ok) => $"{a.Id}\t{StatusText(ok)}");
            var skipped   = all.Except(eligible).Select(a => $"{a.Id}\tskipped");

            await FrameCodec.WriteAsync(stream, LocalFrame.StopAck(string.Join('\n', stopped.Concat(skipped))), ct);

            return;
        }

        if (_agents.TryGetValue(agentId, out var agent)) {
            if (!force && agent.Kind != LaunchKind.Default) {
                await FrameCodec.WriteAsync(stream, LocalFrame.Error(
                    $"{agentId} is a {ProtectionReason(agent)}. Stopping it mid-round leaves the flow "
                  + "without a participant. Pass --force to stop it anyway."), ct);

                return;
            }

            var ok = await StopAgentCoreAsync(agent);
            await FrameCodec.WriteAsync(stream, LocalFrame.StopAck($"{agentId}\t{StatusText(ok)}"), ct);

            return;
        }

        // Not live here — it may be a survivor of a previous daemon incarnation, which the PID
        // record can still reap. Kind is unknown for those, so protection cannot apply.
        var reaped = await TryStopByPidRecordAsync(agentId);

        await FrameCodec.WriteAsync(
            stream,
            reaped ? LocalFrame.StopAck($"{agentId}\t{StatusText(true)}") : LocalFrame.Error($"no such agent {agentId}"),
            ct);
    }
```

- [ ] **Step 4: Route the frame**

In `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs`, add a case after the `Stop` one and update the fallback message:

```csharp
                case FrameType.Stop:   await orchestrator.HandleLocalStopAsync(first.Text, stream, ct); break;
                case FrameType.StopV2: {
                    var (force, id) = FrameCodec.StopV2(first);
                    await orchestrator.HandleLocalStopV2Async(force, id, stream, ct);

                    break;
                }
                case FrameType.Restart: await HandleRestartAsync(first.Text, stream, ct); break;
                default: await FrameCodec.WriteAsync(stream, LocalFrame.Error($"expected Spawn/Attach/List/Stop/StopV2/Restart, got {first.Type}"), ct); break;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentOrchestratorVendorTests/Stop*"`
Expected: PASS (the four new tests plus the existing `Stop`-path ones).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.LocalIpc.cs src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs test/Capacitor.Cli.Tests.Unit/AgentOrchestratorLocalAttachTests.cs
git commit -m "feat(daemon): refuse to stop review and flow agents without force"
```

---

### Task 6: `kcap agent stop --force`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/AgentCommand.cs` (`StopAsync`, `SendStopAsync`)
- Test: `test/Capacitor.Cli.Tests.Unit/AgentCommandRoutingTests.cs`

**Interfaces:**
- Consumes: `LocalFrame.StopV2(bool, string)` (Task 2); `AgentRow.Kind` and `AgentCommand.IsProtectedKind` (Task 1).
- Produces: `internal static (string[] Stoppable, string[] Protected) PartitionByProtection(IReadOnlyList<AgentRow> agents)`.

- [ ] **Step 1: Write the failing partition test**

Append to `test/Capacitor.Cli.Tests.Unit/AgentCommandRoutingTests.cs`, inside the class:

```csharp
    [Test]
    public async Task Protected_agents_are_partitioned_out_for_the_confirmation_prompt() {
        AgentRow[] agents = [
            new("a1", "Running", "/r1", "agent", "", ""),
            new("f1", "Running", "/r2", "review-flow", "flow-7f3a", "reviewer"),
            new("v1", "Running", "/r3", "review", "", ""),
        ];

        var (stoppable, prot) = AgentCommand.PartitionByProtection(agents);

        await Assert.That(stoppable).IsEquivalentTo(new[] { "a1" });
        await Assert.That(prot).IsEquivalentTo(new[] { "f1", "v1" });
    }
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentCommandRoutingTests/*"`
Expected: FAIL — compile error, `PartitionByProtection` does not exist.

- [ ] **Step 3: Add the partition helper**

In `src/Capacitor.Cli/Commands/AgentCommand.cs`, next to `IsProtectedKind`:

```csharp
    /// <summary>Splits an agent list into what `stop --all` will stop and what it will skip.</summary>
    internal static (string[] Stoppable, string[] Protected) PartitionByProtection(IReadOnlyList<AgentRow> agents) => (
        [.. agents.Where(a => !IsProtectedKind(a.Kind)).Select(a => a.Id)],
        [.. agents.Where(a => IsProtectedKind(a.Kind)).Select(a => a.Id)]
    );
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/AgentCommandRoutingTests/*"`
Expected: PASS.

- [ ] **Step 5: Accept `--force` and group the prompt**

In `StopAsync`, add the flag beside the existing `all`/`yes` reads:

```csharp
        var force = args.Contains("--force");
```

Replace the usage block so `--force` appears:

```csharp
        if (!all && !hasId) {
            await Console.Error.WriteLineAsync("usage: kcap agent stop <agent-id> [--force] [--daemon <name>]");
            await Console.Error.WriteLineAsync("       kcap agent stop --all [-y] [--force] [--daemon <name>]");

            return 1;
        }
```

Replace the `--all` listing block (the `Console.WriteLine($"Found {agents.Count} agents:")` loop) with a grouped one:

```csharp
            var (stoppable, protectedIds) = PartitionByProtection(agents);
            var targets = force ? agents.Select(a => a.Id).ToArray() : stoppable;

            if (targets.Length == 0) {
                Console.WriteLine(protectedIds.Length > 0
                    ? $"No agents to stop. {protectedIds.Length} review agent(s) skipped — pass --force to include them."
                    : "No agents.");

                return 0;
            }

            Console.WriteLine($"Found {targets.Length} agents:");
            foreach (var a in agents.Where(a => targets.Contains(a.Id))) Console.WriteLine($"  • {a.Id}  {a.Repo}");

            if (!force && protectedIds.Length > 0) {
                Console.WriteLine($"Skipping {protectedIds.Length} review agent(s) — pass --force to include them:");
                foreach (var a in agents.Where(a => protectedIds.Contains(a.Id)))
                    Console.WriteLine($"  • {a.Id}  {a.Kind}  {a.Repo}");
            }

            if (!yes) {
                await Console.Out.WriteAsync($"Stop {targets.Length}? [y/N] ");
                var reply = await Console.In.ReadLineAsync();

                if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                    await Console.Out.WriteLineAsync("Cancelled.");

                    return 0;
                }
            }

            target = "";
```

- [ ] **Step 6: Send `StopV2` and report `skipped`**

Change the `SendStopAsync` call and signature to carry the flag:

```csharp
        return await SendStopAsync(sock, target, name, force);
```

```csharp
    static async Task<int> SendStopAsync(string sock, string agentId, string daemonName, bool force) {
```

Inside it, replace the write with the V2 frame:

```csharp
            await FrameCodec.WriteAsync(stream, LocalFrame.StopV2(force, agentId), default);
```

And replace the `StopAck` arm so `skipped` reads differently from `stopped`/`failed`:

```csharp
                case FrameType.StopAck:
                    string[] lines = resp.Text.Length == 0 ? [] : resp.Text.Split('\n');
                    if (lines.Length == 0) { Console.WriteLine("No agents."); return 0; }

                    var failed  = 0;
                    var skipped = 0;

                    foreach (var line in lines) {
                        var parts  = line.Split('\t');
                        var id     = parts[0];
                        var status = parts.Length > 1 ? parts[1] : "failed";

                        switch (status) {
                            case "stopped": Console.WriteLine($"Stopped {id}."); break;
                            case "skipped": Console.WriteLine($"Skipped {id} — review agent; pass --force to stop it."); skipped++; break;
                            default:        Console.Error.WriteLine($"Failed to stop {id} — see `kcap daemon logs`."); failed++; break;
                        }
                    }

                    if (skipped > 0)
                        Console.WriteLine($"{skipped} review agent(s) left running — pass --force to stop them.");

                    // Skipping is the documented default, not a failure, so it does not affect
                    // the exit code.
                    return failed > 0 ? 1 : 0;
```

- [ ] **Step 7: Verify the build and the full unit suite**

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
```

Expected: build succeeds; no new failures beyond the ~42 baseline.

- [ ] **Step 8: Verify end to end**

```bash
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- daemon start -d
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent start claude --detach
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent stop --all
```

Expected: the prompt lists the one agent, stopping prints `Stopped <id>.`, exit 0. Protected agents can't be created locally, so their paths are covered by the Task 5 daemon tests — say so in your report rather than inventing a flow.

- [ ] **Step 9: Commit**

```bash
git add src/Capacitor.Cli/Commands/AgentCommand.cs test/Capacitor.Cli.Tests.Unit/AgentCommandRoutingTests.cs
git commit -m "feat(cli): add `agent stop --force` and report skipped agents"
```

---

### Task 7: Documentation and final verification

**Files:**
- Modify: `src/Capacitor.Cli.Core/Resources/help-agent.txt`
- Modify: `README.md`
- Test: `test/Capacitor.Cli.Tests.Integration/AgentVerbDispatchTests.cs`

- [ ] **Step 1: Update the per-command help**

In `src/Capacitor.Cli.Core/Resources/help-agent.txt`, replace the `Options for stop` block:

```
Options for stop:
  --all                   Stop every agent, including --private ones. Review and
                          review-flow agents are skipped unless --force is given.
  --force                 Include review and review-flow agents. Stopping a flow
                          participant mid-round leaves the flow without it.
  --yes, -y               Skip the confirmation prompt for --all
```

And add a section after the `Agent ids:` block:

```
Review and flow agents:
  Agents the daemon runs as part of a review flow (KIND `review-flow`) or a PR
  review (`review`) are not yours to drive directly — they are addressed through
  the flow tools. `attach` gives you a read-only view of one: you see its output,
  your input is not delivered. `stop` refuses unless you pass --force.
```

- [ ] **Step 2: Update the README**

In `README.md`, in the `### Local agents (`kcap agent`)` section, replace the paragraph beginning "Agent ids are long" (it currently carries the `--all` warning added when this gap was deferred, pointing at #379):

```markdown
Agent ids are long, so `attach` and `stop` accept **any unique prefix** — an ambiguous one lists the candidates instead of guessing. `stop --all` includes `--private` agents and prompts for confirmation unless you pass `--yes`/`-y`; a stop that cannot be confirmed prints a per-agent failure line and exits non-zero.

**Agents that aren't yours.** `kcap agent ls` shows a `KIND` column: `agent` for ones you started, `review` for PR-review agents, and `review-flow` for review-flow participants (with their role). The daemon protects the latter two, because they are driven by the flow protocol rather than by you:

- `kcap agent attach` on one is **read-only** — you see its output, your keystrokes are not delivered, and your terminal size is not applied to it.
- `kcap agent stop` on one is **refused** unless you pass `--force`, and `stop --all` skips them and says how many it skipped.

Enforcement is in the daemon, so this holds regardless of client version. It does not apply when talking to a daemon older than this feature — that daemon reports no kind, every agent reads as `agent`, and the protections do not engage until it restarts onto the new binary.
```

- [ ] **Step 3: Add an integration case for the refusal**

Append to `test/Capacitor.Cli.Tests.Integration/AgentVerbDispatchTests.cs`, inside the class:

```csharp
    [Test]
    public async Task Stop_accepts_the_force_flag_without_treating_it_as_an_id() {
        // --force must parse as a flag, not as a positional agent id, or the usage line fires.
        var (_, stderr, _) = await RunCli("agent stop --all --force -y --daemon kcap-dispatch-test-absent");

        await Assert.That(stderr).DoesNotContain("cannot combine an agent id with --all");
        await Assert.That(stderr).DoesNotContain("usage: kcap agent stop");
    }
```

- [ ] **Step 4: Run both suites**

```bash
dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```

Expected: unit has no new failures beyond the ~42 baseline; integration fully passes.

- [ ] **Step 5: Verify AOT publishes clean**

```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```

Expected: no output from either.

- [ ] **Step 6: Verify the help renders**

```bash
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- agent --help
```

Expected: the new `Options for stop` and `Review and flow agents` text appears.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/Resources/help-agent.txt README.md test/Capacitor.Cli.Tests.Integration/AgentVerbDispatchTests.cs
git commit -m "docs: document read-only attach and stop --force for review agents"
```

---

## PR

Reference both trackers in the description (title stays clean):

- `Closes #379`
- `AI-1557`

Call out in the body that protection does **not** engage against a daemon older than this change (it reports no kind), and that `--force` still leaves the flow unaware its participant was stopped — attributing that needs server-side work.
