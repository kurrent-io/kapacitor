# Work-context sidebar (AI-2198)

Slice of the desktop shell (parent AI-2171; design canvas "Hosted Agents Shell", Session
artboard, right pane). The 400px right column of the session workspace shows work context in
place of a diff: the session's work item with its declared breakdown and blockers, the linked
pull request, who is attached, and the session's facts. Per session, scrolling, always shown.

Out of scope, by prior decomposition: structured turn frames and attention events (AI-2197),
Home's work-item lanes, and any diff view. Out of scope by the server's current HTTP surface,
and shipped as SOON slots (Decision 1): the work item's state, its overview text, per-part
completion, the linked issue with its URL and state, and person-level attribution.

## Decisions

Settled with the owner during brainstorming, 2026-09-03:

1. **Build on today's HTTP surface; mark the rest SOON.** The server exposes three reads the
   pane can use: the session's work-item assignments (`GET /api/work-items/session/{id}`), a
   work item's topology (`GET /api/work-items/{id}/topology`), and the session summary
   (`GET /api/sessions/{id}/summary`). Work-item detail — lifecycle, overview, links with URL
   and state, part settlement, contributors with avatars — is served in-process to the Blazor
   dashboard only, by design ("visibility is enforced inside the single in-process
   implementation"). The pane renders what the three reads give and carries a SOON pill where
   the detail would go. A Linear issue on the server asks for the read endpoint the pane needs,
   listing exactly those fields. Rejected: adding the endpoint first (cross-repo, blocks on a
   server release for a pane that is mostly buildable now); building both in parallel (two PRs
   that must merge together, and an older server to tolerate).
2. **Core owns the contract.** DTOs, the channel seam, the HTTP client and the fetch
   composition live in `Capacitor.Cli.Core.WorkItems`, registered in `CapacitorJsonContext`.
   The app composes and renders. Rejected: an app-local client (a second copy of the contract
   the moment a CLI read command wants one); routing through the daemon (a new frame family
   in an append-only, capability-gated protocol, for a read-only pane).
3. **Two additive wire fields**: `session_id` and `branch` on `AgentStatusDto`. The work-item
   routes are keyed by session id, which the daemon already resolves and reports to the server
   but never put on the status wire; branch was designated as exactly this kind of field by the
   rail spec. Trailing nullable members, the sanctioned path since the terminal slice.
4. **Refresh is a slow poll.** Nothing on the daemon socket signals a declaration — the agent
   declares through MCP straight to the server — so the pane re-reads every 30 seconds while
   the workspace is open, plus an immediate read when the session id first resolves and a
   Refresh command. Three GETs per half minute per open workspace.
5. **The card is the session's primary work item.** The assignments route returns every item
   the session is attached to, primary first. The card shows the primary (the first row when no
   row is primary). A primary that is itself a part gets a "Part of …" line from the topology.
   Parts the session is also attached to carry the "this session" mark.
6. **Two premises in the issue text are corrected by the server's own rules.** A work item
   requires a repository (`work_items.repo_hash NOT NULL`; declare answers 400 "Session has no
   known repository"), so a repo-less session has no work item, not one without a key. The
   same-repository rule for breakdown and relations was removed server-side; visibility is the
   only boundary. The pane's no-repository copy says what is true: nothing can attach until the
   work lands in a repository.

## 1. Wire change (additive only)

`AgentStatusDto` (`src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`) gains two trailing members
after `BorrowedFrom`, always emitted (null, never omitted — the context's own rule):

- `SessionId` — `string? SessionId = null`, serialized `session_id`. The session id the daemon
  reports to the server: `AgentInstance.SessionId` for the PTY vendors, set when transcript
  discovery finds the session; the ACP runtime's `AcpSessionId` for ACP vendors, known from
  the handshake. Null is any of: an older daemon, not resolved yet, a runtime with neither.
  The app waits; it never distinguishes them.
- `Branch` — `string? Branch = null`, serialized `branch`. `WorktreeInfo.Branch` of the
  checkout the agent runs in, normalized at the wire boundary like `Model`: blank becomes null.
  A borrowed in-place launch (`WorktreeInfo.Borrowed`) records no branch, so such a session
  shows "—" here; reading the checkout's HEAD at launch is a daemon change this slice does not
  make.

The daemon stamps both in `SnapshotAgentsForStatus` (`AgentOrchestrator.LocalIpc.cs`):
`SessionId: a.SessionId ?? (a.Runtime as IAcpTranscriptSource)?.AcpSessionId`, and the branch
normalization above. Nothing else on the wire changes; `FrameType` is untouched.

Serialization acceptance (extends `StatusIpcJsonTests`): old JSON without the members
deserializes to null; both serialize, present and in the declared trailing order, for a value
and for null. The daemon snapshot test (`AgentStatusSnapshotTests`) asserts the serialized
payload carries `session_id` null before discovery and the value after, and a blank branch as
null.

`WorkspaceFixtures.Agent` gains `sessionId` and `branch` parameters — the construction site the
workspace suites share. Suites that build the DTO positionally elsewhere stay source-compatible
through the trailing defaults.

## 2. Core: DTOs, channel, client, reader

New namespace `Capacitor.Cli.Core.WorkItems` under `src/Capacitor.Cli.Core/WorkItems/`, BCL +
System.Text.Json only (Core compiles into the AOT CLI and daemon). Every root the client
deserializes is registered in `CapacitorJsonContext` as the exact closed type it reads —
`List<SessionWorkItemAssignmentDto>`, `WorkItemTopologyDto`, `SessionSummaryDto`,
`WorkItemErrorDto` — and the client reads only through the `JsonTypeInfo<T>` overloads.
Registering the element type alone does not produce metadata for the list, and an unregistered
type throws at runtime under NativeAOT, not at build.

### DTOs (`WorkContextDtos.cs`)

Public sealed records with explicit `JsonPropertyName`s, mirroring the server field for field;
unmapped members are ignored (STJ default), so a server that adds fields never breaks a client.

- `SessionWorkItemAssignmentDto(WorkItemId, Label, Source, Confidence, IsPrimary)` — one row
  of `GET /api/work-items/session/{id}`. `Label` is the server's display label: for a keyed
  item, `"KEY — title"` with an em dash and spaces; otherwise the title alone, which may
  itself be a key. `Source` ∈ user|mcp|agent|mechanical, opaque here.
- `WorkItemRefDto(WorkItemId, Title)`, `WorkItemTopologyPartDto(WorkItemId, Title, Ordinal)`,
  `WorkItemTopologyDto(Parts, PartOf, Blocks, BlockedBy, Cycle, Item)` — the body of
  `GET /api/work-items/{id}/topology`. `Cycle` ∈ none|cyclic|indeterminate. `Item` is
  nullable on the wire and consumers tolerate null. The topology carries no completion or
  progress figure, by the server's deliberate decision.
- `SessionSummaryDto` — the subset of `GET /api/sessions/{id}/summary` the pane reads:
  `SessionId, Title, Vendor, Model, RepoOwner, RepoName, RepoBranch, PrNumber, PrUrl, PrTitle,
  Repositories: List<SessionRepositoryDto>, PullRequests: List<SessionPullRequestDto>`, with
  `SessionRepositoryDto(RepoHash, Owner, RepoName, Branch, IsPrimary)` and
  `SessionPullRequestDto(RepoHash, Owner, RepoName, Number, Url, Title, HeadRef)`. The
  server's enum-valued members are not mapped, so no converter question arises.
- `WorkItemErrorDto(Error, Message)` — the 4xx body every `/api/work-items*` route shares;
  `work_items_not_in_plan` is the plan-gate value.

### Channel (`IWorkContextChannel.cs`)

The three routes as a seam, on `IFirstRunFlowChannel`'s convention, so the reader is testable
without a socket:

```csharp
public sealed record WorkContextOutcome<T>(int StatusCode, T? Body, WorkItemErrorDto? Error) where T : class;

public interface IWorkContextChannel {
    Task<WorkContextOutcome<List<SessionWorkItemAssignmentDto>>> GetSessionAssignmentsAsync(string sessionId, CancellationToken ct);
    Task<WorkContextOutcome<WorkItemTopologyDto>>                 GetTopologyAsync(string workItemId, CancellationToken ct);
    Task<WorkContextOutcome<SessionSummaryDto>>                   GetSessionSummaryAsync(string sessionId, CancellationToken ct);
}
```

`StatusCode` 0 is a transport failure. `Body` is present on a 2xx whose body parsed; `Error`
on a 4xx whose body parsed as the error shape. An unparseable 2xx body reads as a failure of
that call (status kept, body null).

### Client (`WorkContextClient.cs`)

`WorkContextClient(HttpClient http, string serverUrl) : IWorkContextChannel`. The client must
already carry the caller's bearer — there is no anonymous overload. Routes:

- `{server}/api/work-items/session/{sessionId}`
- `{server}/api/work-items/{workItemId}/topology`
- `{server}/api/sessions/{sessionId}/summary`

Both ids are opaque values from another process and go through the same containment before
they enter a route: canonicalize, validate, escape. A session id is trimmed and has its dashes
stripped — the rule the MCP shim (`McpWorkItemsServer.ResolveSessionId`) and the permission
wire already apply — so a dashed vendor id resolves to the key the server files the session
under. After canonicalization, an empty id (blank input, or dashes only) and the dot segments
`"."`/`".."` are refused before any request: `.` is unreserved, so escaping leaves a dot
segment intact and URI normalization would walk it out of the route. What survives is escaped
with `Uri.EscapeDataString`. A work item id takes the validate-and-escape half of the same path.
A refused id is a status-0 outcome, not an exception.

Degrades rather than throws, with one distinction the first-run client also draws: a transport
exception becomes status 0, but an `OperationCanceledException` whose token is the caller's
propagates — the caller asked for it, and turning it into a failed read would mask a teardown
as an outage.

### Reader (`WorkContextReader.cs`)

The fetch composition, totalized:

```csharp
public enum WorkContextReadKind { Ready, SessionUnknown, SignedOut, NotInPlan, Unreachable }

public sealed record WorkContextRead(
    WorkContextReadKind                       Kind,
    IReadOnlyList<SessionWorkItemAssignmentDto> Assignments,
    SessionWorkItemAssignmentDto?             Primary,
    WorkItemTopologyDto?                      Topology,
    SessionSummaryDto?                        Summary,
    bool                                      TopologyFailed,
    bool                                      SummaryFailed,
    string?                                   Detail);

public static Task<WorkContextRead> ReadAsync(IWorkContextChannel channel, string sessionId, CancellationToken ct);
```

Assignments and summary are requested concurrently and both are awaited before anything is
classified — no early return leaves the summary task running or faulting unobserved. Two rules
apply to every one of the three calls: a final 401 on any of them makes the whole read
`SignedOut` (the retry handler has already spent the refresh, and only `SignedOut` makes the
source drop its client), and a 2xx whose body did not parse is a failure of that call, never a
silently empty success.

Classification then follows the assignments call, first match wins: 2xx with a body →
continue; 2xx with no body → `Unreachable` with "malformed response" in `Detail`; 404 →
`SessionUnknown` (the server has not ingested the session yet); 403 whose parsed error code is
exactly `work_items_not_in_plan` → `NotInPlan` with the body's message in `Detail`; anything
else — another 403, status 0, any other status — → `Unreachable` with the status in `Detail`. On a good 2xx the primary is
the first row with `IsPrimary`, else the first row, else null — `Ready` with a null primary is
the no-work-item state, and the summary is still carried for the PR cards and facts. The
primary's topology is then requested; a 403 carrying `work_items_not_in_plan` makes the whole
read `NotInPlan` — both work-item routes share the gate, and a plan change between the two
calls must not leave the pane `Ready` on retained data — and anything else but a 2xx with a
body sets `TopologyFailed` and leaves `Topology` null. A summary that is anything but a 2xx
with a body sets `SummaryFailed` and leaves `Summary` null. A section failing inside an otherwise ready read degrades that
section; it never fails the pane.

Deriving the key and title is the reader's too, as pure functions beside it
(`WorkContextLabel.Split(label)` → `(Key?, Display)`): the key is the part before the first
`" — "` when there is one, the display the remainder; a label without the separator is display
only. The pane's title is the topology item's title when the topology carries an `Item`, else
the display half — `Item` is nullable on the wire and is never dereferenced. The server owns
the label format; a change there degrades to showing the whole label as the title, never to a
wrong key.

## 3. App: composition and the view model

### Source (`Services/IWorkContextSource.cs`, `Services/ServerWorkContextSource.cs`)

```csharp
public interface IWorkContextSource {
    Task<WorkContextRead> ReadAsync(string sessionId, CancellationToken ct);
}
```

`ServerWorkContextSource(ConfigRoot config, ProfileContext? profiles)` is built once in
`App.axaml.cs` beside the launch client, from the same two inputs, and handed to the workspace
factory. It builds its `HttpClient` lazily through
`HttpClientExtensions.CreateClientWithAuthStatusAsync(config, profiles, serverUrl, ct,
autoRetryUnauthorized: true)` and caches it; an `AuthStatus` other than `Ok` or
`NoAuthRequired` returns `SignedOut` without a request, and so does a null profile. A read
that comes back `SignedOut` drops the cached client, so the next read rebuilds against
whatever the user has since done. Builds are serialized behind one gate, the launch client's
own shape. The client build is a constructor-injected delegate defaulting to the Core factory,
so the source's own tests never touch the network.

Every `HttpClient` the source receives is owned and disposed, and never under a reader. Reads
can overlap — a session-id switch starts the new session's read while the old one is still in
flight — so the cached client is held by lease: each `ReadAsync` borrows the current client for
its duration, a `SignedOut` result retires that client for future borrows rather than disposing
it, and the retired client is disposed by whichever borrower releases it last. The factory
returns a client even for a rejected auth status; that one has no borrowers and is disposed on
the spot. The source is `IAsyncDisposable`: disposal stops new borrows (a later `ReadAsync`
returns `Unreachable`), cancels active reads through the source's own token, awaits them, then
disposes the live client — the launch client's gate-before-dispose rule, in ref-count form.
`App` holds the source beside the launch client and disposes it on both cleanup paths, the
normal shutdown and the startup-failure catch, after the workspace teardown drain each already
performs. Nothing is left to the finalizer — a signed-out poll every 30 seconds would otherwise
strand a handler per tick.

### `WorkContextViewModel` (`ViewModels/WorkContextViewModel.cs`)

Constructed by `WorkspaceViewModel` once per workspace, ctor-scoped, `TeardownAsync` the one
exit. Inputs: the workspace's presence stream projected to `IObservable<AgentStatusDto?>` (one
subscription to the daemon cache per workspace, not two), `IWorkContextSource`,
`TimeProvider`, `IUrlOpener`, `Action? requestSignIn` — the same action Home's Sign in button
invokes — and `IObservable<Unit>? signInCompleted`, the signal `App` raises where it today
calls `NotifySignInCompleted` on Home. Both are threaded from `App.axaml.cs` through the
workspace factory; `App` owns one `Subject<Unit>` for the signal so a sign-in completing
reaches every consumer without rebuilding the workspace or the source.

**Session facts** derive from the dto alone, live from the first dto:

| Fact | Source |
|---|---|
| Repository | `RepoLabel.Leaf(dto.RepoPath)`; full path as tooltip; "—" when null |
| Worktree | `CheckoutLabel.Format(CheckoutLabel.CheckoutPathFor(dto), dto.RepoPath)`, with " · borrowed" for a borrowed reviewer — the same helper the header uses |
| Branch | `dto.Branch`; "—" when null |
| Harness | the vendor's registry label (`HostedHarnessCatalog.LabelFor` over the catalog's default list; the raw token for an unknown vendor) plus `HostedHarnessCatalog.ModelLabelFor` (never blank: "default") |
| Transport | `HostedHarnessCatalog.EffectiveFamily(dto.HasTerminal, dto.Vendor)` → "PTY", "ACP", or "chat" — the picker's own family wording, so the two never disagree |
| Id | `dto.SessionId`, or "resolving…" until the daemon reports it |

The collapsed Session line is `"{harness} · {transport}"`.

**Phases** (`WorkContextPhase`): `WaitingForSession` (no `SessionId` on the dto yet),
`Loading` (first read in flight; later refreshes are silent), `Ready`, `NoWorkItem`,
`SignedOut`, `NotInPlan`, `Unreachable`, `SessionUnknown` (rendered with the waiting copy — the
server has not seen the session yet; the poll keeps trying). `NoWorkItem` with a null
`RepoPath` renders the no-repository copy (Decision 6).

**Fetching.** The session id is the read's identity, the way the transcript path is the chat
tab's, and the in-flight state lives on the lease, not on the VM: a `ReadLease(SessionId,
Generation, CancellationTokenSource)` is taken as one reference and owns its pending read task,
every read carries its lease, and the apply on the UI thread is dropped unless the lease is
still the current one. The generation bumps on two events — a session-id change and
`TeardownAsync`. The first non-null `SessionId` on the dto takes a lease and reads at once. A
different non-null id (a daemon restart re-resolving) clears every projection, sets `Loading`,
cancels the old lease's token, takes a new lease and reads at once, whether or not the old read
has settled — the old read's completion is dropped by the lease guard, so it can never paint
the old session's facts under the new id, and its settling touches only its own lease, so it
can neither re-enable Refresh nor let a tick start a duplicate of the new read. A dto whose id
goes back to null changes nothing here: the last id stays and the Id fact keeps showing it. This
is a rule of this VM, not of the presence stream — `WorkspaceViewModel.Accumulate` freezes a
dto only on removal and replaces it wholesale on an update, nulls included.

**Every lease transition happens on the UI thread.** The timer tick, the sign-in signal and a
presence update each dispatch there before deciding anything; only the HTTP work runs on the
pool, and the result hops back via `Dispatcher.UIThread.InvokeAsync` under the lease check.
The lease bookkeeping therefore has one owner, and so does the bound `IsReading` flag that
flips as the current lease's read starts and settles — `RefreshCommand`'s can-execute observes
it, so the command is visibly disabled for a timer-started read exactly as for a click, and
its notifications never come from a pool thread.

A superseded lease is retired, not forgotten: the VM keeps every lease until its task settles,
and the task catches its own cancellation, so nothing faults unobserved. `TeardownAsync` bumps
the generation, cancels every outstanding lease and awaits all of them — the current one and
any retired read still unwinding — so the workspace teardown drain the app performs before
disposing shared services holds for this VM even on an ordinary workspace replacement, where
the app-scoped source stays alive and cannot serve as the drain.

A `TimeProvider` periodic timer at 30 seconds re-reads until teardown. The in-flight skip
applies to the timer and to `RefreshCommand`, never to an id change: a tick or a click while
the current lease's read is running is dropped. The sign-in signal is coalesced rather than
dropped: it reads at once when the current lease is idle, and otherwise marks that lease
refresh-pending, so the read that starts the moment the active one settles carries the new
credentials — a completed sign-in replaces "Sign in" within one round trip rather than one poll,
even when a pre-sign-in tick was in flight and came back `SignedOut`. A pending refresh is
consumed only if its lease is still the current one when the read settles; a retired lease
discards it, because the lease that replaced it already read with the new credentials and a
second request for the old session would be pure waste. A signal after teardown is inert.

**Merging a result into the pane** is per section, for the same lease:

- An `Unreachable` read after a `Ready` one keeps every projection and sets `IsStale`.
  `SignedOut`, `NotInPlan` and `SessionUnknown` switch the phase and clear every
  server-derived projection — key, title, part-of, parts, blockers, cycle note and the link
  cards — because the pane would otherwise show data the server has just said the viewer may
  not have. The requester row and the session facts derive from the daemon's dto and stay.
- A `Ready` read replaces the card from its assignments. A null primary is an authoritative
  answer: the card clears to `NoWorkItem`. A changed primary id clears parts, part-of and
  blockers before the topology is applied — retained parts would belong to another item.
- `TopologyFailed` with an unchanged primary retains the last parts, part-of, blockers and
  cycle note and sets `IsStale`; a successful topology replaces them, an empty one included.
- `SummaryFailed` retains the last link cards and sets `IsStale`; a successful summary
  replaces them, an empty one included.
- `IsStale` clears on the next read in which every section it covers succeeded.

A section blip therefore dims the pane rather than blanking a card, while an authoritative
empty answer still empties it.

**Ready-state projections**, all plain bound properties mutated on the UI thread:

- `Key` (may be null), `Title`, `PartOfTitle` (null unless the topology has `PartOf`).
- `Parts`: `ReadOnlyObservableCollection<WorkContextPartViewModel>` — `Title`, `Mark` ∈
  `ThisSession` (the part's id is among the session's assignments) | `Unknown`. `PartsHeader`
  is `"{n} parts"`, `"1 part"`. `PartsExpanded` defaults true.
- `BlockedBy`: the blocker titles; `CycleNote` when `Cycle` is not "none": "Dependencies form
  a cycle" for cyclic, "Dependencies could not be fully resolved" for indeterminate.
- `Links`: one `WorkContextLinkViewModel(Eyebrow, Key, Title, Url)` per pull request, eyebrow
  "PULL REQUEST", key `"#{Number}"`. The set is `Summary.PullRequests` plus the top-level
  `PrNumber`/`PrUrl`/`PrTitle` triple when the list has no entry for the same pull request —
  the summary carries both and does not promise the list contains the primary, so the triple
  is the fallback. PR numbers are repository-local, so "same" is owner, repository and number
  together, the triple's repository being the summary's `RepoOwner`/`RepoName`; when the
  summary carries no repository identity the match falls back to number alone, the
  conservative reading that never shows one PR twice. `OpenCommand` is enabled only when `LinkPolicy.IsOpenable`
  accepts the URL — the same absolute-HTTP(S)-only boundary the chat tab applies before a URL
  reaches the shell opener — and an opener exception is caught and logged the way
  `ChatTabViewModel.OpenLinkCommand` does, never surfaced as a crash. Plus one issue
  placeholder row rendered by the view as a SOON card (no data behind it).
- `Requester`: the first non-blank of `dto.RequesterDisplay`, `dto.Requester`, `"You"`, each
  trimmed; the role line `"This session · {vendor label}"`; `RequesterInitial` the first
  letter of that, upper-cased, so a blank value can never index past its end.
  `PeopleExpanded` and `SessionExpanded` default false.
- `IsStale`, `PhaseNote` (the copy for every non-ready phase), `SignInCommand`
  (`requestSignIn`, inert when null).

Copy, in one place on the VM as constants:

- Waiting / session unknown: "Waiting for the session to register…"
- Loading: "Loading work context…"
- No work item: "No work item attached yet. The agent's declare tool attaches one."
- No repository: "This session has no repository. A work item cannot attach until the work
  lands in one — breakdown and blockers come with it."
- Signed out: "Sign in to see the work context." (with the Sign in button)
- Not in plan: "Work items are not in this workspace's plan."
- Unreachable: "Couldn't reach the server." (with Retry, which is `RefreshCommand`)

### Workspace

`WorkspaceViewModel` gains `IWorkContextSource workContext`, `Action? requestSignIn` and
`IObservable<Unit>? signInCompleted` constructor parameters and a `WorkContext` property, built
in the constructor from the shared presence stream; `TeardownAsync` tears it down after Chat and
before Terminal. `App.axaml.cs` builds the source once, owns the sign-in subject, and passes all
three into `BuildWorkspace`; the test builders and `WorkspaceViewSmokeTests.Build` pass a
`FakeWorkContextSource` and leave the signal null.

Cleanup follows the launch client, which is the app's one other server client: it is torn down
by an idempotent, guarded async helper that both cleanup paths already call after the
workspace teardown drain — inside the normal path's `DisposeLifecycleAndServiceAsync`, and
right after `HandleStartupFailureAsync` on the startup-failure path. That helper widens to
dispose the server clients as a set — launch client, then the work-context source, then the
sign-in subject, which is completed before it is disposed — with each step guarded so a
throwing disposal never skips the next. The synchronous UI disposables list stays what it is:
the source is `IAsyncDisposable` because it awaits its active reads, and blocking that on the
UI thread is not an option. One helper with two callers is the rule that keeps the source on
both paths; a third cleanup site is not added.

The helper has two halves with two responsibilities. Ownership is a small app-owned
`ServerClients` holder: it takes the three fields in one atomic exchange and memoizes the
cleanup task, so a second call — sequential or concurrent with the first, since the
startup-failure and shutdown paths can overlap — awaits the same task and touches nothing
twice; today the launch client's idempotence comes only from `App` nulling its field, and
`ServerLaunchClient.DisposeAsync` itself disposes its gate and is not re-entrant. The
sequence is a static over the one captured set — launch client, source, subject completed then
disposed, each step guarded — testable without an `App`, and never idempotent on its own.

## 4. App: the view

### Layout

`WorkspaceView.axaml`'s root becomes `<Grid ColumnDefinitions="*,400">`. The existing
three-row grid (header, tab strip, content) moves into column 0 unchanged; column 1 hosts
`<views:WorkContextView x:Name="WorkContextHost" DataContext="{Binding WorkContext}" />`. The
terminal control keeps its own column, so the pane size it reports to the PTY is the real
center-pane width — the same rule the tab switch already keeps.

That honesty needs a floor: the window is resizable with no minimum, and 310 of rail plus 400
of pane would let the center column shrink to nothing. `MainWindow` gets `MinWidth="1200"`,
its current default width, so the center never drops below the 490 the default layout already
gives it. A collapsible pane is the richer answer and is not this slice.

`WorkContextView` is a `UserControl` on `KcapSurfaceBrush` with a one-pixel `KcapBorderBrush`
left edge, its content a `ScrollViewer` (vertical auto) over a `StackPanel` with 16px
horizontal and 18px top padding. An expanded pane scrolls; nothing clips.

### Sections, top to bottom

- **Header row**: "ABOUT THIS WORK" in `KcapFaintBrush`, 10px, bold, `LetterSpacing="1.2"`
  (the rail's eyebrow idiom); on the right a chrome-neutral `RefreshButton` (a stroked
  circular-arrow `Path`) and a 6px `KcapWarningBrush` dot beside it, visible on `IsStale`,
  tooltip "Last refresh failed; showing the previous result".
- **Work item card** (`KcapSurfaceRaisedBrush`, 1px border, radius 10, padding 13):
  - eyebrow "WORK ITEM" left; the SOON pill right, where the state pill will go;
  - `WorkContextKey` in `KcapAccentBrush`, 12px bold, hidden when null; `WorkContextTitle` in
    `KcapTextBrush`, 12.5px, wrapping; `PartOfLine` "Part of {title}" in `KcapMutedBrush`,
    hidden when null;
  - parts block: a toggle row (chevron + `PartsHeader` + a SOON pill in place of the progress
    figure), then `PartsList` — one row per part: a 12px circle, filled `KcapAccentBrush` with
    a dark tick for `ThisSession`, a `KcapBorderBrush` ring for `Unknown`; the title in text
    or muted accordingly;
  - blocked-by block, hidden when empty: `KcapWarningDimBrush` fill, radius 7, eyebrow
    "BLOCKED BY" and one `KcapWarningBrush` line per title;
  - `CycleNote` as its own muted line under the card body, gated on the note alone — a cyclic
    or indeterminate topology with no visible inbound blocker still shows its one warning;
  - while not ready, the card body is the `PhaseNote` in `KcapMutedBrush` with, per phase,
    the `SignInButton` (accent fill, "Sign in") or the `RetryButton` ("Retry").
- **Link cards**: one card per `Links` entry, the card itself a `Button` bound to
  `OpenCommand`; eyebrow, key in accent, title in text. Then one `IssueSoonCard`: eyebrow
  "ISSUE" and the SOON pill, nothing else.
- **Who's on it**: `WhoToggle` row — eyebrow "WHO'S ON IT", the SOON pill, a chevron. Collapsed:
  one 24px `KcapAccentDimBrush` circle with `RequesterInitial` in accent. Expanded: the same
  circle beside `Requester` (11.5px semibold) over the role line (10.5px muted).
- **Divider**, then **Session**: `SessionToggle` row — eyebrow "SESSION", a chevron. Collapsed:
  `SessionSummaryLine` in muted. Expanded: `SessionFacts`, six rows of a 96px eyebrow label
  and a `SelectableTextBlock` value (11px, text brush, wrapping); the repository and worktree
  values carry the full path as tooltip.

The SOON pill is a `Border` with `CornerRadius="999"`, `KcapPurpleDimBrush` fill, `Padding="6,2"`,
holding "SOON" in `KcapPurpleBrush`, 8.5px bold, `LetterSpacing="0.6"`. `KcapPurpleDimBrush`
(`#29233E`) joins the palette in `App.axaml`.

Chevrons are the stroked-`Path` idiom from the rail and the chat's tool groups (`M3,4.5 L6,7.5
L9,4.5` open, `M4.5,3 L7.5,6 L4.5,9` closed). Collapse state lives on the VM as three booleans
with `ToggleParts/People/SessionCommand`. No `Expander`.

## 5. Testing

`test/Capacitor.Cli.Core.Tests.Unit/WorkItems/`:
- `WorkContextDtoTests`: each DTO deserializes from a literal server-shaped body; an extra
  member is ignored; the error body parses; `CapacitorJsonContext.Default.GetTypeInfo` returns
  metadata for each of the four roots, the list included — round trips alone do not prove the
  client avoided reflection, and the PR also runs the repo's AOT publish check.
- `WorkContextClientTests` (over a stub `HttpMessageHandler`): the three routes; a dashed
  session id is sent dashless; a session id with a slash or percent is escaped; a work item id
  is escaped; `"."`, `".."`, blank, whitespace-only and dashes-only ids are refused before any
  request, for both id kinds; a 4xx body becomes `Error`; a transport exception becomes status
  0; the caller's own cancellation propagates; an unparseable 2xx body is a null body with the
  status kept.
- `WorkContextReaderTests` (over a scripted channel): ready with primary and topology; primary
  by flag, else first row; no assignments is `Ready` with null primary and a summary;
  assignments 2xx with no body → `Unreachable`; 401 on assignments, on topology and on
  summary each → `SignedOut`; 403 with the plan code → `NotInPlan` carrying the message; 403
  with another code or no body → `Unreachable`; 404 → `SessionUnknown`; 0 → `Unreachable`;
  topology 403 with the plan code → `NotInPlan`; topology non-2xx and topology 2xx-no-body each
  set `TopologyFailed`; summary non-2xx and
  summary 2xx-no-body each set `SummaryFailed`; a null topology `Item` falls back to the label
  display; the summary task is awaited on every early return. `WorkContextLabelTests`: the
  split on `" — "`, a label without it, a title that is itself a key.

`test/Capacitor.Cli.Core.Tests.Unit/LocalIpc/StatusIpcJsonTests`: `session_id`/`branch` round
trip, trailing order, nulls emitted, old JSON → null.

`test/Capacitor.Cli.Daemon.Tests.Unit/Services/AgentStatusSnapshotTests`, on the serialized
payload: `session_id` null before discovery and the value after (the PTY branch); an ACP
runtime with no discovered id emits its `AcpSessionId`; with both present the discovered id
wins; a blank branch → null and a non-blank `WorktreeInfo.Branch` passes through.

`test/Capacitor.App.Tests.Unit` (over `FakeWorkContextSource`, `FakeDaemonClientService`,
`FakeTimeProvider`, `RecordingOpener`):
- `WorkContextViewModelTests`: `WaitingForSession` until a dto with a session id; the first
  read on arrival with the canonical id; a 30-second tick reads; a tick and a Refresh during an
  in-flight read are skipped; the id-switch race — session B's id arrives while A's read is
  gated, A's completion mutates nothing, B is read at once rather than on the next tick; A
  settling while B is still gated leaves Refresh disabled and a tick starts no second B read; a
  rapid A→B→C switch applies only C; an id going back to null changes nothing and the Id fact
  keeps the last id; the sign-in signal reads at once when idle, and a signal during a
  pre-sign-in read that returns `SignedOut` starts a second read without advancing time; a
  signal during A's read followed by a switch to B before A settles starts no second A read
  when A settles, and only B can apply; a
  signal after teardown starts nothing; `RefreshCommand.CanExecute` is false during a
  timer-started read and its notifications arrive on the main-thread scheduler; teardown after
  an A→B switch with both reads gated cancels both, observes both, and returns only once both
  have settled; each `WorkContextReadKind`
  maps to its phase; from `Ready` with a PR link and parts, each of `SignedOut`, `NotInPlan`
  and `SessionUnknown` clears the key, title, parts, blockers, cycle note and link cards while
  the requester row and session facts remain; an
  `Unreachable` refresh after `Ready` keeps every projection and sets `IsStale`; a refresh with
  `TopologyFailed` and the same primary keeps parts and blockers and sets `IsStale`, and one
  with a changed primary clears them; a refresh with `SummaryFailed` keeps the link cards; a
  successful empty topology and an empty summary each clear their section; `IsStale` clears
  once every section succeeds; a `Ready` read with a null primary clears the card to
  `NoWorkItem`; `NoWorkItem` with a null repo shows the no-repository copy; key/title split,
  the topology title winning, and the label fallback when `Item` is null; part marks; part-of;
  blocked-by and both cycle notes, the cycle note rendering with no blockers; PR cards from the
  list, the top-level triple as fallback, the triple suppressed when the list already carries the
  same owner/repo/number, the triple kept when another repository's PR shares the number, and
  the number-only match when the summary has no repository identity; `OpenCommand` disabled
  for a relative and a `file:` URL, opens
  an https URL through the opener, and a throwing opener is caught; the requester row, its
  "You" fallback, and blank display and id values skipped; every session fact including
  transport wording, the borrowed-launch "—" branch, and the resolving id; collapse defaults
  and toggles; teardown stops ticks and a completion landing after teardown mutates nothing.
- `ServerWorkContextSourceTests` (over an injected client factory with an observable disposable
  handler; no network): a null profile reads `SignedOut` with no request; a rejected auth
  status disposes the client it was handed; a `SignedOut` read retires the cached client and
  the next read builds a new one; read A returning `SignedOut` while read B is still borrowing
  the same client leaves B unaffected and disposes that client exactly once, after B releases
  it; `DisposeAsync` during an active read cancels it, awaits it, disposes the live client once
  and leaves no unobserved task, and a read after disposal returns `Unreachable`.
- `WorkspaceViewModelTests`: `WorkContext` is built from the same presence stream and torn
  down by `TeardownAsync`.
- `AppStartupTests`, over async spies: the cleanup static disposes the launch client, the
  source and the subject in that order, completes the subject before disposing it, and a
  throwing launch-client disposal still disposes the source; the `ServerClients` holder
  disposes each exactly once when called twice sequentially, and when called twice
  concurrently both callers await the one cleanup. The two production call sites are pinned by
  construction: the holder is the only place the launch client is disposed, and the source and
  subject join it rather than gaining sites of their own.
- `WorkspaceViewSmokeTests`: the name list grows by `WorkContextHost`, `RefreshButton`,
  `WorkContextKey`, `WorkContextTitle`, `PartsList`, `WhoToggle`, `SessionToggle`,
  `SessionFacts`, `SignInButton`, `RetryButton`, `IssueSoonCard`; one layout test pins the
  host at 400 wide and the terminal host at the remainder. `MainWindowSmokeTests` pins the
  window's `MinWidth`.

README: no CLI surface changes and the desktop app has no README section, so it is unchanged.
`docs/CHANGES.md` gains a "Desktop shell: work-context sidebar" entry recording Decisions 1,
5 and 6 and the label-split contract.

Follow-ups filed with this slice: a Linear issue on the server for the work-item read
endpoint the SOON slots need (lifecycle, overview, key, links with URL and state, parts with a
settled flag, contributors with avatars — behind the existing visibility service and plan
gate); a GitHub issue here for the MCP tool descriptions that still claim the same-repository
rule the server dropped.

## Risks

- **The label split is a convention, not a contract.** The server composes `"KEY — title"`
  in one place; a change there shows the whole label as the title and drops the key chip. Safe
  failure, but silent — the CHANGES entry names the dependency so a server change knows to
  look here.
- **Session id identity across vendors.** PTY sessions resolve through discovery (up to three
  minutes); ACP sessions carry their id from the handshake. The routes strip dashes; a server
  that files an ACP session under something other than its canonical ACP id would leave the
  pane in `SessionUnknown`, which polls quietly and never errors.
- **Polling against an unreachable server** costs two failed GETs every 30 seconds per open
  workspace (assignments and summary; the topology is only requested after a good assignments
  read with a primary), and a rebuilt-and-disposed client on every `SignedOut`. Bounded by the
  number of open workspaces (one), and the pane says what it is doing.
- **Half the designed pane is SOON.** State, progress, the issue card and attribution are
  pills until the server endpoint lands. The pills are one component, so removing them is one
  edit per slot when the data arrives.
- **Layout width.** 400 fixed plus the 310 rail leaves 490 of chat at the 1200 default window
  width, and the new `MinWidth` pins that as the floor. The canvas drew the pane at 1440; a
  collapsible pane is the upgrade if 490 proves cramped in use.
- **A borrowed in-place launch shows no branch.** The daemon records none for it, so the fact
  row reads "—" until the daemon reads the checkout's HEAD at launch.
