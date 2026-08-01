# Control IPC hello + consent hardening (AI-1648) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PR 1 of the slice-2 pre-work — versioned hello frame on the daemon control socket, subscriber grace + monotonic deadline discipline for consent prompts, sequenced-lane receive-loop unparking with a daemon-lifetime cross-format latch.

**Architecture:** Extends the slice-1 control IPC (`FrameCodec`/`LocalControlServer`/`LaunchConsentBroker`/`LaunchConsentGate`) and the orchestrator's launch routing. Spec (authoritative, review-hardened over 6 rounds): `docs/superpowers/specs/2026-08-01-slice2-prework-control-ipc-design.md`. Read the spec sections named in each task; where this plan and the spec disagree, the spec governs.

**Tech Stack:** .NET 10, AOT-safe (source-gen JSON only), TUnit on MTP.

## Global Constraints

- `FrameType` values are append-only single bytes: `Hello = 15` (client→daemon), `HelloReply = 75` (daemon→client). The value-9 hole stays. PR 2's 16/76 are NOT claimed here.
- All new JSON payloads: snake_case source-gen context in Core, nulls always written, unmapped members skipped (STJ default — never opt into Disallow). No reflection serialization anywhere.
- `protocol_version` starts at 1; capabilities list for this PR is exactly `["consent/1"]`, assembled next to the `LocalControlServer` routing so a capability cannot be advertised without its handler.
- Coded strings (stable contracts, do not rename): `prompt_no_ui`, `prompt_timeout`, `prompt_user`, `launch_denied_by_owner`, new `mixed_command_formats`.
- Consent deadline discipline: ONE monotonic deadline per prompt path via injected `TimeProvider`; grace = `min(5 s, PromptTimeoutSeconds)` burned from the deadline; every wait duration computed immediately before waiting; zero remaining budget is legal and settles as `prompt_timeout`.
- The format latch trips on RECEIPT of a launch/stop carrying ANY of `Epoch`/`Seq`/`CommandId` (the shipped `anySeq` discriminator), BEFORE routing/submission, via `Volatile` write; set once per process, never cleared.
- The legacy lane's inline-await behavior is deliberately UNCHANGED (spec decision 4). Do not introduce any queue/worker for legacy commands.
- Tests: no wall-clock sleeps for consent timing — use `Microsoft.Extensions.TimeProvider.Testing` `FakeTimeProvider`. Real-socket tests use short daemon names (macOS `sockaddr_un` ~104-byte path limit).
- kcap-cli conventions: no Linear issue ids in code comments; clean commit titles. 42 pre-existing unit failures in the codex-config/MCP-registry/uninstall area are an accepted baseline — do not chase them.

---

### Task 1: Hello frames + DTOs + codec arms (Core)

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`
- Create: `src/Capacitor.Cli.Core/LocalIpc/HelloIpc.cs`
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameCodec.cs` (both text-frame switch arms)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalFrame.cs` (reuse pattern)
- Test: `test/Capacitor.Cli.Tests.Unit/FrameCodecHelloTests.cs` (mirror `FrameCodecConsentTests`, same directory)

**Interfaces:**
- Produces: `FrameType.Hello = 15`, `FrameType.HelloReply = 75`; `ClientHelloDto(string? ClientName, string? ClientVersion)`; `HelloReplyDto(int ProtocolVersion, string DaemonVersion, string DaemonName, List<string> Capabilities)`; `HelloIpcJsonContext`; `LocalFrame.HelloJson(FrameType type, string json)`.

- [ ] **Step 1: Write failing round-trip tests** — `Hello` with a `ClientHelloDto` payload, `Hello` with empty payload, `HelloReply` with a full `HelloReplyDto`; assert type + JSON text survive `WriteAsync`→`ReadAsync`, using the same stream pattern `FrameCodecConsentTests` uses. Also an exact-JSON assertion: serialized `HelloReplyDto` is `{"protocol_version":1,"daemon_version":"x","daemon_name":"n","capabilities":["consent/1"]}` (snake_case, nulls written — add a `ClientHelloDto(null, null)` case asserting `{"client_name":null,"client_version":null}`), and a forward-compat decode: a payload with an extra unknown property deserializes without error.
- [ ] **Step 2: Run tests, verify they fail** (enum values missing → compile error first; add stubs as needed to reach red).
- [ ] **Step 3: Implement** — append enum members with comments matching the file's existing style: `Hello = 15` in the client→daemon block (`// optional one-shot: client info (Text = ClientHelloDto JSON; empty valid)`), `HelloReply = 75` in the daemon→client block (`// Text = HelloReplyDto JSON: protocol/daemon version, name, capabilities`). `HelloIpc.cs` mirrors `ConsentIpc.cs` exactly:

```csharp
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the hello frames. snake_case on the wire; shared verbatim by the
/// daemon, the CLI, and the desktop app. Deserialization ignores unmapped members (STJ
/// default) — additive fields must never break an older client.
public sealed record ClientHelloDto(string? ClientName, string? ClientVersion);

public sealed record HelloReplyDto(
    int ProtocolVersion, string DaemonVersion, string DaemonName, List<string> Capabilities);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ClientHelloDto))]
[JsonSerializable(typeof(HelloReplyDto))]
public partial class HelloIpcJsonContext : JsonSerializerContext;
```

  Add `FrameType.Hello or FrameType.HelloReply` to BOTH text arms in `FrameCodec` (`Encode` and `Decode`, alongside the Consent* members). In `LocalFrame`, either extend the `ConsentJson` doc to cover hello or add `public static LocalFrame HelloJson(FrameType type, string json) => new(type) { Text = json };` — prefer the dedicated helper for call-site clarity.
- [ ] **Step 4: Run tests to green.**
- [ ] **Step 5: Commit** — `feat: hello frame pair + DTOs on the local control protocol`

### Task 2: Daemon hello handler + capability assembly

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/LocalControlCapabilities.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs` (route `Hello`, extend default-arm message)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/LocalControlHelloTests.cs` (real socket, mirror the existing consent-IPC socket test rig; SHORT daemon names)

**Interfaces:**
- Consumes: `HelloReplyDto`/`HelloIpcJsonContext` (Task 1); the daemon informational-version helper already in `DaemonRunner` (the `[AssemblyInformationalVersion]` reader near the top of the file — expose it `internal static` if it isn't).
- Produces: `LocalControlCapabilities.Current` → `List<string>` (this PR: `["consent/1"]`), with a class doc stating the invariant: an entry may exist ONLY if `LocalControlServer` routes the corresponding frames — PR 2 appends `"status/1"` when it adds the `StatusSubscribe` handler.

- [ ] **Step 1: Failing tests** — over a real socket: (a) send `Hello` with a `ClientHelloDto` → `HelloReply` with `protocol_version == 1`, non-empty `daemon_version`, `daemon_name == config.Name`, capabilities exactly `["consent/1"]`; (b) send `Hello` with empty payload → identical reply; (c) send `Hello` with malformed JSON payload → identical reply (payload is diagnostics-only); (d) regression: a `List` first frame still returns `AgentList`; (e) an unrecognized frame byte gets the `Error` reply mentioning expected frames (the down-level discovery path).
- [ ] **Step 2: Run to red.**
- [ ] **Step 3: Implement** — in `LocalControlServer.HandleConnectionAsync`, add before the default arm:

```csharp
case FrameType.Hello: await HandleHelloAsync(first.Text, stream, ct); break;
```

```csharp
async Task HandleHelloAsync(string payload, Stream stream, CancellationToken ct) {
    // Payload is diagnostics-only: log-and-forget, never gate on it. Malformed = empty.
    try {
        var hello = JsonSerializer.Deserialize(payload, HelloIpcJsonContext.Default.ClientHelloDto);
        if (hello is { ClientName.Length: > 0 }) LogClientHello(hello.ClientName, hello.ClientVersion ?? "");
    } catch (JsonException) { /* diagnostics-only payload; reply identically */ }
    var reply = new HelloReplyDto(1, DaemonRunner.InformationalVersion, config.Name,
        LocalControlCapabilities.Current);
    var json = JsonSerializer.Serialize(reply, HelloIpcJsonContext.Default.HelloReplyDto);
    await FrameCodec.WriteAsync(stream, LocalFrame.HelloJson(FrameType.HelloReply, json), ct);
}
```

  (Adapt the version accessor to what `DaemonRunner` actually exposes; if the existing member is private, make it `internal static string InformationalVersion` and use it from both call sites.) Extend the default-arm error text with `Hello`.
- [ ] **Step 4: Run to green.**
- [ ] **Step 5: Commit** — `feat: daemon answers hello with version and capability list`

### Task 3: Broker subscriber-arrival signal (generational TCS)

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentBroker.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentGate.cs` (interface `ILaunchConsentPrompter` lives at the top of this file)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentBrokerTests.cs`

**Interfaces:**
- Produces on `ILaunchConsentPrompter`: `Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct);` and the changed `Task<bool?> PromptAsync(LaunchConsentPromptRequest req, TimeSpan timeout, TimeProvider time, CancellationToken ct);` (Task 4 consumes; update all existing fakes/implementations in tests to the new signatures in THIS task so the solution compiles).

Spec: §3.2 "Waiter state machine" — read it before coding; its invariants are the test list.

- [ ] **Step 1: Failing tests** (all with `FakeTimeProvider`, no real sleeps):
  - subscriber already present → returns true synchronously;
  - two concurrent waiters, one `Subscribe()` → both complete true;
  - one waiter's timeout (advance fake time past its wait) does not disturb a second waiter that then sees `Subscribe()` → true;
  - subscribe → unsubscribe → new wait blocks; advancing time past the wait returns false (fresh generation — the old completed source must not satisfy it);
  - expiry/subscribe race: complete the wait's timeout and `Subscribe()` "simultaneously" (subscribe before the waiter's post-timeout recheck) → returns true (arrival wins ties);
  - external `ct` cancellation propagates as `OperationCanceledException` (never false).
- [ ] **Step 2: Run to red** (signature changes break fakes — fix fakes to compile, keep behavior asserts red).
- [ ] **Step 3: Implement** in the broker:

```csharp
    // "Next 0→1 subscriber transition". Created at construction and re-armed on each 1→0
    // transition; COMPLETED (never replaced) by the 0→1 transition in Subscribe. All waiters
    // in one zero-subscriber generation share this instance; a waiter's own timeout or
    // cancellation must never complete or replace it.
    TaskCompletionSource _subscriberArrival = NewArrival();
    static TaskCompletionSource NewArrival() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct) {
        TaskCompletionSource arrival;
        lock (_deliveryGate) {
            if (!_subscribers.IsEmpty) return true;
            arrival = _subscriberArrival;
        }
        try {
            await arrival.Task.WaitAsync(wait, time, ct);
            return true;
        } catch (TimeoutException) {
            // Arrival wins ties: a subscriber that landed inside the race window counts.
            lock (_deliveryGate) return !_subscribers.IsEmpty;
        }
    }
```

  In `Subscribe()` (inside the existing `lock (_deliveryGate)` block, after `_subscribers[id] = ch;`): `if (_subscribers.Count == 1) _subscriberArrival.TrySetResult();`
  In `Unsubscribe(...)`: take `_deliveryGate`, and on the 1→0 transition (`_subscribers.IsEmpty` after removal) re-arm: `_subscriberArrival = NewArrival();` (move the `TryRemove` under the gate so the count transition and re-arm are atomic; `ch.Writer.TryComplete()` can stay outside).
- [ ] **Step 4: Run to green.**
- [ ] **Step 5: Commit** — `feat: broker subscriber-arrival wait with generational lifecycle`

### Task 4: Gate deadline discipline + grace + TimeProvider plumbing

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentGate.cs`
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentBroker.cs` (`PromptAsync` timeout via `TimeProvider`)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (register `TimeProvider.System`; pass to gate)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentGateTests.cs` (+ broker timeout tests)

**Interfaces:**
- Consumes: Task 3's `WaitForSubscriberAsync` + new `PromptAsync` signature.
- Gate ctor gains `TimeProvider time` (DI: `builder.Services.AddSingleton(TimeProvider.System);` next to the consent registrations at `DaemonRunner.cs:224` area; gate registration adds `sp.GetRequiredService<TimeProvider>()`).

Spec: §3.2 "Deadline discipline" + "Cancellation" — the plan below is that section in code.

- [ ] **Step 1: Failing tests** (FakeTimeProvider throughout):
  - default `allow` / `deny` never wait (no time advance needed);
  - `prompt`, no subscriber, none arrives: advancing fake time by `min(5, timeout)` yields the `prompt_no_ui` denial; decision log records it;
  - `prompt`, subscriber arrives inside grace: prompt request is delivered (replay), and the prompt budget equals timeout − elapsed grace (assert via the FakeTimeProvider: an answer after the REMAINING budget expires yields `prompt_timeout` — total wall time never exceeds the policy timeout);
  - virtual time advanced BETWEEN gate entry and the grace wait start (simulate by a prompter fake that advances the clock in `WaitForSubscriberAsync`): the wait shrinks, the deadline does not move;
  - subscriber arrives exactly at/after deadline: `PromptAsync` runs with zero budget → `prompt_timeout` (single fail-closed settlement path — assert the denial source);
  - `RequestedAt` equals the fake clock's wall time at gate entry (anchor consistency — not after the grace);
  - external ct cancellation during grace or prompt: `OperationCanceledException` propagates, NO decision-log record is written.
- [ ] **Step 2: Run to red.**
- [ ] **Step 3: Implement** the prompt path (replacing the `HasSubscriber` short-circuit and the fixed-timeout call):

```csharp
        if (prompter is null)
            return Done(agentId, input, allowed: false, source: "prompt_no_ui",
                detail: "owner approval required and no approval UI is attached to this daemon");

        var timeout     = TimeSpan.FromSeconds(policy.PromptTimeoutSeconds);
        var start       = time.GetTimestamp();          // monotonic anchor; the ONE deadline
        var requestedAt = time.GetUtcNow().ToString("O"); // countdown metadata anchors here too
        TimeSpan Remaining() {
            var left = timeout - time.GetElapsedTime(start);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        var grace = TimeSpan.FromSeconds(Math.Min(5, policy.PromptTimeoutSeconds));
        var wait  = grace < Remaining() ? grace : Remaining(); // computed immediately before waiting
        if (!await prompter.WaitForSubscriberAsync(wait, time, ct))
            return Done(agentId, input, allowed: false, source: "prompt_no_ui",
                detail: $"owner approval required and no approval UI attached within {(int)wait.TotalSeconds}s grace");

        var req = new LaunchConsentPromptRequest(agentId, input.RequesterUserId, input.Kind,
            input.RepoPath, input.Vendor, requestedAt, policy.PromptTimeoutSeconds);
        logger.LogInformation("Launch {AgentId} awaiting owner consent (timeout {Timeout}s)", agentId, req.TimeoutSeconds);
        var answer = await prompter.PromptAsync(req, Remaining(), time, ct);
```

  Broker `PromptAsync` timeout mechanics: replace the linked-CTS `CancelAfter` with the TimeProvider-aware wait, keeping the existing claim semantics EXACTLY (read the class doc first — the ABA/instance-scoped invariants are load-bearing):

```csharp
        try {
            return await tcs.Task.WaitAsync(timeout, time, ct);
        } catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) {
            if (ex is OperationCanceledException && ct.IsCancellationRequested) {
                // External teardown: claim-or-defer exactly like timeout, then rethrow — the
                // gate must abort without fabricating a decision.
                if (!_pending.TryRemove(new KeyValuePair<string, Pending>(req.RequestId, pending)))
                    { try { await tcs.Task; } catch { } }
                throw;
            }
            if (_pending.TryRemove(new KeyValuePair<string, Pending>(req.RequestId, pending)))
                return tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : null;
            return await tcs.Task;
        }
```

  Keep the existing `finally` instance-scoped cleanup unchanged. The gate lets OCE propagate (the launch aborts; the sequenced lane settles it as a lane failure — Task 5 pins that).
- [ ] **Step 4: Run gate + broker + IPC test classes to green** (`--treenode-filter "/*/*/LaunchConsentGateTests/*"` etc., one class per invocation).
- [ ] **Step 5: Commit** — `feat: consent grace window with monotonic deadline discipline`

### Task 5: Sequenced-lane unparking + daemon-lifetime format latch

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/AgentOrchestrator.cs` (`HandleLaunchAgent` ~line 864, `HandleStopAgent`, `HandleStopAgentV2` ~line 1829)
- Test: extend the orchestrator-level consent/launch tests + `SequencedCommandProcessor` wire tests

**Interfaces:**
- Consumes: existing `anySeq` discriminator in `HandleLaunchAgent`; `_server.LaunchFailedAsync`.
- Produces: `mixed_command_formats` coded LaunchFailed reason; latch field.

Spec: §3.3 in full — read it before coding; every claim there is a test below.

- [ ] **Step 1: Failing tests:**
  - pump liveness: a sequenced launch whose consent gate is parked on a never-answered prompt (FakeTimeProvider holds time still) does not block a concurrent `HandleStopAgentV2` for another agent, nor a status-report request;
  - acceptance ordering: two back-to-back sequenced launches submitted in wire order are accepted in order (no non-next rejection);
  - accepted-before-terminal + exactly one terminal answer for: success, consent-denial (gate deny → `CommandRejected` semantic + `LaunchFailed`), lane failure (gate OCE via canceled launch token);
  - latch: sequenced launch received (even one REJECTED for a gap seq / partial tuple) → a subsequent un-seq'd launch gets `LaunchFailed` containing `mixed_command_formats` and never reaches `HandleLaunchAgentCore`; an un-seq'd stop is not executed and logs at Error;
  - latch survives reconnect: simulate by keeping the orchestrator alive across a `ServerConnection` re-registration cycle (or call the handlers directly as the hub would) — a detached still-prompting sequenced launch, then un-seq'd stop AND launch → both rejected;
  - a fresh orchestrator instance starts unlatched;
  - legacy pin: an un-seq'd launch (unlatched daemon) still executes inline — `HandleLaunchAgent` returns only after `HandleLaunchAgentCore` completes (shipped-behavior regression).
- [ ] **Step 2: Run to red.**
- [ ] **Step 3: Implement** in `AgentOrchestrator`:

```csharp
    // Daemon-lifetime cross-format latch: trips on RECEIPT of any sequenced-shaped launch/stop
    // (any of Epoch/Seq/CommandId — the same discriminator that routes), BEFORE submission,
    // independent of that command's fate. Set once, never cleared: a server that speaks the
    // sequenced protocol never legitimately downgrades mid-daemon-life, and detached sequenced
    // execution can outlive its hub connection, so no connection-scoped reset is sound.
    int _sequencedSeen;
```

  `HandleLaunchAgent` becomes:

```csharp
    async Task HandleLaunchAgent(LaunchAgentCommand cmd) {
        var anySeq = cmd.Epoch is not null || cmd.Seq is not null || cmd.CommandId is not null;
        if (anySeq) Volatile.Write(ref _sequencedSeen, 1); // before routing — protocol evidence, not command fate
        if (!anySeq) {
            if (Volatile.Read(ref _sequencedSeen) == 1) {
                await _server.LaunchFailedAsync(cmd.AgentId,
                    "mixed_command_formats: this daemon has seen sequenced commands; un-sequenced launch refused");
                return;
            }
            await HandleLaunchAgentCore(cmd); // legacy lane: inline await IS the backpressure — deliberately unchanged
            return;
        }
        if (_processor is { } proc && cmd.Epoch is { } epoch && cmd.Seq is { } seq && cmd.CommandId is { } cmdId) {
            // Submit ON the pump (acceptance ordering depends on pump serialization) but do NOT
            // await execution: terminal acks are the lane's duty; this continuation only logs.
            var execution = proc.SubmitAsync(
                new SequencedItem(SequencedKind.Launch, epoch, seq, cmdId, cmd.AgentId),
                () => HandleLaunchAgentCore(cmd));
            _ = ObserveDetachedExecution(execution, cmd.AgentId);
            return;
        }
        await _server.LaunchFailedAsync(cmd.AgentId, "Malformed sequenced launch: partial Epoch/Seq/CommandId");
    }

    async Task ObserveDetachedExecution(Task execution, string agentId) {
        try { await execution; }
        catch (Exception ex) { LogDetachedLaunchFault(ex, agentId); }
    }
```

  (Keep the shipped doc comment on `HandleLaunchAgent`, amended for the latch + detach; add the `LoggerMessage` for `LogDetachedLaunchFault`.) In `HandleStopAgentV2`: same `anySeq` receipt-write before routing; in its legacy-fallback arm and in `HandleStopAgent`, check the latch — latched → log Error (`LogMixedFormatStopDiscarded`) and return without executing (no reply surface exists for a legacy stop). Preserve the shipped malformed-partial-tuple arm byte-for-byte.
- [ ] **Step 4: Run the touched test classes to green;** re-run `SequencedSettlement`-area classes as regression.
- [ ] **Step 5: Commit** — `feat: unpark receive loop for sequenced launches behind a daemon-lifetime format latch`

### Task 6: Full verification + docs

**Files:**
- Modify: `CLAUDE.md` (kcap-cli — one short entry: hello frame + capabilities, consent grace/deadline discipline, sequenced unparking + latch, pointer to the spec)
- The spec already rides this branch (`docs/superpowers/specs/2026-08-01-slice2-prework-control-ipc-design.md`).

- [ ] **Step 1:** Full unit suite run; assert no NEW failures beyond the accepted 42-failure baseline (codex-config/MCP-registry/uninstall area). Run the daemon/socket integration classes individually.
- [ ] **Step 2:** `rg -n "Hello" src/Capacitor.Cli.Core/LocalIpc/` sanity: enum values 15/75, both codec arms, DTOs, no stray reflection serialization.
- [ ] **Step 3:** Update `CLAUDE.md`; commit — `docs: record hello + consent hardening in project index`
