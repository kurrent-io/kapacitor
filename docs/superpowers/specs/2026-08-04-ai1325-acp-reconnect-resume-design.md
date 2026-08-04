# ACP hosted-agent reconnect/resume across child death — skip-whole-replay design

**Issue:** AI-1325 (deferred from AI-689 at its C0 hard gate; design-of-record `docs/ai-689-design.md` §4).
**Date:** 2026-08-04. **Repo:** kcap-cli (daemon), plus one small additive kcap-server follow-up (§8).
**Evidence:** `docs/probes/2026-08-04-acp-reconnect-c0/` (live C0 re-probe of all four registered ACP vendors).

## 1. Problem and history

A hosted ACP agent's child process (`cursor-agent acp`, `copilot --acp --stdio`, …) dying mid-session
ends the session today: the connection read loop ends, pending requests fault, and the orchestrator
finalizes the agent. ACP itself supports protocol-native resume — relaunch, `initialize`,
`session/load {sessionId, cwd, mcpServers}` — and the AI-689 probe confirmed it works across a
process restart for Cursor.

AI-689 deferred reconnect at its C0 gate: `session/load` replays history as `session/update`
notifications with **no per-message id**, so the streaming-preserving per-envelope dedup ("C3(a)")
was impossible, and the only sound alternative ("C3(b)" whole-turn atomic staging) would have removed
within-turn live streaming for **all** hosted-Cursor turns — a global UX regression the owner
declined to ship. The deferral's revisit trigger — ACP specifying an optional `messageId` on message
chunks — has since fired in the spec (a MAY on `agent_message_chunk`).

## 2. Probe evidence (2026-08-04, live, all four vendors)

| Fact | Cursor 2026.07.23 | Copilot 1.0.78 | Kiro 2.16.0 | Gemini 0.53.0 |
|---|---|---|---|---|
| `loadSession` advertised | yes | yes | yes | yes |
| `session/load` after SIGKILL of the owner | **works** | **works** | **refused** — `"Session is active in another process (PID …)"` | **refused** — `"No previous sessions found for this project"` |
| `messageId` on chunks (live or replay) | none | none | none | (moot) |
| Replay granularity vs live | coalesced (~1 chunk/message vs 77 live chunks) | coalesced; plus one **empty** user chunk not matching any turn | — | — |
| `toolCallId` stability live↔replay | **rewritten** (`toolu_…` → synthetic `replay-2-1`) | stable | — | — |
| Mid-turn-killed prompt in replay | **absent** (3 chunks had already streamed live) | **present** (user msg + partial agent output persist) | — | — |
| Closed-world barrier (no conversation update after the `session/load` response) | holds (only an `available_commands_update` trailer) | holds | — | — |
| Prompting the loaded session | works | works | — | — |

Additional per-vendor findings that gate eligibility:

- **Kiro:** the stale-owner lock is **durable** — the same refusal, naming the dead PID, at 0s, 15s,
  and 60s cumulative delay. A bounded-backoff retry cannot clear it; Kiro is ineligible until the
  vendor fixes the lock.
- **Gemini:** a crash-killed session is never persisted, so there is nothing to load. Gemini also
  **self-re-execs** (its sandbox wrapper spawns an inner process with identical argv), so "the
  spawned pid exited" and "the agent died" are not the same event; any future Gemini enablement must
  re-probe both persistence and process-tree semantics.
- **No vendor implements the `messageId` MAY**, and Cursor's replay rewrites `toolCallId`s — so even
  a future composite key could not match live↔replay envelopes on Cursor. C3(a) stays closed.

## 3. Decision: skip-whole-replay (approved 2026-08-04)

The July design assumed a resume must *match* the replay against already-forwarded envelopes. The
probe shows matching is unnecessary:

- Everything the runtime ever emitted was already handed to the transcript pipeline, whose channel,
  forwarder, seq cursor, and unacked buffer all survive a child swap — only the child process died,
  never the daemon↔server link. The client-side transcript is authoritative.
- The `session/load` replay exists to restore the **agent's** context, not ours.
- ACP guarantees the `session/load` response arrives only after all conversation entries have
  streamed (spec MUST; measured for both capable vendors), giving a protocol-backed end-of-replay
  barrier.

So the resume **suppresses the entire replay** — emits nothing between entering reconnect and the
gate reopening after the `session/load` response. Consequences:

- **No streaming regression, for any vendor, ever.** The C3(a)/C3(b) fork and its product tradeoff
  dissolve. No staging, no dedup keys, no replay bookkeeping.
- The cost is confined to the crash-boundary turn and is the at-most-once floor the July design
  already mandated (§7): the interrupted turn is never auto-completed and never auto-requeued
  (except when provably never sent), and replay-only content for it is dropped. Measured: Cursor
  drops that turn from replay anyway; Copilot persists it agent-side, so a user resend reads as a
  normal follow-up in the agent's context.

**Owner decisions (2026-08-04):** (1) skip-whole-replay is the design center, over C3(b)-behind-flag
and over staying deferred; (2) vendor scope is **Cursor + Copilot now**, Kiro and Gemini excluded
with the measured refusals documented above.

## 4. Eligibility gate

Reconnect is attempted for a launch only when **all four** hold:

1. The vendor descriptor's new **`SupportsReconnectResume`** flag (probe-verified: `true` for Cursor
   and Copilot; `false` with a doc-comment citing §2 for Kiro and Gemini). Advertisement is not
   capability — Kiro and Gemini both *advertise* `loadSession` and both fail it across a crash.
2. The handshake actually advertised `loadSession` (`AcpHostedAgentRuntime.SupportsLoadSession`).
3. The launch is interactive (`!RuntimeStartContext.IsReviewFlow`). Review-flow/unattended
   participants keep relaunch-fresh and the flows recovery protocol — a reviewer death mid-round is
   a flow-layer event, not a runtime-layer one, and transparently resuming it would hide the death
   from the machinery that owns round recovery.
4. The kill switch is not engaged: `KCAP_ACP_RECONNECT=0` (or `false`) disables reconnect globally.
   Default on. No other tuning knobs — attempts/backoff/cap are fixed constants.

Ineligible launches keep today's behavior byte-for-byte: child death → read loop ends → finalize.

## 5. Crash detection and the reconnect gate

### 5.1 BeginReconnect — the pre-fault hook

`AcpConnection` gains a `BeginReconnect` callback invoked **before** its read-loop `finally` faults
pending requests, and from the write path when a send fails. Contract (design-of-record C6.2/C6.3):
synchronous, non-blocking, idempotent, and a no-op once intentional stop is marked. It only (a) flips
the runtime state to `Reconnecting` (closing the gate and starting notification suppression) and
(b) schedules the reconnect owner. All blocking work happens in the owner. This makes "`Reconnecting`
is set before any prompt-fault continuation runs" a guarantee — without it, the faulted turn's
continuation and the reconnect entry race.

### 5.2 What Reconnecting changes

- **Notification suppression:** `HandleNotification` drops every `session/update` while
  `Reconnecting` (no `_updates` write, no aggregation, no envelope). Scope is deliberately
  notifications-only: the turn worker's flush path and the owner's own emissions are not suppressed.
  Suppression covers both the dying connection's last-gasp updates and the replacement connection's
  entire replay; it ends only at gate reopen (§6 step 7).
- **Terminal signals withheld:** the connection-loop `finally` does not complete `_updates` when the
  runtime is `Reconnecting`; `ReadOutputAsync` is re-keyed from "this process exited" to "the runtime
  went terminal" (intentional stop, reconnect exhausted, or ineligible-launch process exit), so the
  orchestrator's finalize trigger never fires for a crash that reconnect will absorb.
  `HasExited`/`ExitCode` report **logical** liveness: `false`/`null` while `Reconnecting`.
- **The turn worker parks:** it awaits the gate before every dequeue. Queued turns survive in
  `_pendingTurns`; new `SendUserInputAsync` calls enqueue normally (bounded, as today).
- **The in-flight turn's flush is kept — C4 inverted.** The faulted `session/prompt`'s
  `finally { FlushOpenRun(); }` now emits a *correct* envelope: under skip-whole-replay nothing is
  ever re-emitted from replay, so the flushed partial run cannot be duplicated — it is the only copy
  of what the agent said before dying. The C4 discard machinery is deliberately not built.
- **Pending interactions voided:** entering reconnect cancels any in-flight permission/elicitation
  bridge request so no pending permission card outlives the crashed child; the dead child cannot
  receive an answer anyway.

`_connection`/`_process` stop being `readonly` and become swappable references guarded by the §7
lock.

## 6. The resume sequence

One reconnect owner per incident, at most **3 attempts** (backoff 1s/3s/9s, `TimeProvider`-driven for
test determinism), each step under a stop-cancellable token, using a relaunch closure the factory
supplies at construction (binary path, argv, env, cwd, the **original `mcpServers` list**, the
requested model — the runtime itself knows none of these today):

1. Spawn a fresh child via the same factory pathway as the original launch.
2. `initialize`; require protocol v1 and `loadSession` still advertised — else this attempt fails.
3. `session/load {sessionId, cwd, mcpServers}` with the same session id.
4. The response is the barrier: everything before it was replay (suppressed); non-conversation
   trailers after it (`available_commands_update`) are translator-dropped anyway.
5. Re-apply the originally-requested model via the vendor's model selector (best-effort, same
   non-fatal contract as launch — a no-op for vendors carrying `NoOpModelSelector`).
6. C8 disposition (§7): if the interrupted turn is provably-unsent, stage it for head-of-queue
   redelivery.
7. Under the reconnect lock: re-check stop; swap `_process`/`_connection`; re-arm the read loop;
   update `AgentInstance.Pid` and the **durable PID record** (the server's registry-independent
   physical stop and `kcap daemon status` must target the live incarnation, not the corpse); reopen
   the gate. The worker then delivers the staged turn (if any) first, then drains the queue.
8. Emit the §8 `system_note` envelope.

On attempt exhaustion, or when any eligibility fact degraded (e.g. `loadSession` withdrawn): the gate
opens into the terminal path and the agent finalizes exactly as an ineligible crash does today —
once, with a Warning and `acp.reconnects{outcome=exhausted}`.

**Flap containment:** at most **5 successful resumes per session lifetime**; the 6th crash finalizes.
A child that dies seconds after every resume is a broken installation, not a transient.

**Unchanged by design:** the SignalR server binding (same agentId, same ACP session id — C5), the
forwarder and its seq/ack state, the `_updates`/`Envelopes` channels, agent registration, and the
daemon slot. The server never learns a reconnect happened except via the `system_note` (and logs).

## 7. Interrupted-turn disposition (C8, at-most-once floor)

ACP has no prompt ack and no round-tripped prompt id (still true in the current spec), so the
in-flight turn's fate is decided by **local** facts only — never by replay content:

- **Provably-not-sent** — the send faulted before any byte was handed to the connection's stream
  write (serialization failure, or the write gate was never entered): the agent cannot have seen it.
  The owner re-stages **the original `PendingTurn` object** — same text *and* same write-ack
  `TaskCompletionSource` — for head-of-queue delivery at reopen, before any queued turn. A caller
  awaiting `SendUserInputAndWaitForWriteAsync` sees its ack resolve on the successful redelivery.
  The staged turn is delivered by the worker, not the owner, so single-flight turn serialization is
  preserved.
- **Anything else** (fully written, partially written, outcome unknown, or the turn was mid-stream
  when the child died): **surface, never requeue.** Replay-absence is not proof of non-delivery
  (Copilot measurably persists the interrupted turn agent-side; a blind requeue would duplicate a
  possibly side-effecting turn), and replay-presence is not proof it completed. The turn's write-ack
  TCS faults as it does today; the flushed partial run (§5.2) plus the `system_note` (§8) are the
  user-visible record.

Turns behind the in-flight one were never dequeued and simply run after reopen.

## 8. Surfacing: the `system_note` envelope (the one server touch)

After a successful resume the runtime emits one envelope with a new additive kind
`AcpEventKind.SystemNote = "system_note"`:

> "Agent process restarted; the session was resumed." — plus, only when a turn was actually in
> flight at the crash: "Your last message may not have been processed — resend it if the agent
> doesn't continue."

Wire safety is **verified, not assumed**: the server's per-envelope loop treats an unrecognised
`Kind` as *advance-without-persist* (`CapacitorHub.AcpSessionEvents`, the AI-685 Finding-7 branch —
mapped `null` → `Advance`, never a gap rejection), so a new daemon speaking to a current server
degrades to log-only without wedging the forwarder. A small kcap-server follow-up maps `system_note`
to a visible system/info event in `AcpSessionMapper` (+ `AcpEventKind` mirror constant); it is not a
blocker for this PR and ships separately.

## 9. Stop and dispose serialization (C7 + C9, unchanged in substance)

The design-of-record's contract is adopted as-is:

- One reconnect lock guards **only** fast state: the `Reconnecting`/intentional-stop flags, the
  owner's CTS cancellation, the swap-permission check, and candidate-child ownership transfer. It is
  never held across connection I/O, a request await, a process wait, or disposal — the C7 deadlock
  analysis (stop holding the lock while `BeginReconnect` blocks on it from the pre-fault path)
  depends on this.
- Stop, arriving at any point: cancels the owner's token, forbids further launch/swap (checked after
  every cancellation point, before installing a candidate), disposes any in-flight candidate child,
  and releases the parked terminal signal so finalization proceeds. All three stop entry points
  (`RequestGracefulStopAsync` / `WaitForExitAsync` / `TerminateAsync` as driven by the orchestrator)
  funnel into the same intentional-stop marking; a graceful-stop `session/cancel` notify against a
  dead connection is swallowed and logged while `Reconnecting`.
- The owner re-checks stop/terminal at every checkpoint — before relaunch, after each await, before
  `session/load`, before the swap, before reopening the gate, before staging a requeue — and unwinds
  in a `finally`: dispose its candidate, resolve `Reconnecting`, and drive the gate to the terminal
  path rather than leave it closed. No swap, no requeue, no envelope emission after intentional stop
  or terminal.
- Acceptance cases (each finalizes once, leaks nothing, emits nothing post-stop): stop while the
  owner is scheduled but not started; stop during relaunch / `initialize` / `session/load`; a
  terminate overlapping a pre-fault `BeginReconnect`; attempts exhausting during teardown.

`DisposeAsync`'s external contract is unchanged; it marks intentional stop first, so a concurrent
crash cannot resurrect a disposing runtime.

## 10. Observability

- `[LoggerMessage]` events (payload-free): reconnect started (agentId, vendor, attempt), resumed
  (attempt, suppressed-update count, elapsed), gave up (reason), suppressed-count summary.
- Metrics via the existing `AcpMetrics`: `acp.reconnects{outcome=resumed|exhausted|stopped}`,
  reusing `acp.sessions_loaded` for successful loads.
- The suppression window counts dropped updates; the count rides the resumed log line — a
  wildly-large count on a short session is the observable signature of a barrier violation.

## 11. Testing

All deterministic tests ride the existing in-memory ACP fabric (fake process + piped
`AcpConnection`), with a fake agent extended to serve `session/load` (replay N updates, then
respond) plus a deliberately barrier-violating variant:

1. Crash mid-turn → gate closes before the faulted turn's flush runs (pre-fault ordering); the
   partial run is **flushed and forwarded**, not discarded.
2. Replayed updates are fully suppressed; the forwarder's seq stream contains no duplicates; a
   queued turn parks and is sent only after reopen.
3. Barrier-violating fake (conversation update *after* the load response): the late update is
   emitted as live — the test pins that this is exactly the failure the per-vendor probe gate
   exists to exclude (defined, documented behavior; not silent corruption of the gate machinery).
4. Write-failure (not just read-EOF) enters reconnect via the same pre-fault path.
5. Provably-unsent in-flight turn: redelivered at head, original write-ack TCS resolves; ambiguous
   in-flight turn: TCS faults, no redelivery, `system_note` carries the resend sentence.
6. Every §9 stop acceptance case; exhaustion finalizes once; the 5-resume lifetime cap.
7. PID record and `AgentInstance.Pid` update on swap; `HasExited` reads false while reconnecting.
8. Ineligible launches (flag off / no `loadSession` / review-flow / kill switch) behave
   byte-for-byte as today: child death finalizes.

Live verification is the archived probe (§2), re-runnable per vendor; CI never spawns real vendor
binaries.

## 12. Non-goals and future work

- **No C3(a)/exactly-once requeue** until some vendor emits `messageId` (or a round-tripped prompt
  id) — re-probe trigger recorded on AI-1325's successor comment.
- **No Kiro/Gemini enablement** — each is a one-line descriptor flip plus a passing re-probe;
  upstream defects (Kiro stale lock; Gemini crash persistence) reported separately.
- **No daemon-restart survival** — the child dies with the daemon; that path stays owned by the
  existing lifecycle (registry reconcile, orphan reaper).
- **No flow-participant reconnect** — deliberate (§4.3).
- The kcap-server `system_note` rendering follow-up (§8).
