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

## 1. Contracts project: `Capacitor.Remote.Models`

New `src/Capacitor.Remote.Models/`, referenced by `Capacitor.App`. Zero dependencies, AOT-clean,
snake_case source-gen `JsonSerializerContext`. It holds the UI-client↔server contract:

- **DTOs** mirrored from kcap-server: `AgentInstanceDto` (26 fields incl. `AgentId`, `SessionId`,
  `Status`, `Prompt`, `Vendor`, `RepoPath`, `RepoOwner/RepoName/RepoHash`, `OwnerUserId`,
  `DaemonName`, `SandboxPolicy`, `ApprovalPolicy`, `PermissionPreset`, timestamps), `DaemonInfo`
  (`Name`, `Platform`, `RepoPaths`, `MaxAgents`, `ActiveAgents`, `Connected`, `OwnerUserId`,
  `Version`, `MachineId`, vendor capability lists), `PermissionResponsePayload` (`behavior`,
  `apply_permissions`, `updated_input`, `selected_option_id(s)`, `selected_option_label(s)`,
  `free_text`), `AcpInteractionOption` (`OptionId` is identity, `Label` display-only,
  `MinSelections`/`MaxSelections` stamped on every option of a multi-select), and
  `AcpEventEnvelope`.
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
- **HTTP**: a lazily built authenticated client via the existing
  `HttpClientExtensions.CreateClientWithAuthStatusAsync` choke point, used for the seed fetches
  and the permission-response POST.
- **Auth states**: signed out → lane dormant, no connection attempts. 401/negotiate failure →
  the existing `SignInExpiredNotice` surfaces; no new auth UX. The lane starts when a valid
  profile token exists and the server URL is configured, independent of local daemon state.

## 3. Remote agents source and the merge

`RemoteAgentsService` maintains:

- an agents cache seeded from `GET /api/agent-instances` and refreshed on
  `AgentInstancesChanged` (debounced; the ping carries no payload),
- a daemons cache from hub `GetConnectedDaemons()` refreshed on `DaemonsChanged`.

`AgentDirectory` merges the local `DaemonClientService.Agents` cache and the remote cache into
one keyed stream all surfaces consume. Rows are a common projection (`AgentRow`) with an
`Origin` (`Local`/`Remote`), the shared fields both wires carry, and nullable rich fields only
the local wire has (`worktree_path`, `work_location`, `borrowed_from`, `has_terminal`, `title`,
`transcript_path`, `branch`).

**Dedup rule**: while the local lane is `Connected`, rows whose `(OwnerUserId, DaemonName)`
matches the local daemon's identity are served by the local lane only and their server twins are
suppressed (daemon names are unique per owner — that pair is the identity). When the local
socket is unreachable, the suppression lifts and the server's rows for the local daemon show
instead — daemon-down observability comes free.

**Rail grouping** generalizes from "local repo root path" to repository identity: group by
`RepoOwner/RepoName` (falling back to repo path when unset), so the same repository checked out
on two machines forms one group. Remote sessions sit under the repo group carrying a machine
badge (the daemon name, machine identity via the daemons cache); worktree sub-grouping applies
only where `worktree_path` is known — local rows today, remote rows once §7 enrichment lands.

Field degradation for remote rows: title falls back to `Prompt` (plus `SessionTitleChanged` /
session detail titles when available); terminal availability derives from `Vendor` through
`HostedHarnessCatalog` (the server DTO has no `has_terminal`); no branch/worktree until §7.
Private local agents are never registered with the server, so they correctly never appear as
remote rows anywhere.

## 4. Aggregated attention

NEEDS YOU pips, the tray attention state, and pending cards all derive from
`PermissionService`'s pending cache today, so aggregation is achieved by feeding that cache from
both lanes rather than by new UI:

- Remote pending prompts arrive as `PermissionRequested(sessionId, requestId, toolName,
  toolInput, suggestions)` and `AcpElicitationRequested(sessionId, requestId, prompt, options,
  isMultiSelect)` on the session's `chat:{sessionId}` group; the org-wide `PermissionPending`
  /`PermissionResponded(sessionId, requestId?)` pings (a null requestId means "all cleared")
  cover sessions the app hasn't joined, prompting a re-pull so rail pips stay truthful without
  every session being subscribed.
- `PermissionService` keys pending items by request id as now, adds the origin, and routes
  resolution per origin: local → `LocalControlOps.ResolvePermissionAsync`; remote → HTTP
  `POST /api/sessions/{sessionId}/permission-response/{requestId}`. The HTTP seam is mandatory,
  not a preference: the hub's `RespondToPermission` arity is frozen at 7 arguments and cannot
  express multi-select or free-text answers; a 404 means no longer pending. Question cards
  (`ElicitationQuestions`, `QuestionCardViewModel`) work unchanged on top.

## 5. Remote workspace

Opening a remote session opens the same workspace shell with per-origin adapters behind the
existing view models.

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
- **Launch**: the launcher pane gains a machine picker over the daemons cache (name, platform,
  connected state, active/max agents). `RequestLaunchAgentV2` already carries `daemon_name`.
  Repo selection for a remote machine comes from that daemon's advertised `RepoPaths` (remote
  filesystem paths — the local repo browser doesn't apply); vendor/model/effort options filter
  by the daemon's advertised vendor capability lists.

## 6. Failure handling

- **Launch denial**: `LaunchFailed(agentId, reason)` with a reason prefixed
  `launch_denied_by_owner:` renders as "denied by that machine's consent policy — approve on
  that machine or pre-set a rule", not as a raw string. Other reasons surface verbatim (the
  server truncates to 400 chars).
- **Server lane loss** is data, not error: remote rows grey out with the last-known snapshot
  retained; on reconnect the caches re-seed and group joins replay. The local lane is
  unaffected, and vice versa.
- **Silent-deafness diagnostic**: on a shared cell, a JWT without a `team_id` claim joins no
  broadcast group — the connection looks healthy but hears nothing. The lane detects "connected,
  seeded via HTTP, zero broadcasts, claim absent in the token" and surfaces a diagnostic notice
  instead of an eternally-stale view.
- **Access revocation**: `RegisterSessionAccessWatch` per opened remote session;
  `SessionAccessChanged` closes or re-checks the workspace. Terminal/chat subscribe denials are
  silent server-side (no throw) — treat "no replay arrived" as unauthorized, not as empty.

## 7. Companion kcap-server work (Linear-only issue)

Non-blocking enrichment, trailing optional fields per the established additive convention:
`title`, `has_terminal`, `worktree_path`, `branch`, `work_location`, `borrowed_from` on the
agent registration path and `AgentInstanceDto`, so remote rows reach parity with local ones and
worktree sub-grouping works for remote sessions. Separately: re-point `Capacitor.Mobile.Core`
and the server's wire-DTO consumers at `Capacitor.Remote.Models` via the `src/cli` submodule.
Slices 1–3 below do not wait on either.

## 8. Testing

- **Contracts**: round-trip serialization tests pinning snake_case property names — the wire
  contract is the property name.
- **Merge/dedup**: unit tests over fake local and remote sources — suppression while local is
  connected, lift on unreachable, repo-identity grouping, degraded-field fallbacks.
- **Permission routing**: origin dispatch (local op vs HTTP POST), multi-select via HTTP,
  404-as-not-pending.
- **Server lane**: an in-process SignalR host in `Capacitor.Tests.Helpers` playing the hub —
  broadcast fan-in, reconnect with group re-join, terminal replay-then-live ordering, the
  closed-restart loop. WireMock.Net covers the HTTP endpoints, including 401 → sign-in notice.
- **App view models**: headless Avalonia tests for the machine badge, machine picker, and
  aggregated NEEDS YOU, following the existing suite's session/thread conventions.

## 9. Delivery slices

1. **Visibility + machine-aware launch**: contracts project, `ServerConnectionService`,
   `RemoteAgentsService`, `AgentDirectory` merge, rail/tray aggregation with machine badges,
   launcher machine picker. Read-only otherwise; remote row actions deep-link to the web.
2. **Control**: `RequestStopAgent`, remote permission and question cards through
   `PermissionService`'s origin-aware responder, aggregated attention complete.
3. **Remote workspace**: chat read/write path, read-only terminal with resize reporting,
   access watches, launch-denial rendering.
