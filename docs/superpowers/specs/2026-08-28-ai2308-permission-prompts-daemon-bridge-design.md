# Permission prompts for PTY sessions through the daemon bridge (AI-2308)

Slice of the desktop shell (parent AI-2171), split out of AI-2197 because the
source differs: a PTY-hosted Claude or interactive Codex session has no
structured turn stream, but its permission prompt already reaches the daemon.

The Chat tab (AI-2196) is rendered from the transcript, and the transcript never
carries a permission prompt — the prompt lives in the vendor's TUI on the PTY.
So Chat goes quiet mid-turn while the Terminal tab waits on a `y`, and a composer
send can answer a prompt the user cannot see.

What already exists: every hosted PTY agent that is registered with the server
— a server-driven launch, or a local `kcap agent start` without `--private` —
launches with `KCAP_RENDERED_AGENT=1`, `KCAP_AGENT_ID` and `KCAP_DAEMON_URL` (a
loopback ephemeral port). Claude's `PermissionRequest` hook posts to
`KCAP_DAEMON_URL/claude/permission-request`; Codex's to `/codex/permission-request`.
The daemon's `LocalPermissionBridge` forwards the request to the server
(`RequestPermission2`, which returns a request id at once), awaits the server's
`PermissionResolved` push, and hands the hook its allow/deny JSON. The hook ↔
daemon leg is done; this slice adds daemon ↔ desktop app. A `--private` local
spawn gets none of those variables and never reaches the bridge: its only
prompt is the vendor's TUI, and nothing here changes that.

Out of scope: structured turn frames and routing the structured vendors' ACP
`request_permission` into these frames (AI-2197 consumes the frame pair defined
here); editing a tool's input before allowing it; an Activity-feed row for
permission decisions; detecting a prompt answered in the TUI; a server-side
claim protocol between the web UI and the daemon.

## Decisions

Settled with the owner during brainstorming, 2026-08-28:

1. **The card lives on the Chat tab only.** The vendor's TUI keeps its own
   prompt up while the hook waits, so the Terminal tab needs nothing: the user
   can answer there directly. The session row and its collapsed worktree row
   get the needs-you pip (the rail has no repository-level pip today and this
   slice adds none), and the tray goes to Attention while any request is
   pending. No separate prompt window.
2. **Allow / Allow always / Deny.** "Allow always" is what the web UI sends —
   `applyPermissions = [{type:"toolAlwaysAllow", tool}]`, composed by the client,
   not taken from `permission_suggestions` — and the daemon's Codex response
   builder strips `applyPermissions`, so the button appears for Claude sessions
   only. No `updatedInput` editing.
3. **One claim per request, in the daemon's broker.** The app, the server's
   push, agent withdrawal and the no-UI deny all settle a request through the
   same claim; the first claim wins and everything downstream — the hook's
   answer, the app's ack, the log record, the settlement push — derives from
   the claimed settlement. A local claim settles the server by invoking the
   hub's existing `RespondToPermission` — the method the web UI calls, gated on
   the caller owning the agent, which the daemon's own hub connection satisfies
   — so the web card clears with no kcap-server change. A server claim settles
   the app by a `PermissionResolved` push over the local socket. The two
   authorities are not mutually locked: a web answer that reaches the server's
   tracker in the same instant as an app claim leaves the server recording the
   web answer while the hook follows the daemon's claim (§4).
4. **A request is identified by a daemon-minted GUID, no identity echo.** The
   consent frames need a `prompt_id` echo because their request id is the agent
   id, reused across launches. A permission request id is never reused, so
   resolve-by-id is exact.
5. **Attribution to an agent is a ladder that succeeds only on exactly one
   match**: the hook payload's `agent_id`, then the live agent whose resolved
   vendor session id matches, then the live agent whose worktree path equals
   the payload's `cwd`. A rung with two matches falls through to the
   server-only path. Both CLI hooks stamp `agent_id` from `KCAP_AGENT_ID`, and
   the Claude hook's bridge payload gains `cwd`; an older CLI omits them and
   the remaining rungs carry it.
6. **Local decisions go to their own owner-only log**,
   `permission-decisions.jsonl`, written by the same 0600-from-first-byte,
   rotating writer the consent log uses, lifted into a shared type. No reader in
   this slice.
7. **A faulted server leg denies only when no local subscriber holds the
   request.** Today a server fault is an immediate deny; with the desktop app
   subscribed the request stays answerable locally.

## 1. Wire contract (`Capacitor.Cli.Core/LocalIpc`)

Five `FrameType` values, append-only, next free in each direction:

| Value | Direction | Payload | Semantics |
|---|---|---|---|
| `PermissionSubscribe = 20` | client → daemon | none | long-lived: replay every pending request, then push new pendings and every settlement |
| `PermissionResolve = 21` | client → daemon | `PermissionResolveDto` JSON | one-shot; reply is `PermissionAck` |
| `PermissionPending = 77` | daemon → client | `PermissionPendingDto` JSON | pushed on subscribe (replay) and on arrival |
| `PermissionResolved = 78` | daemon → client | `PermissionResolvedDto` JSON | pushed on every settlement, whichever side claimed it |
| `PermissionAck = 79` | daemon → client | `PermissionAckDto` JSON | reply to `PermissionResolve` |

`FrameCodec` encodes all five as UTF-8 text (the consent arm of `Encode`/`Decode`;
the subscribe frame's payload is empty text); `LocalFrame.PermissionJson(type,
json)` is the constructor, beside `ConsentJson`. A daemon that predates these
values rejects byte 20 at its codec and closes — that codec-level rejection is
the fail-closed contract for a down-level daemon.

DTOs, snake_case on the wire, in `PermissionIpc.cs` with their own
`PermissionIpcJsonContext`:

```
PermissionPendingDto(
    string RequestId,            // daemon-minted GUID "N"; the resolve key
    string AgentId,              // the hosted agent the prompt belongs to
    string SessionId,            // vendor session id, dashless
    string Vendor,               // "claude" | "codex"
    string ToolName,             // may be empty: the Codex hook permits it
    JsonElement? ToolInput,      // the hook's tool_input, verbatim, or null when omitted
    JsonElement? Suggestions,    // the hook's permission_suggestions, verbatim, or null when omitted
    bool ToolInputOmitted,       // true when tool_input exceeded MaxElementBytes
    bool SuggestionsOmitted,     // same for permission_suggestions
    string RequestedAt)          // ISO-8601 "O"

PermissionResolveDto(
    string RequestId,
    string Decision,               // "allow" | "deny"
    JsonElement? ApplyPermissions, // relayed verbatim into PermissionDecision
    JsonElement? UpdatedInput)     // carried for AI-2197; the card never sets it

PermissionResolvedDto(
    string RequestId,
    string Outcome,   // "allow" | "deny" | "withdrawn"
    string Source)    // "app" | "server" | "agent_gone" | "no_ui"

PermissionAckDto(bool Ok, string? Error)
```

**Size bound.** `FrameCodec` rejects any frame over 8 MiB, and a rejected frame
kills the subscription in the codec before structural validation can skip the
entry — a resubscribe would replay it and die again, forever. So every
caller-controlled value that reaches the local wire is bounded before
`Register`, and the hook body itself is read exactly as today:

- `agent_id` and `session_id` must be 32 lowercase hex characters (a `Guid`
  in `"N"` form — what `KCAP_AGENT_ID` carries and what both hooks normalize
  the session id to). A payload whose `session_id` is not that shape is
  unattributed; an `agent_id` that is not that shape skips its rung.
- `tool_name` over `MaxToolNameBytes = 512` bytes of UTF-8 makes the request
  unattributed — no real tool has such a name, and the server path still
  answers the hook.
- `MaxElementBytes = 64 KiB` of raw UTF-8 per JSON element; an element over it
  is sent as `null` with its `*_omitted` flag set, and the card says the input
  was too large to show.
- `vendor` comes from the URL path (`claude` | `codex`), `request_id` and
  `requested_at` are daemon-minted.

A maximal pending therefore serializes under 130 KiB; a test pins that against
`FrameCodec.MaxPayload` so the bound survives a DTO change.

Structural validity of a pending (the consent rule: STJ source-gen leaves a
missing member null, `{}` decodes fine): `request_id`, `agent_id`, `session_id`,
`vendor` and `requested_at` non-empty. `tool_name` may be empty. An invalid
pending is skipped by the subscriber, never fatal — ending the stream would make
the resubscribe replay redeliver it forever.

`LocalControlCapabilities.Current` gains `"permission/1"`, assembled beside the
two new `switch` arms in `LocalControlServer` (`PermissionSubscribe` and
`PermissionResolve` route to `PermissionIpc`; the `default` arm's expected-list
text names both). `HelloReply` advertises it; the app gates on it.

## 2. Daemon

### 2.1 `PermissionPromptBroker` — the one claim point

A singleton the bridge, `PermissionIpc` and the orchestrator all take (§2.8).
It owns every pending request and the only way to settle one:

```
PermissionSettlement(PermissionDecision Decision, string Outcome, string Source)
```

- `Register(PermissionPendingDto) → Task<PermissionSettlement>`. Under the
  delivery gate: if the dto's `agent_id` is in the withdrawn set, return an
  already-completed task (`deny` / `withdrawn` / `agent_gone`) and broadcast
  nothing — no subscriber ever saw a pending. Otherwise add the entry and
  broadcast `Pending` to every subscriber.
- `TrySettle(requestId, PermissionDecision, outcome, source) → bool` — the
  claim. Under the delivery gate: remove the exact entry instance (the
  `KeyValuePair` conditional remove), broadcast `Resolved(outcome, source)`,
  complete the entry's TCS with the settlement. `false` when no entry is
  pending under that id: the caller lost the claim and must not act as if it
  won. The TCS is created with `RunContinuationsAsynchronously`: it is completed
  while the gate is held, and a continuation running inline would re-enter the
  gate.
- `Subscribe() → (id, ChannelReader<PermissionStreamItem>)`: under the delivery
  gate, replay every pending entry into a fresh unbounded channel, then register
  it. `Unsubscribe(id)` completes the channel. `HasSubscriber` is read by the
  server leg (decision 7).
- `WithdrawForAgent(agentId)`: under the delivery gate, add the id to the
  withdrawn set, then `TrySettle(…, deny, "withdrawn", "agent_gone")` for every
  pending entry of that agent.

Replay/registration and claim/broadcast take the **same** gate, so for any
subscriber a request is observed as either nothing, or `Pending` then
`Resolved` — never `Pending` alone. The withdrawn set is service-lifetime:
agent ids are GUIDs never reused, so an entry can never suppress a future
agent, and it grows by one id per agent the daemon ever tears down.

`PermissionStreamItem` is `Pending(dto) | Resolved(dto)`. Never persisted: a
daemon restart clears pending prompts, and the hook's HTTP request died with
the daemon anyway.

### 2.2 `ServerConnection` split

`RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct)` becomes
the composition of two new virtual members, so a fake can script each leg:

- `BeginPermissionRequestAsync(…, ct) → string serverRequestId` — the
  `RequestPermission2` invoke under `ConnectionRetry`, unchanged. The retry
  loop honours `ct` before every attempt, so a caller can abandon a request
  that is still waiting for readiness before any server request exists.
- `AwaitPermissionDecisionAsync(serverRequestId, ct) → PermissionDecision` —
  `PendingPermissionRegistry.AwaitDecisionAsync`, unchanged. Cancelling `ct`
  drops the registry entry; a decision pushed afterwards is buffered and
  FIFO-evicted, the existing behaviour for a late push. The registry's
  remove-then-complete can lose a decision to a cancellation that lands
  between the two; the leg below cancels only after the broker has already
  settled by another source, so a decision lost there had already lost the
  claim.

Plus `RespondToPermissionAsync(sessionId, serverRequestId, PermissionDecision)`:
one `InvokeAsync("RespondToPermission", sessionId, requestId, decision.Behavior,
decision.ApplyPermissions, decision.UpdatedInput)` on the daemon-lifetime token
— never a per-request token, which the caller has just cancelled. The hub
method's two trailing ACP parameters are optional and are not sent. Failures — a
`HubException` (already answered, the ownership gate, a server without the
method) or a disconnected hub — are logged at Information with the request id
and swallowed: the local settlement has already applied.

### 2.3 `LocalPermissionBridge`, interactive branch

Only the shared-token branch changes; the reviewer-token (unattended) branch is
untouched. Per request:

1. **Attribute** through the seam in §2.8 with the payload's `agent_id`, the
   canonical `session_id` and `cwd`. Unattributed → the server-only path,
   byte-for-byte today's, plus one Debug log line per session id.
2. **Publish locally first**: `settlement = broker.Register(dto)`. The app sees
   the prompt before the server leg has even dialled, so a disconnected daemon
   still gets its prompt answered.
3. **Start the server leg**, detached (§2.3.1).
4. **Await `settlement`** with `WaitAsync(daemonToken)`. A completed
   settlement always wins over the token: if the wait throws but
   `settlement.IsCompletedSuccessfully`, the claim is final and is used — an
   app that was acked `Ok=true` must never see its answer turn into a shutdown
   deny. A genuine cancellation (shutdown before any claim) answers the hook
   deny with no log record, today's behaviour for a shutdown mid-wait.
   Otherwise: **append the log record first**, from `settlement.Outcome`/
   `Source`, then write the hook response from `settlement.Decision` (the JSON
   is built exactly as today). The response write is fallible — the hook may
   already be gone — and the record must not depend on it.

#### 2.3.1 The server leg

One method, `RunServerLegAsync(request, settlement)`, owns everything that
touches the server for a request. It runs on `legCt`, a per-request CTS
linked to the daemon token, and it is **total**: every exit below returns
normally, every exception is observed, and the bridge never awaits it.

- **Cancellation, any phase.** An `OperationCanceledException` on `legCt`
  ends the leg with no claim, no `RespondToPermission` and no record. It
  fires for daemon shutdown and for a settlement that completed while `Begin`
  was still waiting for readiness (below) — in that case no server request
  ever existed, so there is nothing to settle.
- `Begin(…, legCt)` → `serverRequestId`. A non-cancellation fault: with
  `broker.HasSubscriber` false, `TrySettle(requestId, deny, "deny", "no_ui")`
  — today's instant deny; with a subscriber, nothing — the request stays
  answerable locally, and a later subscriber sees it in its replay. No server
  request id exists either way.
- If `settlement` is already complete when `Begin` returns (the app or a
  withdrawal beat the server): `RespondToPermissionAsync(settlement.Decision)`
  unless the source is `server`; return.
- Otherwise `WhenAny(AwaitPermissionDecisionAsync(serverRequestId, legCt),
  settlement)`:
  - **`settlement` first** → cancel the per-request CTS, then
    `RespondToPermissionAsync(settlement.Decision)` unless the source is
    `server`. A `HubException` saying the request is no longer pending means
    the web answered inside the same window: log at Information that the
    server and the app both answered, with both decisions.
  - **the await first**, holding a decision → `TrySettle(requestId, decision,
    decision.Behavior, "server")`. If that claim loses, the app claimed in the
    gap between the push arriving and this continuation running: log the same
    Information line; nothing is sent back — the server's tracker is already
    resolved. The await completing cancelled (shutdown) is the cancellation
    exit above.

**Settlement cancels a `Begin` that has not reached the server.** The
settlement-first arm exists at every phase: `settlement` completing while
`Begin` is still inside the readiness wait cancels `legCt`, and
`ConnectionRetry` throws before invoking. So a local answer during an outage
leaves no server request behind, and neither does a teardown — the withdrawal
settles the entry and the leg ends. Only an invoke already on the wire at the
moment of cancellation can leave a request the daemon never learns of; it
clears at session end like any unanswered web card, and there is at most one
per request. The leg is observed by a `ContinueWith` that logs any fault at
Warning; there is no other completion boundary because there is nothing to
wait for.

### 2.4 Withdrawal

In the orchestrator's teardown path, immediately before `UnpublishAgent(agentId)`
drops the agent from `_agents`, it calls `broker.WithdrawForAgent(agentId)`.
Each pending entry settles `withdrawn`/`agent_gone`, the app drops its card,
and the hook — if it is somehow still connected — receives deny. The withdrawn
set closes the window between attribution (which found the agent live) and
`Register`: a registration for an already-withdrawn agent settles at once and
never becomes an ownerless entry.

### 2.5 Decision log

`OwnerOnlyJsonlLog(path, logger, maxBytes)` is `LaunchConsentDecisionLog`'s body
with the record type and file name lifted out: lazy 0700 directory, 0600 from the
first byte via `UnixCreateMode`, `.1` rotation at `maxBytes`, best-effort (an I/O
fault is logged and swallowed — audit must never fail a decision).
`LaunchConsentDecisionLog` becomes a thin wrapper over it; `PermissionDecisionLog`
is its sibling at `{stateDir}/permission-decisions.jsonl`.

```
PermissionDecisionRecord(
    string DecidedAt, string AgentId, string SessionId, string Vendor,
    string ToolName, string Outcome, string Source)
```

Exactly one record per settled, attributed request, written by the bridge from
the claimed settlement before the hook response is written (§2.3 step 4) —
never from a branch that guessed the source, never after fallible I/O.
`Source` is `app`, `server`, `agent_gone` or `no_ui`; `Outcome` is `allow`,
`deny` or `withdrawn`. A request cancelled by daemon shutdown before any claim
has no record.

### 2.6 `PermissionIpc`

Beside `LaunchConsentIpc`, same trust model (anything on the 0600 socket is the
owner):

- `HandleSubscribeAsync(stream, ct)`: `broker.Subscribe()`, an EOF watcher that
  cancels the loop when the client closes, then `ReadAllAsync` writing
  `PermissionPending` / `PermissionResolved` frames. `Unsubscribe` in `finally`.
  `IOException`/`SocketException` on a push are a vanished subscriber, absorbed
  the way `DaemonStatusIpc` absorbs them.
- `HandleResolveAsync(payload, stream, ct)`: deserialize; a null dto, empty
  `request_id`, or a decision outside `allow|deny` acks
  `Ok=false, "invalid resolve payload (decision must be allow|deny)"`; malformed
  JSON acks `Ok=false, "malformed resolve payload"`. Otherwise
  `broker.TrySettle(requestId, new PermissionDecision(decision,
  applyPermissions, updatedInput), decision, "app")` → `Ok=true`, or
  `Ok=false, "no pending permission request with that id"` when the claim lost.
  `Ok=true` is therefore a hard guarantee the app's decision is the one the
  hook receives.

### 2.7 Hook payloads (`Capacitor.Cli`)

`PermissionRequestCommand.HandleRenderedAgent` builds one payload today and
posts it to whichever endpoint it picks. It now builds the bridge payload on the
bridge branch only, as a copy of the server payload plus `agent_id` (from
`KCAP_AGENT_ID`, when set and non-empty) and `cwd` (the hook input's `cwd`,
when present) — the server-bound fallback payload is byte-for-byte unchanged.
`CodexHookCommand.HandlePermissionRequestViaBridge` adds `agent_id` the same
way to the object it posts, which already carries the hook's `cwd`. The bridge
reads both members by name from the parsed `JsonNode`, so an older daemon
ignores them and an older CLI simply omits them.

### 2.8 Composition

`AgentOrchestrator` already takes `LocalPermissionBridge` in its constructor
(it publishes the bridge URL and mints reviewer grants), so the bridge cannot
take the orchestrator. The seam is the one `ServerConnection` uses for
`FindRepoForRemoteHandler`: the bridge exposes

```
internal Func<PermissionAttribution, AgentInstance?>? AttributeHandler { get; set; }
PermissionAttribution(string? AgentId, string SessionId, string? Cwd)
```

and the orchestrator assigns it in its constructor, beside its other handler
assignments. A request arriving before assignment is unattributed and takes
the server-only path; the orchestrator is constructed before any agent can be
launched, so no hosted prompt can precede it. `PermissionPromptBroker` is a
plain singleton registered in `DaemonRunner` and injected into the bridge,
`PermissionIpc` and the orchestrator — no cycle.

The handler resolves "live" as *present in `_agents`* — the registry keeps an
instance through teardown until `UnpublishAgent`, and §2.4 makes a prompt
attributed in that window withdraw with the agent. Each rung must match
**exactly one** live agent, else it falls through: the `agent_id` rung by
ordinal id; the session rung by `SessionId` with dashes stripped, lower-cased
on both sides; the `cwd` rung by `Worktree.Path` with trailing separators
trimmed under `RepoPathStore.PathComparison` — several local in-place agents
can share one borrowed checkout, and a first-match over
`ConcurrentDictionary.Values` would be arbitrary.

## 3. App

### 3.1 Core: subscription and ops

`PermissionSubscription.RunAsync(store, daemonName, ct) →
IAsyncEnumerable<PermissionStreamEvent>` is `ConsentSubscription` with a third
event: `Subscribed` (client-local boundary, emitted after the subscribe write
flushes), `Pending(PermissionPendingDto)`, `Resolved(PermissionResolvedDto)`. A
failed dial ends the attempt with no `Subscribed`; a transport death, an
undecodable frame, an unexpected frame type or EOF ends it; an undecodable JSON
payload ends it; a structurally invalid pending is skipped; a resolved with an
empty `request_id` is skipped.

`ILocalControlOps.ResolvePermissionAsync(PermissionResolveDto, ct) →
PermissionAckDto`: one fresh-socket exchange with `ConsentReplyTimeout`, the
`ResolveConsentAsync` error mapping (`daemon_unreachable`, `daemon_rejected`,
`unexpected_reply`, `timed_out`).

`ClaudePermissions.AlwaysAllow(toolName) → JsonElement` owns the
`[{"type":"toolAlwaysAllow","tool":<name>}]` shape in one place.

### 3.2 `PermissionService`

`IPermissionService : IDisposable` beside `IConsentService`:

- `IObservable<IChangeSet<PendingPermission, string>> Pending` — keyed by request
  id; mutated on background continuations, consumers `ObserveOn` the UI
  scheduler.
- `IObservable<int> PendingCount` — DynamicData's `CountChanged`, replaying the
  current count on subscribe (the tray's combine relies on the seed).
- `IObservable<IReadOnlySet<string>> AgentsWithPending` — the distinct agent ids
  in the cache, replaying the current set; the rail's needs-you input.
- `Task<PermissionResolveOutcome> ResolveAsync(PendingPermission, PermissionAnswer, ct)`
  with `PermissionAnswer ∈ Allow | AllowAlways | Deny` and outcome kinds
  `Applied | AlreadyDecided | TransportFailure(reason)`.

`PendingPermission(dto)` exposes `RequestId`, `AgentId`, `Vendor`, `ToolName`,
`ToolInputJson` (raw text of the element, null when omitted),
`ToolInputOmitted` and `RequestedAt`.

Behaviour, carrying the consent service's reasoning where it applies:

- The loop starts when status is `Connected` with `permission/1` in the
  capability list, stops otherwise; a `Connected` daemon without the capability
  clears the cache (a different incarnation cannot answer these), a
  disconnected state retains it (the daemon may still hold live prompts).
- `Subscribed` clears the cache — at the boundary, never before the dial; the
  daemon's replay is then authoritative.
- `Pending` upserts unless the id is tombstoned. `Resolved` removes and
  tombstones. Tombstones live for the service lifetime: ids are never reused, so
  a tombstone can never suppress a future request, and any retirement boundary
  would reopen the ghost window (a replay snapshotted before a concurrent
  settlement can arrive after the settlement's push).
- **One lock**, the consent service's `_lock`, guards every cache mutation and
  the sets around it: the tombstone test and the upsert are one critical
  section; the tombstone add and the conditional evict (on an ack and on a
  `Resolved` push) are one critical section; the Connected-without-capability
  clear runs under it so an upsert that passed its tombstone test cannot land
  after the clear; `Subscribed`'s clear runs under it for the same reason; the
  disposed flag is set under it and every entry point returns once it is set.
  The stream loop, the status subscription and `ResolveAsync` all run on
  different continuations, and this lock is what makes the reasoning above
  hold rather than merely resemble the consent service's.
- `ResolveAsync` builds the DTO — `AllowAlways` is `allow` plus
  `ClaudePermissions.AlwaysAllow(toolName)` — sends it, and on any ack removes
  and tombstones the entry: `Ok=true` is `Applied`; `Ok=false` is
  `AlreadyDecided` (the `Resolved` push has done or will do the same removal).
  A `LocalControlOpsException` or unmapped failure leaves the entry in place
  and returns `TransportFailure` with the coded reason. Caller cancellation
  propagates.
- No prune timer: a permission has no deadline, and every settlement is
  pushed.
- Concurrency is per entry (`IsBusy` on the card), not a global lane — the
  daemon's claim serializes competing answers.

Wired in `App.StartAsync` beside `ConsentService`, with `PermissionSubscription.
RunAsync` as its subscribe function, disposed in the same order.

### 3.3 Chat tab

`ChatTabViewModel` takes `IPermissionService` and exposes
`IAvaloniaReadOnlyList<PermissionCardViewModel> PendingPermissions`: `Pending`
filtered to its agent id, `ObserveOn` the UI scheduler, sorted by `RequestedAt`
then request id, bound to an `AvaloniaList`. Cards are created per entry and
disposed on removal.

`PermissionCardViewModel(PendingPermission, IPermissionService, string? root)`:

- `ToolName` (`Tool call` when the wire's name is empty); `Detail =
  ToolDetail.From(ToolInputJson, root)` (the tool-row rule: a path under the
  session root reads relative to it), or `Input too large to show` when
  `ToolInputOmitted`.
- `ShowsAllowAlways = Vendor == "claude"`.
- `AllowCommand`, `AllowAlwaysCommand`, `DenyCommand`: each sets `IsBusy`, calls
  `ResolveAsync`, and on `TransportFailure` clears `IsBusy` and sets `ErrorText`
  to a short line carrying the coded reason ("Daemon unreachable — try again").
  `Applied` and `AlreadyDecided` need no UI action: the entry leaves the cache
  and the card with it. Commands are disabled while busy.

`ChatTabView` gains a grid row between the items list and the composer: hidden
when `PendingPermissions` is empty; otherwise a `NEEDS YOU` caption (the muted
small-caps style the design canvas uses for section labels) over an
`ItemsControl` of cards. A card is an accent-bordered `Border` on the raised
surface: tool name in semibold, the detail beneath it trimmed with an ellipsis,
the error line when set, and a right-aligned button row — `Deny` (transparent,
danger foreground), `Allow always` (transparent, only when `ShowsAllowAlways`),
`Allow` (accent, the Send button's style). The composer stays enabled: the risk
AI-2196 accepted was a prompt the user could not see, and now they can.

### 3.4 Rail and tray

`AgentsWithPending` is threaded down the rail the way `selectedAgentId` is —
`SessionRailViewModel` → `RailRepoViewModel` → `RailWorktreeViewModel` →
`RailSessionViewModel`; the rail's constructor takes the
`IObservable<IReadOnlySet<string>>` so tests script it.

- `RailSessionViewModel.NeedsYou` becomes an `ObservableAsPropertyHelper<bool>`:
  `SessionStatusDots.NeedsAttention(status) || set.Contains(id)`, initial value
  from the status alone.
- `RailWorktreeViewModel.NeedsYou` today queries its own dto cache for
  `NeedsAttention` and does not read its session rows. It becomes that query
  combined with the set: `cache.Any(NeedsAttention) || cache.Keys ∩ set ≠ ∅`,
  via `QueryWhenChanged().CombineLatest(agentsWithPending, …)` — the shape its
  `HoldsSelected` already uses. A collapsed worktree therefore still shows a
  permission-only alert.
- `RailRepoViewModel` has no pip and gains none.

`TrayViewModel` takes `IPermissionService` and adds `PendingCount` to its
combine: Attention while `Connected` and the count is positive, the consent
row's rule; header text `N permission request(s) waiting`, preceding the consent
text when both apply. No new menu entry — the rail pip and the workspace are the
path. The tray's replay-on-subscribe assertion extends to the new input.

### 3.5 Older daemon

No `permission/1` → the loop never starts, the cache stays empty, the row is
collapsed, the rail and tray are unchanged. The vendor's own prompt on the
Terminal tab is the answer path, as today. Nothing tells the user a card would
exist on a newer daemon; the daemon-update nudge already covers that.

## 4. Edge cases

- **Server and app answer within one push latency** — two real interleavings,
  both following the daemon's claim for the hook. (a) The app claims before the
  server's push reaches the daemon: the settlement-first arm sends
  `RespondToPermission`, the server rejects it as already answered, the leg
  logs both decisions. (b) The push reaches the daemon first but the app claims
  before the leg's continuation runs: the server's `TrySettle` loses, nothing
  is sent back, the same line is logged. The reverse — the server's claim
  wins — leaves the app's ack `Ok=false` and the card gone. Not closable
  without a server-side claim protocol, which is out of scope.
- **Answered in the TUI while the card is up.** The vendor proceeds and ignores
  the hook's later answer. The daemon's pending entry lingers until the server
  cancels it at session end (`EndSessionForAgentAsync` → `PermissionResolved(deny)`
  → claimed `server`) or the agent is withdrawn. The web card has the same
  limitation; detecting a TUI answer is out of scope.
- **Outage.** The app keeps answering; each answer cancels its own `Begin`
  inside the readiness wait, so nothing accumulates and the reconnect replays
  no stale prompts. Withdrawal on teardown does the same.
- **`Begin` already on the wire when the settlement lands.** At most one
  server request per local answer can survive the cancellation; it clears at
  session end.
- **Withdrawal between attribution and `Register`.** The withdrawn set makes the
  registration settle at once; no subscriber ever sees a pending.
- **Two answers from the app** (a double click across a slow socket): the first
  claim wins; the second acks `Ok=false`. `IsBusy` makes it unreachable from one
  card anyway.
- **Transport failure on resolve.** The entry stays, the card shows the reason,
  the buttons re-enable.
- **Daemon restart.** Pending is never persisted; the app's `Subscribed` clear
  empties the cache; the hook's HTTP request died with the daemon.
- **Daemon shutdown mid-request.** Before any claim: the bridge's wait is
  cancelled, the hook gets deny, no log record, the leg exits through its
  cancellation arm, and the broker and registry entries die with the process.
  After a claim the bridge had not yet observed: the completed settlement wins
  and is answered and recorded normally.
- **App disconnected.** Entries retained; reconnect replays whatever the daemon
  still holds and the tombstones drop what it does not.
- **Subscriber gone after a faulted `Begin`.** The request was kept because a
  subscriber existed at fault time; it stays pending until the app returns, the
  agent exits, or the hook's own ceiling — never denied on the subscriber's
  departure.
- **Oversized `tool_input`.** Omitted on the wire with the flag set; the card
  still offers the decisions; the hook receives them unchanged. An oversized
  `tool_name` or a non-canonical id makes the request unattributed instead.
- **Hook gone before the response.** The record was appended first; the
  response write's failure is logged, as today.
- **Local claim, `RespondToPermission` fails.** Logged; the web card stays until
  session end, the same as any unanswered web card today.
- **Late server push after a local claim.** The per-request cancellation removed
  the registry entry; the push is buffered and FIFO-evicted by the existing cap.
- **Reviewer and unattended tokens.** Untouched — they never reach the race.
- **`--private` local spawns.** Never on the bridge; unchanged.

## 5. Testing

- **Core.Tests.Unit/LocalIpc:** `FrameCodec` round-trips for 20/21/77/78/79,
  including the empty-payload subscribe frame; wire contracts (snake_case names,
  `JsonElement` members survive a round-trip, `{}` decodes to nulls and false
  flags); `PermissionSubscriptionTests` mirroring `ConsentSubscriptionTests` —
  `Subscribed` boundary, pending, resolved, invalid pending skipped, empty
  `tool_name` delivered, unexpected frame ends, EOF ends; `LocalControlOpsTests`
  for resolve ack, `Ok=false` pass-through, timeout, unreachable;
  `ClaudePermissions.AlwaysAllow` shape.
- **Daemon.Tests.Unit/Services:** `PermissionPromptBrokerTests` — register
  broadcasts, subscribe replays exactly once, `TrySettle` claim semantics (the
  second claim loses, the TCS carries the first), settle and withdraw broadcast
  `Resolved`, register for a withdrawn agent settles at once and broadcasts
  nothing, subscribe-versus-settle under a barrier yields nothing or
  Pending-then-Resolved and never Pending alone, unsubscribe completes the
  channel; `PermissionIpcTests` — replay on subscribe, resolve ack ok/false,
  malformed and invalid payloads ack false; `LocalPermissionBridgeTests`
  additions with the fake server scripting both legs and barriers between them —
  app claim first (hook JSON carries it, `RespondToPermission` invoked with it
  on the daemon token, server await cancelled, one `app` record; and the
  variant where `RespondToPermission` reports already-answered logs both
  decisions), server claim first (hook JSON carries it, `Resolved("server")`
  pushed, an app resolve after it acks false, one `server` record), server
  push delivered then app claim before the leg's continuation (hook keeps the
  app decision, no `RespondToPermission`, the disagreement logged), `Begin`
  faulted with and without a subscriber (`no_ui` deny only without), `Begin`
  waiting for readiness when the app answers (leg cancelled, no server
  request, no `RespondToPermission`), several local answers across a
  prolonged disconnect then reconnect (no server requests created, no burst),
  `Begin` waiting when the agent is torn down (withdrawn, leg cancelled),
  withdrawal between attribution and `Register`, attribution by `agent_id`, by
  session id, by `cwd`, two agents on one borrowed `cwd` and two agents with one
  session id both fall through, non-canonical ids and an oversized `tool_name`
  fall through, oversized elements omitted with flags at the exact boundary,
  a maximal pending frame under `FrameCodec.MaxPayload`, unattributed falls
  through unchanged, shutdown before any claim answers deny with no record and
  the detached leg completes cleanly, shutdown after an unobserved claim
  answers and records the claim, a hook response write that throws still
  leaves the record, registry cleanup after every interleaving;
  `LocalControlHelloTests` — `permission/1` advertised;
  `OwnerOnlyJsonlLogTests` (0600 from first byte, rotation) with the consent log
  tests kept green on the wrapper; `PermissionDecisionLogTests` record shape;
  an orchestrator test that teardown withdraws before `UnpublishAgent`.
- **Cli.Tests.Unit/Commands:** the Claude bridge payload carries `agent_id` and
  `cwd` iff present, the server fallback payload is unchanged, and the Codex
  bridge object carries `agent_id` (`EnvScope`, bare `[NotInParallel]`).
- **App.Tests.Unit:** `PermissionServiceTests` — capability gate start/stop,
  clear at `Subscribed`, replay, tombstone drops a ghost, resolve outcomes,
  `AgentsWithPending` seeds and updates, forced interleavings under the lock
  (Pending racing an ack, Pending racing a `Resolved` push, Pending racing the
  capability clear, disposal racing the loop); `PermissionCardViewModelTests` —
  commands, busy, error line, `ShowsAllowAlways` per vendor, omitted input
  text, empty tool name; `ChatTabViewModelTests` — cards filtered to the agent,
  ordering, removal on `Resolved`; `RailSessionViewModelTests` and
  `RailWorktreeViewModelTests` — `NeedsYou` flips with the set and with the
  status, and a collapsed worktree shows a permission-only alert;
  `TrayViewModelTests` — Attention and header text; `ChatTabViewSmokeTests` —
  the card and its three buttons render headless and the row collapses when
  empty. A `FakePermissionService` joins `FakeConsentService`.

## Risks

- **`RespondToPermission` as the daemon.** The ownership gate passes because the
  daemon authenticates as the owner; the attribution event will name the owner
  as the responder, which is true. If a future server tightens the gate to UI
  connections, the local claim still applies and only the web card lingers.
- **Old Claude hook binaries.** Today's Claude bridge payload carries neither
  `agent_id` nor `cwd`, so a prompt from an older CLI reaches the daemon with
  the session-id rung only — and that rung needs discovery to have resolved the
  session id (a 2 s poll). A prompt in that window from an older CLI is
  unattributed and takes the server-only path. The Codex hook forwards its
  whole input, so its `cwd` rung works from any version.
- **Codex session-id shape.** The hook normalizes `session_id` to a dashless
  GUID; the daemon's `CodexSessionRolloutLocator` yields the rollout file's
  UUID. The ladder compares both dashless and lower-cased.
- **Frame value collisions with AI-2197.** That slice must take values after 21
  and 79; this spec is the reservation.
