# Session rail and the tabless shell (AI-2199)

Slice 3 of the desktop shell (parent AI-2171; design canvas "Hosted Agents Shell",
Session artboard plus the Home artboard's structure). The navigation half: a 310px
left rail grouping **repository → worktree → session**, and the window restructure
it forces — the tabbed shell (Home | Agents | Activity) is replaced by two
top-level views, Home and Sessions. The workspace the rail selects into is
AI-2195's, already merged.

Out of scope, by prior decomposition and by the canvas annotations: the Chat tab
and composer (AI-2196), structured turn frames and real attention events
(AI-2197), the work-context sidebar (AI-2198), Home's full 250px nav column with
the Needs-you view and per-repo counts, the work-item lanes, and real search.
Deferred within the rail itself: the per-repo "+" quick-launch (needs
repo-preselection plumbing into the launcher) and branch names on worktree rows
(needs a wire field; see Decisions 3).

## Decisions

Settled with the owner during brainstorming, 2026-08-25:

1. **Two views, no tabs.** The UX artifact removes the `TabControl` entirely: the
   window is either the **Home** view (daemon status block, launcher, session
   cards) or the **Sessions** view (rail | workspace). An earlier
   rail-beside-tabs shape was considered and superseded by the artifact. The
   Agents tab is **deleted** — the rail plus the workspace header's Stop /
   Open in web supersede it. The Activity feed **moves onto Home** as a
   collapsed section: the consent surface survives, just not as a tab.
2. **Needs-you pip = `Status == "Failed"`, for now.** The wire carries no
   attention signal yet; failure is the one real needs-attention state the
   daemon already reports. The pip plumbing (per-session flag, worktree
   roll-up, collapsed-row visibility) is built now; AI-2197's attention events
   later become another input to the same flag. Rejected: a `needs_attention`
   wire field now (reaches into AI-2197's territory with nothing real to feed
   it), and no pip at all (the roll-up is the part that needs designing now).
3. **Worktree label = checkout leaf.** No daemon or wire change; the worktree
   directory's own leaf (`federated-shimmying-key`) labels the row, full path in
   the tooltip. The artifact shows branch names — that is the designated
   upgrade, an additive `branch` field a later issue populates at registration.
   Note `RepoLabel.Leaf` is the WRONG helper here: it deliberately maps
   `.claude/worktrees/<slug>` to the repo name; the rail needs the actual leaf.
4. **Home's session cards stay.** Cards remain the richer at-a-glance surface;
   the rail is the always-visible navigator. Consolidation can come with the
   later Home issue if the redundancy bothers.
5. **Projection: nested DynamicData groups** (owner's pick over a
   flat-rebuild-per-changeset). `Group` by repo root, nested `Group` by
   checkout path, `SortAndBind` at each level, `DisposeMany` everywhere. The
   disposal chain and group-recreation behavior are the known hazards and get
   explicit tests.
6. **`AgentStatusDto` gains `Title` now** (owner's pick over vendor-only rows).
   The daemon already holds the launch prompt per agent; the row's primary text
   is the prompt's first line, truncated. Null-safe: older daemons and
   prompt-less launches fall back to the vendor line.
7. **Minimal Home ⇄ Sessions switcher.** The rail's top carries the artifact's
   two icon buttons (Home, Sessions); Home gets a matching "Sessions" entry
   point. The full nav column stays with a later Home issue.

## 1. Wire change (additive only)

`AgentStatusDto` (`src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`) gains one
field:

- `Title` — a **trailing `string? Title = null`** member, so every existing
  positional construction stays valid; serialized `title`, always emitted
  (null, never omitted — the context's own doc pins that rule). The daemon
  stamps it in `SnapshotAgentsForStatus`
  (`AgentOrchestrator.LocalIpc.cs`): the agent's `Prompt`, normalized at the
  wire boundary like `Model` already is — first non-blank line, trimmed,
  truncated to 80 chars with a trailing ellipsis when cut; null when the
  prompt is null or whitespace. `null` means "older daemon, or no goal text".

The truncation is deliberate: the full prompt can be arbitrarily long and the
status payload is re-sent on every revision. The socket is the owner's 0600
local channel, so this is a size decision, not a secrecy one. Nothing else on
the wire changes; `FrameType` is untouched.

Serialization acceptance (extends the existing StatusIpc JSON tests): old JSON
without the member deserializes to `null`; a value and `null` both serialize
with `title` present in the declared trailing order. The daemon-side test
asserts the serialized payload for a prompted launch, a blank prompt, and a
multi-line prompt (first line only).

## 2. Shell restructure

### Views

`MainWindowViewModel` gains a view state alongside the existing workspace slot:

- `CurrentView` ∈ { Home, Sessions } (default Home), and the existing
  `CurrentWorkspace` (null = no session selected) now scoped to the Sessions
  view. `MainWindow.axaml`'s root becomes: Home surface visible when
  `CurrentView == Home`; Sessions surface (rail | workspace-or-placeholder)
  when `CurrentView == Sessions`.
- `OpenSession(agentId)` keeps its shutdown-latch and null-factory guards,
  gains a **same-session no-op** (`CurrentWorkspace?.AgentId == agentId`
  returns early — today's path would tear down and rebuild a live attach for
  nothing), and now also sets `CurrentView = Sessions`. Every entry point
  (rail row, Home card, launch auto-open with its captured generation) lands
  in the Sessions view with the workspace open.
- `CloseWorkspaceCommand` survives for the coordinator's close/shutdown paths
  and clears the workspace without leaving the Sessions view (the pane shows
  the muted "Select a session" placeholder). The workspace's visible **Back
  button goes away** — the rail is always beside it and the Home icon covers
  the way out. `LatchShutdown` is unchanged.
- The **daemon status block** (name/version, connection line, Start/Retry,
  StartMessage/Reason) stays at the top of the Home view, unchanged. The
  Sessions view's equivalent is the rail footer (§3).
- Default window size 840×480 → **1200×760**: the workspace keeps a
  terminal-usable width beside the 310px rail.

### Activity moves to Home

The Activity feed becomes a collapsed section on Home, below the session
cards. Its refresh-cadence contract is preserved with the same shape it has
today: `ActivityViewModel.OnTabVisibleChanged` now receives the AND of
window `IsVisible`, `CurrentView == Home`, and the section being expanded — a
background window, the Sessions view, and a collapsed section all stop the
polling, exactly as an unselected tab does today. The tab-selection wiring in
`MainWindow.axaml.cs` (`OnTabSelectionChanged`) is replaced by view-change and
expander wiring feeding the same method.

### Agents tab deletion

The Agents grid, its header, and the `Agents` projection in
`MainWindowViewModel` (the `_agentsSource` SortAndBind pipeline) are deleted,
along with `AgentRowViewModel` if nothing else consumes it (the tray has its
own models; `AgentActionService` stays — the workspace and tray use it).
Tests pinned to the grid (`AgentGridTests`, tab-related assertions in the
startup/smoke tests) are deleted or retargeted at the rail.

## 3. The rail

### Layout (matching the Session artboard)

Top-to-bottom inside the 310px column:

- Header row: spacer for the traffic lights, then two icon buttons — **Home**
  and **Sessions** — the view switcher; Sessions renders active while this
  view is up.
- **New session** row (⌘N hint): navigates to the Home view, where the
  launcher is.
- The tree (scrollable): repo header rows, worktree rows, session rows, and
  the No-repository group.
- Footer: status dot + connection word, bound to the window VM's existing
  `StatusDotBrush`/`ConnectionDisplay` (the rail view lives inside
  `MainWindow.axaml`, where that DataContext is at hand — the rail VM does not
  re-derive them), plus "N hosted" from the rail VM's own session count.

Empty rail (no sessions at all): a muted "No sessions" placeholder in the
tree area.

### Rows

- **Repository header**: caps leaf label of the resolved main root, "N
  sessions" count. Not collapsible — only the worktree level collapses, so a
  repo header can never hide an alert. Tooltip: full root path.
- **Worktree row**: chevron, branch icon, checkout-leaf label, session count,
  amber `!` pip when any child session's `Status == "Failed"` (shown expanded
  or collapsed — the pip is what makes a collapsed row safe). Click toggles
  expansion. Tooltip: full checkout path. **The main checkout is its own
  worktree-level row labeled `main checkout`, starting collapsed** (the
  artboard annotation: "main starts collapsed — that is what keeps three
  levels readable in 310px"); other worktrees start expanded.
- **Session row**: indented under a left guide-line; status dot (reuse
  `SessionCardViewModel`'s immutable-brush status mapping); primary text =
  `Title`, falling back to the vendor line when null; sub-line
  "Vendor · Model · age" — with a null Title the vendor moves up to primary
  and the sub-line drops it ("Model · age"). Model omitted when null; age is
  a point-in-time snapshot like the Home cards — refreshed on dto revisions,
  not ticked. Non-`agent` kinds append their kind to the vendor
  ("codex · review"). `!`
  pip when Failed. Selected row highlighted; the worktree row holding the
  selection carries the artifact's holds-selected tint. Tooltip: id, status,
  requester.
- **No repository group**: sorts last; header plus the inline muted note
  ("Work with no checkout yet. It keeps a title and a session; parts and
  blockers arrive once it lands in a repo."), sessions directly under it —
  no worktree level.

Sort orders: repos by leaf label (ordinal-ignore-case), No-repository last;
worktrees within a repo: main checkout first, then leaf label; sessions by
`CreatedAt` then `Id` ordinal — the existing convention everywhere.

### Projection

New `SessionRailViewModel`, ctor-scoped and disposable like `HomeViewModel`,
over the same `daemon.Agents` cache:

```
daemon.Agents.Connect()
  .ObserveOn(RxSchedulers.MainThreadScheduler)   // before anything bound mutates
  .Group(dto => repoRootFor(dto))                // "" sentinel for null RepoPath
  .Transform(g => new RailRepoViewModel(g, …))
  .DisposeMany()
  .SortAndBind(_repos, RepoComparer)
```

Inside `RailRepoViewModel`: `group.Cache.Connect().Group(dto => dto.RepoPath)`
→ `Transform` to `RailWorktreeViewModel` → `DisposeMany` → `SortAndBind`.
Inside `RailWorktreeViewModel`: sessions `Transform`ed to
`RailSessionViewModel`, `SortAndBind`, plus the two aggregates (`SessionCount`,
`NeedsYou` = any child Failed) derived from the same shared connect. Every
level disposes its nested subscription when its VM is disposed; `DisposeMany`
covers per-item removal and teardown.

The No-repository repo VM (the "" sentinel group) still nests one worktree
group internally — its sessions all carry the same null RepoPath — but the
view renders that single group headerless, sessions directly under the repo
header and note.

`repoRootFor` = `GitRepository.ResolveMainRepoRoot(dto.RepoPath)`, memoized
per path inside the rail VM — it reads `.git` files, cheap once but not
per-changeset cheap, and a path's resolution never changes within a daemon's
lifetime.

**Collapse state lives outside the group VMs**: the rail VM owns a
`Dictionary<string, bool>` of explicit user choices keyed by checkout path,
with the default rule "collapsed iff main checkout" applied when no entry
exists. A `RailWorktreeViewModel` reads it at construction and writes it on
toggle. DynamicData drops and re-forms a group whenever it empties or the
cache resets (reconnect) — state held on the VM itself would silently reset.
`OpenSession` clears the collapsed entry for the target session's worktree,
so a launch auto-open into a collapsed `main checkout` never highlights an
invisible row.

Selection: the window pushes `CurrentWorkspace?.AgentId` into
`SessionRailViewModel.SelectedAgentId`; session rows derive `IsSelected`,
worktree rows derive holds-selected. A session that ends while open stays in
the workspace (frozen header, existing behavior) but leaves the rail; nothing
is then highlighted.

Disconnects: rows persist (the service retains the `Agents` cache across
disconnects) and stay clickable — the rail is navigation, not a daemon
mutation; the footer's connection word is what says the daemon is gone.

## 4. Testing

`test/Capacitor.Cli.Core.Tests.Unit`:
- StatusIpc JSON: `title` round-trip, trailing order, null always emitted,
  old-JSON-without-member → null.

`test/Capacitor.Cli.Daemon.Tests.Unit`:
- `SnapshotAgentsForStatus` stamps Title: prompted launch (verbatim short
  prompt), multi-line prompt (first line only), long prompt (80-char cut +
  ellipsis), blank/null prompt (null on the wire). Asserts the serialized
  payload, not just the in-memory DTO.

`test/Capacitor.App.Tests.Unit` (over `FakeDaemonClientService`):
- Grouping: worktree paths resolve under their repo; main checkout and
  worktrees split correctly; null RepoPath lands in No-repository, last.
- Sort orders at all three levels.
- Aggregates: session count, pip roll-up, pip clears when the failed session
  recovers or leaves.
- Collapse: main checkout defaults collapsed, others expanded; a toggle
  survives dto revisions AND group recreation (empty the group, refill it);
  `OpenSession` expands the target's worktree.
- Row text: Title primary, vendor fallback, kind suffix, model-less sub-line.
- Selection: `SelectedAgentId` → `IsSelected`/holds-selected; ended-session
  leaves nothing highlighted.
- Disposal: disposing the rail VM tears down every nested subscription (no
  further mutations after dispose when the cache changes).
- `MainWindowViewModel`: same-session no-op; `OpenSession` sets Sessions
  view; `CloseWorkspaceCommand` keeps the view; shutdown latch unchanged.
- Activity cadence: polling gated on window-visible AND Home view AND section
  expanded — each of the three flips it off.
- Headless `AvaloniaSession` smoke: tabless window renders both views; rail
  click opens the workspace; New session lands on Home.

README: the desktop-app section is checked in the same PR — if it documents
the tabbed layout or the Agents tab, it moves to the two-view structure.

## Risks

- **Nested DynamicData pipelines** are the hardest DynamicData shape to hold
  correct — double-subscription on reactivation, leaked inner subscriptions,
  and group-recreation edge cases. Mitigated by ctor-scoping (no
  WhenActivated re-runs), `DisposeMany` at every level, and the explicit
  disposal/recreation tests above.
- **Tab removal has a wide test blast radius**: startup, smoke, and grid
  tests reference tab items by name. The deletion is deliberate and the spec
  treats retargeting those tests as part of the work, not collateral.
- **`Title` mirrors prompt text into every status payload.** Bounded to one
  truncated line, and the payload already travels only the owner's 0600
  socket; still, the field must never grow past the truncation rule without
  revisiting this.
- **Age on session rows is a point-in-time snapshot** (Home-card precedent):
  a quiet session's age stales until its next dto revision. Accepted; a
  shared ticker can upgrade both surfaces later.
