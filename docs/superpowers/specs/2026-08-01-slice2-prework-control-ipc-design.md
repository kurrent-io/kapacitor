# Slice-2 pre-work: control IPC hello, consent hardening, supervision surface

**Status:** Approved design, pending user spec review. Child of the desktop-supervisor umbrella
([2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md), §9).
One shared spec, two PRs, **merge-ordered**:

- **PR 1 (AI-1648):** versioned hello + consent-window hardening. Merges first.
- **PR 2 (AI-1649):** supervision IPC — daemon state, live agent list, stop. Builds on PR 1's
  hello/capability machinery; rebases on it.

Both are kcap-cli only (daemon + `Capacitor.Cli.Core`), headless-complete, and land behind
nothing. The desktop app (AI-1650) is their first consumer.

## 1. Current state (grounding)

Facts this design builds on, verified against `main` after the slice-1 merge:

1. `FrameType` values are append-only single bytes. Client→daemon used: 1–8, 10–14 (9 is a
   permanent hole). Daemon→client used: 64–74. Next free: **15/16** and **75/76**.
2. `LocalControlServer` routes each connection on its FIRST frame. Consent subscribe
   (`ConsentSubscribe`) is a long-lived connection with an EOF watcher; everything else is
   one-shot or attach-style.
3. `LaunchConsentBroker.Subscribe()` atomically (under `_deliveryGate`) replays all pending
   prompts to a new subscriber, then adds it — so a prompt raised *before* the app subscribes IS
   delivered, provided the pending entry exists at subscribe time.
4. `LaunchConsentGate` short-circuits `prompt` with no subscriber to an immediate
   `prompt_no_ui` deny — the pending entry is never created, so item 3's replay has nothing to
   deliver. This is the race being closed: a launch arriving while the app is starting or
   reconnecting dies instantly even though a UI is milliseconds away.
5. `ConsentPendingDto` already carries `RequestedAt` + `TimeoutSeconds`, and app and daemon share
   the same machine clock. **The absolute prompt deadline is already computable client-side; no
   new consent field is needed.** (The umbrella's "wire deadline hint" concern reduces to the
   receive-loop problem below plus a documented non-goal, §3.4.)
6. The SignalR client awaits `_hub.On` handlers serially. `HandleLaunchAgent` awaits launch
   execution end-to-end — legacy lane inline, sequenced lane via the execution-completion task
   `SubmitAsync` returns. A consent prompt therefore parks EVERY server→daemon command on the
   connection for up to the prompt timeout (45 s default), not just the launch being prompted.
7. `SequencedCommandProcessor`: `SubmitAsync` performs acceptance/rejection **synchronously
   before returning** — `SubmitLocked` runs under `_lock` with no awaits, and the
   accepted/rejected/duplicate answer sends happen before the method returns; only execution
   completion is deferred (the returned task). Execution runs on a single-reader serial lane
   (`RunLaneAsync`), **which owns terminal CommandAck/CommandRejected emission** — terminal
   settlement never depended on the pump awaiting the returned task. `SequencedKind { Launch,
   Stop }` — stops share the lane with launches, so the lane already serializes a stop behind a
   multi-second launch execution today. Consent extends that existing serialization; it does not
   create it. The lane's memory is bounded by design: `_cache.Count >= _cacheBound` (256)
   rejects further submissions with the coded `Backpressure` answer, identity preserved.
   Server-side, launches are admitted **one at a time per daemon** — the kcap-server settlement
   admission (AI-1526) holds the daemon's single sequenced slot for the duration of a launch —
   so at most ONE prompt-capable launch can precede any queued sequenced stop.
8. Legacy (un-seq'd) `StopAgent` is fire-and-forget on the wire — **no reply or failure surface
   exists** for it; the server learns outcomes from agent state, status reports, and its
   reconciliation lanes.
9. The local socket already has `StopV2` (force flag, protected-kind semantics). The app needs
   no new stop machinery.
10. `AgentInstance` does not store the requester — slice 1 passes `cmd.RequesterUserId` through
   to the consent gate only. The supervision payload's `requester` column needs a new field.
11. `_agents` is a `ConcurrentDictionary<string, AgentInstance>` (enumeration is exception-free
    and weakly consistent); `ActiveCount` counts entries whose `Status` is in the daemon's
    active set. `LaunchConsentStore` clamps `PromptTimeoutSeconds` to **[5, 300]**.
12. Server-side (kcap-server, already shipped and tested there): an agent the server no longer
    tracks is reclaimed by existing machinery — the daemon status report cross-check with
    physical retry-until-gone stop (AI-1391 S3b/S3c), the stale-reviewer sweep for terminal flow
    runs (AI-1313 S3a), and `OrphanedHostedAgentReaper` for hosted agents whose session ended
    (AI-1185). This spec cites these as the reconciliation backstop; it does not modify them.
13. Slice 1's `ILaunchConsentPrompter.PromptAsync` takes a **`TimeSpan`** budget — sub-second
    remaining budgets survive end-to-end; the store's [5, 300] clamp applies to the persisted
    policy value only, never to a computed remaining budget. `LaunchConsentPromptRequest`
    carries `RequestedAt`/`TimeoutSeconds` as the client's countdown metadata.

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | Hello is an **optional** one-shot frame. Existing clients that open with `Spawn`/`List`/`ConsentSubscribe`/… are untouched; nothing ever requires hello-first. |
| 2 | Feature discovery is **capability strings**, not protocol-version comparisons. `protocol_version` exists for a future framing break only; it starts at 1 and nothing gates on it yet. |
| 3 | The no-UI instant deny gains a bounded **subscriber grace** — `min(5 s, prompt timeout)` — burned from a single **monotonic absolute deadline** established at prompt-path entry, not added to it. The coded deny reason stays `prompt_no_ui` (stable contract); the decision log records that grace elapsed. |
| 4 | Receive-loop unparking applies to the **sequenced lane only**: the pump stops awaiting sequenced execution (acceptance stays synchronous on the pump). The legacy lane **deliberately keeps the shipped inline-await** — the await IS the existing backpressure, and replacing it with a queueing domain would demand its own memory bounds, cumulative-delay story, and cross-epoch ordering semantics, all for a shrinking pre-settlement server population. A one-directional **daemon-lifetime** format latch rejects un-seq'd launch/stop after any sequenced traffic — triggered on RECEIPT of a sequenced-shaped command (the shipped `anySeq` discriminator), before submission, independent of that command's fate (§3.3). |
| 5 | Supervision pushes are **full snapshots** driven by a **monotonic change generation** with per-subscriber cursors (§4.2) — never per-event deltas, never a consumable one-shot signal — so N subscribers each converge and a missed pulse cannot desync anyone. |
| 6 | The server→daemon "patience hint" (clamping prompt timeout to the server's launch-admission window) stays **out of scope** (cross-repo). The late-launch residual is bounded and reconciled: the prompt timeout is capped at 300 s by the existing store clamp, and an agent launched after the server abandoned its launch is reclaimed by the server's shipped reconciliation lanes (§1.12). |
| 7 | **PR order is pinned: PR 1 before PR 2.** PR 2's `status/1` capability is advertised through the hello machinery PR 1 introduces; the capability list is built from what the binary actually serves, so each build advertises exactly its own surface. |

## 3. PR 1 (AI-1648): hello + consent hardening

### 3.1 Versioned hello

New frames, values append-only:

- `Hello = 15` (client→daemon). Text payload: optional `ClientHelloDto` JSON
  (`client_name`, `client_version`) — logged for diagnostics, never trusted for anything.
  An empty payload is valid.
- `HelloReply = 75` (daemon→client). Text payload `HelloReplyDto`:

```json
{
  "protocol_version": 1,
  "daemon_version": "0.12.3",
  "daemon_name": "main",
  "capabilities": ["consent/1", "status/1"]
}
```

`daemon_version` comes from the same source as the daemon's server registration (assembly
informational version). `capabilities` is the discovery surface: the app enables its consent UX
iff `consent/1` is present and its agents view iff `status/1` is present; **unknown capability
strings are ignored by clients** (forward compat). PR 1 ships `["consent/1"]`; PR 2 appends
`"status/1"` when it wires the `StatusSubscribe` handler — the list is assembled next to the
`LocalControlServer` routing table so a capability cannot be advertised without its handler
existing, and each build advertises exactly what it serves.

`LocalControlServer` answers `Hello` and closes, same one-shot shape as `List`. An unknown
frame type still gets the existing `Error` reply — that IS the down-level daemon signal for an
app talking to a pre-hello binary (tested, §6).

### 3.2 Subscriber grace (closes the `prompt_no_ui` race)

**Deadline discipline.** At prompt-path entry the gate establishes one absolute deadline from a
monotonic time source (`TimeProvider`, injectable for tests):

```
start    = time.GetTimestamp()
deadline = start + PromptTimeoutSeconds            // monotonic, established BEFORE any waiting
grace    = min(5 s, PromptTimeoutSeconds)
```

Every subsequent budget computation derives from `deadline` and monotonic now — never from
wall-clock arithmetic, a stored duration, or an accumulated "elapsed" variable:

- the grace wait's duration is computed **immediately before waiting** as
  `min(grace, max(0, deadline − now))` — setup/scheduling time between deadline creation and
  the wait can never push the wait past the deadline;
- immediately before `PromptAsync`, remaining = `max(0, deadline − now)`, passed as a
  `TimeSpan` (sub-second budgets survive, §1.13; no integral-seconds conversion, no re-clamp
  to the store's policy minimum — the [5, 300] clamp applies only to the persisted policy
  value);
- **zero remaining is not a special case** — `PromptAsync` runs with a zero budget and settles
  as the standard timeout denial (`prompt_timeout`), keeping one fail-closed settlement path;
- **all timed waits on this path — the grace wait and `PromptAsync`'s timeout — run against
  the same injected `TimeProvider`** (the `WaitAsync`/`CancelAfter` overloads that accept a
  `TimeProvider`), so a controlled provider deterministically drives every timeout in tests.

**Client countdown anchoring (best-effort).** `LaunchConsentPromptRequest.RequestedAt` is
stamped at prompt-path entry — the same instant the monotonic deadline anchors — and
`TimeoutSeconds` is the policy timeout, so `RequestedAt + TimeoutSeconds` tracks the true
deadline as closely as wall-clock metadata can, including after a grace wait. It is **display
metadata, not an enforcement input**: enforcement is monotonic and daemon-side only, so a
wall-clock step (NTP/manual) after entry can skew the displayed countdown without weakening
fail-closed settlement — a resolve that arrives after the daemon's deadline is answered by the
existing "no pending consent request with that id" ack, never raced.

**Waiter state machine.** `LaunchConsentBroker` gains a subscriber-arrival signal with an
explicitly generational lifecycle, all transitions under the existing `_deliveryGate`:

- The broker holds one shared `TaskCompletionSource` (`RunContinuationsAsynchronously`)
  representing "the next 0→1 subscriber transition". It is created when the subscriber count
  falls to zero (and at construction), and **completed — never replaced — by the 0→1 transition
  in `Subscribe()`**. The 1→0 transition in `Unsubscribe()` installs a fresh incomplete source.
- `Task<bool> WaitForSubscriberAsync(TimeSpan wait, TimeProvider time, CancellationToken ct)`:
  - under `_deliveryGate`: if a subscriber exists, return `true` immediately; otherwise capture
    the CURRENT shared source.
  - await it with the TimeProvider-aware `WaitAsync(wait, time, ct)` (the caller passes the
    deadline-derived duration per the discipline above). All concurrent waiters in the same
    zero-subscriber generation share the one source; a waiter's timeout or cancellation
    **never completes or replaces** the shared source (it only stops that waiter's wait).
  - on timeout, re-check under `_deliveryGate` before reporting `false`: if a subscriber
    arrived in the race window, return `true`. Arrival wins ties.
- A completed source from an earlier generation can never satisfy a later zero-subscriber wait,
  because entering a zero-subscriber period is exactly what installs a fresh incomplete source.

`LaunchConsentGate` prompt path becomes:

```
wait    = min(grace, max(0, deadline − now))       // computed immediately before waiting
arrived = await prompter.WaitForSubscriberAsync(wait, time, ct)
if (!arrived) → deny prompt_no_ui                  // same coded reason, log notes grace ran
else → PromptAsync(max(0, deadline − now), time, ct)   // recomputed, TimeSpan, zero allowed
```

**Cancellation.** `ct` here is the launch's own token (daemon shutdown / launch teardown). On
cancellation the wait and the prompt abort and the launch aborts with them — no consent
decision is fabricated, nothing proceeds, and the decision log records nothing for a launch
that was torn down before deciding. Fail-closed is preserved: every path that *continues* the
launch passed an explicit allow; every decided denial is coded.

Behavior deltas vs. shipped: a truly headless daemon whose operator set `default: prompt` waits
the grace before each unmatched denial (acceptable — the operator opted into prompting), and a
subscriber arriving inside the grace now receives the request via the existing atomic replay
instead of the launch having already died.

### 3.3 Unpark the receive loop (sequenced lane only)

- **Sequenced lane**: `SubmitAsync` is still called synchronously on the pump. The acceptance
  contract is pinned at the API boundary: **`SubmitAsync` must complete acceptance/rejection
  (including the accepted/rejected/duplicate answer sends) before returning, with no await
  before `SubmitLocked`** — true of the shipped code (§1.7) and now an explicit, test-pinned
  invariant, because pump serialization is the only thing guaranteeing in-order submission once
  the return value is no longer awaited. The returned execution-completion task gets a
  fault-observing continuation whose ONLY job is logging: terminal CommandAck/CommandRejected
  emission is the lane's duty (§1.7) and is unaffected by whether anyone awaits that task —
  exactly-once settlement (accepted answer first, exactly one terminal answer per accepted
  item: success, consent denial, or lane failure) does not move.
- **Legacy lane** (un-seq'd commands, old servers): **deliberately unchanged** — the pump keeps
  awaiting legacy launch/stop execution inline, exactly as shipped. The inline await IS the
  backpressure: no queue exists, so there is no memory bound to invent, no cumulative-delay
  story, and no cross-epoch drain semantics. The cost — a consent prompt inside a legacy launch
  parks that connection's pump, as it does today — is confined to pre-settlement servers, a
  shrinking population that predates the settlement liveness guarantees anyway. The desktop
  app CAN encounter this mode (a current daemon attached to a pre-settlement server — hello
  advertises daemon capabilities, not the server's): the app remains fully functional, because
  consent delivery/resolution and status ride the local control socket, which is independent
  of the server pump; what stays degraded on such servers is concurrent server-command
  processing during a prompt — exactly as shipped.
- **Cross-format latch (one direction, daemon-lifetime).** Detaching sequenced execution
  opens one new interleaving: after a sequenced launch is submitted (pump freed), a later
  un-seq'd launch or stop would execute inline while the sequenced item is still running — two
  ordering domains at once. Because detached sequenced work can also outlive its hub
  connection (nothing here assumes handler serialization spans a reconnect), the latch is
  deliberately NOT connection-scoped and its trigger is pinned as **protocol evidence, not
  command fate**: the latch trips on RECEIPT of any launch/stop command carrying ANY of
  `Epoch`/`Seq`/`CommandId` — the exact discriminator the shipped router already uses — set
  BEFORE routing or submission, published with a thread-safe monotonic write (`Volatile`; set
  once, never cleared). It therefore needs no acceptance outcome from `SubmitAsync` at all,
  and a first sequenced command that is subsequently rejected (partial tuple, gap, duplicate
  collision, backpressure) STILL latches — correctly, because even a rejected sequenced
  command proves the server speaks the sequenced protocol, and such a server never
  legitimately sends legacy commands. Ordering: the latch write strictly precedes lane
  eligibility (submission follows the write), so an un-seq'd handler that reads `false` read
  it before the first sequenced command was even routed. The one residual window is the
  format-TRANSITION overlap across a reconnect (e.g. a legacy launch still executing from a
  pre-upgrade server while the upgraded server's first sequenced command arrives): that
  overlap exists identically in the shipped code (an in-flight handler survives its
  connection; the new receive loop dispatches regardless) — this spec neither widens nor
  claims to close it, and the server reconciliation lanes (§1.12) remain its backstop. The
  latch is sound daemon-wide because a server that speaks sequenced never legitimately
  downgrades mid-daemon-life (the daemon reconnects to the same configured server); a genuine
  downgrade fails loudly (coded rejections) and is cleared by a daemon restart. Rejection
  surfaces: an un-seq'd launch via the existing `LaunchFailedAsync` lane (coded
  `mixed_command_formats`); an un-seq'd stop is **deliberately discarded** — logged at Error,
  not executed — because no reply surface exists for a legacy stop (§1.8). The REVERSE
  direction (sequenced after un-seq'd) needs no latch: legacy work is awaited inline, so under
  pump serialization it has completed before the pump reads the next command (the
  transition-across-reconnect case is the shipped residual above).
- The malformed-partial-tuple arm is synchronous already; unchanged.

Net effect: on sequenced (current) servers, every non-launch handler (StopAgentV2 acceptance,
evals, status-report requests, reviewer-model resolution) dispatches while a launch is
executing or parked on a consent prompt. On legacy servers the pump keeps shipped semantics.

**Documented residual (deliberately retained):** a sequenced stop's *execution* still queues
behind the in-flight launch on the serial lane — but the cumulative bound is ONE prompt-capable
launch, because the server's settlement admission holds the daemon's single sequenced slot per
launch (§1.7); lane memory is bounded by the processor's existing `Backpressure` rejection
(§1.7). Legacy pump parking is bounded per prompted launch by the 300 s timeout ceiling
(§1.11) and reachable only on pre-settlement servers with a prompt default/rule. Stops are
never lost or reordered **within their own format lane**; the mixed-format case is an explicit
**degraded mode** — the un-seq'd stop is discarded (logged, not executed), its fire-and-forget
sender gets no signal (§1.8), and the server's reconciliation lanes (§1.12) are the eventual
corrective path for any live agent the server wants gone. This spec's acceptance criteria
cover the daemon-side rejection observables (non-execution + log); server-side eventual
stopping is that machinery's own shipped, separately tested behavior, out of this repo's test
reach.

### 3.4 Out of scope

- Server→daemon patience hint (decision 6). Late-launch reconciliation is §1.12's shipped
  server machinery; this spec adds nothing to it and takes nothing from it.
- Any change to consent semantics, rule matching, storage, or the decision log format.

## 4. PR 2 (AI-1649): supervision IPC

### 4.1 Frames

- `StatusSubscribe = 16` (client→daemon), no payload. Long-lived connection, same EOF-watcher
  discipline as `ConsentSubscribe` (a vanished subscriber must be reaped promptly).
- `DaemonStatus = 76` (daemon→client). Text payload `DaemonStatusDto`, pushed once immediately
  on subscribe, then re-pushed (debounced ~250 ms) whenever the change generation advances:

```json
{
  "daemon": {
    "name": "main",
    "version": "0.12.3",
    "server_url": "https://tenant.example.com",
    "connection": "connected",
    "max_agents": 5,
    "active_agents": 2
  },
  "agents": [
    {
      "id": "agent-abc123",
      "kind": "review-flow",
      "vendor": "codex",
      "repo_path": "/Users/x/dev/repo",
      "status": "Live",
      "flow_run_id": "flow_...",
      "flow_role": "reviewer",
      "requester": "github:12345",
      "created_at": "2026-08-01T12:34:56.789Z",
      "model": "gpt-5-codex"
    }
  ]
}
```

Wire semantics, pinned:

- `connection` ∈ `connected | connecting | reconnecting | disconnected` (lowercase), mapped
  from `ServerConnection.HubState`.
- `agent.status` is the daemon's internal status string **verbatim** (PascalCase as stored:
  `Starting`, `Live`, `Quarantined`, …). The vocabulary is deliberately open — clients treat
  unknown values as opaque display text, never as errors.
- `kind` reuses the existing `KindText` wire spellings (`agent`/`review`/`review-flow`, unknown
  enum names pass through) — one vocabulary across `AgentList` and this payload.
- **Every field is always emitted; absent values are JSON `null`** (no omit-when-null option on
  this context) — one wire shape, exact-JSON testable. `requester` is `null` when unknown (old
  servers, local spawns); rendering "unknown" is the app's presentation concern, not a wire
  value.
- `agents` includes every entry in `_agents` (all statuses); quarantined-but-removed children
  are not listed (same visibility rule as `kcap agent ls`).
- `agents` order is **pinned: `created_at` ascending, tie-broken by `id` (ordinal)** —
  `ConcurrentDictionary` enumeration order is not stable, and leaving the order unspecified
  would let exact-JSON tests or the first UI silently turn enumeration order into a wire
  contract. Sorting ≤ `max_agents` entries is trivial.
- `active_agents` is computed **from the materialized agents array in the same snapshot**,
  using the same status predicate as the daemon's existing `ActiveCount` — the count and the
  array can never disagree within one payload.

**Snapshot mechanics.** A snapshot is: one enumeration pass over `_agents` (weakly consistent,
exception-free per §1.11) materializing the agent DTOs, then the daemon block (name, version,
server URL, `HubState`, `max_agents`) read once, then `active_agents` derived from the
materialized array. Cross-field coherence beyond a single payload is explicitly NOT promised —
a snapshot racing a mutation is stale, not torn, and the generation mechanism below guarantees
a fresh snapshot follows every mutation.

### 4.2 Change signal (generation counter, broadcast-safe)

`DaemonStatusNotifier` (daemon service) maintains a **monotonic change generation**:

- `long Version` and the shared rearm `TaskCompletionSource` live **under one lock**:
  `Pulse()` increments `Version` and completes-and-rearms the source in the same critical
  section; `WaitBeyondAsync(long seen, CancellationToken ct)` reads `Version` and captures the
  current source in that same critical section (returning synchronously if `Version > seen`).
  No torn interleaving between the version check and the source capture is possible. This is a
  broadcast: N subscribers each hold their own cursor and can never consume each other's
  signal.
- **Mutation first, `Pulse()` second — always.** A pulse published before its state mutation
  lets a subscriber read the new version, snapshot the OLD state, and then wait forever (no
  later generation exists). Every call site therefore pulses strictly after the mutation is
  visible (`_agents` add/remove committed, `Status` written, `HubState` transitioned), and the
  call sites are centralized behind small helpers (e.g. a status-setter that mutates then
  pulses) so a future writer cannot forget the ordering.

Subscriber loop (per `StatusSubscribe` connection):

```
while (true) {
    var seen = notifier.Version;      // BEFORE snapshotting — a mutation during
    push(Snapshot());                 // the snapshot/push advances Version past
    await notifier.WaitBeyondAsync(seen, ct);   // `seen` and re-triggers immediately
    await Task.Delay(debounce, ct);   // coalesce bursts into one trailing snapshot
}
```

Reading the cursor before the snapshot closes the subscribe-boundary race (a change landing
between snapshot and wait is seen); full-snapshot pushes make a coalesced burst equivalent to
its last state. A slow subscriber delays only itself — cursors are per-connection and `Pulse()`
never blocks on consumers.

**The observable delivery guarantee is convergence, not per-generation delivery** (debounce
deliberately collapses bursts): after any change followed by a quiet period, every live
subscriber receives a snapshot at least as new as the final generation. No guarantee is made
about intermediate states inside a burst.

Pulse call sites:

- `AgentOrchestrator`: agent added to `_agents`, `Status` transitions, cleanup/removal.
- `ServerConnection`: `Reconnecting`, `Reconnected`, `Closed`, initial connect.

### 4.3 Requester on the agent record

`AgentInstance` gains `public string? RequesterUserId { get; init; }`, stamped from
`LaunchAgentCommand.RequesterUserId` at construction (null for old servers and local spawns).

### 4.4 Stop

The app uses the existing `StopV2` frame on a one-shot connection. No new stop frames, no
protection-semantics changes.

## 5. Error handling

- Hello with a malformed payload: logged, treated as an empty `ClientHelloDto` — the reply is
  identical either way (the payload is diagnostics-only, never gating).
- `StatusSubscribe` on a shutting-down daemon: connection closes; the app's reconnect loop
  (AI-1650) owns retry.
- All new daemon→client payloads serialize via a new nested snake_case `JsonSerializerContext`
  in Core (AOT source-gen; no reflection; nulls always written), shared verbatim by daemon,
  CLI, and app.
- **JSON payload forward compatibility is a pinned contract**: deserialization ignores unmapped
  members (STJ's default `UnmappedMemberHandling.Skip` — no context or DTO may opt into
  `Disallow`), so a future additive field in `HelloReply`/`DaemonStatus` never breaks an older
  app; unknown capability strings and open `status`/`kind` values are likewise non-fatal.
  `protocol_version` does NOT gate additive DTO changes — only a framing break would bump it.
- Unknown frame values keep the existing `Error` reply from `LocalControlServer`'s default arm;
  the arm's expected-frames message is extended.

## 6. Testing

Both PRs: unit + real-socket integration in the existing kcap-cli suite (TUnit on MTP; short
test daemon names — macOS `sockaddr_un` ~104-byte path limit). Time-dependent consent tests use
the injectable `TimeProvider` — no stopwatch-flake assertions.

PR 1:

- FrameCodec round-trips for `Hello`/`HelloReply` (incl. empty hello payload).
- Hello over a real socket returns the daemon's actual version + capabilities; a non-hello
  first frame still routes as before (regression); the capability list matches the handlers the
  binary actually serves; an unknown-to-the-daemon frame type yields the `Error` reply (the
  down-level discovery path a new app relies on).
- Grace matrix: subscriber already present / arrives inside grace / arrives at-or-after expiry
  / never arrives × defaults `allow`/`deny`/`prompt` (allow/deny never wait); the deadline
  discipline holds under a controlled `TimeProvider` (total ≤ timeout; zero-remaining settles
  as `prompt_timeout`), **including virtual time advanced between deadline creation and the
  start of the grace wait** (the wait shrinks; the deadline does not move); `RequestedAt` is
  stamped at prompt-path entry (anchor consistency; exact wall-clock equality is deliberately
  not asserted — countdown is best-effort display); denial reason stays `prompt_no_ui`;
  decision log records the graced denial; launch-token cancellation aborts without fabricating
  a decision.
- Broker waiter state machine: multiple concurrent waiters in one zero-subscriber generation
  all complete on the 0→1 transition; one waiter's timeout/cancellation doesn't disturb the
  others or the shared source; subscribe→unsubscribe→wait blocks again (fresh generation — a
  stale completed source never satisfies a later wait); the expiry/subscribe race resolves in
  favor of arrival.
- Pump + lane contract: with a SEQUENCED launch parked on a consent prompt, StopAgentV2 and
  status-report handlers dispatch promptly, and a sequenced stop's ACCEPTANCE is answered
  promptly;
  back-to-back sequence numbers submitted in wire order are accepted in order (submit-on-pump
  regression); accepted answer precedes the terminal answer; exactly one terminal answer per
  accepted item across success / consent-denial / lane-failure outcomes; a legacy launch still
  parks the pump (shipped-behavior regression pin — the legacy lane is deliberately unchanged).
- Cross-format latch: an un-seq'd launch after any sequenced traffic is rejected with the
  coded `mixed_command_formats` `LaunchFailed`; an un-seq'd stop after sequenced traffic is
  not executed and logged at Error (no legacy-stop reply surface exists, §1.8); **a REJECTED
  first sequenced command (gap seq / partial tuple / backpressure) still latches** — legacy
  traffic after it is rejected identically (protocol evidence, not command fate); the latch
  write precedes submission and is immediately visible cross-thread (volatile publication —
  a concurrent reader observing `false` implies the sequenced command had not yet been
  routed); the latch survives a reconnect — regression: a detached, still-prompting sequenced
  launch, then a simulated reconnect, then an un-seq'd stop AND an un-seq'd launch — both
  rejected, neither executes concurrently with or ahead of the sequenced item; a sequenced
  command after un-seq'd traffic needs no latch (legacy work completed inline before the pump
  read it) and proceeds normally; a fresh daemon process starts unlatched.

PR 2:

- FrameCodec round-trips for `StatusSubscribe`/`DaemonStatus`, plus **exact-JSON contract
  tests** (null fields present, lowercase `connection`, verbatim `status`, `KindText`
  spellings, pinned agents order — `created_at` asc, `id` tie-break — and `active_agents`
  consistent with the array); an extra/unknown property in a payload decodes without error
  (unmapped-member forward compat).
- Subscribe → immediate snapshot with correct daemon block and agents array.
- Convergence with two simultaneous subscribers: after each separated change (and after a
  burst followed by quiet), BOTH subscribers receive a snapshot at least as new as the final
  generation — per-generation delivery is deliberately NOT asserted (debounce collapses
  bursts); a mutation landing between a subscriber's snapshot and its wait triggers a
  follow-up push (cursor-before-snapshot regression); **a final mutation at the snapshot
  boundary with no later pulse still converges on both subscribers** (pulse-after-mutation
  regression); a slow subscriber does not stall the other.
- Agent add/status-change/removal each trigger a re-push; a burst of pulses coalesces
  (debounce) into at most one trailing snapshot after the in-flight push.
- Snapshot stress: concurrent add/status/remove while serializing snapshots — no exceptions,
  every payload internally consistent (count vs array), converges to the final state.
- Subscriber EOF flips the handler down promptly (no dangling pushes).
- `AgentInstance.RequesterUserId` stamped from the command; null-safe for old servers.

## 7. Risks & residuals

- **Sequenced stop execution delayed behind a prompted launch** (§3.3) — accepted, documented;
  cumulative bound is one prompt-capable launch (server single-slot admission, §1.7) × the
  300 s prompt-timeout ceiling; stops are delayed, never lost or reordered within their lane.
  **Legacy pump parking** persists by decision 4 — shipped behavior, pre-settlement servers
  only, and the app stays functional there (consent rides the local socket, not the pump).
- **Mixed-format degraded mode** (§3.3) — an un-seq'd stop after sequenced traffic is
  discarded (logged, not executed); eventual physical stopping of a live agent falls to the
  server's shipped reconciliation lanes (§1.12). A genuine server downgrade trips the
  daemon-lifetime latch until restart — loud by design.
- **Late launch after server abandonment** (decision 6) — bounded by the same ceiling and
  reclaimed by the server's shipped reconciliation lanes (§1.12); revisit only if a patience
  hint ever ships.
- **Debounce tuning** — 250 ms is a starting value; it is a constant in one place and not a
  contract.
- Frame values 15/16/75/76 are claimed here; any concurrent kcap-cli work adding frames must
  rebase on whichever lands first (append-only discipline makes the conflict loud, not silent).
