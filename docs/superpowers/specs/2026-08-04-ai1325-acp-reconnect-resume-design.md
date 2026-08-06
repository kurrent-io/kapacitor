# ACP hosted-agent reconnect/resume across child death — skip-whole-replay design

**Issue:** AI-1325 (deferred from AI-689 at its C0 hard gate; design-of-record `docs/ai-689-design.md` §4).
**Date:** 2026-08-04 (rev 9, after codex spec-review rounds 1–8). **Repo:** kcap-cli (daemon), plus
one small additive kcap-server follow-up (§8).
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
  already mandated (§7): the interrupted turn is never auto-completed and never blindly re-sent, and
  replay-only content for it is dropped. Measured: Cursor drops that turn from replay anyway;
  Copilot persists it agent-side, so a user resend reads as a normal follow-up in the agent's
  context.

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

## 5. Crash detection, send admission, and the reconnect gate

### 5.1 Incarnation identity and the lock order

Every connection/process pair this runtime ever spawns — the original launch and every reconnect
candidate — is assigned a unique, monotonically increasing **incarnation id** at spawn, never
reused. The runtime tracks the **installed** incarnation (the one serving the session). Crash
signals carry the incarnation id of the connection that raised them, and a signal is **live** only
if its id equals the installed id at the moment the signal takes the reconnect lock; anything else
is a no-op. This is strictly stronger than a swap-counter generation: two retry candidates can never
share a stamp, so a delayed callback from a disposed candidate can never impersonate its healthy
successor (spec-review r3 B2), and a pre-commit candidate's callback is inert by construction
because the candidate is not yet installed.

**Lock order:** the reconnect lock may acquire the aggregation lock (admission emits the
`UserMessage` envelope inside the critical section, §5.3); the reverse order never occurs. The
suppression flag read by `HandleNotification` is a volatile read, never a reconnect-lock
acquisition, so the read loop's notification path cannot deadlock against admission.

### 5.2 BeginReconnect — the crash triggers and incident chaining

`AcpConnection` gains a `BeginReconnect` pre-fault callback invoked **before** its read-loop
`finally` faults pending requests, and each connection carries a monotonic **transport-ended
latch** — a per-incarnation flag set as the first act of every trigger path (read-loop end,
stream-write failure), strictly before the hook is invoked. The latch is what lets a commit observe
a death whose only hook invocation fired while the candidate was still uninstalled and was
therefore rightly discarded (§6.3, r4 B2): an uninstalled callback stays globally inert, but its
source incarnation remembers the terminal fact locally, forever.

The hook contract (design-of-record C6.2/C6.3): synchronous, non-blocking (it takes only the
reconnect lock — a fast-state lock, never held across I/O), stamped with its connection's
incarnation id. The incident additionally tracks **`lastHandledCrashIncarnation`** — the id whose
crash the incident has already accepted — because one transport failure can legitimately raise the
hook more than once from the *same* incarnation (a write failure followed by the read loop's own
EOF), and incarnation uniqueness across processes provides no idempotence within one (r4 B1).
Under the lock:

- intentional stop marked, or the stamp is not the installed incarnation → **no-op**;
- installed incarnation and the runtime is **`Running`** → flip to `Reconnecting` (close the send
  gate, start notification suppression, snapshot the §5.3 in-flight registration as the incident's
  C8 input), record the stamp as `lastHandledCrashIncarnation`, and schedule the reconnect owner;
- installed incarnation, runtime **already `Reconnecting`**, and the stamp **equals**
  `lastHandledCrashIncarnation` → a duplicate signal for a crash already being handled → **no-op**;
- installed incarnation, runtime already `Reconnecting`, and the stamp **differs** from
  `lastHandledCrashIncarnation` — i.e. a *committed successor* died → set the incident's
  **`crashedAgainIncarnation`** marker to the stamp (id-qualified, not a bare boolean; setting it
  twice for the same id is idempotent). This is the successor-incident handoff (r3 B1): a
  just-committed candidate dying during the settlement/note/reopen window must neither be discarded
  nor spawn a second owner. The single owner observes the marker at its checkpoints (§6.4), records
  it as handled (`lastHandledCrashIncarnation` ← the marker, marker cleared), and folds the death
  back into its own attempt loop — exactly one owner per incident, always.

All blocking work happens in the owner. Triggers are deliberately narrow — only evidence the
**transport** is gone: the read loop ending (EOF or fault) while not intentionally stopped — the
primary signal — or a failure **of the connection stream write/flush itself** (`WriteLineAsync`'s
write path) on the installed incarnation.

A failure *before* the stream write — payload serialization, or any local fault while the write was
never entered — does **not** trigger reconnect: the child is healthy, and the turn faults exactly as
today. (A transport-level write failure is evidence the *transport* is unusable, not proof the child
exited — which is why §6.1 retires the old incarnation explicitly rather than assuming it died.)

### 5.3 Send admission — two lock-serialized transitions

The turn worker's loop becomes: dequeue → **admission** → acquire the turn-execution gate → send →
settle → release. Both admission points are short critical sections under the reconnect lock, and
the turn object is **always worker-owned** — the reconnect owner never touches turns (it only reads
the incident snapshot):

- **Admission (pre-gate):** performed after dequeue, *before* acquiring the turn-execution gate. If
  `Reconnecting` (or the gate is otherwise closed), the dequeued turn **parks** in the worker's
  single held-turn slot — no envelope, no ack action — and the worker awaits reopen without holding
  any gate. Otherwise, inside the same critical section, the turn is **registered as the in-flight
  turn for the installed incarnation** with write-state `not-started` **and its `UserMessage`
  envelope is emitted** (the aggregation lock nests inside — §5.1). Registration and transcript
  emission are therefore atomic with respect to any crash snapshot: a snapshot can never observe a
  registered turn whose envelope state is indeterminate.
- **Write entry (`TryEnterWrite`):** inside the turn-execution gate, immediately before the stream
  write, a second critical section re-checks `Reconnecting` and atomically advances
  `not-started → entered`. If it observes `Reconnecting` instead, the worker **does not write**: it
  moves the turn to the held slot flagged `skip-user-envelope` (the transcript already carries its
  envelope exactly once), leaves the ack untouched, **releases the turn-execution gate**, and only
  then awaits reopen. A parked turn therefore never holds the gate — from either park path — which
  is what makes §6.4's settlement wait deadlock-free. The failed write-entry guarantees the turn's
  bytes never reach any incarnation, old or new — the race a swap-counter alone cannot close,
  because no swap has happened yet.

`onWritten` advances `entered → written`. The settle step (the existing catch/finally) now consults
the state: the park paths are non-exceptional and never fault the ack; an exception can only reach
the catch once `TryEnterWrite` succeeded, where faulting the ack is correct for `entered` and a
no-op for `written` (the TCS already resolved; `TrySetException` against a resolved TCS is a no-op —
current behavior, now normative).

### 5.4 What Reconnecting changes

- **Notification suppression:** `HandleNotification` drops every `session/update` while the
  suppression flag is set (no `_updates` write, no aggregation, no envelope; a counter records the
  drops). Scope is deliberately notifications-only: the turn worker's flush path and the owner's own
  emissions are not suppressed. Suppression covers the dying connection's last-gasp updates and the
  candidate's entire replay; it ends at gate reopen (§6.4), and §6.5 bounds the residual between the
  barrier and reopen.
- **Terminal signals withheld:** the connection-loop wrapper completes `_updates` only when the loop
  ended for a non-reconnecting reason; `ReadOutputAsync` is re-keyed from "this process exited" to
  "the runtime went terminal" (intentional stop, reconnect exhausted or ineligible, process exit
  with reconnect not engaged), so the orchestrator's finalize trigger never fires for a crash that
  reconnect will absorb. `HasExited`/`ExitCode` report **logical** liveness: `false`/`null` while
  `Reconnecting`.
- **The in-flight turn's flush is kept — C4 inverted.** The faulted `session/prompt`'s
  `finally { FlushOpenRun(); }` now emits a *correct* envelope: under skip-whole-replay nothing is
  ever re-emitted from replay, so the flushed partial run cannot be duplicated — it is the only copy
  of what the agent said before dying. The C4 discard machinery is deliberately not built.
- **Pending interactions voided — the three-step sweep (r5 B1, r6 B1):** entering `Reconnecting`
  must void registered interactions without running foreign code under the fast lock **or on the
  pre-fault hook's stack**. Step one, inside the hook's critical section: flip the state and
  **mark** every registry entry `Cancelled` (a plain field write per entry — no token is signalled,
  no callback can run), capturing the marked set. Step two is **dispatched as separately
  supervised, fire-and-forget work** (explicitly scheduled tasks whose failures are logged, never
  awaited by the reconnect owner — r7 I2), and is internally ordered so that **terminal bookkeeping
  never depends on foreign callback completion** (r8 I1): for each swept entry, the
  incarnation-bound cancelled-response attempt and the entry's terminal transition + removal (the
  same `finally` discipline as any claim winner — `Responded`/`WriteFailed`, entry removed under
  the lock) are performed **first, independent of the token**; only then is the entry's
  cancellation token signalled — the one step that executes foreign callbacks — as its own
  best-effort tail. A callback that never returns therefore strands only that tail: the single
  response attempt has already happened, the registry entry is already gone (nothing leaks across
  incarnations), and the owner — which awaits none of this — has long since moved on toward
  retirement, attempts, reopen, or terminal. Card teardown through the token is consequently
  best-effort with a bounded normal path; the server-side pending-interaction lifecycle remains the
  backstop for a card whose token callback chain is wedged, exactly as it is today for any other
  daemon-side stall. Step three is the router's own obligation (below): phase two's may-start
  check refuses a swept entry, so an interaction registered just before the crash normally never
  surfaces a card (the bounded exception is the surfaced-then-cancelled window, below). No
  cancellation-token callback and no connection response write ever executes under the reconnect
  lock (the C7 lock-scope contract).
- **Interaction routing is state-derived and two-phase (r3 I2, r4 I1), with a single
  terminal-claim rule (r5 I1):** every incarnation's server→client request handler is one
  **router** installed at spawn and never swapped. Phase one, under the reconnect lock: consult
  (is my incarnation installed?) ∧ (is the runtime `Running`?) — if no, answer immediately with the
  decline/cancelled outcome, never surfaced; if yes, **atomically register the interaction** in the
  pending-interaction registry, keyed by (incarnation id, ACP request id). Phase two: a
  **lock-acquired may-start check** (r6 I2 — briefly take the fast lock, read the entry's state;
  anything but `Pending` means the sweep owns the outcome, so return without touching the bridge),
  then, after releasing the lock, invoke the real bridge under the entry's cancellation token, and
  **the bridge honors a pre-cancelled token before surfacing UI**. The may-start check is a
  filter, not an atomicity claim (r7 I1): a sweep can land in the post-check/pre-invoke window, and
  the contract for that window is *surfaced-then-cancelled* — the invocation runs under a token the
  sweep's step two signals, so the card either never appears (token already cancelled at
  invocation) or appears transiently and is torn down by the same cancellation path a user-visible
  cancel takes, bounded by the cleanup task's dispatch latency. Single-response correctness never
  depends on the window: the terminal-claim rule below holds regardless. Each
  entry is a small state machine — `Pending → Completing | Cancelled → Responded | WriteFailed`,
  transitions serialized under the reconnect lock — and **exactly one path may claim the transition
  out of `Pending`**: a bridge completion claims `Pending → Completing` and then (outside the lock)
  attempts the real response write; the crash/stop sweep claims `Pending → Cancelled` and then
  (outside the lock, off the hook's stack) the cancelled response write is attempted. The guarantee
  is **exactly one response attempt with exactly one owner** — not guaranteed delivery over a
  transport the same crash may have retired (r6 I1): whichever winner attempted the write
  transitions the entry to its terminal state in a `finally` — `Responded` on success,
  `WriteFailed` otherwise (logged; the child is dead or dying, and no second attempt is ever
  made) — and removes the entry under the lock, so no entry can leak across incarnations. Terminal
  stop/dispose drains any claimed-but-uncleaned entries the same way, without creating a second
  response attempt. Response writes are bound to the entry's incarnation and never execute under
  the lock. There is no "rewire the bridge" step to
  mis-order: a candidate's requests are declined while it is uninstalled *and* while recovery is
  still hidden post-commit, and become live exactly when the gate reopens.

`_connection`/`_process` stop being `readonly` and become swappable references guarded by the
reconnect lock.

## 6. The resume sequence

One reconnect owner per incident. **Attempts:** up to 3 candidate spawns per incident, at t=0, +1s,
+4s (delays of 1s then 3s between attempts; `TimeProvider`-driven for test determinism). A
`session/load` JSON-RPC **error** is terminal for the incident without consuming further attempts —
both measured refusal classes (Kiro's lock, Gemini's absence) are durable, and a session the vendor
refuses to load will not become loadable seconds later. Spawn, handshake, and transport faults are
retryable; a candidate that negotiates protocol ≠ 1 or no longer advertises `loadSession` is
terminal. Every step runs under a stop-cancellable token; the owner re-checks stop **and
`crashedAgain`** at every checkpoint (§6.4, §9).

### 6.1 Step 0 — retire the corpse (confirm or go terminal)

Before any candidate work, the owner retires the old incarnation, outside the reconnect lock:
cancel the old connection-loop's token, dispose the old `AcpConnection` (closing its streams),
`TerminateAsync` the old process **tree**, and await exit with a bounded wait. **Confirmed exit is a
precondition for every candidate handshake:** if the wait ends without confirmation that the old
tree is gone, the incident is **terminal** — no candidate is spawned, and the runtime finalizes
exactly as an ineligible crash does today (which is also precisely today's behavior for a process
that won't die: bounded teardown, then finalization — the design makes resume *stricter* than
finalization, never looser). Rationale: an unconfirmed corpse can still hold vendor-session
ownership against the candidate (the Kiro-lock failure mode), emit output nobody will hear, or leak
past a successful swap — `session/load` must never race a possibly-live prior owner. Retirement is
idempotent; a second entry (e.g. stop racing the owner) finds it already done.

### 6.2 The candidate contract (attempt-local, no global side effects)

The factory supplies a **pure spawn closure** at construction — binary path, argv, env, cwd, the
original `mcpServers` list, and the requested model; invoking it constructs a process +
`AcpConnection` (with a fresh incarnation id and the §5.4 router installed) and nothing else. No
agent registration, no forwarder, no slot accounting, no `AgentInstance` mutation. Per attempt, the
owner:

1. Invokes the closure; **writes the durable PID record for the candidate pid immediately at
   spawn** (before any handshake). Leak containment dictates this ordering: if the daemon dies
   mid-attempt, restart reclamation kills the recorded candidate. **If the record write itself
   fails, the attempt fails and the candidate is disposed before any handshake** — an unrecorded
   child may never proceed, or the containment story is fiction. On attempt failure the record is
   cleared (or replaced by the next attempt's spawn) in the same disposal path, bounding the window
   in which a stale record names a dead pid; residual PID-reuse risk is parity with the existing
   PID-record machinery, not worsened by this design. On incident give-up, terminal finalization
   clears records exactly as today.
2. **Wires the candidate's `BeginReconnect` hook at spawn**, stamped with the candidate's own
   incarnation id. This is safe arbitrarily early: the stamp is not the installed incarnation, so a
   firing during the attempt is a structural no-op (§5.1) — and it is required arbitrarily early,
   so that a death at any instant after commit is reportable with no wiring gap (§6.3).
3. Starts the **candidate's own read-loop task** and wires the candidate's notifications into the
   (suppressed) handler — this is what lets `initialize`/`session/load` responses resolve at all,
   and routes the replay into the suppression counter. A candidate dying mid-attempt faults only
   that attempt's pending requests (the owner's own awaits), failing the attempt; its hook fires
   and no-ops on its uninstalled stamp.
4. Inbound candidate requests during recovery are declined by the §5.4 router (uninstalled
   incarnation ⇒ decline) — recovery is headless, and a card raised for a hidden replay would be
   answerable against state the user cannot see. Measured, neither vendor issues requests during
   load; a vendor whose load *requires* interaction will fail its load and the incident goes
   terminal — the honest outcome.
5. `initialize`; require protocol v1 and `loadSession` (terminal otherwise, per above).
6. `session/load {sessionId, cwd, mcpServers}` with the same session id. The response is the
   protocol barrier: everything before it was replay, and it was suppressed.
7. Re-applies the originally-requested model via the vendor's model selector (best-effort, same
   non-fatal contract as launch — a no-op for vendors carrying `NoOpModelSelector`).

A failed attempt's `finally` disposes the candidate (process tree + connection) and clears its PID
record before any retry delay. Disposal does not need to "unwire" anything for safety — a disposed
candidate's already-captured callback stays inert forever because its incarnation id can never
become installed (r3 B2).

### 6.3 Commit

Under the reconnect lock (fast state only): re-check stop and the chained-crash marker; **verify
candidate liveness** — the candidate's **transport-ended latch is unset** (§5.2; this, not the
read-loop task's completion state, is the authoritative death fact — the latch is set strictly
before the hook fires, so a death whose only hook invocation was discarded as uninstalled is still
visible here, closing the r4 B2 window) and its process has not exited (fast reads); swap
`_process`/`_connection`; adopt the candidate's already-running read-loop task; unwire the corpse's
notification handler; **set the installed incarnation to the candidate's id**. `AgentInstance.Pid`
now reflects the new child (the durable record already did, from §6.2 step 1).

The death-racing-commit window is closed from both sides by the latch and identity, not timing: a
candidate death *before* the install set the latch before its (discarded, uninstalled-stamped) hook
fired, so the in-lock latch check fails the attempt; a death *after* the install fires the hook
with the installed stamp — and because the runtime is still `Reconnecting` and the stamp differs
from `lastHandledCrashIncarnation`, that sets the chained-crash marker (§5.2), which the owner
observes at its very next checkpoint. The latch-set → hook-invoke ordering plus the shared lock
make the intermediate case impossible: either the latch is visible at commit, or the hook
serializes after commit and takes the chained-crash arm. If the liveness check fails, the owner
disposes the candidate and the attempt fails normally.

### 6.4 Settle, note, reopen — and the crashedAgain checkpoints

After commit the owner **awaits the incident turn's settlement** — the turn-execution gate coming
free, which by construction (§5.3: neither park path holds the gate) means the faulted
`ProcessTurnAsync`, including its retained partial flush, has fully completed. The wait runs outside
the reconnect lock, under the owner's stop-cancellable token, with a bounded timeout (generous —
the faulted turn's awaits were already faulted, so completion is prompt; a pathological hang goes
terminal rather than waiting forever). The owner then emits the `system_note` envelope (§8) — still
`Reconnecting`, worker still parked — and finally performs the **reopen transition: one atomic,
lock-linearized operation** (r4 B3) that re-checks stop, the installed incarnation, and the
chained-crash marker, and — only if all are clean — sets the state to `Running` and opens the gate
before releasing the lock. A crash callback serializing *before* the transition sets the marker and
the reopen is refused; one serializing *after* observes `Running` and starts a fresh incident
through the front door. No marker can be stranded across the reopen, because the final check and
the state change share one critical section. Ordering is deterministic: partial flush → note →
held turn (if any) → queued turns.

**If the chained-crash marker is observed at any checkpoint** — after commit, after settlement,
before the note, or by the reopen transition itself — the owner does not reopen. It records the
marker as handled (§5.2), retires the just-installed (now dead) incarnation via §6.1, and continues
its own attempt loop against the remaining attempt budget and the same terminal rules. A note
already emitted for a resume whose successor then died is left in place — each note truthfully
described a completed resume, and the next successful resume emits its own. Exactly one owner
drives the whole chain; the gate stays closed throughout; exhaustion goes terminal as below.

On attempt exhaustion or a terminal condition: the gate opens into the terminal path and the agent
finalizes exactly as an ineligible crash does today — once, with a Warning and
`acp.reconnects{outcome=exhausted}`. The owner's `finally` disposes any live candidate it still
owns, clears that candidate's PID record, resolves `Reconnecting`, and never leaves the gate closed.

**Flap containment and the success linearization point (r5 I2):** a resume **counts** — for the
lifetime cap, the `acp.reconnects{outcome=resumed}` metric, and the resumed log line — exactly when
the atomic reopen transition commits to `Running`. A candidate that loads, commits, and even has
its note emitted, but dies before the reopen transition, is **not** a resume: it consumed attempt
budget only, and its note (already truthful about the completed load/commit) stays per §6.4. At
most **5 counted resumes per session lifetime**; the 6th crash finalizes. A child that dies seconds
after every resume is a broken installation, not a transient.

### 6.5 Suppression residual (documented, bounded)

Between the load response (the protocol barrier) and gate reopen, the only work is model re-apply,
the C8 read, the commit, the settlement wait, and the note — a short, prompt-free window. A
conversation update cannot legitimately arrive there: ACP turns are prompt-driven, `session/load`
does not auto-resume an interrupted turn (measured on both capable vendors), and no prompt is in
flight. What can arrive is orderless metadata (`available_commands_update`, `config_option_update`,
`usage_update`, `session_info_update`) whose loss is self-healing — the translator drops the first
two anyway, usage is peak-compared server-side and re-arrives with the next turn, and a replayed
title was already forwarded before the crash. The probe additionally observed zero conversation
updates in a 3s post-response window on both vendors.

**Unchanged by design:** the SignalR server binding (same agentId, same ACP session id — C5), the
forwarder and its seq/ack state, the `_updates`/`Envelopes` channels, agent registration, and the
daemon slot. The server never learns a reconnect happened except via the `system_note` (and logs).

## 7. Interrupted-turn disposition (C8, at-most-once floor)

ACP has no prompt ack and no round-tripped prompt id (still true in the current spec), so the
in-flight turn's fate is decided by **local** facts only — never by replay content. The §5.3
registration is the single source of truth, `BeginReconnect` snapshots it atomically with the gate
close, and the turn object is always worker-owned — the owner reads the snapshot solely for the
resend-sentence decision and logging:

- **Parked at pre-gate admission** (dequeued while the gate was closed, or never dequeued):
  provably never sent — no envelope emitted, no ack touched. Delivered after reopen (held turn
  first, then the queue) as an ordinary turn: its `UserMessage` envelope is emitted at its
  (re)admission, and a caller awaiting `SendUserInputAndWaitForWriteAsync` sees its ack resolve on
  the eventual successful write.
- **Parked at write entry** (registered `not-started`; `TryEnterWrite` observed `Reconnecting`):
  equally provably-unsent — the failed write-entry guarantees its bytes never reached any
  incarnation — but its `UserMessage` envelope is already in the transcript (emitted atomically
  with registration, §5.3). The worker parks it flagged `skip-user-envelope` and releases the
  turn-execution gate before awaiting reopen; its ack was never faulted (the settle path never ran
  a fault for it) and resolves on the eventual write after reopen.
- **Write-state `entered` or `written`:** ambiguous or delivered-but-unresponded — **surface, never
  re-send.** Replay-absence is not proof of non-delivery (Copilot measurably persists the
  interrupted turn agent-side; a blind re-send would duplicate a possibly side-effecting turn), and
  replay-presence is not proof it completed. For `entered`, the ack TCS faults (as today); for
  `written`, the ack already resolved at write time and stays resolved. The flushed partial run
  (§5.4) plus the `system_note` resend sentence (§8) are the user-visible record.

Turns behind the in-flight one were never dequeued and simply run after reopen.

## 8. Surfacing: the `system_note` envelope (the one server touch)

After commit and the settlement wait, before gate reopen (§6.4), the runtime emits one envelope
with a new additive kind `AcpEventKind.SystemNote = "system_note"`:

> "Agent process restarted; the session was resumed." — plus the resend sentence, **iff the
> incident's C8 snapshot held a turn in write-state `entered` or `written`** (the surfaced cases —
> a parked or write-entry-parked turn is delivered automatically and needs no user action): "Your
> last message may not have been processed — resend it if the agent doesn't continue."

Wire safety is **verified, not assumed**: the server's per-envelope loop treats an unrecognised
`Kind` as *advance-without-persist* (`CapacitorHub.AcpSessionEvents`, the AI-685 Finding-7 branch —
mapped `null` → `Advance`, never a gap rejection), so a new daemon speaking to a current server
degrades to log-only without wedging the forwarder. A small kcap-server follow-up maps `system_note`
to a visible system/info event in `AcpSessionMapper` (+ `AcpEventKind` mirror constant); it is not a
blocker for this PR and ships separately.

## 9. Stop and dispose serialization (C7 + C9)

The design-of-record's contract is adopted with one precision (the emission fence):

- One reconnect lock guards **only** fast state: the `Reconnecting`/intentional-stop flags, the
  installed incarnation id, `lastHandledCrashIncarnation` and the chained-crash marker, both
  send-admission transitions, the in-flight registration snapshot, the interaction router's
  admit-and-register decision, the reopen transition, the owner's CTS cancellation, the
  swap-permission and candidate-liveness (transport-ended latch) checks, and candidate-child
  ownership transfer. It is
  never held across connection I/O, a request await, a process wait, durable-record I/O, or
  disposal — the C7 deadlock analysis (stop holding the lock while `BeginReconnect` blocks on it
  from the pre-fault path) depends on this. The §6.2 PID-record write is I/O and therefore happens
  outside the lock, which is safe precisely because it happens at spawn, before the candidate is
  reachable through any registry state. The one lock nesting is reconnect → aggregation (§5.1),
  never the reverse.
- Stop, arriving at any point: marks intentional stop and cancels the owner's token under the lock,
  forbids further launch/swap (checked after every cancellation point, before installing a
  candidate), disposes any in-flight candidate child, and releases the parked terminal signal so
  finalization proceeds. All stop entry points funnel into the same intentional-stop marking; a
  graceful-stop `session/cancel` notify against a dead connection is swallowed and logged while
  `Reconnecting`. Corpse retirement (§6.1) is idempotent against a concurrent stop's own teardown.
- The owner re-checks stop/terminal **and `crashedAgain`** at every checkpoint — before retirement,
  before each spawn, after each await, before `session/load`, before commit, after settlement,
  before the note, before reopening the gate — and unwinds in a `finally`: dispose its candidate,
  clear that candidate's PID record, resolve `Reconnecting`, and drive the gate to the terminal
  path rather than leave it closed. **No swap and no held-turn delivery after stop** — both are
  lock-serialized decisions.
- **The emission fence (C9's "no emission after stop", made precise):** envelope emission is fenced
  by the transcript channel's completion, exactly as today's dispose path — `EmitEnvelope` against a
  completed channel is a logged drop. A courtesy flush or a `system_note` racing a concurrent stop
  therefore either lands before the channel completes (delivered; identical in kind to today's
  dispose-time courtesy flush) or drops at the completed channel. What is categorically impossible
  is emission after final drain, because the channel completes before the drain reads to end. The
  acceptance cases below are stated against this fence, not against an unimplementable
  every-emission lock.
- Acceptance cases (each finalizes once, leaks nothing — corpse, candidate, and PID records
  accounted — and performs no swap or held-turn delivery post-stop): stop while the owner is
  scheduled but not started; stop during retirement / relaunch / `initialize` / `session/load`; a
  terminate overlapping a pre-fault `BeginReconnect`; a crash-again overlapping a stop; attempts
  exhausting during teardown.

`DisposeAsync`'s external contract is unchanged; it marks intentional stop first, so a concurrent
crash cannot resurrect a disposing runtime.

## 10. Observability

- `[LoggerMessage]` events (payload-free): reconnect started (agentId, vendor, incarnation,
  attempt), corpse retired (confirmed/unconfirmed), resumed (attempt, suppressed-update count,
  elapsed), crashed-again (chained), gave up (reason), per-incident suppressed-count summary.
- Metrics via the existing `AcpMetrics`: `acp.reconnects{outcome=resumed|exhausted|stopped}`,
  reusing `acp.sessions_loaded` for successful loads.
- The suppression counter rides the resumed log line — a wildly-large count on a short session is
  the observable signature of a barrier violation.

## 11. Testing

All deterministic tests ride the existing in-memory ACP fabric (fake process + piped
`AcpConnection`), with a fake agent extended to serve `session/load` (replay N updates, then
respond) plus a deliberately barrier-violating variant:

1. Crash mid-turn → gate closes before the faulted turn's flush runs (pre-fault ordering); the
   partial run is **flushed and forwarded**, not discarded.
2. Replayed updates are fully suppressed; the forwarder's seq stream contains no duplicates; a
   queued turn parks and is sent only after reopen.
3. **The pre-gate admission race:** a turn dequeued after the gate closed parks (no send at the
   dead incarnation, no fault, no envelope; ack resolves after resumed delivery).
4. **The write-entry race:** a crash snapshot taken while a registered turn is `not-started` →
   `TryEnterWrite` refuses the write, the worker **releases the turn-execution gate before
   parking** (no settlement deadlock); the turn is delivered after reopen exactly once with no
   duplicate `UserMessage` envelope and its ack resolving; no bytes reach the old connection after
   the snapshot.
5. A pre-write local failure (e.g. serialization) faults the turn **without** entering reconnect —
   the healthy child keeps streaming and nothing is suppressed.
6. A connection write failure enters reconnect via the same incarnation-stamped path as read-EOF;
   a stale-incarnation trigger is a no-op; **a write failure followed by the same connection's
   read-EOF is one incident, not a chained crash** (duplicate-signal idempotence via
   `lastHandledCrashIncarnation` — the r4 B1 case).
7. **Corpse retirement:** the old process tree is terminated and its exit **confirmed** before the
   first candidate handshake; when confirmation fails, the incident goes terminal and **no
   candidate handshake ever begins**; a still-alive old child cannot emit into the transcript
   after retirement.
8. Candidate lifecycle: a candidate dying during `initialize`/`session/load` fails only that
   attempt (global state untouched, next attempt proceeds); a `session/load` error is terminal
   without further attempts; the candidate's replay reaches the suppression counter; an inbound
   permission/elicitation request from an uninstalled candidate is declined by the router and never
   surfaces — including after the crash hook is wired.
9. **Identity fencing and the latch:** a delayed `BeginReconnect` callback from a disposed earlier
   candidate no-ops even after a later candidate commits (unique incarnation ids — the r3 B2 case);
   a candidate death just before commit — its only hook invocation discarded as uninstalled — still
   fails the attempt because commit reads the **transport-ended latch** (the r4 B2 case).
10. **Incident chaining:** a candidate death after commit (installed stamp, still `Reconnecting`,
    stamp ≠ `lastHandledCrashIncarnation`) sets the id-qualified chained-crash marker; the owner
    skips reopen, retires the dead successor, and continues its attempt loop — one owner, gate
    closed throughout; a crash after reopen starts a fresh incident; **the reopen transition is
    atomic** — a crash callback racing it either refuses the reopen (marker set first) or starts a
    fresh incident (observes `Running`), never a stranded marker (the r4 B3 case).
11. C8 dispositions: all four (§7) including ack semantics; the `system_note` resend sentence
    appears exactly for `entered`/`written`.
12. Ordering: partial flush → `system_note` → held turn → queued turns (the settlement wait), and
    the note precedes every resumed envelope; the interaction router serves the real bridge only
    once the candidate is installed **and** the gate has reopened; **an interaction admitted just
    before a crash is registered under the lock and therefore swept by the crash's voiding pass —
    no orphan card** (the r4 I1 case); **the sweep marks entries under the lock but signals tokens
    outside it** — pinned by a test whose cancellation callback synchronously re-enters the runtime
    (the r5 B1 C7 property); **`BeginReconnect` returns before any token is signalled** — pinned by
    a test whose cancellation callback blocks until after the hook has returned (the r6 B1 case);
    **a permanently-blocked cancellation callback cannot wedge the owner** — reconnect still
    reaches resumed or terminal while the callback never unblocks (the r7 I2 case); **nor can it
    defeat terminal bookkeeping** — with the callback still blocked, the swept entry's single
    cancelled-response attempt has run and the registry entry is removed (the r8 I1 case); **the
    post-check/pre-invoke sweep window is surfaced-then-cancelled** — inject the sweep after the
    may-start lock releases but before bridge invocation: the card is torn down by the token path
    and exactly one cancelled response is written (the r7 I1 case);
    **exactly one response attempt per interaction** — a bridge completion racing the sweep yields
    one winner via the entry's `Pending → Completing | Cancelled` claim, never two attempts and
    never none, and a winner whose write fails still reaches a terminal cleaned state
    (`WriteFailed`) with the entry removed — no leak across incarnations (the r5 I1 + r6 I1
    cases); **the may-start check is lock-acquired** — a router racing the sweep never invokes the
    bridge for a swept entry (the r6 I2 case); **a commit-then-chained-death is not a counted
    resume** — the cap and `outcome=resumed` increment only at the atomic reopen (the r5 I2 case).
13. Barrier-violating fake (conversation update *after* the load response): the late update is
    emitted as live — the test pins that this is exactly the failure the per-vendor probe gate
    exists to exclude (defined, documented behavior; not silent corruption of the gate machinery).
14. Every §9 stop acceptance case (including crash-again overlapping stop); exhaustion finalizes
    once; the 5-resume lifetime cap; the emission fence (a racing courtesy flush either precedes
    channel completion or drops — never lands after final drain).
15. PID records: written at candidate spawn; a failed record write disposes the candidate and fails
    the attempt; cleared on candidate disposal and at terminal cleanup; `AgentInstance.Pid` updates
    at commit; `HasExited` reads false while reconnecting.
16. Ineligible launches (flag off / no `loadSession` / review-flow / kill switch) behave
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
