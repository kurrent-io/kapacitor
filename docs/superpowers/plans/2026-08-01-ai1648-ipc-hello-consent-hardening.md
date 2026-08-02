# Control IPC hello + consent hardening (AI-1648) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** PR 1 of the slice-2 pre-work — versioned hello frame on the daemon control socket, subscriber grace + monotonic deadline discipline for consent prompts, receive-loop unparking with one serial execution domain for server launch/stop commands.

**Architecture:** Extends the slice-1 control IPC (`FrameCodec`/`LocalControlServer`/`LaunchConsentBroker`/`LaunchConsentGate`) and the orchestrator's launch routing. Spec (authoritative, review-hardened over 6 rounds): `docs/superpowers/specs/2026-08-01-slice2-prework-control-ipc-design.md`. Read the spec sections named in each task; where this plan and the spec disagree, the spec governs.

**Tech Stack:** .NET 10, AOT-safe (source-gen JSON only), TUnit on MTP.

## Global Constraints

- `FrameType` values are append-only single bytes: `Hello = 15` (client→daemon), `HelloReply = 75` (daemon→client). The value-9 hole stays. PR 2's 16/76 are NOT claimed here.
- All new JSON payloads: snake_case source-gen context in Core, nulls always written, unmapped members skipped (STJ default — never opt into Disallow). No reflection serialization anywhere.
- `protocol_version` starts at 1; capabilities list for this PR is exactly `["consent/1"]`, assembled next to the `LocalControlServer` routing so a capability cannot be advertised without its handler.
- Coded strings (stable contracts, do not rename): `prompt_no_ui`, `prompt_timeout`, `prompt_user`, `launch_denied_by_owner`. (An earlier draft added `mixed_command_formats`; that whole idea was retracted — see Task 5.)
- Consent deadline discipline: ONE monotonic deadline per prompt path via injected `TimeProvider`; grace = `min(5 s, PromptTimeoutSeconds)` burned from the deadline; every wait duration computed immediately before waiting; zero remaining budget is legal and settles as `prompt_timeout`.
- One execution domain: server launch/stop execution routes through the processor lane per spec §3.3 — nothing is ever refused for its command format.
- The inline-await behavior survives ONLY for a null `_processor` (a pre-settlement server), unchanged for exactly the population that already had it. Introduce no SECOND queue/worker: un-sequenced commands ride the one existing lane.
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

### Task 5: One execution domain for server launch and stop commands (SUPERSEDED PLAN TEXT)

**The plan text that used to live here is superseded and has been deleted.** It described a
daemon-lifetime `mixed_command_formats` latch that refused un-sequenced commands once any sequenced
command had been seen. That rests on a false premise: the shipped kcap-server mixes formats
PERMANENTLY BY DESIGN — the sequenced tuple rides only the review-flow settlement lane, while ordinary
launches and EVERY stop are un-sequenced (spec §1.9). A latch would have bricked every dashboard launch
and silently discarded every stop after the first review flow.

**Authoritative requirement: spec §3.3 in full, plus the §6 bullets "Pump + lane contract",
"One-domain ordering", "Stop admission, coalescing + fault isolation" and "Handler classification".**
Nothing may ever be refused for its FORMAT. Un-sequenced launches and stops are committed onto the SAME
serial lane as sequenced ones via `SequencedCommandProcessor.SubmitUnsequenced`.

Implemented state and its deviations are recorded in
`.superpowers/sdd/2026-08-01-ai1648-ipc-hello-consent-hardening/task-5-rework-report.md`.

### Task 6: Full verification + docs

**Files:**
- Modify: `CLAUDE.md` (kcap-cli — one short entry: hello frame + capabilities, consent grace/deadline discipline, one execution domain for server launch/stop routing (spec §3.3), pointer to the spec)
- The spec already rides this branch (`docs/superpowers/specs/2026-08-01-slice2-prework-control-ipc-design.md`).

- [ ] **Step 1:** Full unit suite run; assert no NEW failures beyond the accepted 42-failure baseline (codex-config/MCP-registry/uninstall area). Run the daemon/socket integration classes individually.
- [ ] **Step 2:** `rg -n "Hello" src/Capacitor.Cli.Core/LocalIpc/` sanity: enum values 15/75, both codec arms, DTOs, no stray reflection serialization.
- [ ] **Step 3:** Update `CLAUDE.md`; commit — `docs: record hello + consent hardening in project index`
