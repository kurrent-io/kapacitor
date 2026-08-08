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
threading through the consent pipeline (additive); headless ViewModel tests for the full
prompt matrix.

**Out:** macOS system notification (AI-1653); consent-rules editor UI (slice 4); any daemon
policy/engine behavior change; new IPC frame types; Windows/Linux app packaging.

## 4. Wire & Core changes

No new frame types and no protocol version change. Everything below is additive.

### 4.1 `ResolveConsentAsync` on `ILocalControlOps`

```csharp
Task<ConsentAckDto> ResolveConsentAsync(ConsentResolveDto resolve, CancellationToken ct);
```

One-shot, exactly the existing `LocalControlOps` pattern (connect → send → single reply →
close): writes `FrameType.ConsentResolve` with the `ConsentResolveDto` JSON as `Text`, expects
exactly one `ConsentAck` frame, deserializes `ConsentAckDto`. Per-phase timeouts and failure
classification are identical to the existing ops — caller-cancellation propagates
(`OperationCanceledException` checked first), `EndOfStreamException` before `IOException`
(derivation order) → `unexpected_reply`, transport → `daemon_unreachable`, phase timeout →
`timed_out`; a null/malformed ack or a non-`ConsentAck` frame → `unexpected_reply`. The
`ConsentAckDto` is returned as-is: `Ok` reflects the resolution outcome, `Error` carries either
the failure detail or the partial-failure warning on `Ok=true` (rejected `save_rule`).

The daemon handler exists (`LaunchConsentIpc.HandleResolveAsync`); this is client plumbing only.
No hello is needed on one-shot connections (hello is optional; the CLI consent verbs already
send bare frames).

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

Termination contract: EOF and transport `IOException` end the enumeration normally (an ended
stream means "disconnected" — the consumer decides whether to resubscribe). A frame of any
other type, or a `ConsentPending` frame whose JSON does not deserialize, also ends the
enumeration — protocol confusion is treated as a dead connection, not skipped, and the
consumer's resubscribe gets a fresh replay. Only `OperationCanceledException` (the caller's
`ct`) propagates.

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

`ReadTail` reads `{path}.1` then `{path}` (each may be absent), keeps the last `max` parseable
records, newest first. Rules:

- **Sharing:** every open is `FileAccess.Read` with `FileShare.ReadWrite | FileShare.Delete` —
  the daemon appends and rotates (`File.Move`) this file live; a default write-denying or
  delete-denying open would block the daemon's own writer on Windows (the AI-1629 bug class).
- **Malformed lines** (torn tail write, hand-edited file) are skipped, not fatal.
- **Rotation race:** a rotation between the two reads can transiently duplicate or miss lines.
  Records have value equality — the merged list is `Distinct()`-ed, and a miss self-heals on
  the next refresh (§7). Accepted: this is a display feed, not an audit query.
- Absent files → empty list (the feed renders its empty state; no error).

## 5. `ConsentService` (app)

`src/Capacitor.App/Services/ConsentService.cs` (+ `IConsentService`): owns the consent
subscription, the pending queue, and resolution. Single instance, created at startup beside
`DaemonClientService`.

**State:** `SourceCache<PendingConsent, string>` keyed by `RequestId`. `PendingConsent` wraps
the `ConsentPendingDto` plus a computed `DeadlineHint = RequestedAt + TimeoutSeconds`
(`DateTimeOffset` parse of the daemon's ISO stamp; same machine, so no clock-skew handling; an
unparseable `RequestedAt` falls back to arrival time + `TimeoutSeconds`).
The deadline is a *hint* — AI-1648's subscriber grace means the daemon's real deadline can
differ slightly; the authoritative outcome is only ever a resolve ack.

**Subscription lifecycle — status-driven.** The service observes
`DaemonClientService.Status`:

- On `Connected` with capabilities containing `"consent/1"` → start the subscription loop.
- On leaving `Connected` (Connecting/Unreachable) → cancel the loop. Pending entries are NOT
  cleared on disconnect — the daemon may still be alive holding live prompts; entries expire
  via their own deadline hints, and a resubscribe reconciles (below).
- `Connected` without `consent/1` (down-level daemon) → no subscription, no prompts; the
  existing shell surfaces the daemon-outdated condition.

**Loop:** while in the connected state: clear the pending cache, then enumerate
`ConsentSubscription.RunAsync(daemonName, ct)`, upserting each DTO by `RequestId`. The clear
immediately precedes each connection attempt — the daemon's replay is the authoritative
pending set, and there is no end-of-replay marker, so reset-then-rebuild is the only honest
reconciliation. If the enumeration ends while status still reports Connected (protocol
confusion, half-dead socket), delay 1s and go around. Loop cancellation stops cleanly.

**Pruning:** on the shared 1-second ticker, entries with `now > DeadlineHint + 5s` are removed
from the cache. This bounds ghost entries from requests that expired daemon-side (there is no
withdrawal push) and from disconnected periods. The prompt VM shows the expired state before
the prune fires (§6).

**Resolution:**

```csharp
Task<ConsentResolveOutcome> ResolveAsync(string requestId, bool allow, bool saveRule, CancellationToken ct);
```

Builds the `ConsentResolveDto` (`decision: allow|deny`; when `saveRule`, a requester-only
`save_rule` from the pending entry's `Requester`) and calls
`ILocalControlOps.ResolveConsentAsync`. One resolve in flight at a time — serialized on one
lane, same discipline as `PauseController`. Outcome mapping:

| Result | Cache effect | Meaning |
|---|---|---|
| `Ok=true, Error=null` | remove entry | applied |
| `Ok=true, Error!=null` | remove entry | applied; rule save rejected — warn |
| `Ok=false` | remove entry | already decided (daemon timeout / raced) |
| `LocalControlOpsException` / OCE | keep entry | transport failure — request may still be pending daemon-side |

**Exposed:** the pending cache (`IObservable` via DynamicData `.Connect()`),
`IObservable<int> PendingCount`, `ResolveAsync`, and a raise signal: an event/observable that
fires when an entry is added while the prompt window is not visible (drives auto-raise, §6).
The count feeds `TrayViewModel` (§8).

## 6. Prompt window

`ConsentPromptWindow` + `ConsentPromptViewModel`. One instance ever, owned by a coordinator
(pattern of `MainWindowCoordinator`): open-or-activate; closing hides/releases but a later
raise re-creates. Fixed compact size (460×260 starting point, subject to live-acceptance
polish; non-resizable), `Topmost = true`, centered.

**Raise policy:** the window opens and the app activates when a pending entry is added while
the window is not visible — this covers both the 0→1 transition and a new request arriving
after the user closed the window. Closing the window without deciding is an explicit *defer*:
the queue is untouched, the tray stays in `Attention`, and the tray menu's "Review pending
launches…" reopens it. Additions while the window is already visible just update the queue
indicator — no re-activation (no focus stealing mid-interaction).

**Content** — the oldest pending request (sort: `RequestedAt`, then `RequestId` ordinal for
determinism):

- Requester: `RequesterDisplay ?? Requester ?? "unknown"`, prominent.
- Kind label: `agent` → "Agent", `review` → "Review", `review-flow` → "Review flow".
- Repo: `RepoLabel.Leaf`, full path as tooltip. Vendor as-is.
- Countdown: "Expires in 37s", ticked by the shared UI-thread ticker from `DeadlineHint`.
- Queue indicator "1 of 3" when more than one pending.

**Buttons:**

| Button | Wire | Notes |
|---|---|---|
| Allow once | `ConsentResolve{decision: "allow"}` | |
| Always allow | `ConsentResolve{decision: "allow", save_rule: {action: "allow", requester: <id>, kind: null, repo: null, vendor: null}}` | Atomic — daemon stays the single rule writer. **Hidden when `Requester` is null**: a requester-only rule with a null requester would be a wildcard allow-everything rule. |
| Deny | `ConsentResolve{decision: "deny"}` | |

While a resolve is in flight all three disable (no double-submit); the countdown keeps
ticking.

**Terminal states (per request):**

- **Decided (`Ok=true`)** → entry removed, window advances to the next pending or closes when
  the queue empties. A non-null `Error` on `Ok=true` additionally shows a warning toast over
  the prompt window: "Decision applied — rule not saved: {error}".
- **Already decided (`Ok=false`)** → buttons are replaced by "Already decided" for 2 seconds
  (ticker-driven), then the entry is removed and the window advances. Never a silent success:
  the user always sees that their click did not decide this launch.
- **Expired (countdown ≤ 0)** → buttons are replaced by "Expired — denied by timeout" for
  2 seconds, then the entry is removed (the §5 prune is the backstop). If the user's click races the
  daemon's timeout, the ack (`Ok=false`) wins the display — same "Already decided" path.
- **Transport failure** → toast over the prompt window ("Daemon unreachable — the request is
  still pending"), buttons re-enable, entry stays. The daemon may still be waiting.

Toasts use `AppNotifier` extended to attach a `WindowNotificationManager` to the prompt window
— the main window may be closed, so prompt-related warnings must surface on the prompt window
itself.

**Pause interplay:** none needed. Pause is a wildcard deny rule at `rules[0]`, so paused
launches are rule-denied without prompting and simply appear in the Activity feed as denials.

## 7. Activity tab

The main window becomes a two-tab layout — **Agents** | **Activity** — with the AI-1651 agents
grid unchanged inside its tab. `ActivityViewModel` renders the decision log, newest first,
capped at 200 records.

**Refresh triggers** (no `FileSystemWatcher` — platform quirks, untestable timing):

- The Activity tab becomes visible (also covers window open on that tab).
- A stat poll on the shared ticker, every 2s while the tab is visible: compare
  (`LastWriteTimeUtc`, `Length`) of both files against the previous poll; re-read on change.
- A local resolve reached a conclusive ack (the user's own decision should appear
  immediately).

The reader is `ConsentDecisionLogReader.ReadTail(daemonName, 200)` — pure file I/O, so the
feed works with the daemon stopped or unreachable.

**Row rendering:** local timestamp (`yyyy-MM-dd HH:mm:ss` from `decided_at`), outcome badge
(`allowed` green / `denied` red), requester (`requester_display ?? requester ?? "unknown"`),
kind label (as §6), repo leaf (`RepoLabel`, full path tooltip), vendor, and a human source
label: `owner` → "owner", `rule[i]` → "rule", `default` → "default policy", `prompt_user` →
"you", `prompt_timeout` → "timeout", `prompt_no_ui` → "no UI attached" (unrecognized values
render verbatim). Empty state mirrors the Agents tab: centered "No decisions yet", no column
headers.

## 8. Tray integration

**State derivation.** `TrayViewModel` gains a `PendingConsentCount` input (from
`IConsentService.PendingCount`, combined into the existing derivation stream). New rule,
appended to the AI-1651 state table: when the connection-derived state is `Idle` or `Running`
and `PendingConsentCount > 0`, the state becomes `Attention` with header
"{N} launch(es) awaiting approval" — e.g. "1 launch awaiting approval". All
connection-trouble rows keep precedence: pending consent asserts `Attention` only while
Connected (which is also the only state where the subscription runs). The icon shows the
existing `Attention` rendering; the running-count badge continues to reflect the agent count.

**Menu.** A "Review pending launches…" `NativeMenuItem`, visible only when
`PendingConsentCount > 0`, placed between the agents section and the pause toggle. Click →
open/activate the prompt window via the coordinator. The existing `NeedsUpdate` rebuild
cadence picks up count changes through the model stream — no new refresh machinery.

## 9. Error handling summary

| Failure | Surface | Behavior |
|---|---|---|
| Daemon unreachable (no subscription) | Tray | Existing AI-1651 rows; no prompts possible; feed still renders from file |
| Daemon without `consent/1` | Tray | No subscription; existing down-level surfacing |
| Consent stream dies while Connected | none (self-heal) | 1s delay → resubscribe (fresh replay reconciles) |
| Resolve transport failure | Prompt toast | Entry kept; buttons re-enable |
| Resolve `Ok=false` | Prompt window | "Already decided" — never a silent success |
| Rule save rejected on `Ok=true` | Prompt toast | Decision applied; warning shown |
| Request expired (no withdrawal push) | Prompt window / prune | Countdown shows expiry; cache prune at hint+5s |
| Decision log absent/malformed lines | Activity tab | Empty state / lines skipped |
| Rotation race during read | Activity tab | `Distinct()` merge; miss self-heals next poll |

## 10. Testing

All headless (TUnit; `Avalonia.Headless` for VM/window behavior; fake `TimeProvider`
throughout — no real sleeps).

**Core:**
- `ResolveConsentAsync` against an in-proc fake server (pattern of existing `LocalControlOps`
  tests): ack round-trip (all four `Ok`/`Error` shapes), malformed ack → `unexpected_reply`,
  EOF → `unexpected_reply`, transport → `daemon_unreachable`, phase timeout → `timed_out`,
  caller-cancellation propagates.
- `ConsentSubscription`: replay + push frames yield DTOs in order; EOF ends enumeration;
  unexpected frame type ends enumeration; malformed pending JSON ends enumeration; `ct`
  propagates OCE. Absent `requester_display` deserializes to null.
- `ConsentDecisionLogReader`: tail across the rotation pair (order, cap, newest-first);
  malformed-line skip; `Distinct()` dedup; absent files → empty; old-format lines (no
  `requester_display`) parse with null; **a read succeeding while a writer holds an open
  append handle** (`FileShare` regression guard — the test that would have caught AI-1629).

**Daemon:**
- Gate threads `RequesterDisplay` into the prompt request and the decision record (extend
  existing gate tests); log writer round-trips through the hoisted Core type unchanged
  (existing files' snake_case field names asserted verbatim).

**App (the "full prompt matrix" from the issue):**
- Allow once / Deny → correct `ConsentResolveDto`, entry removed, queue advances.
- Always allow → `save_rule` is requester-only; button hidden when `Requester` is null.
- Countdown expiry → "Expired — denied by timeout", entry removed; prune removes ghost
  entries at hint+5s.
- `Ok=false` → "Already decided" state, then advance — never silent.
- `Ok=true` + `Error` → warning surfaced, entry removed.
- Transport failure → entry kept, buttons re-enable.
- Queue: "1 of N" indicator; oldest-first ordering; additions while visible don't re-raise;
  addition while closed raises.
- Subscription lifecycle: clear-before-(re)subscribe reconciliation; no subscription without
  `consent/1`; stream-end-while-Connected retries.
- `ActivityViewModel`: rows map records (fallback chains, source labels, unrecognized source
  verbatim); refresh on tab-visible / stat-change / own-resolution; empty state.
- `TrayViewModel`: new Attention row (pending>0 while Idle/Running → Attention + header);
  connection-trouble precedence unchanged; menu item visibility.

## 11. Deferred / out of scope

- macOS system notification → AI-1653 (needs bundle identity).
- Consent rules editor UI → umbrella slice 4.
- Attach/approve from the CLI (`kcap daemon consent` gains no resolve verb — the app is the
  approval surface; the CLI keeps rules + log only).
- Any change to engine matching, policy storage, or prompt timeout semantics.
