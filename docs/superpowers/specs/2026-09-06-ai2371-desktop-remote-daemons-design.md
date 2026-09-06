# Desktop app: remote development against other machines' daemons

**Issues:** kcap-cli [#708](https://github.com/kurrent-io/kcap-cli/issues/708) / AI-2371
**Status:** approved design, pre-implementation

## Problem

The desktop app renders only what the local daemon's 0600 control socket carries. A user whose
agents run on another machine — a service-installed daemon registered with the server — sees none
of them; the app's answer to "what is running on my other machines?" is a link to the web UI.
The goal is full web-UI parity from the desktop app: see every daemon and agent in the org,
launch on a chosen machine, chat, answer permission prompts and questions, watch the terminal,
and stop agents — all for daemons the app has no socket to.

## Decision: the app becomes a direct server client

The app grows one long-lived authenticated connection to the server's SignalR hub
(`{server}/hubs/sessions`) plus authenticated HTTP, and consumes the same remote contract the
MAUI app already proves out (`Capacitor.Mobile.Core`'s `RemoteAgentService` /
`RemoteSessionService` in kcap-server). The daemon-relay alternative (proxying the server's
registry over new local IPC frame families) is rejected: the app is already a server client for
launches (`RequestLaunchAgentV2`) and already owns the profile credential (the sign-in wizard
runs in-process), so the relay's premise — a server-blind, credential-free app — does not hold,
and it would cost a frame pair per stream, a daemon that becomes a UI proxy, and a double hop.

Facts about the server contract in this document were read from kcap-server `main` and are
restated here so implementation doesn't re-derive them; verify against kcap-server when a call
site looks different.

### Sub-decisions

- **Consent stays local.** A server-driven launch on a remote daemon passes that machine's
  consent gate over its local socket only. No UI attached there and no matching rule means the
  launch is denied (`prompt_no_ui`) — the web has the same limitation. The app renders the
  denial well (§6); a hub consent relay would change a deliberately local-owner-only trust
  surface and is out of scope.
- **Contracts move into this repo** as a new models project (§1). kcap-server consumes kcap-cli
  as a submodule, so the server side (mobile app, server abstractions) can re-point at this
  project later; this repo never references server-side projects.
- **Local daemon stays authoritative for its own agents.** The local IPC wire is richer than the
  server projection; the server lane covers everything else, and covers the local daemon too
  when its socket is unreachable.
- **When suppression is uncertain, show duplicates rather than hide agents.** Every dedup rule
  below fails open: an agent rendered twice is an annoyance, an agent hidden by a wrong identity
  guess is an invisible running process.

## 1. Contracts project: `Capacitor.Remote.Models`

New `src/Capacitor.Remote.Models/`, referenced by `Capacitor.App`. Zero dependencies, AOT-clean,
snake_case source-gen `JsonSerializerContext`. It holds the UI-client↔server contract:

- **DTOs** mirrored from kcap-server: `AgentInstanceDto` (26 fields incl. `AgentId`, `SessionId`,
  `Status`, `Prompt`, `Vendor`, `RepoPath`, `RepoOwner/RepoName/RepoHash`, `OwnerUserId`,
  `DaemonName`, `SandboxPolicy`, `ApprovalPolicy`, `PermissionPreset`, timestamps, `EndedAt`),
  `DaemonInfo` (`Name`, `Platform`, `RepoPaths`, `MaxAgents`, `ActiveAgents`, `Connected`,
  `OwnerUserId`, `Version`, `MachineId`, vendor capability lists), `PermissionResponsePayload`
  (`behavior`, `apply_permissions`, `updated_input`, `selected_option_id(s)`,
  `selected_option_label(s)`, `free_text`), `AcpInteractionOption` (`OptionId` is identity,
  `Label` display-only, `MinSelections`/`MaxSelections` stamped on every option of a
  multi-select), and `AcpEventEnvelope`.
- **Names as constants**: hub methods a UI client invokes (`GetConnectedDaemons`,
  `RequestLaunchAgentV2`, `RequestStopAgent`, `SendUserInput`, `SendSpecialKey`,
  `SubscribeToTerminal`/`UnsubscribeFromTerminal`, `RequestResizeTerminal`/`ReleaseResizeTerminal`,
  `SubscribeToChat`/`UnsubscribeFromChat`, `SubscribeToAcpEphemeral`, `SubscribeToStream`,
  `RegisterSessionAccessWatch`, `ResolveAttribution`), broadcasts it receives
  (`AgentInstancesChanged`, `DaemonsChanged`, `LaunchFailed`, `PermissionPending`,
  `PermissionResponded`, `PermissionRequested`, `AcpElicitationRequested`, `PendingInputChanged`,
  `TerminalOutput`, `TerminalDimensions`, `SessionTitleChanged`, `ActiveSessionAdded/Changed/`
  `Removed`, `SessionAccessChanged`), the special-key vocabulary (`Escape`, `Tab`, `Enter`,
  `CtrlC`, `ArrowUp`, `ArrowDown`, `ShiftTab`), and HTTP routes (`/api/agent-instances`,
  `/api/daemons`, `/api/sessions/{id}/detail`,
  `/api/sessions/{id}/permission-response/{requestId}`).

Wire rules the project's doc comments must carry: SignalR JSON does not backfill missing trailing
arguments, so every hub method's arity is frozen — new capability means a new method taking one
record; DTO property names are the contract on both transports.

Existing daemon-facing mirrors in `Cli.Core/Models.cs` stay put. Migrating them here, and
re-pointing kcap-server's `Capacitor.Api.Public.Abstractions` consumers and
`Capacitor.Mobile.Core` at this project through the submodule, is server-side follow-up work
(§7) — the end state is one contract project, owned here.

## 2. Server lane: `ServerConnectionService`

One app-lifetime singleton owning the hub connection; `ServerLaunchClient` retires into it so the
app holds exactly one server connection instead of one per launch.

- **Connection**: `HubConnection` to `{serverUrl}/hubs/sessions`, `AccessTokenProvider` →
  `TokenStore.GetValidTokensForServerAsync(profile, serverUrl)` (the existing pattern, shared
  with the daemon's `ServerConnection` so conventions never drift), snake_case JSON protocol,
  automatic reconnect plus a manual closed-restart loop — SignalR raises neither `Reconnected`
  nor retry for cold-start failures, so the loop re-dials with backoff exactly as the MAUI
  client documents.
- **Subscriptions**: org-wide broadcasts arrive without any join call (the server adds every
  authenticated connection to its org/team group on connect). Per-scope groups — `chat:{sessionId}`,
  `terminal:{agentId}`, ephemeral, session-access watches — are joined on demand by the surfaces
  in §5 and re-joined by the service after every reconnect; the server forgets group membership
  on disconnect.
- **Exposure**: Rx streams in the house style (`BehaviorSubject`/replay-1 status, typed
  observables per broadcast), plus thin async ops for invokes. The service is transport only;
  interpretation lives in the consumers.
- **HTTP with live credentials**: HTTP calls do not hold one client with a frozen bearer header
  for the app's lifetime. Requests go through a leased client acquired per call (the
  `ServerWorkContextSource` refresh-enabled pattern) with bounded 401 recovery opted in via the
  existing `HttpClientExtensions` choke point, so a token refreshed by the hub's
  `AccessTokenProvider` is also what HTTP sends. An unrecoverable auth result retires the lease
  and parks the lane behind the sign-in notice.
- **Auth states**: signed out → lane dormant, no connection attempts. 401/negotiate failure →
  the existing `SignInExpiredNotice` surfaces; no new auth UX. The lane starts when a valid
  profile token exists and the server URL is configured, independent of local daemon state.
  Successful re-authentication rebuilds both transports and re-seeds the caches. If the
  authenticated identity changes (different user or tenant than the one the caches were seeded
  under), the remote caches, pending attention, and open remote workspaces are invalidated —
  nothing seeded under one identity survives into another.
- **Viewer identity**: the app derives its own user id from the bearer token's subject claim —
  the same principal the server stamps into `OwnerUserId` on daemons and agents. It is used only
  to classify rows as "mine" (launchability, §5); all authorization stays server-side.

## 3. Remote agents source and the merge

`RemoteAgentsService` maintains:

- an agents cache seeded from `GET /api/agent-instances` and refreshed on
  `AgentInstancesChanged` (debounced; the ping carries no payload),
- a daemons cache from hub `GetConnectedDaemons()` refreshed on `DaemonsChanged`.

`AgentDirectory` merges the local `DaemonClientService.Agents` cache and the remote cache into
one stream all surfaces consume. Entries are keyed per source — `(lane, agentId)` — so the two
lanes can never clobber each other, and a *logical* row identity is layered on top: when the
twin match below proves the two entries describe one agent, they bind into one logical row
(precedence per "Origin changes" below) and workspaces/navigation address that logical row,
stable across origin changes; when twin identity is unproven, the entries render as separate
rows, each acting only through its own lane — the fail-open duplicates. Rows are a common
projection (`AgentRow`) with an `Origin` (`Local`/`Remote`), the shared fields both wires
carry, and nullable rich fields only the local wire has (`worktree_path`, `work_location`,
`borrowed_from`, `has_terminal`, `title`, `transcript_path`, `branch`).

### Local-daemon identity and suppression

The local wire carries no owner id, so the merge never compares `(OwnerUserId, DaemonName)`
against local data. Instead the local daemon's server twin is identified inside the *server's*
daemons cache:

- **Server scoping first**: the local lane participates in suppression only when the local
  daemon's reported `ServerUrl` (from `DaemonInfoDto`) canonicalizes equal to the app profile's
  server. A local daemon bound to a different server still renders locally, but no remote row is
  suppressed on its account — they are different worlds and cannot be twins.
- **Twin match**: the server daemon row whose `MachineId` equals this machine's persisted
  machine id (`MachineId` store, `machine.json`) *and* whose `Name` equals the local daemon's
  name. Exactly one match → its agents are twins of the local rows and are suppressed while the
  local lane is `Connected`. Zero or multiple matches (older daemon that never reported a
  machine id, two owners running same-named daemons on one machine) → no suppression, per the
  fail-open sub-decision: worst case is a duplicate row, never a hidden agent.
- When the local socket is unreachable, suppression lifts and the server's rows for the local
  daemon show instead — daemon-down observability comes free.

### Grouping

Rail grouping generalizes from "local repo root path" to a repository identity key, kept
separate from any physical checkout path (the group key is no longer assumed to be a real local
directory; per-row checkout labels come from the row's own worktree/path fields):

- **Local rows**: repository identity is derived locally — the existing repo-root resolution
  plus the checkout's origin remote (owner/name) where one exists. A local repo with no remote
  keeps a path-scoped identity.
- **Remote rows**: `RepoOwner/RepoName` (or `RepoHash`) where the server has it; otherwise the
  fallback key is machine-scoped — `(daemon identity, repo path)` — so two machines both
  advertising `/work/repo` never merge on bare path.
- Rows with the same repository identity form one group regardless of machine; remote sessions
  carry a machine badge (daemon name, machine identity via the daemons cache). Worktree
  sub-grouping applies only where `worktree_path` is known — local rows today, remote rows once
  §7 enrichment lands.

Field degradation for remote rows: title falls back to `Prompt` (plus `SessionTitleChanged` /
session detail titles when available); terminal availability derives from `Vendor` through
`HostedHarnessCatalog` (the server DTO has no `has_terminal`); no branch/worktree until §7.
Private local agents are never registered with the server, so they correctly never appear as
remote rows anywhere.

### Origin changes and open workspaces

`DaemonClientService` deliberately retains local rows across disconnects, so the merge states an
explicit precedence: while the local lane is `Connected`, the local row wins; while it is not,
the server row (if any) wins and the retained local row is display-only history. A session ends
only when the winning authority says so (server `EndedAt`/terminal status, or local terminal
status) — a local cache removal while a live server row exists must not mark the workspace
ended. Open workspaces bind to the logical row and observe its origin: on a transition the
origin-bound adapters (transcript feed, terminal transport, composer target) are torn down and
rebuilt for the new origin; actions (stop, sends) route against the row's origin *at invocation
time*, and an in-flight action that loses its lane fails visibly rather than silently.
Permission and question answers are the exception: they never route by row origin — each pending
request carries its own response handle bound to the lane that delivered it (§4).

## 4. Aggregated attention

NEEDS YOU pips, the tray attention state, and pending cards all derive from
`PermissionService`'s pending cache today, so aggregation is achieved by feeding that cache from
both lanes rather than by new UI.

### Lane-scoped stores

`PermissionService` holds two lane-scoped pending stores; each is snapshot-replaced only by its
own lane. The local IPC `Subscribed` snapshot replaces local-lane items only; a server-lane
re-seed replaces server-lane items only. (Today's whole-cache clears on `Subscribed`/`Connected`
are re-scoped — the local lane's lifecycle must never erase remote pending state, or vice
versa.)

### Response handles: a request is answered on the lane that delivered it

Every pending item carries a response handle — the delivering lane plus that lane's request id —
and is answered only through it: a local-lane item via `LocalControlOps.ResolvePermissionAsync`
with the local id, a server-lane item via the HTTP permission-response POST with the server
request id. The two ids for one prompt are independently allocated and each transport rejects
the other's, so ids are never cross-submitted, whatever the agent row's origin says (an ACP
question on a *local* agent is a server-lane item and is answered over HTTP). A "no longer
pending" rejection from an item's *own* lane (local no-pending reply, HTTP 404) means it was
settled elsewhere — drop the card. Local-lane items live only as long as the local permission
subscription that delivered them: on subscription loss they are dropped, not answerable — the
server twins, no longer suppressed, surface with their own valid handles.

### Cross-lane dedup is per request, by proven correlation

The daemon is the only party that knows both ids of a dual-lane PTY prompt, so it exposes the
mapping: the local permission wire's pending DTO gains a trailing nullable `server_request_id`,
set by the daemon's server leg once the server accepts the request (an updated pending
notification when it becomes known later). Additive trailing field per the local-IPC convention;
older daemons simply never set it. That late notification is a **metadata-only update**: it
must be applied to the existing pending item in place — same item identity, same card — never
through the replace-the-card upsert path, so a mapping arriving mid-edit or mid-submit changes
dedup state without erasing draft selections, free text, the navigation step, or cancelling an
in-flight answer. The rules:

- A server-lane `PermissionRequested` is suppressed only when a live local-lane item claims it
  (`server_request_id` equal). No claim — because the daemon is older, the local permission
  subscription is down or the capability is absent, the daemon took its server-only path for
  bounds the local wire cannot express, or the local item simply hasn't arrived yet — means the
  server-lane item stands. A mere `Connected` status socket is never evidence. The only
  duplicate window is the brief gap before a claiming local item lands, and it converges;
  fail-open, per the sub-decision.
- **ACP elicitations** (`AcpElicitationRequested`) are server-lane-only for *every* daemon —
  the daemon's ACP interaction bridge has no local broker — so they are never suppressed, local
  agents included. (This closes a live gap: today the desktop app never shows local ACP-hosted
  agents' questions at all.)

### Settlement is lane-authoritative; correlation extends it

A settlement clears its own lane's item always. Across lanes, only a *request-specific* signal
reaches the twin: `PermissionResponded(sessionId, requestId)` clears the server-lane item and
any local item claiming that `server_request_id` — the id names exactly one request, so it
cannot retire a different one. A session-scoped `PermissionResponded(sessionId, null)` clears
**server-lane items only**, never local items, claims or no claims: the bulk clear is
unversioned, and the app's two connections have no cross-ordering, so a clear emitted before
the server accepted a newer request can arrive after that request's correlation did — retiring
by claim would erase a genuinely pending card. Local items are otherwise settled solely by
their own lane; that loses nothing, because the daemon's broker is the source of local truth —
a request settled server-side reaches the daemon as its own resolution and the local lane
clears it from there. In the reverse direction, a local resolution's server twin is cleared by
the server's own broadcast when the daemon's settlement relay lands — the relay can fail and
merely log, so the twin may linger until the next server-lane event or an HTTP answer's 404
drops it. A resolution always wins a race with an in-flight seed or reconciliation fetch — a
settled request id is never resurrected by late fetch results.

### Pending discovery and recovery

The org-wide `PermissionPending(sessionId)`/`PermissionResponded(sessionId, requestId?)` pings
arrive without any group join, but a session can hold several unresolved requests and the
Pending ping names no request id, so a per-session boolean cannot count truthfully. Attention
for an *unopened* session is a per-session set of pending server request ids, populated by a
headless reconciliation of that session's event stream (`InterruptIssued`/`InterruptResolved` —
the same reconciliation the MAUI client performs on open, run without UI), triggered debounced
by each `PermissionPending` ping. A ping marks the session *dirty*, and the dirty mark is a
separate durable state from the set: it clears only when a reconciliation completes
authoritatively (including with an empty result), survives disconnects, and drives retry with
backoff after a transient fetch failure — so a session whose first reconciliation never
finished (connection dropped mid-debounce or mid-fetch, or the fetch failed while the hub
stayed up) is not skipped just because its set is still empty. Thereafter
`PermissionResponded(sessionId, requestId)` removes one id (the pip clears when the set
empties, not on the first response), null-requestId empties the set, and after every reconnect
sessions that are dirty *or* have non-empty sets re-reconcile, so a response missed while
disconnected cannot leave a pip on forever. Full card detail materializes when a session's
workspace opens: the app joins `chat:{sessionId}` (live payloads) and runs the same
reconciliation for the cards themselves.

What pings-plus-reconciliation cannot cover is a prompt raised *before* the lane connected in a
session the user never opens: cold-start pips for those need a pending-interrupts summary from
the server, which no verified endpoint provides today. That seed is a **required** item of the
§7 companion work for slice 2's "aggregated attention complete" claim; until it lands, remote
attention is truthful for everything raised while the lane is up plus anything discovered on
open, and the limitation is documented in the slice.

### Answering

Resolution goes through the item's response handle (above): local-lane →
`LocalControlOps.ResolvePermissionAsync`; server-lane → HTTP
`POST /api/sessions/{sessionId}/permission-response/{requestId}`. The HTTP seam is mandatory,
not a preference: the hub's `RespondToPermission` arity is frozen at 7 arguments and cannot
express multi-select or free-text answers; a 404 means no longer pending.

**Question/answer model.** The existing elicitation model is label-based (options carry only
label/description; answers return selected labels; the Claude parser collapses duplicate labels)
— that is correct for the Claude PTY path, whose `updated_input` encoding stays byte-compatible
and unchanged. It cannot carry the ACP contract, where `OptionId` is the identity, labels may
collide, and `MinSelections`/`MaxSelections` bound the answer. ACP elicitations therefore get an
id-preserving card model: options keep `OptionId` + label + kind, selection state is id-keyed,
`IsAnswered` enforces the min/max bounds, free-text rides `free_text`, and the remote responder
sends `selected_option_ids` (labels only as display echo). The existing Claude
`QuestionCardViewModel` is unchanged; the ACP card is a sibling on the same shared card shell.

## 5. Remote workspace

Opening a remote session opens the same workspace shell with per-origin adapters behind the
existing view models. The chat/cards pane is decoupled from terminal capability — today's
workspace constructs chat only for terminal-capable agents, which cannot hold for remote
sessions (cards must render without a terminal, §9 slice 2).

- **Chat (read path)**: seed from `GET /api/sessions/{sessionId}/detail` (events + last event
  number), then live-tail via hub `SubscribeToStream(agent session stream, fromPosition)` —
  the Eventuous SignalR subscription protocol; the app references `Eventuous.SignalR.Client`
  rather than re-deriving it. Turns are rebuilt client-side from raw events (the shape
  `ChatMessageSynchronizer` proves in kcap-server) through an adapter feeding the app's existing
  chat item view models; sender attribution resolves via hub `ResolveAttribution`. This adapter
  is the natural first consumer of the canonical-transcript converters planned for Core
  (kcap-cli #679); build it as its own unit so those converters can slot in. Queued-but-
  undelivered input comes from the `SubscribeToChat(sessionId)` snapshot plus
  `PendingInputChanged`. Optional polish: `SubscribeToAcpEphemeral` delivers live token-level
  deltas no client consumes yet.
- **Chat (write path)**: the composer sends hub `SendUserInput(agentId, text, attachmentIds?)`
  instead of writing PTY stdin.
- **Terminal**: read-only, matching the web. `SubscribeToTerminal(agentId)` replays the server's
  per-agent ring buffer (2 MB cap) then streams `TerminalOutput` (base64) live;
  `TerminalDimensions` locks the viewer to the source size. The viewer reports its viewport via
  `RequestResizeTerminal(agentId, cols, rows)` (server aggregates the min across viewers) and
  must call `ReleaseResizeTerminal` on close — never send `(0,0)`, it is a server-internal clear
  sentinel. Clear local scrollback before a re-subscribe replay so output doesn't stack. Input
  affordances: the fixed special-key set as buttons/shortcuts (`Escape`, `Tab`, `Enter`,
  `CtrlC`, `ArrowUp`, `ArrowDown`, `ShiftTab`; anything else is a server-side no-op) — full
  keystroke passthrough stays a local-PTY-only feature.
- **Stop**: hub `RequestStopAgent(agentId)`; the contract deliberately carries no routing — the
  server resolves the owning daemon and authorization from the JWT. `AgentActionService` routes
  per origin.
- **Launch**: the launcher pane gains a machine picker over the daemons cache. Name-based
  routing is only defined within one owner (`RequestLaunchAgentV2` carries `daemon_name` and no
  owner or machine id, and daemon names are unique only per owner), so the picker offers as
  launch targets **only the daemons owned by the signed-in user** (`DaemonInfo.OwnerUserId` ==
  the viewer identity of §2); other owners' daemons render in visibility surfaces as explicitly
  non-launchable. Widening launch routing to other owners' machines would need an owner-scoped
  launch contract server-side and is out of scope. Repo selection for a remote machine comes
  from that daemon's advertised `RepoPaths` (remote filesystem paths — the local repo browser
  doesn't apply); vendor/model/effort options filter by the daemon's advertised vendor
  capability lists.
- **Launch outcome correlation** ships with the picker, not later: the returned agent id from
  `RequestLaunchAgentV2` is a *request accepted*, not a success. The launcher tracks the id as
  launching-in-flight; success is the agent's row appearing (`AgentInstancesChanged` →
  registered row), failure is `LaunchFailed(agentId, reason)` — which may arrive before or
  after the invoke returns, and after navigation. A workspace opened optimistically on a
  launching id shows the launching state and converts to the failure rendering (§6) rather than
  sitting empty when the denial lands.

## 6. Failure handling and surface gating

- **Launch denial**: `LaunchFailed(agentId, reason)` with a reason prefixed
  `launch_denied_by_owner:` renders as "denied by that machine's consent policy — approve on
  that machine or pre-set a rule", not as a raw string. Other reasons surface verbatim (the
  server truncates to 400 chars).
- **Server lane loss** is data, not error: remote rows grey out with the last-known snapshot
  retained; on reconnect the caches re-seed and group joins replay. The local lane is
  unaffected, and vice versa.
- **Tray and launcher gate on both lanes.** Today the tray derives its state and entries from
  the local lane alone and the launcher disables Start on local attach state — retained as-is,
  the primary remote-only scenario would read "stopped" with no entries and no launch. The
  aggregate rule: attention (either lane) > running (either lane) > idle; "stopped" only when
  the local daemon is down *and* the server lane has nothing to show. Tray entries include
  remote sessions needing attention. Launch readiness is per selected machine: a remote target
  needs only a healthy server lane and a connected target daemon; local-daemon start/consent/
  pause controls remain tied to local state only.
- **Authorization is an explicit signal, never inferred from silence.** Quiet-but-authorized is
  a normal state: a fresh agent has no terminal bytes, an authorized chat may have no events.
  The session-scoped authorization oracle is the throwing surface — `SubscribeToChat` (and the
  seed fetch) fail loudly on denial, and `RegisterSessionAccessWatch` + `SessionAccessChanged`
  report revocation; the silently-denying `SubscribeToTerminal` is attempted only after
  session access is established, so an empty terminal renders as "no output yet", distinct from
  loading and from denied. Watches and subscriptions re-establish after every reconnect;
  a denial on re-establishment moves the workspace to the access-lost state.
- **Silent-deafness diagnostic**: on a shared cell, a JWT without a `team_id` claim joins no
  broadcast group — the connection looks healthy but hears nothing. The lane detects "connected,
  seeded via HTTP, zero broadcasts, claim absent in the token" and surfaces a diagnostic notice
  instead of an eternally-stale view.

## 7. Companion kcap-server work (Linear-only issue)

- **Required for slice 2's cold-start attention parity**: a pending-interrupts summary a remote
  client can seed from (which sessions have unresolved prompts, with request ids), per §4 —
  pings + on-open reconciliation cover everything else, so slices ship without it, with the
  cold-start gap documented until it lands.
- Non-blocking enrichment, trailing optional fields per the established additive convention:
  `title`, `has_terminal`, `worktree_path`, `branch`, `work_location`, `borrowed_from` on the
  agent registration path and `AgentInstanceDto`, so remote rows reach parity with local ones
  and worktree sub-grouping works for remote sessions.
- Non-blocking: re-point `Capacitor.Mobile.Core` and the server's wire-DTO consumers at
  `Capacitor.Remote.Models` via the `src/cli` submodule.

## 8. Testing

- **Contracts**: round-trip serialization tests pinning snake_case property names — the wire
  contract is the property name.
- **Merge/dedup**: unit tests over fake local and remote sources — twin match by
  `(MachineId, Name)` with server-URL scoping; fail-open on zero/multiple matches (same-named
  daemons under different owners on one machine stay visible twice, never hidden); the same
  agent id fed from both lanes with unproven twin identity yields two source-scoped rows each
  routing through its own lane, and a proven match binds them into one logical row preserving
  the open workspace; suppression lift on unreachable; local daemon bound to a different server
  suppresses nothing; origin precedence and no-false-ended on local removal with a live server
  row.
- **Grouping**: local+remote checkouts of one repository form one group; two machines both
  advertising the same path do not; identity-less local repos stay path-scoped; group keys are
  not treated as local directories.
- **Permissions**: lane-scoped snapshot replacement (local `Subscribed` never clears remote
  items); a mirrored PTY prompt correlates by `server_request_id`, renders one card, and
  settles both twins whichever side answers; a server PTY prompt with no local claim stays
  visible and answerable when the daemon lacks the mapping field, the local permission
  subscription is down, or the daemon took its server-only path; a local ACP question answers
  over HTTP; answers always use the item's own response handle across Local→Remote→Local
  transitions with a card open — never the other transport's id — and an own-lane
  no-longer-pending result drops the card without being mistaken for settlement elsewhere;
  a session-wide all-cleared never retires a local item — including one whose
  `server_request_id` correlation arrived *after* the (stale) clear was emitted: that item is
  neither removed nor tombstoned; a correlation-only pending update preserves card identity,
  draft selections/free text, the navigation step, and an in-flight answer, both mid-edit and
  mid-submission; resolution racing a reconciliation fetch never resurrects;
  404-as-not-pending.
- **Attention counting**: two prompts in an unopened session — the first `PermissionResponded`
  leaves the pip on, the last clears it; a response missed while disconnected clears via
  reconnect re-reconciliation; a disconnect after a `PermissionPending` ping but before the
  first reconciliation completes still populates attention after reconnect (dirty mark, empty
  set); a single transient reconciliation failure with no further ping retries to an
  authoritative result.
- **ACP question cards**: options with identical labels but distinct ids stay distinct;
  min/max selection bounds gate `IsAnswered`; free-text answers; the Claude PTY card's
  `updated_input` encoding unchanged.
- **Launch**: correlation of accepted-id → registered row vs `LaunchFailed` arriving before and
  after the invoke returns, including after navigation; picker offers only own daemons and
  refuses same-named other-owner targets.
- **Server lane**: an in-process SignalR host in `Capacitor.Tests.Helpers` playing the hub —
  broadcast fan-in, reconnect with group re-join and watch re-registration, terminal
  replay-then-live ordering, the closed-restart loop. WireMock.Net covers the HTTP endpoints:
  401 mid-lifetime with bounded recovery, unrecoverable auth → sign-in notice, re-auth
  rebuilding both transports, identity change invalidating caches.
- **App view models**: headless Avalonia tests for the machine badge, machine picker, tray
  aggregate states (local stopped + server healthy + remote prompt pending, and the inverse
  outage with local controls still usable), aggregated NEEDS YOU, and the slice-2 card host
  lifecycle: revoking session access with a question card open removes/disables the card and
  stops the session's subscriptions, and a reconnect rechecks access before restoring them —
  following the existing suite's session/thread conventions.

## 9. Delivery slices

1. **Visibility + machine-aware launch**: contracts project, `ServerConnectionService`,
   `RemoteAgentsService`, `AgentDirectory` merge, rail/tray aggregation with machine badges
   (including the §6 tray/launcher gating changes), launcher machine picker restricted to own
   daemons, **and launch outcome correlation with `LaunchFailed`/denial rendering** — launch
   feedback ships with launch, not later. Otherwise read-only; remote row actions deep-link to
   the web.
2. **Control**: `RequestStopAgent`, remote permission and question cards through
   `PermissionService`'s lane-scoped stores and response handles, the id-preserving ACP card
   model, the daemon's `server_request_id` addition to the local permission wire (the §4
   correlation field), and a minimal remote card host: opening a remote session shows the
   pending-cards pane (the shared card shell without transcript or terminal, chat construction
   decoupled from terminal capability) so a remote ACP question is answerable entirely in the
   app before slice 3. The card host is a session-scoped remote surface, so the session
   authorization lifecycle ships with it, not later: `RegisterSessionAccessWatch` +
   `SessionAccessChanged` and the reconnect access recheck (§6) — a revocation removes the
   session's cards and stops its subscriptions. Cold-start pips depend on the §7 required
   seed; until it lands the documented scope is prompts raised while the lane is up plus
   on-open reconciliation.
3. **Remote workspace**: chat read/write path, read-only terminal with resize reporting, the
   remaining §6 authorization signals for the transcript/terminal surfaces, origin-transition
   rebinding for open workspaces.
