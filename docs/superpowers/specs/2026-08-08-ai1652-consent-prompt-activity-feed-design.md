# Consent prompt window + Activity feed (desktop supervisor, AI-1652)

Umbrella: `2026-07-31-desktop-supervisor-app-design.md` §5–6. Builds on the AI-1650 app shell,
the AI-1651 tray/agents/stop slice, and the AI-1623/AI-1648 consent IPC. One PR.

## 1. Problem

The daemon-side consent machinery is complete: a `prompt`-mode launch parks at
`LaunchConsentGate`, the broker replays/pushes `ConsentPending` frames to local-socket
subscribers, and `ConsentResolve` settles the launch. But nothing subscribes. On a machine
running the app, a `prompt` policy currently burns the AI-1648 subscriber grace and denies as
`prompt_no_ui` — the original "daemon launches without asking" complaint is only answerable
once the app raises a prompt. Separately, every decision (rule-matched and human) is appended
to `consent-decisions.jsonl`, but no UI renders it.

This slice ships the consent UX: an auto-raised prompt window, an Activity tab rendering the
decision log, and pending-consent wired into the tray's existing `Attention` state.

## 2. Decisions (settled in brainstorming, 2026-08-08)

| Decision | Choice |
|---|---|
| Activity feed read surface | **Direct file read** of `consent-decisions.jsonl` (+ `.1`), formalized as a documented read-only contract with a shared record type in Core. No new IPC frames. Precedent: `kcap daemon consent log` already reads the file and works with the daemon stopped. |
| "Always allow" rule scope | **Requester-only**: `(action: allow, requester: <id>, kind: null, repo: null, vendor: null)`. The trust decision is about the person. Finer grain arrives with the Settings rules editor (umbrella slice 4). |
| macOS system notification | **Deferred to AI-1653.** `UNUserNotificationCenter` needs a signed bundle identity; the auto-raised prompt window is the always-works path (umbrella §11). |
| Main window layout | **Tabs**: Agents \| Activity. Settings slots in as a third tab in slice 4. |
| Multiple pending requests | **One window, queued**: shows the oldest pending with a "1 of N" indicator; deciding or expiry advances. |

## 3. Scope

**In:** consent subscription service in the app; prompt window (Allow once / Allow & remember
/ Deny, countdown, queue); Activity tab; tray `Attention` on pending consent + "Review pending
launches…" menu item; `ResolveConsentAsync` on `ILocalControlOps`; consent subscription client
in Core; decision-record hoisting to Core (shared write/read shape); `requester_display`
threading through the consent pipeline (additive); `rule_saved` reporting on `ConsentAckDto`
(additive) so a rule installed for an already-decided request is disclosed, not hidden; a
daemon-minted `prompt_id` request identity on `ConsentPendingDto` echoed on
`ConsentResolveDto` and atomically claimed by the broker, carried on **new v2 consent frame
types** an old daemon structurally cannot decode (fail-closed on the wire) and advertised as
a new `consent/2` capability for discovery, so a stale resolve can never decide a different
launch that reused the id; hoisting the AI-1651 per-window ticker into an app-lifetime
service; headless ViewModel tests for the full prompt matrix.

**Out:** macOS system notification (AI-1653); consent-rules editor UI (slice 4); any change to
engine matching, rule ordering/precedence, policy storage, or prompt timeout semantics; new
IPC frame types; Windows/Linux app packaging.

## 4. Wire & Core changes

Everything below is additive: two new append-only `FrameType` values (§4.1) and trailing
DTO fields; no protocol version change and no change to any existing frame's semantics.

### 4.1 `ResolveConsentAsync` on `ILocalControlOps`, and `rule_saved` on the ack

```csharp
Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct);
```

One-shot, exactly the existing `LocalControlOps` pattern (connect → send → single reply →
close): writes `FrameType.ConsentResolveV2` (below) with the `ConsentResolveDto` JSON as
`Text`, expects exactly one `ConsentAck` frame, deserializes `ConsentAckDto`. Per-phase timeouts and failure
classification are identical to the existing ops — caller-cancellation propagates
(`OperationCanceledException` checked first), `EndOfStreamException` before `IOException`
(derivation order) → `unexpected_reply`, transport → `daemon_unreachable`, phase timeout →
`timed_out`, a decodable `Error` frame → `daemon_rejected` (matching the existing consent
ops), any other frame type or a null/malformed ack → `unexpected_reply`.

**`ConsentAckDto` gains trailing `bool? RuleSaved`** (wire `rule_saved`, positional-required
convention: no C# default, nulls always written). Daemon semantics: null when the resolve
carried no `save_rule`; `true`/`false` = the rule write succeeded/failed. The daemon's
`HandleResolveAsync` deliberately persists `save_rule` *before* attempting the resolution —
"Allow & remember" expresses durable trust in the requester, which stands even when this
particular launch has already been decided — so today an `Ok=false` ack can silently hide an
installed rule. The handler change: populate `RuleSaved` on **both** ack branches (today the
no-pending branch drops the save outcome entirely). `Ok`'s meaning is unchanged: it reflects
the resolution outcome only.

**Down-level daemons are gated out, not disambiguated.** A pre-AI-1652 daemon advertises the
same `consent/1` capability, omits `rule_saved` (deserializes as null), and — critically —
silently ignores unmapped members on `ConsentResolveDto`, so it would resolve by id alone and
void the identity guarantee below. The app therefore requires the new **`consent/2`**
capability (below) before subscribing or resolving at all; behind that gate `RuleSaved` is
always present. The service still keeps a *defensive* mapping (§5): `save_rule` sent but
`RuleSaved` null → rule outcome unknown — unreachable behind the gate, honest if it ever
fires.

**Rule ordering is deliberately untouched.** The handler appends the saved rule at the end
and `LaunchConsentEngine` is first-match-wins, so an earlier matching rule — the pause
wildcard deny at `rules[0]`, or any manual deny — takes precedence over an appended allow
until that earlier rule is removed. This is correct: a remembered allow must never silently
override pause or an explicit deny. The UI copy carries the consequence honestly (§6: the
button says "Allow & remember", not "Always allow" — the label must not promise what
precedence may withhold).

The daemon resolve handler otherwise exists (`LaunchConsentIpc.HandleResolveAsync`); no hello
is needed on one-shot connections (hello is optional; the CLI consent verbs already send bare
frames).

**Request identity: the daemon-minted `prompt_id`.** `ConsentResolveDto` carries only
`request_id` today, and the broker's `TryResolve` resolves whichever pending entry currently
holds that key. A request id is the agent id, and the daemon explicitly makes **no
id-non-reuse assumption** (`SequencedCommandProcessor` treats two launches for one id as
distinct instances, and the orchestrator builds consent input afresh from each command) — so
a successor B under A's id can carry a *different* requester, kind, repo, or vendor, and a
delayed resolve for A must never decide B. Wall-clock stamps cannot serve as the
discriminator (`RequestedAt` comes from `GetUtcNow()` — clock steps can revisit an earlier
exact value, and OS clock resolution can repeat across calls), so the identity is an opaque
value the daemon mints:

- `LaunchConsentGate` mints a fresh **`PromptId`** (`Guid.NewGuid().ToString("N")` —
  process-lifetime unique by construction, no clock involvement) for every prompt entry;
  `LaunchConsentPromptRequest` carries it.
- `ConsentPendingDto` gains trailing `string? PromptId` (wire `prompt_id`,
  positional-required convention: no C# default, nulls always written), appended **after**
  §4.3's `RequesterDisplay` — final positional order: …, `RequesterDisplay`, `PromptId`.
- `ConsentResolveDto` gains trailing `string? PromptId` (wire `prompt_id`) — the echo.
- `LaunchConsentBroker.TryResolve` gains the echo parameter, and for a non-null echo the
  match and removal are **one atomic claim**: look up the pending entry, compare the echo to
  its `PromptId`, then conditionally remove the exact captured `Pending` *object* via the
  `KeyValuePair`-conditional overload — the same primitive the broker's timeout/cleanup paths
  already use — before completing its TCS. A mismatch, or a lost claim (the entry was
  replaced between match and removal), returns false and **never retries against the
  successor**; the handler acks `Ok=false`, the honest already-decided path. A null echo (a
  caller that never saw the id) preserves legacy resolve-by-key behavior.
- The app always sends the echo (§5).

`PromptId` is thereby the **request identity**, enforced end-to-end — the client's cache
guards and tombstones (§5) key on it, `RequestId` remains only the cache/broker key and the
per-agent queue identity.

**V2 frames: enforcement is structural, not negotiated.** The identity guarantee is only real
when the daemon enforces it — a pre-AI-1652 daemon deserializes the v1 resolve while silently
dropping the unmapped `prompt_id` and resolves by id alone. A capability check cannot close
this: capabilities are learned on one socket while consent ops travel on fresh bare-frame
connections, so a daemon restart/downgrade between them (TOCTOU) would route a v2-shaped
resolve into the v1 handler. The v2 operations therefore ride **new append-only frame
types** — `ConsentSubscribeV2 = 17` and `ConsentResolveV2 = 18` — which the app uses
exclusively. A v1 daemon's routing switch answers any unknown frame type with an `Error`
frame (`LocalControlServer` default arm), so against a v1 incarnation the v2 resolve draws
`daemon_rejected` (no resolution occurred — the cached prompt is *structurally impossible* to
resolve through the v1 id-only handler, whichever incarnation answers the socket) and the v2
subscribe ends without registering a subscriber (the v1 daemon keeps its fast `prompt_no_ui`
denials instead of waiting on a subscriber that renders nothing). On a v2 daemon, frame 17
routes to the same subscribe handler as frame 11, and frame 18 routes to a resolve path that
**requires** `prompt_id` (missing/empty → `Ok=false` "invalid resolve payload"); the v1
frames 11/12 remain routed unchanged for legacy callers, served by `TryResolve`'s null-echo
path.

**Capability `consent/2`** is appended to `LocalControlCapabilities.Current` as the
*discovery* signal (advertised, per the AI-1648 convention, only because the live v2 handlers
exist): §5 uses it to decide whether to run the consent loop and to surface the down-level
condition. Security does not rest on it — the frames fail closed on their own.

### 4.2 Consent subscription client (Core)

`ConsentSubscription` in `Capacitor.Cli.Core/LocalIpc/`:

```csharp
public abstract record ConsentStreamEvent {
    public sealed record Subscribed : ConsentStreamEvent;               // connect + subscribe write succeeded
    public sealed record Pending(ConsentPendingDto Request) : ConsentStreamEvent;
}

public static async IAsyncEnumerable<ConsentStreamEvent> RunAsync(
    string daemonName, [EnumeratorCancellation] CancellationToken ct)
```

Connects to the daemon's socket, writes `FrameType.ConsentSubscribeV2` (empty text; §4.1 —
a v1 daemon answers the unknown frame with `Error`, ending the enumeration without
registering a subscriber), **yields `Subscribed` once** immediately after that write
succeeds, then reads frames and yields one `Pending` per `ConsentPending` frame. The `Subscribed` event exists because an async iterator
exposes no other observable boundary between "dialing" and "subscribed": with an empty replay
the first read never completes, so a consumer that must act at the subscribe boundary (the §5
cache clear) would otherwise have no signal. The daemon replays the full pending set
immediately after the subscribe registers, then pushes new requests (broker contract:
exactly-once per subscriber, no withdrawal push).

**`Subscribed` is a client-local boundary, not a server acknowledgment.** A flushed subscribe
write does not prove the daemon registered the subscription — the peer can die between the
write and the replay. §5 states the accepted consequence.

**Termination and validation contract:**

- EOF, transport `IOException`, and `SocketException` (a failed *connect* throws
  `SocketException` directly — it is not an `IOException`) end the enumeration normally: an
  ended stream means "this attempt is over", and the consumer decides whether to resubscribe.
- A frame of any type other than `ConsentPending`, or a frame whose JSON does not
  *deserialize*, also ends the enumeration — protocol confusion is a dead connection.
- A frame that deserializes but is **structurally invalid** — null root, or null/empty
  `RequestId`, `PromptId` (a `consent/2` daemon always stamps it — §4.1), `Kind`, `RepoPath`,
  `Vendor`, `RequestedAt`, or `TimeoutSeconds <= 0` (STJ source-gen does not enforce
  non-nullable members; `{}` decodes "successfully") — is **skipped**, not fatal. Ending the enumeration here would be a thrash loop: the resubscribe
  replay would redeliver the same invalid entry forever.
- Only `OperationCanceledException` (the caller's `ct`) propagates.

### 4.3 `requester_display` threading (additive)

The consent pipeline predates AI-1791's `RequesterDisplay` on `LaunchAgentCommand` and still
carries only the requester *id* — the prompt and the feed would show `github:2821205`, which
the grid work already established is unacceptable. Thread the display name through, additively:

- `LaunchConsentInput` gains trailing `string? RequesterDisplay`. Engine matching is untouched
  — rules match on `RequesterUserId` only.
- `LaunchConsentPromptRequest` gains trailing `string? RequesterDisplay`.
- `ConsentPendingDto` gains trailing `string? RequesterDisplay` (wire `requester_display`,
  positional-required convention: no C# default, nulls always written). A pre-AI-1652 daemon
  never sends it; STJ source-gen leaves the member null on absence, and the app falls back to
  the id.
- The hoisted decision record (§4.4) gains trailing `string? RequesterDisplay`
  (`requester_display`); old log lines lack the field and read back as null.
- `AgentOrchestrator` passes `cmd.RequesterDisplay` into `LaunchConsentInput` at the gate call
  site; `LaunchConsentGate` threads it into the prompt request and into `Done()`'s record.

Presentation rule everywhere (prompt, feed): `RequesterDisplay ?? Requester ?? "unknown"` —
same fallback chain as `AgentRowViewModel.Requester`.

### 4.4 Decision-log read contract (record hoisting)

The daemon's internal `LaunchConsentRecord` + private JSON context move to Core as the single
write/read shape — the "documented read-only contract" is a shared type, not prose:

```csharp
// Capacitor.Cli.Core/LocalIpc/ConsentDecisionLog.cs
public sealed record ConsentDecisionRecord(
    string DecidedAt, string AgentId, string? Requester, bool RequesterIsOwner,
    string Kind, string RepoPath, string Vendor, string Outcome, string Source,
    string? RequesterDisplay);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ConsentDecisionRecord))]
public partial class ConsentDecisionJsonContext : JsonSerializerContext;
```

Field names are the existing snake_case wire names verbatim (`decided_at`, `agent_id`,
`requester`, `requester_is_owner`, `kind`, `repo_path`, `vendor`, `outcome`, `source`) plus the
new trailing `requester_display` — existing log files remain readable byte-for-byte.
`Outcome` is `allowed | denied`; `Source` is `owner | rule[i] | default | prompt_no_ui |
prompt_user | prompt_timeout` (engine + gate, recorded verbatim). `Kind` is the engine token
`agent | review | review-flow`.

The daemon's `LaunchConsentDecisionLog` writes this Core type via this Core context (its
internal record and private context are deleted); its writer behavior — append-only, 0600 from
first byte, 1MB rotation to `.1` via `File.Move` — is unchanged. The CLI `log` verb prints raw
lines and is unaffected.

**Reader** (Core, same file):

```csharp
public sealed record ConsentLogReadResult(IReadOnlyList<ConsentDecisionRecord> Records, bool Complete);

public static class ConsentDecisionLogReader {
    public static string PathFor(string daemonName);   // DaemonLockPaths.Directory / Sanitize(name) / consent-decisions.jsonl
    public static ConsentLogReadResult ReadTail(string daemonName, int max);
}
```

`ReadTail` reads `{path}.1` then `{path}` (each may be absent), keeps the last `max` valid
records, newest first. `Complete` distinguishes what a bare list cannot: a **clean absence**
(the file does not exist — `FileNotFoundException`/`DirectoryNotFoundException` on open)
counts as complete, while an **I/O failure** (`IOException`, `UnauthorizedAccessException`,
including a file vanishing mid-read) makes that file contribute nothing and flips
`Complete=false`. `ReadTail` itself never throws for any of these; worst case is
`([], false)`. The consumer contract (§7) needs exactly this bit: an incomplete read must not
be mistaken for a genuinely shorter/empty log. Rules:

- **Sharing:** every open is `FileAccess.Read` with `FileShare.ReadWrite | FileShare.Delete` —
  the daemon appends and rotates (`File.Move`) this file live; on Windows a write-denying open
  would block the daemon's appends and a delete-denying open would fail its rotation (the
  AI-1629 bug class; `FileShare.Delete` is specifically what `File.Move` needs).
- **Invalid lines are skipped, never fatal:** lines that do not parse as JSON (torn tail
  write), and lines that parse but are structurally invalid — null root, or null `DecidedAt`,
  `AgentId`, `Kind`, `RepoPath`, `Vendor`, `Outcome`, or `Source` (`Requester`,
  `RequesterDisplay` stay nullable; a missing `requester_is_owner` reads as `false`).
- **Rotation race:** a rotation between the two reads can transiently duplicate or miss lines.
  Records have value equality — the merged list is `Distinct()`-ed, and a miss self-heals on
  the next refresh (§7). Accepted: this is a display feed, not an audit query.

## 5. `ConsentService` (app)

`src/Capacitor.App/Services/ConsentService.cs` (+ `IConsentService`): owns the consent
subscription, the pending queue, and resolution. Single instance, created at startup beside
`DaemonClientService`. **The service is the sole owner of the pending cache** — every
insertion and removal happens here (removals occur only on conclusive acks and via the prune;
§6's ViewModel never removes, it only reads and pins).

**Shared ticker (infrastructure, in scope):** the AI-1651 1-second ticker currently lives as a
private observable inside `MainWindowViewModel`. It hoists into an app-lifetime `ITicker`
service — a shared 1 Hz `IObservable<long>`, created and subscribed on the UI thread at
startup (the AI-1651 lesson: an off-UI-thread `Observable.Interval` subscription binds an
orphan thread-local dispatcher and never ticks). `MainWindowViewModel` consumes it instead of
owning it; `ConsentService` (prune), `ConsentPromptViewModel` (countdown, terminal-state
holds), and `ActivityViewModel` (stat poll) consume the same instance.

**State:** `SourceCache<PendingConsent, string>` keyed by `RequestId`. `PendingConsent` wraps
the `ConsentPendingDto` plus a computed `DeadlineHint = RequestedAt + TimeoutSeconds`
(`DateTimeOffset` parse of the daemon's ISO stamp; an unparseable `RequestedAt` falls back to
arrival time + `TimeoutSeconds`). **The deadline is a heuristic, never an outcome.** The
daemon enforces its timeout on a *monotonic* clock anchored at prompt entry; `RequestedAt` is
wall-clock metadata, so an NTP/manual clock step can make the hint disagree with the daemon's
real deadline in either direction. Nothing in the app treats hint expiry as a decision: the
authoritative outcome is only ever a resolve ack (§6 renders expiry accordingly), and the
prune below is availability hygiene, not settlement — a prematurely pruned still-live request
simply times out daemon-side exactly as if no UI were attached.

**Identity-guarded removal (ABA defense).** `RequestId` is the agent id, and the broker
explicitly documents that a successor prompt can reuse a predecessor's id. The subscription
and resolve travel on separate connections, so successor B can be upserted (replacing A's
cache slot by key) before predecessor A's ack is processed — an unconditional remove-by-key
would then evict B, and with no withdrawal push nothing would restore it. Therefore **every**
removal is guarded by the §4.1 request identity: it captures the `PromptId` it is acting on
and removes only if the entry currently cached under that `RequestId` carries the same
`PromptId`. B carries a different daemon-minted `PromptId` by construction, so a stale removal
for A can never evict B — while a *replayed instance of A itself* (same `PromptId`, fresh
object) is correctly evicted. A removal that finds a different `PromptId` is a no-op. Because
the resolve carries the echo, the ack verifiably concerns exactly the identity the app
targeted: whether B replaced A in the daemon before the resolve arrived (mismatch/lost claim
→ `Ok=false`) or B arrived after A's resolution (`Ok=true` for A), B's cache entry survives
either way and is prompted on its own terms.

**Conclusion tombstones (ghost-replay defense).** The broker's `Subscribe` snapshots pending
entries without synchronizing against a concurrent `TryResolve`, and a snapshotted frame can
sit in the unbounded per-subscriber channel arbitrarily long (backpressure, suspension, a
large replay) — so a `Pending` for a request the app already concluded can arrive *before*
the conclusive ack (a reconnect's replay raced the resolve) or *after* it, with no bounded
delivery latency. On every conclusive ack the service atomically (1) records the concluded
`PromptId` as a tombstone and (2) evicts any currently cached entry carrying it — closing the
ghost-admitted-before-ack ordering, which a guard keyed on the original instance alone would
miss. An incoming `Pending` whose `PromptId` is tombstoned is dropped; by the §4.1 identity
contract a matching frame *is* the concluded request — never a distinct successor — so
dropping is always correct — **forever**. Tombstones therefore live for the
**`ConsentService` lifetime: no TTL, no per-connection retirement, no size cap.** Any time-
or boundary-based retirement reopens a window: a client-local `Subscribed` is not ordered
against a concurrent ack (the daemon can snapshot a replay *before* a resolve concludes while
the app processes the ack *before* its local `Subscribed` — retiring there would admit the
still-in-flight snapshotted frame), and any cap's eviction re-admits a frame delayed past it.
None of that trades against anything real: a `PromptId` is a GUID that is never reused, so a
lifetime tombstone can never wrongly suppress a future request, and the memory cost is ~50
bytes per *concluded human decision* — thousands of conclusions in one app session amount to
hundreds of kilobytes, accepted and documented. Expiry/prune do **not** tombstone: a request
the app merely gave up on locally is still live daemon-side (the clock-step case) and must be
allowed to reappear.

**Subscription lifecycle — status-driven.** The service observes
`DaemonClientService.Status`:

- On `Connected` with capabilities containing `"consent/2"` → start the subscription loop.
- On leaving `Connected` (Connecting/Unreachable) → cancel the loop. Pending entries are NOT
  cleared on disconnect — the daemon may still be alive holding live prompts; entries expire
  via their own deadline hints, and a successful resubscribe reconciles (below).
- On `Connected` **without** `"consent/2"` — including a daemon advertising only `consent/1`
  (§4.1: it would resolve without the identity check) → no subscription, no prompts, no
  resolves, and the cache **is** cleared: any retained entries belong to a previous
  incarnation and can never be safely resolved against this one. The existing shell surfaces
  the daemon-outdated condition.

**Loop:** while in the connected state: enumerate `ConsentSubscription.RunAsync`; on the
`Subscribed` event clear the pending cache; on each `Pending` event upsert by `RequestId`
(tombstones filtering, above). The clear sits at the `Subscribed` boundary, not before the
dial: a failed dial while the status socket still reports Connected must not erase a
still-actionable queue — retained entries keep the tray attention and their countdowns alive
through the transient. After the boundary the daemon's replay is authoritative: anything it no
longer holds is gone from the cache by construction.

**Accepted post-`Subscribed` window:** `Subscribed` is client-local (§4.2) — if the daemon
dies between the subscribe write and the replay, the cache has been cleared and no replay
restores it until the next successful attempt (~1s cadence). The loss is bounded and
UI-only: the daemon-side prompts were never dismissed, and the next successful replay
restores them; if no daemon comes back, the entries were unanswerable anyway. If the
enumeration ends while status still reports Connected (protocol confusion, half-dead socket,
failed dial), delay 1s and go around. Loop cancellation stops cleanly.

**Pruning:** each entry carries `PruneAfter`, initialized to `DeadlineHint + 5s`. On the
shared ticker, entries with `now > PruneAfter` are removed (identity-guarded) — bounding ghost
entries from requests that expired daemon-side (there is no withdrawal push) and from
disconnected periods. Two qualifications keep the prune from contradicting §6's interaction
guarantees: the prune **skips the entry currently targeted by the in-flight resolve** (a user
clicking just before the boundary must not have the entry vanish mid-call; the lane's
settlement disposes of it — a conclusive ack evicts, and the skip ends), and a
`TransportFailure` settlement **refreshes the target's `PruneAfter` to `now + 5s`** so the
promised entry-stays-interactive state actually exists for a beat instead of being pruned on
the next tick. Under a clock step the prune inherits the hint's heuristic nature (above) —
accepted.

**Resolution:**

```csharp
Task<ConsentResolveOutcome> ResolveAsync(PendingConsent target, bool allow, bool saveRule, CancellationToken ct);
```

`ResolveAsync` builds the `ConsentResolveDto` (`decision: allow|deny`; **always** the
`prompt_id` echo from `target` — the §4.1 identity check; when `saveRule`, a requester-only
`save_rule` from `target.Requester`) and calls `ILocalControlOps.ResolveConsentAsync`. One
resolve in flight at a time — serialized on one lane, same discipline as `PauseController`. **Null-requester guard lives here, not only on the
button:** when `saveRule` is requested but `target.Requester` is null or empty, the service
sends the resolve *without* `save_rule` (a null requester would serialize into a wildcard
allow-everything rule) and reports it in the outcome; the §6 button-hiding is UX, this guard
is the safety boundary, and it is tested directly.

`ConsentResolveOutcome` (cache effects identity-guarded on `target`; `RuleOutcome` is the
service's disambiguation — it knows whether it sent `save_rule` (§4.1): `Saved`, `Rejected`,
`Unknown` (save requested but `RuleSaved` null — unreachable behind the `consent/2` gate,
kept as a defensive mapping), `NotRequested`, `SkippedNoRequester`):

| Outcome | Ack | Cache effect | Meaning |
|---|---|---|---|
| `Applied` | `Ok=true`, rule outcome `Saved`/`NotRequested` | remove + tombstone | applied |
| `AppliedRuleRejected` | `Ok=true`, rule outcome `Rejected` or `Unknown` | remove + tombstone | decision applied; rule not saved (or outcome unknown on a down-level daemon) — warn |
| `AlreadyDecided` | `Ok=false` | remove + tombstone | decided elsewhere or superseded (daemon timeout, or the §4.1 identity check found a different request under this id); carries `RuleOutcome` — a rule may still have been installed (§4.1) and §6 must disclose saved / not saved / unknown |
| `RuleSkippedNoRequester` | `Ok=true` (no save_rule sent) | remove + tombstone | applied; rule not saved because the request had no requester identity |
| `TransportFailure` | `LocalControlOpsException` | keep | daemon unreachable/timeout — request may still be pending daemon-side |
| *(propagates)* | `OperationCanceledException` | keep | caller/app-shutdown cancellation is its own path — never rendered as a transport failure; the VM treats it as a silent abort |

**Exposed:** the pending cache (`IObservable` via DynamicData `.Connect()` — mutated on
background continuations; consumers marshal with
`ObserveOn(RxSchedulers.MainThreadScheduler)`, the app's existing scheduler seam),
`IObservable<int> PendingCount`, `ResolveAsync`, and an **unconditional** entry-added signal.
The service knows nothing about windows: the prompt-window *coordinator* subscribes to the
added signal, filters by window visibility, and marshals to the UI thread (§6). The count
feeds `TrayViewModel` (§8).

**Shutdown:** app shutdown disposes in order: prompt-window coordinator (closes the window,
cancelling any in-flight resolve via the OCE path above) → `ConsentService` (cancels the
subscription loop and the resolve lane) → `DaemonClientService` (existing). Quitting with the
prompt window open is a tested path.

## 6. Prompt window

`ConsentPromptWindow` + `ConsentPromptViewModel`. At most one instance at a time, owned by a
coordinator (pattern of `MainWindowCoordinator`): open-or-activate; closing releases the
instance and a later raise creates a fresh one. Fixed compact size (460×260 starting point,
subject to live-acceptance polish; non-resizable), `Topmost = true`, centered. All window
open/activate calls are marshalled to the UI thread by the coordinator
(`Dispatcher.UIThread.Post`) — the trigger originates on a socket continuation.

**Raise policy:** the coordinator opens the window and activates the app when the service's
entry-added signal fires while the window is not visible — this covers both the 0→1
transition and a new request arriving after the user closed the window. Closing the window
without deciding is an explicit *defer*: the queue is untouched, the tray stays in
`Attention`, and the tray menu's "Review pending launches…" reopens it. Additions while the
window is already visible just update the queue indicator — no re-activation (no focus
stealing mid-interaction).

**Displayed item is a pinned snapshot.** The VM sorts the cache (by `RequestedAt`, then
`RequestId` ordinal for determinism) and pins the head as `Current`. While `Current` is in a
terminal display or has a resolve in flight, cache changes (new arrivals, prune, replay
reconciliation) do **not** swap the displayed item out from under the user — the pin releases
only on advance (or, in the hint-expired state, when the service's prune removes the pinned
entry — below). The VM never removes cache entries; conclusive acks and the prune do (§5),
and the identity guard makes any stale action a no-op that can never touch a same-id
successor.

**Content** — for the pinned current request:

- Requester: `RequesterDisplay ?? Requester ?? "unknown"`, prominent.
- Kind label: `agent` → "Agent", `review` → "Review", `review-flow` → "Review flow".
- Repo: `RepoLabel.Leaf`, full path as tooltip. Vendor as-is.
- Countdown: "Expires in 37s", ticked by the shared ticker from `DeadlineHint`.
- Queue indicator "1 of 3" when more than one pending.

**Buttons:**

| Button | Wire | Notes |
|---|---|---|
| Allow once | `ConsentResolve{decision: "allow"}` | |
| Allow & remember | `ConsentResolve{decision: "allow", save_rule: {action: "allow", requester: <id>, kind: null, repo: null, vendor: null}}` | Daemon stays the single rule writer. Label is deliberately not "Always allow" — an appended rule is first-match-shadowed by earlier denies (§4.1), so the label must not promise "always". **Hidden when `Requester` is null *or empty*** — the same predicate as §5's service guard, which remains the real safety boundary. Tooltip: "Saves a rule allowing future launches from this requester. Existing deny rules — including Pause — take precedence until removed." |
| Deny | `ConsentResolve{decision: "deny"}` | |

While a resolve is in flight all three disable (no double-submit); the countdown keeps
ticking, but **hint expiry never preempts an in-flight resolve**: if the countdown reaches
zero mid-resolve, the display shows "Expiring…" and waits for the ack — the ack is
authoritative and governs the terminal state.

**Hint expiry (no resolve in flight) is not a verdict.** When the countdown reaches zero the
window does *not* claim a denial — the hint is wall-clock and the daemon's deadline is
monotonic (§5), so the request may in fact still be live. The countdown is replaced by
"Response time elapsed — unanswered requests are denied by the daemon", and **the buttons
stay active**: a click still resolves normally (if the daemon really did time out, the ack
comes back `Ok=false` and the honest "Already decided" path runs; after a backward clock step,
the click simply works). The entry stays in the cache until the §5 prune removes it
(`PruneAfter`, identity-guarded); the VM advances when its pinned entry leaves the cache in
this state.

**Terminal states (per pinned request):**

- **Decided (`Applied`)** → advance immediately (no hold): window shows the next pending or
  closes when the queue empties. `AppliedRuleRejected` and `RuleSkippedNoRequester`
  additionally show a warning toast over the prompt window: "Decision applied — rule not
  saved: {reason}" (for rule outcome `Unknown`: "Decision applied — this daemon version
  doesn't report whether the rule was saved").
- **Already decided (`AlreadyDecided`)** → buttons are replaced by "Already decided" for a
  2-second hold (ticker-driven), then advance (the cache entry is already gone — the pin is
  what the user is looking at). Never a silent success. After an **Allow & remember** click
  the text discloses the §4.1 side effect per `RuleOutcome`: `Saved` → "Already decided —
  your allow rule for {requester} was still saved."; `Rejected` → "Already decided — no rule
  was saved."; `Unknown` → "Already decided — this daemon version doesn't report whether your
  allow rule was saved."
- **Transport failure (`TransportFailure`)** → toast over the prompt window ("Daemon
  unreachable — the request is still pending"), buttons re-enable, entry stays. The daemon
  may still be waiting.
- **Cancellation (OCE)** → silent abort: no toast, no removal; on app shutdown the window is
  closing anyway.

A late ack for a request the VM already advanced past updates nothing: the service's
instance-guarded removal already ran or no-ops, and the VM only renders its pin.

Toasts use `AppNotifier` extended to attach a `WindowNotificationManager` to the prompt window
— the main window may be closed, so prompt-related warnings must surface on the prompt window
itself.

**Pause interplay:** none needed for prompting. Pause is a wildcard deny rule at `rules[0]`,
so paused launches are rule-denied without prompting and simply appear in the Activity feed as
denials. (Its interaction with "Allow & remember" is the §4.1 precedence note.)

## 7. Activity tab

The main window becomes a two-tab layout — **Agents** | **Activity** — with the AI-1651 agents
grid unchanged inside its tab. `ActivityViewModel` renders the decision log, newest first,
capped at 200 records.

**Refresh triggers** (no `FileSystemWatcher` — platform quirks, untestable timing):

- The Activity tab becomes visible (also covers window open on that tab).
- A stat poll on the shared ticker, every 2s while the tab is visible: compare
  (`LastWriteTimeUtc`, `Length`) of both files against the previous poll; re-read on change. A
  stat failure (file/directory absent, transient IO) counts as "no stats" — compared as a
  change when stats reappear — and is swallowed: the poll subscription never terminates on an
  exception.
- A local resolve reached a conclusive ack. **Own decisions are eventual, not instant:** the
  daemon appends the log record *after* completing the resolution TCS
  (`RunContinuationsAsynchronously`), so the ack can beat the append. The VM refreshes
  immediately on the ack *and* relies on the regular 2s stat poll to converge — the spec
  promise is "your decision appears no later than the next poll", not "immediately".

**Display rule, keyed off `ConsentLogReadResult.Complete` (§4.4):** a `Complete` read
replaces the rows — including replacing them with the empty state when it is genuinely empty
(legitimate log deletion must be able to empty the feed). An **incomplete** read (any per-file
I/O failure) never replaces existing rows: last-good rows stay on display and the next poll
retries; if there are no previous rows, the partial records are shown as best-effort. The
empty state therefore renders only on a clean empty read.

The reader is `ConsentDecisionLogReader.ReadTail(daemonName, 200)` — pure file I/O, so the
feed works with the daemon stopped or unreachable.

**Row rendering:** local timestamp (`yyyy-MM-dd HH:mm:ss` from `decided_at`; an unparseable
stamp renders the raw string verbatim), outcome badge (`allowed` green / `denied` red),
requester (`requester_display ?? requester ?? "unknown"`), kind label (as §6), repo leaf
(`RepoLabel`, full path tooltip), vendor, and a human source label: `owner` → "owner",
`rule[i]` → "rule", `default` → "default policy", `prompt_user` → "you", `prompt_timeout` →
"timeout", `prompt_no_ui` → "no UI attached" (unrecognized values render verbatim). Empty
state mirrors the Agents tab: centered "No decisions yet", no column headers.

## 8. Tray integration

**State derivation.** `TrayViewModel` gains a `PendingConsentCount` input (from
`IConsentService.PendingCount`, combined into the existing derivation stream). New rule,
appended to the AI-1651 state table: when the connection-derived state is `Idle` or `Running`
and `PendingConsentCount > 0`, the state becomes `Attention` with header
"{N} launch(es) awaiting approval" — e.g. "1 launch awaiting approval". All
connection-trouble rows keep precedence: pending consent asserts `Attention` only while
Connected. (Retained entries can outlive a disconnect (§5); the connection-trouble row wins
the display during it.) The icon shows the existing `Attention` rendering; the running-count
badge continues to reflect the agent count.

**Menu.** A "Review pending launches…" `NativeMenuItem`, visible only when
`PendingConsentCount > 0`, placed between the agents section and the pause toggle. Click →
open/activate the prompt window via the coordinator. The existing `NeedsUpdate` rebuild
cadence picks up count changes through the model stream — no new refresh machinery.

## 9. Error handling summary

| Failure | Surface | Behavior |
|---|---|---|
| Daemon unreachable (no subscription) | Tray | Existing AI-1651 rows; no prompts possible; feed still renders from file |
| Daemon without `consent/2` (incl. `consent/1`-only) | Tray | No subscription, prompts, or resolves; cache cleared; down-level surfacing. Enforcement doesn't depend on the check: a v1 incarnation answering a v2 frame (restart TOCTOU) errors it — structurally unable to resolve or subscribe (§4.1) |
| Consent dial fails while status Connected | none (self-heal) | Cache NOT cleared (clear sits at the `Subscribed` boundary); 1s delay → retry |
| Daemon dies between subscribe write and replay | none (self-heal) | Cache cleared but restored by the next successful replay (~1s cadence); bounded, UI-only (§5) |
| Consent stream dies while Connected | none (self-heal) | 1s delay → resubscribe (fresh replay reconciles at the `Subscribed` boundary) |
| Structurally invalid pending frame | none | Skipped (ending the stream would thrash the resubscribe loop) |
| Ghost replay of a just-concluded request | none | Arrives after the ack → service-lifetime tombstone drops it (no TTL, no retirement, no cap — arbitrarily delayed frames stay dropped, across resubscribes); admitted before the ack → the ack's identity-matched eviction removes it — no second prompt either way |
| Wall-clock step vs. daemon's monotonic deadline | Prompt window | Hint expiry is never a verdict: non-authoritative copy, buttons stay active, ack governs |
| Resolve transport failure | Prompt toast | Entry kept; buttons re-enable |
| Resolve cancelled (shutdown) | none | OCE propagates; silent abort — never rendered as transport failure |
| Resolve `Ok=false` | Prompt window | "Already decided" — never a silent success; rule outcome disclosed as saved / not saved / unknown (down-level) |
| Rule save rejected / unknown / skipped (no requester) | Prompt toast | Decision applied; warning shown |
| Request expired (no withdrawal push) | Prompt window / prune | Non-authoritative expiry display; cache prune at `PruneAfter` (identity-guarded, skips the in-flight resolve target, refreshed on transport failure) |
| Same-id successor while ack in flight | none | Identity-pair removal guard — a stale ack/prune never evicts the successor |
| Stale resolve reaching the daemon after id reuse | daemon | Closed: the `prompt_id` echo mismatches (or the atomic claim is lost) and the broker answers no-pending (`Ok=false`) — a verdict for A can never decide B (§4.1) |
| Decision log absent / IO failure / bad lines | Activity tab | Clean absence → `Complete` empty read → empty state; I/O failure → `Complete=false` → last-good rows kept; invalid lines skipped |
| Rotation race during read | Activity tab | `Distinct()` merge; miss self-heals next poll |

## 10. Testing

All headless (TUnit; `Avalonia.Headless` for VM/window behavior; fake `TimeProvider`/ticker
throughout — no real sleeps).

**Core:**
- `ResolveConsentAsync` against an in-proc fake server (pattern of existing `LocalControlOps`
  tests): ack round-trip (`Ok`/`Error`/`RuleSaved` shapes, including an old-format ack with no
  `rule_saved` member → null; the sent `ConsentResolveDto` carries the `prompt_id` echo),
  decodable `Error` frame → `daemon_rejected`, malformed ack → `unexpected_reply`, EOF →
  `unexpected_reply`, transport → `daemon_unreachable`, phase timeout → `timed_out`,
  caller-cancellation propagates.
- `ConsentSubscription`: `Subscribed` yielded after connect+write and **before any read**
  (observable with an empty replay — first `MoveNextAsync` completes); replay + push frames
  yield `Pending` events in order; EOF ends enumeration; failed connect (`SocketException`)
  ends enumeration without yielding `Subscribed`; unexpected frame type ends enumeration;
  undecodable JSON ends enumeration; **decodable-but-structurally-invalid pending (null
  request_id via `{}`) is skipped and the stream continues**; **the `PromptId` requirement is
  isolated: a v1-shaped pending with every pre-existing field valid and `prompt_id`
  separately absent, null, and empty yields no `Pending` event and the stream continues** (a
  `{}` frame alone would stay green with the PromptId check forgotten); `ct` propagates OCE.
  Absent `requester_display` deserializes to null.
- `ConsentDecisionLogReader`: tail across the rotation pair (order, cap, newest-first);
  undecodable-line skip; **parseable-but-structurally-invalid line (`{}`) skip**;
  `Distinct()` dedup; absent files → `([], Complete=true)`; **one file unreadable (I/O
  failure) → partial records with `Complete=false`**; file vanishing between stat and open →
  clean absence, no throw; old-format lines (no `requester_display`) parse with null; **a
  reader holding an open handle (the reader's own share mode) does not block the writer's
  actual operations — append AND `File.Move` rotation — while a concurrent `ReadTail`
  succeeds** (the sharing regression guard; trivially green on Unix, load-bearing on the
  Windows CI leg — both directions and both operations covered, since `FileShare.Delete` is
  what rotation needs).

**Daemon:**
- Gate threads `RequesterDisplay` into the prompt request and the decision record (extend
  existing gate tests); log writer round-trips through the hoisted Core type unchanged
  (existing files' snake_case field names asserted verbatim).
- `HandleResolveAsync`: `save_rule` + no pending request → rule IS persisted and the ack is
  `Ok=false, RuleSaved=true`; rejected save → `RuleSaved=false` on both `Ok` branches; no
  `save_rule` → `RuleSaved=null` (written as JSON null).
- Engine: an earlier matching deny (index 0 wildcard) shadows a later appended allow —
  first-match-wins pinned by test.
- Prompt identity: every prompt entry gets a fresh `PromptId` — two same-agent-id prompts
  under a **frozen clock** (identical `RequestedAt`) still carry distinct identities, and all
  guards discriminate them (the failure mode a timestamp identity would have).
- Broker identity check: `TryResolve` with a matching `prompt_id` echo resolves; a
  mismatching echo returns false (and the handler acks `Ok=false`) leaving the pending entry
  untouched; a null echo preserves legacy resolve-by-id behavior; **a lost claim — the
  captured `Pending` replaced between match and conditional removal — returns false and the
  successor is untouched** (exercised deterministically against the claim primitive with a
  stale captured instance). The two orderings the echo exists for, end to end through the
  handler: **B replaces A in the broker before A's resolve arrives** → A's resolve
  mismatches, `Ok=false`, B stays pending; **A resolved, then A's ack raced by B's arrival**
  → `Ok=true` concerned only A, B stays pending.
- Capability: `LocalControlCapabilities.Current` advertises `consent/2` alongside
  `consent/1`; mixed-version acceptance — an app facing a `consent/1`-only capability set
  starts no subscription and sends no resolve.
- Incarnation swap (the restart TOCTOU): against a v1-routing server (routes frames 11/12,
  answers 17/18 with the shipped `default`-arm `Error` frame), a cached v2 prompt's resolve
  via `ConsentResolveV2` draws `daemon_rejected` — **no resolution occurs and a same-id
  pending on that daemon is untouched**; `ConsentSubscribeV2` against the same server ends
  the enumeration without registering a subscriber. V2-daemon side: frame 18 with a
  missing/empty `prompt_id` acks `Ok=false` "invalid resolve payload"; frames 11/12 still
  route for legacy callers.

**App (the "full prompt matrix" from the issue):**
- Allow once / Deny → correct `ConsentResolveDto`, entry removed, queue advances.
- Allow & remember → `save_rule` is requester-only; button hidden when `Requester` is null
  **and** when it is empty (same predicate as the service guard); **the service guard:
  `ResolveAsync(saveRule: true)` on a null/empty-requester target sends NO `save_rule` and
  reports `RuleSkippedNoRequester`** (tested directly, not via the button).
- Rule-outcome disambiguation: after an Allow & remember click, `AlreadyDecided` with
  `RuleSaved=true` / `false` / absent (old-format ack) shows the saved / not-saved /
  unknown-down-level disclosure respectively.
- ABA (both orderings, distinct cache outcomes asserted): **B replaces A's cache slot before
  A's ack is processed** → the ack's identity-guarded eviction no-ops, B survives and is
  prompted; **B arrives after A's `Ok=true` ack** → A was evicted, B is admitted and
  prompted. In both, the successor is decided only on its own terms.
- Ghost replay (both orderings): a ghost `Pending` arriving *after* A's conclusive ack is
  dropped by the tombstone — **including one delayed arbitrarily long, and including the
  snapshot-before-ack / `Subscribed`-after-ack interleaving** (daemon snapshots the replay,
  the resolve concludes and tombstones A, the local `Subscribed` fires, THEN the snapshotted
  A frame arrives — still dropped: tombstones survive the boundary); a ghost admitted
  *before* the ack (replay raced the resolve — a fresh instance with A's identity) is evicted
  by the ack's identity-matched removal — no second raise either way. Entries never concluded
  are admitted normally after any resubscribe (their `PromptId`s are not tombstoned).
- Hint expiry with no resolve in flight → non-authoritative copy, **buttons remain active**;
  a click after hint-zero that acks `Ok=true` is `Applied` (the wall-clock-step case: hint
  fired early, request was still live); one that acks `Ok=false` runs "Already decided"; the
  prune removes the entry at `PruneAfter` (identity-guarded) and the VM advances on that
  removal.
- Prune vs. in-flight resolve (spanning the `PruneAfter` boundary): the prune skips the
  resolve target while the call is pending; a **transport-failure** settlement retains the
  entry with `PruneAfter` refreshed to now+5s (interactive for a beat, not pruned on the next
  tick); a **conclusive ack** settlement evicts it normally.
- Countdown reaches zero while a resolve is in flight → no expiry preemption; the ack
  governs; the next request is unaffected.
- `AlreadyDecided` → 2s hold, then advance — never silent.
- `AppliedRuleRejected` (including rule outcome `Unknown`) → warning surfaced, entry removed.
- Transport failure → entry kept, buttons re-enable. Cancellation (lane-queued and
  in-flight) → silent abort, entry kept, no toast.
- Queue: "1 of N" indicator; oldest-first ordering; the pinned display does not swap while a
  terminal hold or in-flight resolve is active; additions while visible don't re-raise;
  addition while closed raises (coordinator-filtered, marshalled to the UI thread — the add
  originates off the UI thread in the test).
- Subscription lifecycle: clear happens at the `Subscribed` event (failed dial retains
  entries — no `Subscribed`, no clear); reconnect-with-empty-replay leaves an empty cache
  (the `Subscribed` boundary makes this observable); stream death after `Subscribed` but
  before any replay frame → cache cleared, restored by the next attempt's replay; `Connected`
  without `consent/2` (including `consent/1`-only) clears the cache and never subscribes;
  stream-end-while-Connected retries.
- Shutdown: app quits with the prompt window open and a resolve in flight → clean disposal in
  the §5 order, OCE path exercised.
- `ActivityViewModel`: rows map records (fallback chains, source labels, unrecognized source
  verbatim, unparseable timestamp verbatim); refresh on tab-visible / stat-change /
  own-resolution (eventual — asserted via the next poll tick, not instant); **`Complete=false`
  read keeps last-good rows; `Complete=true` empty read shows the empty state**.
- `TrayViewModel`: new Attention row (pending>0 while Idle/Running → Attention + header);
  connection-trouble precedence unchanged; menu item visibility.

## 11. Deferred / out of scope

- macOS system notification → AI-1653 (needs bundle identity).
- Consent rules editor UI → umbrella slice 4.
- Attach/approve from the CLI (`kcap daemon consent` gains no resolve verb — the app is the
  approval surface; the CLI keeps rules + log only).
- Any change to engine matching, rule ordering/precedence, policy storage, or prompt timeout
  semantics.
