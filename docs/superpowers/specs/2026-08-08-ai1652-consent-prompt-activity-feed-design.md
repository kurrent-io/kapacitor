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

**In:** consent subscription service in the app; prompt window (Allow once / Always allow /
Deny, countdown, queue); Activity tab; tray `Attention` on pending consent + "Review pending
launches…" menu item; `ResolveConsentAsync` on `ILocalControlOps`; consent subscription client
in Core; decision-record hoisting to Core (shared write/read shape); `requester_display`
threading through the consent pipeline (additive); `rule_saved` reporting on `ConsentAckDto`
(additive) so a rule installed for an already-decided request is disclosed, not hidden;
hoisting the AI-1651 per-window ticker into an app-lifetime service; headless ViewModel tests
for the full prompt matrix.

**Out:** macOS system notification (AI-1653); consent-rules editor UI (slice 4); any change to
engine matching, rule ordering/precedence, policy storage, or prompt timeout semantics; new
IPC frame types; Windows/Linux app packaging.

## 4. Wire & Core changes

No new frame types and no protocol version change. Everything below is additive.

### 4.1 `ResolveConsentAsync` on `ILocalControlOps`, and `rule_saved` on the ack

```csharp
Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct);
```

One-shot, exactly the existing `LocalControlOps` pattern (connect → send → single reply →
close): writes `FrameType.ConsentResolve` with the `ConsentResolveDto` JSON as `Text`, expects
exactly one `ConsentAck` frame, deserializes `ConsentAckDto`. Per-phase timeouts and failure
classification are identical to the existing ops — caller-cancellation propagates
(`OperationCanceledException` checked first), `EndOfStreamException` before `IOException`
(derivation order) → `unexpected_reply`, transport → `daemon_unreachable`, phase timeout →
`timed_out`, a decodable `Error` frame → `daemon_rejected` (matching the existing consent
ops), any other frame type or a null/malformed ack → `unexpected_reply`.

**`ConsentAckDto` gains trailing `bool? RuleSaved`** (wire `rule_saved`, positional-required
convention: no C# default, nulls always written). Semantics: null when the resolve carried no
`save_rule`; `true`/`false` = the rule write succeeded/failed. The daemon's
`HandleResolveAsync` deliberately persists `save_rule` *before* attempting the resolution —
"Always allow" expresses durable trust in the requester, which stands even when this
particular launch has already been decided — so today an `Ok=false` ack can silently hide an
installed rule. The handler change: populate `RuleSaved` on **both** ack branches (today the
no-pending branch drops the save outcome entirely). `Ok`'s meaning is unchanged: it reflects
the resolution outcome only.

**Rule ordering is deliberately untouched.** The handler appends the saved rule at the end
and `LaunchConsentEngine` is first-match-wins, so an earlier matching rule — the pause
wildcard deny at `rules[0]`, or any manual deny — takes precedence over an appended allow
until that earlier rule is removed. This is correct: "Always allow" must never silently
override pause or an explicit deny. The UI copy carries the consequence honestly (§6).

The daemon resolve handler otherwise exists (`LaunchConsentIpc.HandleResolveAsync`); no hello
is needed on one-shot connections (hello is optional; the CLI consent verbs already send bare
frames).

### 4.2 Consent subscription client (Core)

`ConsentSubscription` in `Capacitor.Cli.Core/LocalIpc/`:

```csharp
public static async IAsyncEnumerable<ConsentPendingDto> RunAsync(
    string daemonName, [EnumeratorCancellation] CancellationToken ct)
```

Connects to the daemon's socket, writes `FrameType.ConsentSubscribe` (empty text), then reads
frames and yields one `ConsentPendingDto` per `ConsentPending` frame. The daemon replays the
full pending set immediately, then pushes new requests (broker contract: exactly-once per
subscriber, no withdrawal push).

**Termination and validation contract:**

- EOF, transport `IOException`, and `SocketException` (a failed *connect* throws
  `SocketException` directly — it is not an `IOException`) end the enumeration normally: an
  ended stream means "this attempt is over", and the consumer decides whether to resubscribe.
- A frame of any type other than `ConsentPending`, or a frame whose JSON does not
  *deserialize*, also ends the enumeration — protocol confusion is a dead connection.
- A frame that deserializes but is **structurally invalid** — null root, or null/empty
  `RequestId`, `Kind`, `RepoPath`, `Vendor`, `RequestedAt`, or `TimeoutSeconds <= 0` (STJ
  source-gen does not enforce non-nullable members; `{}` decodes "successfully") — is
  **skipped**, not fatal. Ending the enumeration here would be a thrash loop: the resubscribe
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
public static class ConsentDecisionLogReader {
    public static string PathFor(string daemonName);   // DaemonLockPaths.Directory / Sanitize(name) / consent-decisions.jsonl
    public static IReadOnlyList<ConsentDecisionRecord> ReadTail(string daemonName, int max);
}
```

`ReadTail` reads `{path}.1` then `{path}` (each may be absent), keeps the last `max` valid
records, newest first. Rules:

- **Sharing:** every open is `FileAccess.Read` with `FileShare.ReadWrite | FileShare.Delete` —
  the daemon appends and rotates (`File.Move`) this file live; on Windows a write-denying open
  would block the daemon's appends and a delete-denying open would fail its rotation (the
  AI-1629 bug class; `FileShare.Delete` is specifically what `File.Move` needs).
- **Invalid lines are skipped, never fatal:** lines that do not parse as JSON (torn tail
  write), and lines that parse but are structurally invalid — null root, or null `DecidedAt`,
  `AgentId`, `Kind`, `RepoPath`, `Vendor`, `Outcome`, or `Source` (`Requester`,
  `RequesterDisplay` stay nullable; a missing `requester_is_owner` reads as `false`).
- **Per-file I/O failure = absent:** `FileNotFoundException`, `DirectoryNotFoundException`,
  `IOException`, and `UnauthorizedAccessException` on either file (races with rotation or
  daemon-state-dir deletion between stat and open, or between the two reads) make that file
  contribute nothing. `ReadTail` never throws for these; worst case it returns an empty list.
- **Rotation race:** a rotation between the two reads can transiently duplicate or miss lines.
  Records have value equality — the merged list is `Distinct()`-ed, and a miss self-heals on
  the next refresh (§7). Accepted: this is a display feed, not an audit query.

## 5. `ConsentService` (app)

`src/Capacitor.App/Services/ConsentService.cs` (+ `IConsentService`): owns the consent
subscription, the pending queue, and resolution. Single instance, created at startup beside
`DaemonClientService`. **The service is the sole owner of the pending cache** — every
insertion and removal happens here; ViewModels only read (§6 pins *displayed* items
separately).

**Shared ticker (infrastructure, in scope):** the AI-1651 1-second ticker currently lives as a
private observable inside `MainWindowViewModel`. It hoists into an app-lifetime `ITicker`
service — a shared 1 Hz `IObservable<long>`, created and subscribed on the UI thread at
startup (the AI-1651 lesson: an off-UI-thread `Observable.Interval` subscription binds an
orphan thread-local dispatcher and never ticks). `MainWindowViewModel` consumes it instead of
owning it; `ConsentService` (prune), `ConsentPromptViewModel` (countdown, terminal-state
holds), and `ActivityViewModel` (stat poll) consume the same instance.

**State:** `SourceCache<PendingConsent, string>` keyed by `RequestId`. `PendingConsent` wraps
the `ConsentPendingDto` plus a computed `DeadlineHint = RequestedAt + TimeoutSeconds`
(`DateTimeOffset` parse of the daemon's ISO stamp; same machine, so no clock-skew handling; an
unparseable `RequestedAt` falls back to arrival time + `TimeoutSeconds`). The deadline is a
*hint* — AI-1648's subscriber grace means the daemon's real deadline can differ slightly; the
authoritative outcome is only ever a resolve ack.

**Instance-guarded removal (ABA defense).** `RequestId` is the agent id, and the broker
explicitly documents that a successor prompt can reuse a predecessor's id (retry of the same
agent). The subscription and resolve travel on separate connections, so successor B can be
upserted before predecessor A's ack is processed — an unconditional remove-by-key would then
evict B, and with no withdrawal/replay push nothing would restore it. Therefore **every**
removal (conclusive ack, dismiss, prune) captures the exact `PendingConsent` it is acting on
and removes only if the cache still holds that value (record value-equality; A and B differ at
least in `RequestedAt`). A removal that finds a different value is a no-op.

**Subscription lifecycle — status-driven.** The service observes
`DaemonClientService.Status`:

- On `Connected` with capabilities containing `"consent/1"` → start the subscription loop.
- On leaving `Connected` (Connecting/Unreachable) → cancel the loop. Pending entries are NOT
  cleared on disconnect — the daemon may still be alive holding live prompts; entries expire
  via their own deadline hints, and a successful resubscribe reconciles (below).
- On `Connected` **without** `"consent/1"` (a down-level daemon incarnation) → no
  subscription, and the cache **is** cleared: any retained entries belong to a previous
  incarnation and can never be resolved against this one. The existing shell surfaces the
  daemon-outdated condition.

**Loop:** while in the connected state: dial and write the `ConsentSubscribe` frame; **only
after that write succeeds** clear the pending cache, then upsert each incoming DTO by
`RequestId`. The clear sits at the subscribe-write boundary, not before the dial: a failed
dial while the status socket still reports Connected must not erase a still-actionable queue
— retained entries keep the tray attention and their countdowns alive through the transient.
After the boundary, the daemon's replay is authoritative by construction: anything the daemon
no longer holds is gone from the cache (accepted tradeoff: an entry resolved while
disconnected vanishes without a terminal display; the Activity feed carries its outcome). If
the enumeration ends while status still reports Connected (protocol confusion, half-dead
socket, failed dial), delay 1s and go around. Loop cancellation stops cleanly.

**Pruning:** on the shared ticker, entries with `now > DeadlineHint + 5s` are removed
(instance-guarded). This bounds ghost entries from requests that expired daemon-side (there is
no withdrawal push) and from disconnected periods. The prompt VM shows the expired state and
dismisses before the prune fires (§6); the prune is the backstop.

**Resolution:**

```csharp
Task<ConsentResolveOutcome> ResolveAsync(PendingConsent target, bool allow, bool saveRule, CancellationToken ct);
void Dismiss(PendingConsent target);   // instance-guarded removal; used by the VM's terminal-state advance
```

`ResolveAsync` builds the `ConsentResolveDto` (`decision: allow|deny`; when `saveRule`, a
requester-only `save_rule` from `target.Requester`) and calls
`ILocalControlOps.ResolveConsentAsync`. One resolve in flight at a time — serialized on one
lane, same discipline as `PauseController`. **Null-requester guard lives here, not only on the
button:** when `saveRule` is requested but `target.Requester` is null or empty, the service
sends the resolve *without* `save_rule` (a null requester would serialize into a wildcard
allow-everything rule) and reports it in the outcome; the §6 button-hiding is UX, this guard
is the safety boundary, and it is tested directly.

`ConsentResolveOutcome` (all cache effects instance-guarded on `target`):

| Outcome | Ack | Cache effect | Meaning |
|---|---|---|---|
| `Applied` | `Ok=true, Error=null` | remove | applied (`RuleSaved` carried along: null/true/false) |
| `AppliedRuleRejected` | `Ok=true, Error!=null` or `RuleSaved=false` | remove | decision applied; rule not saved — warn |
| `AlreadyDecided` | `Ok=false` | remove | decided elsewhere (daemon timeout / race); `RuleSaved` carried along — a rule may still have been installed (§4.1) and the UI must disclose it |
| `RuleSkippedNoRequester` | `Ok=true` (no save_rule sent) | remove | applied; rule not saved because the request had no requester identity |
| `TransportFailure` | `LocalControlOpsException` | keep | daemon unreachable/timeout — request may still be pending daemon-side |
| *(propagates)* | `OperationCanceledException` | keep | caller/app-shutdown cancellation is its own path — never rendered as a transport failure; the VM treats it as a silent abort |

**Exposed:** the pending cache (`IObservable` via DynamicData `.Connect()` — mutated on
background continuations; consumers marshal with `ObserveOn(RxApp.MainThreadScheduler)`),
`IObservable<int> PendingCount`, `ResolveAsync`, `Dismiss`, and an **unconditional**
entry-added signal. The service knows nothing about windows: the prompt-window *coordinator*
subscribes to the added signal, filters by window visibility, and marshals to the UI thread
(§6). The count feeds `TrayViewModel` (§8).

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
only on advance. Removal is never the VM's job directly: it calls `ResolveAsync`/`Dismiss` on
the pinned instance and the service's instance guard makes a stale removal a no-op (a
successor with the same id is untouched).

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
| Always allow | `ConsentResolve{decision: "allow", save_rule: {action: "allow", requester: <id>, kind: null, repo: null, vendor: null}}` | Daemon stays the single rule writer. **Hidden when `Requester` is null** (§5 holds the real guard). Tooltip carries the precedence honestly: "Saves a rule allowing future launches from this requester. Existing deny rules — including Pause — take precedence until removed." No stronger promise is made anywhere in the UI (§4.1: the appended rule is first-match-shadowed by earlier denies). |
| Deny | `ConsentResolve{decision: "deny"}` | |

While a resolve is in flight all three disable (no double-submit); the countdown keeps
ticking, but **hint expiry never preempts an in-flight resolve**: if the countdown reaches
zero mid-resolve, the display shows "Expiring…" and waits for the ack — the ack is
authoritative and governs the terminal state. Expiry acts on its own only when no resolve is
in flight.

**Terminal states (per pinned request; each holds for 2 seconds, ticker-driven, then the VM
dismisses/advances — the pin guarantees the display survives concurrent cache changes):**

- **Decided (`Applied`)** → advance immediately (no hold): window shows the next pending or
  closes when the queue empties. `AppliedRuleRejected` and `RuleSkippedNoRequester`
  additionally show a warning toast over the prompt window: "Decision applied — rule not
  saved: {reason}".
- **Already decided (`AlreadyDecided`)** → buttons are replaced by "Already decided" for the
  2-second hold, then `Dismiss` + advance. Never a silent success: the user always sees that
  their click did not decide this launch. When the click was **Always allow** and the ack's
  `RuleSaved=true`, the text discloses the side effect: "Already decided — your always-allow
  rule for {requester} was still saved."
- **Expired (countdown ≤ 0, no resolve in flight)** → buttons are replaced by "Expired —
  denied by timeout" for the 2-second hold, then `Dismiss` + advance (the §5 prune is the
  backstop).
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
denials. (Its interaction with "Always allow" is the §4.1 precedence note.)

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

Read failures never blank the feed: a `ReadTail` that returns empty (or a refresh that
throws) while a previous read had rows keeps the last-good rows on display; the empty state
renders only when the log is genuinely absent/empty on a clean read.

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
| Daemon without `consent/1` | Tray | No subscription; cache cleared (stale incarnation); existing down-level surfacing |
| Consent dial fails while status Connected | none (self-heal) | Cache NOT cleared (clear sits after the subscribe-write boundary); 1s delay → retry |
| Consent stream dies while Connected | none (self-heal) | 1s delay → resubscribe (fresh replay reconciles after the write boundary) |
| Structurally invalid pending frame | none | Skipped (ending the stream would thrash the resubscribe loop) |
| Resolve transport failure | Prompt toast | Entry kept; buttons re-enable |
| Resolve cancelled (shutdown) | none | OCE propagates; silent abort — never rendered as transport failure |
| Resolve `Ok=false` | Prompt window | "Already decided" — never a silent success; `RuleSaved=true` disclosed |
| Rule save rejected / skipped (no requester) | Prompt toast | Decision applied; warning shown |
| Request expired (no withdrawal push) | Prompt window / prune | Countdown expiry display (never preempting an in-flight resolve); cache prune at hint+5s |
| Same-id successor while ack in flight | none | Instance-guarded removals — a stale ack/dismiss/prune never evicts the successor |
| Decision log absent / IO failure / bad lines | Activity tab | Absent → empty contribution; last-good rows kept on failed refresh; invalid lines skipped |
| Rotation race during read | Activity tab | `Distinct()` merge; miss self-heals next poll |

## 10. Testing

All headless (TUnit; `Avalonia.Headless` for VM/window behavior; fake `TimeProvider`/ticker
throughout — no real sleeps).

**Core:**
- `ResolveConsentAsync` against an in-proc fake server (pattern of existing `LocalControlOps`
  tests): ack round-trip (`Ok`/`Error`/`RuleSaved` shapes), decodable `Error` frame →
  `daemon_rejected`, malformed ack → `unexpected_reply`, EOF → `unexpected_reply`, transport
  → `daemon_unreachable`, phase timeout → `timed_out`, caller-cancellation propagates.
- `ConsentSubscription`: replay + push frames yield DTOs in order; EOF ends enumeration;
  failed connect (`SocketException`) ends enumeration (no daemon listening); unexpected frame
  type ends enumeration; undecodable JSON ends enumeration; **decodable-but-structurally-
  invalid pending (null request_id via `{}`) is skipped and the stream continues**; `ct`
  propagates OCE. Absent `requester_display` deserializes to null.
- `ConsentDecisionLogReader`: tail across the rotation pair (order, cap, newest-first);
  undecodable-line skip; **parseable-but-structurally-invalid line (`{}`) skip**;
  `Distinct()` dedup; absent files → empty; file vanishing between stat and open → empty, no
  throw; old-format lines (no `requester_display`) parse with null; **a reader holding an
  open handle (the reader's own share mode) does not block the writer's actual operations —
  append AND `File.Move` rotation — while a concurrent `ReadTail` succeeds** (the sharing
  regression guard; trivially green on Unix, load-bearing on the Windows CI leg — both
  directions and both operations covered, since `FileShare.Delete` is what rotation needs).

**Daemon:**
- Gate threads `RequesterDisplay` into the prompt request and the decision record (extend
  existing gate tests); log writer round-trips through the hoisted Core type unchanged
  (existing files' snake_case field names asserted verbatim).
- `HandleResolveAsync`: `save_rule` + no pending request → rule IS persisted and the ack is
  `Ok=false, RuleSaved=true`; rejected save → `RuleSaved=false` on both `Ok` branches; no
  `save_rule` → `RuleSaved=null` (written as JSON null).
- Engine: an earlier matching deny (index 0 wildcard) shadows a later appended allow —
  first-match-wins pinned by test.

**App (the "full prompt matrix" from the issue):**
- Allow once / Deny → correct `ConsentResolveDto`, entry removed, queue advances.
- Always allow → `save_rule` is requester-only; button hidden when `Requester` is null; **the
  service guard: `ResolveAsync(saveRule: true)` on a null/empty-requester target sends NO
  `save_rule` and reports `RuleSkippedNoRequester`** (tested directly, not via the button).
- ABA: successor with the same `RequestId` upserted while predecessor's resolve is in flight
  → the predecessor's ack removes nothing; the successor survives and is displayed next.
- Countdown expiry with no resolve in flight → "Expired — denied by timeout", 2s hold
  (ticker-driven), `Dismiss`, advance; prune removes ghost entries at hint+5s
  (instance-guarded).
- Countdown reaches zero while a resolve is in flight → no expiry preemption; `Ok=true` after
  zero → `Applied`; `Ok=false` after zero → "Already decided"; the next request is unaffected.
- `AlreadyDecided` → 2s hold, then advance — never silent; with `RuleSaved=true` after an
  Always-allow click, the disclosure text is shown.
- `AppliedRuleRejected` → warning surfaced, entry removed.
- Transport failure → entry kept, buttons re-enable. Cancellation (lane-queued and
  in-flight) → silent abort, entry kept, no toast.
- Queue: "1 of N" indicator; oldest-first ordering; the pinned display does not swap while a
  terminal hold or in-flight resolve is active; additions while visible don't re-raise;
  addition while closed raises (coordinator-filtered, marshalled to the UI thread — the add
  originates off the UI thread in the test).
- Subscription lifecycle: clear happens only after the subscribe write succeeds (failed dial
  retains entries); reconnect-with-empty-replay leaves an empty cache; `Connected` without
  `consent/1` clears the cache and never subscribes; stream-end-while-Connected retries.
- Shutdown: app quits with the prompt window open and a resolve in flight → clean disposal in
  the §5 order, OCE path exercised.
- `ActivityViewModel`: rows map records (fallback chains, source labels, unrecognized source
  verbatim, unparseable timestamp verbatim); refresh on tab-visible / stat-change /
  own-resolution (eventual — asserted via the next poll tick, not instant); last-good rows
  kept when a refresh fails; empty state only on clean-empty read.
- `TrayViewModel`: new Attention row (pending>0 while Idle/Running → Attention + header);
  connection-trouble precedence unchanged; menu item visibility.

## 11. Deferred / out of scope

- macOS system notification → AI-1653 (needs bundle identity).
- Consent rules editor UI → umbrella slice 4.
- Attach/approve from the CLI (`kcap daemon consent` gains no resolve verb — the app is the
  approval surface; the CLI keeps rules + log only).
- Any change to engine matching, rule ordering/precedence, policy storage, or prompt timeout
  semantics.
