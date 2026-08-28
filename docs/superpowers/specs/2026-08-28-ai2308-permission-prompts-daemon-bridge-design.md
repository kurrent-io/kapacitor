# Permission prompts for PTY sessions through the daemon bridge (AI-2308)

Slice of the desktop shell (parent AI-2171), split out of AI-2197 because the
source differs: a PTY-hosted Claude or interactive Codex session has no
structured turn stream, but its permission prompt already reaches the daemon.

The Chat tab (AI-2196) is rendered from the transcript, and the transcript never
carries a permission prompt — the prompt lives in the vendor's TUI on the PTY.
So Chat goes quiet mid-turn while the Terminal tab waits on a `y`, and a composer
send can answer a prompt the user cannot see.

What already exists: every hosted PTY agent — server-driven and local spawn alike
— launches with `KCAP_RENDERED_AGENT=1`, `KCAP_AGENT_ID` and `KCAP_DAEMON_URL` (a
loopback ephemeral port). Claude's `PermissionRequest` hook posts to
`KCAP_DAEMON_URL/claude/permission-request`; Codex's to `/codex/permission-request`.
The daemon's `LocalPermissionBridge` forwards the request to the server
(`RequestPermission2`, which returns a request id at once), awaits the server's
`PermissionResolved` push, and hands the hook its allow/deny JSON. The hook ↔
daemon leg is done; this slice adds daemon ↔ desktop app.

Out of scope: structured turn frames and routing the structured vendors' ACP
`request_permission` into these frames (AI-2197 consumes the frame pair defined
here); editing a tool's input before allowing it; an Activity-feed row for
permission decisions; detecting a prompt answered in the TUI.

## Decisions

Settled with the owner during brainstorming, 2026-08-28:

1. **The card lives on the Chat tab only.** The vendor's TUI keeps its own
   prompt up while the hook waits, so the Terminal tab needs nothing: the user
   can answer there directly. The rail row gets the needs-you pip and the tray
   goes to Attention while any request is pending. No separate prompt window.
2. **Allow / Allow always / Deny.** "Allow always" is what the web UI sends —
   `applyPermissions = [{type:"toolAlwaysAllow", tool}]`, composed by the client,
   not taken from `permission_suggestions` — and the daemon's Codex response
   builder strips `applyPermissions`, so the button appears for Claude sessions
   only. No `updatedInput` editing.
3. **Both sides race; first wins; the other side is settled.** A local decision
   settles the server by invoking the hub's existing `RespondToPermission` — the
   method the web UI calls, gated on the caller owning the agent, which the
   daemon's own hub connection satisfies — so the web card clears with no
   kcap-server change. A server decision settles the app by a `PermissionResolved`
   push over the local socket.
4. **A request is identified by a daemon-minted GUID, no identity echo.** The
   consent frames need a `prompt_id` echo because their request id is the agent
   id, reused across launches. A permission request id is never reused, so
   resolve-by-id is exact.
5. **Attribution to an agent is a ladder**: the hook payload's `agent_id`, then
   the agent whose resolved vendor session id matches, then the agent whose
   worktree path equals the payload's `cwd`. Both CLI hooks stamp `agent_id` from
   `KCAP_AGENT_ID`; an older CLI omits it and the fallbacks carry it.
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
| `PermissionResolved = 78` | daemon → client | `PermissionResolvedDto` JSON | pushed on every settlement, whichever side settled it |
| `PermissionAck = 79` | daemon → client | `PermissionAckDto` JSON | reply to `PermissionResolve` |

`FrameCodec` encodes all five as UTF-8 text (the consent arm of `Encode`/`Decode`);
`LocalFrame.PermissionJson(type, json)` is the constructor, beside `ConsentJson`.
A daemon that predates these values rejects byte 20 at its codec and closes —
that codec-level rejection is the fail-closed contract for a down-level daemon.

DTOs, snake_case on the wire, in `PermissionIpc.cs` with their own
`PermissionIpcJsonContext`:

```
PermissionPendingDto(
    string RequestId,          // daemon-minted GUID "N"; the resolve key
    string AgentId,            // the hosted agent the prompt belongs to
    string SessionId,          // vendor session id, dashless
    string Vendor,             // "claude" | "codex"
    string ToolName,
    JsonElement? ToolInput,    // the hook's tool_input, verbatim
    JsonElement? Suggestions,  // the hook's permission_suggestions, verbatim
    string RequestedAt)        // ISO-8601 "O"

PermissionResolveDto(
    string RequestId,
    string Decision,               // "allow" | "deny"
    JsonElement? ApplyPermissions, // relayed verbatim into PermissionDecision
    JsonElement? UpdatedInput)     // carried for AI-2197; the card never sets it

PermissionResolvedDto(
    string RequestId,
    string Outcome,   // "allow" | "deny" | "withdrawn"
    string Source)    // "app" | "server" | "agent_gone"

PermissionAckDto(bool Ok, string? Error)
```

Structural validity of a pending (the consent rule: STJ source-gen leaves a
missing member null, `{}` decodes fine): `request_id`, `agent_id`, `session_id`,
`vendor`, `tool_name` and `requested_at` non-empty. An invalid pending is
skipped by the subscriber, never fatal — ending the stream would make the
resubscribe replay redeliver it forever.

`LocalControlCapabilities.Current` gains `"permission/1"`, assembled beside the
three new `switch` arms in `LocalControlServer` (`PermissionSubscribe` and
`PermissionResolve` route to `PermissionIpc`; the `default` arm's expected-list
text names both). `HelloReply` advertises it; the app gates on it.

## 2. Daemon

### 2.1 `PermissionPromptBroker`

The rendezvous between the bridge (awaiting a verdict) and local-socket
subscribers, shaped like `LaunchConsentBroker`:

- `Register(PermissionPendingDto) → Task<PermissionDecision>`: adds the pending
  entry under one delivery gate and broadcasts `Pending` to every subscriber.
- `Subscribe() → (id, ChannelReader<PermissionStreamItem>)`: replays every
  pending entry into a fresh unbounded channel under the delivery gate, then
  registers it — replay xor broadcast, exactly once per request per subscriber.
  `Unsubscribe(id)` completes the channel. `HasSubscriber` is read by the bridge
  (decision 7).
- `TryResolve(requestId, PermissionDecision) → bool`: a claim — remove first,
  complete the TCS second, so a `true` result is a hard guarantee the decision
  applied. Broadcasts `Resolved(outcome, "app")`.
- `Settle(requestId, outcome, source)`: the server-won and withdrawn paths.
  Removes the entry, completes the TCS with the settling decision (a withdrawal
  completes it with deny), broadcasts `Resolved`.
- `WithdrawForAgent(agentId)`: `Settle(…, "withdrawn", "agent_gone")` for every
  pending entry of that agent.

`PermissionStreamItem` is `Pending(dto) | Resolved(dto)`. Removal is always
keyed on the exact entry instance (the `KeyValuePair` conditional remove), the
consent broker's discipline, even though ids are never reused — it keeps the
two brokers reviewable side by side.

Never persisted: a daemon restart clears pending prompts, and the hook's HTTP
request died with the daemon anyway.

### 2.2 `ServerConnection` split

`RequestPermissionAsync(sessionId, toolName, toolInput, suggestions, ct)` becomes
the composition of two new virtual members, so a fake can script each leg:

- `BeginPermissionRequestAsync(…) → string serverRequestId` — the
  `RequestPermission2` invoke under `ConnectionRetry`, unchanged.
- `AwaitPermissionDecisionAsync(serverRequestId, ct) → PermissionDecision` —
  `PendingPermissionRegistry.AwaitDecisionAsync`, unchanged. Cancelling `ct`
  drops the registry entry; a decision pushed afterwards is buffered and
  FIFO-evicted, the existing behaviour for a late push.

Plus `RespondToPermissionAsync(sessionId, serverRequestId, PermissionDecision, ct)`:
one `InvokeAsync("RespondToPermission", sessionId, requestId, decision.Behavior,
decision.ApplyPermissions, decision.UpdatedInput)`. The hub method's two trailing
ACP parameters are optional and are not sent. Failures — a `HubException`
("Permission request is no longer pending", the ownership gate, a server that
lacks the method) or a disconnected hub — are logged at Debug and swallowed: the
local decision already applied, and the web card is only cosmetic from here.

### 2.3 `LocalPermissionBridge`, interactive branch

Only the shared-token branch changes; the reviewer-token (unattended) branch is
untouched. Per request:

1. **Attribute.** Read `agent_id`, `cwd` and the canonical `session_id` from the
   payload. Resolve, in order: a live agent with that id; a live agent whose
   `SessionId`, dashes stripped and lower-cased, equals the payload's; a live
   agent whose `Worktree.Path` equals `cwd`. The orchestrator exposes one
   `TryAttributePermission(agentId, sessionId, cwd) → AgentInstance?` for this.
   Unattributed → the server-only path, byte-for-byte today's, plus one Debug
   log line per session id.
2. **Begin the server leg** as today (`BeginPermissionRequestAsync`). A throw
   here is a faulted server leg (step 4).
3. **Race.** `local = broker.Register(dto)`; `server =
   AwaitPermissionDecisionAsync(serverRequestId, linkedCt)` where `linkedCt`
   links the daemon token with a per-request CTS; `winner = await
   Task.WhenAny(local, server)`.
   - *Local won:* cancel the per-request CTS (drops the registry entry), answer
     the hook with the local decision, record `app` in the decision log, and
     fire-and-forget `RespondToPermissionAsync` so the web card clears. The
     broker already broadcast `Resolved("app")` inside `TryResolve`.
   - *Server won:* `broker.Settle(requestId, decision.Behavior, "server")`,
     answer the hook, record `server`.
4. **Faulted server leg** — `BeginPermissionRequestAsync` throws, or the await
   faults (not cancels): if `broker.HasSubscriber`, keep awaiting `local` alone;
   otherwise `Settle(requestId, "deny", "server")` and answer deny, today's
   behaviour. A later subscriber still sees the request in its replay when it
   was kept. When `Begin` itself threw there is no server request id, so a
   local win afterwards has nothing to settle server-side.
5. **Hook response** is built exactly as today from the winning
   `PermissionDecision`.

`RequestPermissionAsync` retries under `ConnectionRetry` until the hub is
ready, bounded only by the daemon token, so a disconnected daemon leaves the
server leg pending rather than faulted — the race handles that without a
special case: the app answers, the server leg is cancelled.

### 2.4 Withdrawal

Where the orchestrator removes an agent from `_agents` (its teardown path, after
the runtime has exited), it calls `broker.WithdrawForAgent(agentId)`. Each pending
entry settles `withdrawn`/`agent_gone`, the app drops its card, and the hook —
if it is somehow still connected — receives deny. Recorded in the decision log
with source `agent_gone`.

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

One record per settlement of an attributed interactive request. `Source` is
`app`, `server` or `agent_gone`; `Outcome` is `allow`, `deny` or `withdrawn`.

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
  `broker.TryResolve(requestId, new PermissionDecision(decision,
  applyPermissions, updatedInput))` → `Ok=true` or
  `Ok=false, "no pending permission request with that id"`.

### 2.7 Hook payloads (`Capacitor.Cli`)

`PermissionRequestCommand.HandleRenderedAgent` adds `"agent_id"` to the bridge
payload when `KCAP_AGENT_ID` is set and non-empty; `CodexHookCommand.
HandlePermissionRequestViaBridge` adds the same member to the node it posts. The
server-bound fallback path (`/hooks/permission-request`) is not touched. The
bridge reads the member by name from the parsed `JsonNode`, so an older daemon
ignores it and an older CLI simply omits it.

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
`ToolInputJson` (raw text of the element, for `ToolDetail.From`) and
`RequestedAt`.

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

- `ToolName`; `Detail = ToolDetail.From(ToolInputJson, root)` (the tool-row rule:
  a path under the session root reads relative to it).
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
`RailSessionViewModel` — and `RailSessionViewModel.NeedsYou` becomes an
`ObservableAsPropertyHelper<bool>`: `SessionStatusDots.NeedsAttention(status) ||
set.Contains(id)`, initial value from the status alone. Worktree and repository
rows already derive their pip from their sessions and need no change. The rail's
constructor takes `IObservable<IReadOnlySet<string>>` so tests script it.

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

- **Answered in the TUI while the card is up.** The vendor proceeds and ignores
  the hook's later answer. The daemon's pending entry lingers until the server
  cancels it at session end (`EndSessionForAgentAsync` → `PermissionResolved(deny)`
  → settled `server`) or the agent is withdrawn. The web card has the same
  limitation; detecting a TUI answer is out of scope.
- **The app answers a request the server just won.** The ack is `Ok=false`; the
  service treats it as `AlreadyDecided` and the entry is already gone.
- **Two answers from the app** (a double click across a slow socket): the first
  claim wins in the broker; the second acks `Ok=false`. `IsBusy` makes it
  unreachable from one card anyway.
- **Transport failure on resolve.** The entry stays, the card shows the reason,
  the buttons re-enable.
- **Daemon restart.** Pending is never persisted; the app's `Subscribed` clear
  empties the cache; the hook's HTTP request died with the daemon.
- **App disconnected.** Entries retained; reconnect replays whatever the daemon
  still holds and the tombstones drop what it does not.
- **Local win, `RespondToPermission` fails.** Logged at Debug; the web card stays
  until session end, the same as any unanswered web card today.
- **Late server push after a local win.** The per-request cancellation removed
  the registry entry; the push is buffered and FIFO-evicted by the existing cap.
- **Reviewer and unattended tokens.** Untouched — they never reach the race.

## 5. Testing

- **Core.Tests.Unit/LocalIpc:** `FrameCodec` round-trips for 20/21/77/78/79,
  including the empty-payload subscribe frame; wire contracts (snake_case names,
  `JsonElement` members survive a round-trip, `{}` decodes to nulls);
  `PermissionSubscriptionTests` mirroring `ConsentSubscriptionTests` —
  `Subscribed` boundary, pending, resolved, invalid pending skipped, unexpected
  frame ends, EOF ends; `LocalControlOpsTests` for resolve ack, `Ok=false`
  pass-through, timeout, unreachable; `ClaudePermissions.AlwaysAllow` shape.
- **Daemon.Tests.Unit/Services:** `PermissionPromptBrokerTests` — register
  broadcasts, subscribe replays exactly once, claim semantics, settle and
  withdraw broadcast `Resolved`, unsubscribe completes the channel;
  `PermissionIpcTests` — replay on subscribe, resolve ack ok/false, malformed
  and invalid payloads ack false; `LocalPermissionBridgeTests` additions with the
  fake server scripting both legs — local wins (server await cancelled,
  `RespondToPermission` invoked with the winning decision, hook JSON carries
  it), server wins (`Resolved("server")` pushed, hook JSON carries it), faulted
  server leg with and without a subscriber, attribution by `agent_id`, by
  session id, by `cwd`, unattributed falls through unchanged, withdrawal on agent
  removal; `LocalControlHelloTests` — `permission/1` advertised;
  `OwnerOnlyJsonlLogTests` (0600 from first byte, rotation) with the consent log
  tests kept green on the wrapper; `PermissionDecisionLogTests` record shape.
- **Cli.Tests.Unit/Commands:** both hooks stamp `agent_id` when `KCAP_AGENT_ID`
  is set and omit it otherwise (`EnvScope`, bare `[NotInParallel]`).
- **App.Tests.Unit:** `PermissionServiceTests` — capability gate start/stop,
  clear at `Subscribed`, replay, tombstone drops a ghost, resolve outcomes,
  `AgentsWithPending` seeds and updates; `PermissionCardViewModelTests` —
  commands, busy, error line, `ShowsAllowAlways` per vendor;
  `ChatTabViewModelTests` — cards filtered to the agent, ordering, removal on
  `Resolved`; `RailSessionViewModelTests` — `NeedsYou` flips with the set and
  with the status; `TrayViewModelTests` — Attention and header text;
  `ChatTabViewSmokeTests` — the card and its three buttons render headless and
  the row collapses when empty. A `FakePermissionService` joins
  `FakeConsentService`.

## Risks

- **`RespondToPermission` as the daemon.** The ownership gate passes because the
  daemon authenticates as the owner; the attribution event will name the owner
  as the responder, which is true. If a future server tightens the gate to UI
  connections, the local decision still applies and only the web card lingers.
- **Codex session-id shape.** The hook normalizes `session_id` to a dashless
  GUID; the daemon's `CodexSessionRolloutLocator` yields the rollout file's
  UUID. The ladder compares both dashless and lower-cased; the `agent_id` rung
  makes the comparison a fallback rather than the path.
- **Attribution before discovery.** A prompt can arrive before the locator has
  resolved the session id (a 2 s poll). The `agent_id` rung does not depend on
  discovery; only an older CLI relies on the `cwd` rung in that window.
- **Frame value collisions with AI-2197.** That slice must take values after 21
  and 79; this spec is the reservation.
