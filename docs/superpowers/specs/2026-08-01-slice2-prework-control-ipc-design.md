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
   create it. The lane's sequenced identity cache is bounded by design: `_cache.Count >=
   _cacheBound` (256) rejects further sequenced submissions with the coded `Backpressure`
   answer, identity preserved. Server-side, SEQUENCED (review-flow) launches are admitted one
   at a time per daemon — the kcap-server settlement admission (AI-1526) holds the daemon's
   single sequenced slot for the duration of such a launch. In the shipped daemon the
   `_processor` is constructed unconditionally before any handler is wired — it is never null
   in production; the inline arm and transition barrier are DEFENSE-IN-DEPTH for any future
   construction ordering, exercised via a test-only deferred-publication seam.
8. Legacy (un-seq'd) `StopAgent` is fire-and-forget on the wire — **no reply or failure surface
   exists** for it; the server learns outcomes from agent state, status reports, and its
   reconciliation lanes.
9. **The shipped server mixes command formats BY DESIGN, permanently** (verified against
   kcap-server main): the sequenced tuple rides ONLY the review-flow settlement lane
   (AI-1391/AI-1526 — `StageSequencedReviewFlowLaunch`, participant stops via the settlement
   transport). Ordinary launches (`CapacitorHub` hub launch, `AgentStoreDataService` hosted
   and PR-review launches) and EVERY stop (user Stop, admin stop, the AI-1313 S2
   registry-independent physical stop / retry-until-gone reaper) are un-sequenced. Any design
   that treats un-seq'd traffic after sequenced traffic as illegitimate is wrong on day one.
10. Server-side, EVERY launch dispatch is capacity-gated by the atomic `DaemonRegistry.TryReserve`
    (AI-1313 S5) before the command is sent, so the number of outstanding (dispatched,
    not-yet-settled) launches per daemon is hard-bounded by the daemon's advertised capacity
    (`MaxConcurrentAgents`, default 5).
11. Stop EXECUTION is already concurrency-safe off the pump today: internal heartbeat reaping
    (reviewer TTL/idle, stuck-Starting) and local-socket stops call the stop core directly from
    their own tasks, guarded by the per-agent single-flight teardown latch (`CleanupStarted`).
    Pump serialization was never the stop-safety mechanism — it only ordered SERVER commands
    relative to each other. Both internal paths SELECT their targets by enumerating `_agents`
    (reaping iterates registry entries; local stops resolve ids against the registry), so they
    can only ever act on an agent that exists — and a consent-parked launch has created NO
    agent anywhere (the gate runs at the top of the launch core, before any registry entry,
    worktree, or process exists). Teardown overlapping late launch initialization is the
    shipped `CleanupStarted` + `PendingEndReason` machinery, unchanged.
11a. The launch path's cancellation token is the daemon shutdown token (`_shutdownCts.Token`)
    — the only token wired into the consent gate. Internal stops act on registry entries and
    never cancel a launch token, so an OCE escaping the gate is shutdown-only by construction.
11b. Commands addressing a specific agent (input, special keys, resize) are sent by the server
    only for agents it has seen register (`AgentRegistered` creates the server-side entry the
    input/terminal paths require), and the daemon's handlers treat an unknown agent id as
    log-and-drop — already today's behavior for post-exit stragglers. The server's
    status-report consumers never infer absence from a report omission (the heal-barrier
    contract) and the physical-stop retry skips never-reported agents.
12. The local socket already has `StopV2` (force flag, protected-kind semantics). The app needs
   no new stop machinery.
13. `AgentInstance` does not store the requester — slice 1 passes `cmd.RequesterUserId` through
   to the consent gate only. The supervision payload's `requester` column needs a new field.
14. `_agents` is a `ConcurrentDictionary<string, AgentInstance>` (enumeration is exception-free
    and weakly consistent); `ActiveCount` counts entries whose `Status` is in the daemon's
    active set. `LaunchConsentStore` clamps `PromptTimeoutSeconds` to **[5, 300]**.
15. Server-side (kcap-server, already shipped and tested there): an agent the server no longer
    tracks is reclaimed by existing machinery — the daemon status report cross-check with
    physical retry-until-gone stop (AI-1391 S3b/S3c), the stale-reviewer sweep for terminal flow
    runs (AI-1313 S3a), and `OrphanedHostedAgentReaper` for hosted agents whose session ended
    (AI-1185). This spec cites these as the reconciliation backstop; it does not modify them.
16. Slice 1's `ILaunchConsentPrompter.PromptAsync` takes a **`TimeSpan`** budget — sub-second
    remaining budgets survive end-to-end; the store's [5, 300] clamp applies to the persisted
    policy value only, never to a computed remaining budget. `LaunchConsentPromptRequest`
    carries `RequestedAt`/`TimeoutSeconds` as the client's countdown metadata.

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | Hello is an **optional** one-shot frame. Existing clients that open with `Spawn`/`List`/`ConsentSubscribe`/… are untouched; nothing ever requires hello-first. |
| 2 | Feature discovery is **capability strings**, not protocol-version comparisons. `protocol_version` exists for a future framing break only; it starts at 1 and nothing gates on it yet. |
| 3 | The no-UI instant deny gains a bounded **subscriber grace** — `min(5 s, prompt timeout)` — burned from a single **monotonic absolute deadline** established at prompt-path entry, not added to it. The coded deny reason stays `prompt_no_ui` (stable contract); the decision log records that grace elapsed. |
| 4 | **One execution domain, latch abolished.** The receive pump never awaits launch/stop EXECUTION; all SERVER-ORIGIN launch/stop execution — sequenced and un-seq'd alike — runs in arrival order on the ONE existing single-reader lane (`RunLaneAsync`), so today's pump serialization is relocated, not changed, and cross-format ordering holds by construction. Sequenced acceptance/ack machinery is untouched. No command is ever refused for its format — the shipped server mixes formats by design (§1.9). Against a pre-settlement server (`_processor` null) the legacy inline-await stays (no sequenced traffic exists, single domain is trivial). Queue bounds are upstream and real: launches ≤ daemon capacity via server-side `TryReserve` (§1.10); stops bounded by live agents × reconcile cadence. |
| 5 | Supervision pushes are **full snapshots** driven by a **monotonic change generation** with per-subscriber cursors (§4.2) — never per-event deltas, never a consumable one-shot signal — so N subscribers each converge and a missed pulse cannot desync anyone. |
| 6 | The server→daemon "patience hint" (clamping prompt timeout to the server's launch-admission window) stays **out of scope** (cross-repo). The late-launch residual is bounded and reconciled: the prompt timeout is capped at 300 s by the existing store clamp, and an agent launched after the server abandoned its launch is reclaimed by the server's shipped reconciliation lanes (§1.15). |
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

`LocalControlServer` answers `Hello` and closes, same one-shot shape as `List`. **Down-level
discovery:** a pre-hello daemon's `FrameCodec.Decode` cannot decode byte 15 at all — it throws
and the daemon drops the connection without a reply, so the app's down-level signal is
hello-then-EOF (no `HelloReply`), NOT an `Error` frame. (The `Error` reply exists only for
frames the codec decodes but the server doesn't route — pinned as a routing regression, §6.)

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
  `TimeSpan` (sub-second budgets survive, §1.16; no integral-seconds conversion, no re-clamp
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

### 3.3 Unpark the receive loop (one execution domain)

The pump stops awaiting launch/stop execution; execution order is preserved by routing ALL
server-origin launch/stop execution through the one existing serial lane:

- **Sequenced commands**: unchanged acceptance — `SubmitAsync` is called synchronously on the
  pump (acceptance ordering depends on pump serialization; the no-await-before-`SubmitLocked`
  contract stays test-pinned), but the returned execution-completion task is no longer awaited
  by any handler (launch OR stop): it gets a fault-observing, logging-only continuation.
  Terminal CommandAck/CommandRejected emission is the lane's duty (§1.7) and does not move.
- **Un-seq'd commands with a live `_processor`**: the handler commits the execution onto the
  SAME lane via a TYPED non-watermark entry point — `SubmitOutcome
  SubmitUnsequenced(UnsequencedItem item)`, `UnsequencedItem(Kind: Launch|Stop, AgentId,
  PayloadKey, Func<Task> Execute)` — no seq, no cache, no acks. **Every predicate and
  mutation happens in ONE processor critical section (`_lock`) before the method returns**:
  admissibility, active-launch tracking, launch-barrier clearing, coalescing, and the lane
  commit — no label parsing, no handler-side registry checks, no await before commit. Pump
  serialization plus this single critical section is the arrival-order guarantee for both
  formats in both directions; a shutdown race can never both commit and refuse. Outcomes:
  `Committed` → the lane owns execution and fault containment (an item's exception, including
  OCE, never terminates `RunLaneAsync`; the lane wrapper logs; no handler-side continuation);
  `Refused` (shutdown) → the caller owns the consequence (launch → best-effort
  `LaunchFailedAsync`, its own send failure swallowed; stop → log); `Coalesced` → nothing.
- **Active-launch tracking (closes the executing-launch stop gap)**: the processor tracks
  active launch INSTANCES — a per-committed-item token added under `_lock` at commit and
  removed under `_lock` by ONE terminal-finalization path covering every ending: normal
  execute completion, lane failure, shutdown-synthesized settlement (item never executed),
  and shutdown discard; id membership lasts until the last instance for that id settles
  (reference-counted — no id-non-reuse assumption anywhere). A launch that has been
  DEQUEUED and is parked at the consent gate is therefore still an admissible stop target.
  Admissible stop targets = `_agents` ∪ durable PID records ∪ active-instance ids; anything
  else drops at admission with a log — observably identical to the eventual unknown-agent
  no-op (§1.8). The PID-record arm is load-bearing, not belt-and-braces: the daemon's stop
  path falls back to a record-driven reap for an id THIS incarnation never registered, which
  is how the server's registry-independent physical stop reclaims a prior incarnation's
  survivor (§1.15) — such a stop is a real action, not the no-op that justifies dropping, so
  admission must see it. The probe stays cheap enough to evaluate inside the processor's
  critical section: a lock-free registry hit answers the common case, and only a miss reaches
  a single record existence check (never a full record read).
  Removal ordering pinned: an instance is removed only once its agent is in `_agents`
  (success) or terminally failed — and the ID stays admissible while ANY instance remains.
  A racing registry removal (agent exits as a stop is admitted) yields a stop that no-ops at
  execution — harmless by the same idempotence.
- **Sequenced integration (same structures, same lock)**: the sequenced lane item already
  carries `SequencedKind` + `AgentId`, so the mutations live inside `SubmitLocked`'s ACCEPT
  branch — the same critical section that advances the watermark and writes the lane item:
  a NEWLY accepted sequenced Launch adds its active instance and clears the id's pending-stop
  keys there; a duplicate replay (answered, never re-executed) mutates NOTHING; an
  out-of-order / backpressure / stale-epoch / shutdown rejection mutates NOTHING. Pinned by
  tests (duplicate and rejected sequenced launches alter neither active counts nor coalescing
  keys nor queue order).
- **Coalescing**, keyed (AgentId, PayloadKey) under the same `_lock`: the legacy `StopAgent`
  payload key is constant (§1.8); `StopAgentV2` keys on its force flag — payload-class
  cardinality C is fixed (≤ 3). **Key lifecycle:** a pending-stop key is removed atomically
  when ITS OWN lane item is dequeued to start (identity-guarded — the key stores the item it
  refers to, so an older item starting can never clear a newer segment's key), so a
  same-payload retry after a started/faulted stop commits a FRESH item — retry semantics
  survive teardown failure. **Launch-aware**: a launch COMMIT for id X (either format, in
  its commit critical section) clears ALL X's pending-stop keys, so
  stop(X)→launch(X)→stop(X) keeps its order. **Bound (lossless for known targets):** a stop
  admitted for a known target is NEVER dropped — losing it would leave an agent running that
  the command would have torn down, which no memory argument justifies. Boundedness is
  structural: coalescing allows at most one queued entry per (target, payload class, launch
  segment); segments per target ≤ that target's active launch instances + 1 (≤ capacity + 1);
  targets are admission-checked against `_agents` ∪ active instances; every entry retires at
  dequeue. **Boundedness is monitored, not proven:** live targets are capacity-capped
  (`MaxConcurrentAgents` governs server launches AND local spawns), a removed target's
  entries never grow again (admission re-checks liveness), and entries retire at dequeue —
  so queue growth requires agent churn DURING lane non-dequeue time (consent parking, launch
  initialization, any long item), and no closed-form bound is claimed for that product.
  Under sustained churn with a stalled lane, queued-stop memory grows with churn — an
  accepted, monitored residual (§7), each entry O(bytes). The 256-entry alarm is
  **edge-triggered with hysteresis**: one Error on crossing, quiet during further growth,
  re-armed only after depth drains below 128 (half the threshold), plus a minimum 60 s
  interval between alarm emissions; current and high-water depth are carried in the alarm
  message and exposed as accessors for future status surfaces (the supervision IPC is the
  natural consumer) — boundary oscillation cannot turn the alarm into its own failure mode. **Scope of losslessness:**
  a known-target stop is never dropped while the lane is ACCEPTING; at daemon shutdown the
  deliberate supersession applies (queued un-seq'd items discarded). Registered children are
  killed by shutdown teardown (captured start identity); a late-starting child teardown
  missed is reaped by the next boot's env-marker/PID-record scan — which is CONDITIONAL on a
  future boot, so the supersession is NOT claimed immediately safe: **an orphan may survive
  from shutdown until the next daemon boot** (explicit §7 residual; relevant to uninstall or
  a never-restarted daemon). Tests pin both layers: (a) live children + queued un-seq'd
  stops → real shutdown → queue discarded AND every registered child gone; (b) end-to-end
  handoff — a child started after the teardown snapshot survives shutdown WITH its durable
  identity record, and the next boot's scan reaps exactly that child (start-identity match;
  a PID-reused unrelated process is untouched). The only dropped stops remain unknown-target ones,
  observably identical to their eventual no-op. Launch items are ≤ capacity (§1.10).
  **Submission outcomes (complete):** `Committed` | `Coalesced` | `Refused` (shutdown) |
  `DroppedUnknownTarget` — the processor owns the drop log; the queued-stop counter is
  incremented at commit and retired under `_lock` at identity-guarded dequeue AND at shutdown
  discard (a stale count can never reject or mis-alarm retries after shutdown).
- **Un-seq'd commands with `_processor` null** (pre-settlement server): the shipped inline
  await stays byte-for-byte. No sequenced traffic can exist (§1.7), so the single domain is
  trivially preserved, and the shipped backpressure story is unchanged for exactly the
  population it already served.
- **Internal stop paths bypass the lane deliberately** (heartbeat reaping, local-socket
  stops): they already run off-pump today and are concurrency-safe via the per-agent
  single-flight teardown latch (§1.11). Routing them through the lane would let a parked
  consent prompt delay reviewer reaping — the exact inversion of what the reaper exists for.
- The malformed-partial-tuple arm is synchronous already; unchanged.

**Handler classification (what unparking means for every other handler).** Agent-ADDRESSED
commands (input, special keys, resize): the server sends them only for registered agents
(§1.11b) and the daemon drops-and-logs unknown ids — already today's post-exit behavior — so
dispatching them while a launch executes is benign by the same contract that already governs
stragglers. Status-report requests: served from the registry snapshot; an in-flight launch is
simply absent, and the server's consumers never infer absence from omission (§1.11b). Evals
and reviewer-model resolution: agent-independent. Nothing else consults launch state. Each
class is pinned by a test (§6).

**`_processor` publication barrier (no dual domain, ever).** The daemon epoch is a per-boot
GUID pinned before services are built, so `_processor` is single-assignment for the process
lifetime — one null→live transition, no replacement/reset case. The transition is guarded by
ONE orchestrator-owned lock shared by handler admission and publication: an un-seq'd handler
takes it to snapshot `_processor`, and on null RESERVES the inline slot with a placeholder
TCS **before invoking the core** (invocation happens outside the lock; the TCS completes in
the core's finally); publication takes the same lock to install the processor and capture the
reserved completion (if any); the lane's read loop awaits that captured completion before
executing its first item. Snapshot+reserve is atomic with publication — a handler that saw
null cannot start inline work after the lane has begun, and the lane cannot begin while a
reserved inline item exists. Deterministic test: pause a hooked handler after the null
snapshot, publish the processor, assert the lane's first item waits for the inline drain.

**Queue bounds (grounded, not asserted):** lane items are launches and stops only. Outstanding
launches per daemon are hard-bounded by the server's atomic capacity reservation before every
dispatch (§1.10, `MaxConcurrentAgents`); un-seq'd stops are hard-bounded by admission (known
targets only) + per-(id, payload-class) coalescing; sequenced items by the 256-item
`Backpressure` bound (§1.7). No new unbounded structure is introduced.

Net effect: every non-launch/stop handler (evals, status-report requests, reviewer-model
resolution, input/resize) dispatches while a launch executes or parks on a consent prompt —
and so do launch/stop ACCEPTANCE and enqueue. What still serializes is launch/stop EXECUTION,
exactly as the shipped pump serialized it: a consent prompt inside one launch delays
subsequent launch/stop executions up to the prompt budget — today's behavior relocated off
the pump, bounded by the 300 s ceiling (§1.14) and the capacity-gated queue depth above.

**Cancellation settlement (pinned by tests):** the launch path's only cancellation source is
the daemon shutdown token (§1.11a) — internal stops never cancel a launch token, so "torn
down while parked" IS daemon shutdown. The OCE propagates out of the gate (no fabricated
decision, §3.2); on the sequenced lane it settles as the existing lane-failure shape
(terminal CommandRejected(InternalError), exactly one terminal answer); on the un-seq'd lane
the item wrapper contains it (no LaunchFailed — the daemon is exiting; §1.15 covers the
rest). **Lane shutdown order (pinned):** shutdown cancels the in-flight item (contained) and
completes the channel writer (subsequent `SubmitUnsequenced` refuses). Before exiting, the
lane settles every ACCEPTED queued SEQUENCED item through the existing synthesized-error
machinery (best-effort terminal answer, cache/watermark bookkeeping completed, AND the item's
execution-completion task completed exactly once with the documented failure — the shipped
`SynthesizeErrorLocked` precedent extended to the per-item task), preserving
exactly-one-terminal-answer wherever the
transport still stands; where it does not, the settlement protocol's own recovery (duplicate
replay from cache, boot-epoch fencing on restart) is the shipped answer to lost terminal
acks. Queued UN-SEQ'D items are discarded silently by design — daemon-wide teardown
supersedes per-agent stops. Fault isolation ("a queued stop behind a faulted item still
executes") applies to NON-shutdown item faults. All pinned by tests, acknowledged in a code
comment at the enqueue site.

### 3.4 Out of scope

- Server→daemon patience hint (decision 6). Late-launch reconciliation is §1.15's shipped
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
exception-free per §1.14) materializing the agent DTOs, then the daemon block (name, version,
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
  binary actually serves; a decodable-but-unrouted first frame yields the `Error` reply
  (routing regression; the actual down-level discovery signal is hello-then-EOF, §3.1).
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
- Pump + lane contract: with a SEQUENCED launch parked on a consent prompt, evals/status
  handlers dispatch promptly AND a subsequent sequenced stop's ACCEPTANCE is answered promptly
  while its EXECUTION queues behind the launch; back-to-back sequence numbers submitted in
  wire order are accepted in order (submit-on-pump regression); exactly one terminal answer
  per accepted item across success / consent-denial / lane-failure (launch-token cancellation
  while parked) outcomes; no handler awaits execution (liveness guards on every await-capable
  call site — a re-added await fails fast, never hangs the suite).
- One-domain ordering: an un-seq'd stop enqueued after an un-seq'd launch executes after it;
  an un-seq'd stop enqueued after a SEQUENCED launch for the same agent executes after that
  launch settles; a SEQUENCED item enqueued after an un-seq'd item executes after it (both
  cross-format directions pinned); two launches never execute concurrently on the lane;
  `SubmitUnsequenced` commits synchronously (no yield before the lane write — pinned the same
  way as sequenced acceptance); internal heartbeat reaping still stops a live agent WHILE the
  lane is parked on a consent prompt (bypass pin); a consent-parked launch has no registry
  entry, so internal paths cannot target it (existence pin); with `_processor` null, an
  un-seq'd launch executes inline and `HandleLaunchAgent` returns only after the core
  completes (pre-settlement regression pin).
- Stop admission, coalescing + fault isolation: N duplicate un-seq'd stops for one agent
  while the lane is parked collapse to one queued entry (queue-depth assertion) and the stop
  still executes after the launch settles; stop(X)→launch(X)→stop(X) executes the second stop
  AFTER the launch (launch-aware coalescing pin, mixed formats); M distinct UNKNOWN target
  ids are dropped at admission (queue depth unchanged); a faulting (non-shutdown) un-seq'd
  item does not kill the lane — a stop queued behind it still executes; cancelling the REAL
  shutdown token with an un-seq'd stop queued exits the lane cleanly without executing it,
  while an ACCEPTED sequenced item queued behind an un-seq'd item gets a synthesized terminal
  answer before exit (shutdown order pins, no hang); a stop arriving while its launch is
  DEQUEUED and parked at the consent gate is admitted and executes after the launch settles
  (active-set pin, both formats); a handler paused between its null `_processor` snapshot and
  inline-slot reservation cannot overlap the lane's first item once the processor publishes
  (transition-lock pin); distinct stop payload classes for one target queue separately
  (bound-formula pin); launch(X)→launch(X)→stop(X) with the first launch failing while the
  second is parked keeps X admissible and the stop executes after the second settles (both
  mixed-format orders — instance-count pin); a duplicate replay and each rejected sequenced
  launch alter neither active counts nor coalescing keys (accept-branch-only pin); the task
  returned by `SubmitAsync` for a shutdown-synthesized item completes exactly once with the
  documented failure, and shutdown synthesis/discard retires the exact active-instance
  tokens (active-count and id-membership assertions, not only task completion); a stop that
  throws followed by a same-payload retry commits and executes the retry (key-retire pin);
  an older stop starting after launch-aware clearing does not erase the newer post-launch
  key (identity-guard pin); the 257th queued known-target stop is ADMITTED and only alarms
  (lossless pin), and stop(X)→launch(X)→stop(X) holds with the queue pre-filled to the alarm
  threshold (saturation ordering pin); unknown-target drops report `DroppedUnknownTarget`;
  the queued-stop counter returns to zero after shutdown discard (counter-cleanup pin); the
  alarm fires once on crossing the threshold, stays quiet during further growth, re-arms
  only after draining below the hysteresis watermark, and repeated boundary oscillation
  within the minimum interval emits no further Errors (hysteresis pin); shutdown with live
  children plus queued un-seq'd stops discards the queue AND leaves every registered child
  physically gone (teardown-reap pin); a child started after the teardown snapshot survives
  with a durable identity record and the next boot's scan reaps exactly that child, never a
  PID-reused unrelated process (handoff pin).
- Handler classification: with a launch parked on consent, an input/resize for an UNKNOWN
  agent id is dropped-and-logged (no throw, no pump stall) and a status-report request is
  served from the registry snapshot omitting the in-flight launch.

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

- **Launch/stop execution serialized behind a prompted launch** (§3.3) — accepted: it is the
  shipped pump serialization relocated off the pump, bounded by the 300 s prompt ceiling
  (§1.14) and the server's capacity-gated dispatch depth (§1.10); while the lane is
  accepting, stops are delayed, never lost, reordered, or refused — shutdown supersession is
  the deliberate exception, safe because teardown reaps the children. Non-launch/stop
  traffic and internal reaping are immune.
- **Shutdown orphan window** (§3.3) — a child that starts after the shutdown teardown
  snapshot survives until the NEXT daemon boot's env-marker/PID-record scan; if the daemon is
  never restarted (uninstall, host decommission), the orphan persists. Deliberate residual of
  shutdown supersession; the durable identity record makes the eventual reap exact.
- **Queued-stop memory under pathological churn** (§3.3) — with sustained agent churn during
  lane NON-DEQUEUE time (consent parking, launch initialization, any long item), queued-stop
  entries grow with churn; no closed-form bound is claimed. Accepted, monitored via the
  hysteresis alarm + current/high-water metrics.
- **Legacy inline-await persists only against pre-settlement servers** (`_processor` null) —
  shipped behavior for exactly the population that already had it.
- **Debounce tuning** — 250 ms is a starting value; it is a constant in one place and not a
  contract.
- Frame values 15/16/75/76 are claimed here; any concurrent kcap-cli work adding frames must
  rebase on whichever lands first (append-only discipline makes the conflict loud, not silent).
