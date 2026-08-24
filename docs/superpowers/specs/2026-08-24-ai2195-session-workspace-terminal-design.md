# Session workspace with terminal (AI-2195)

Slice 2 of the desktop shell (parent AI-2171; design canvas "Hosted Agents Shell",
Session artboard). The workspace pane for one session — header, tab strip, and a
Terminal tab attached to the live PTY — opened by clicking a session card on Home.
This is the half that makes Home's deliberately inert session cards mean something.

Out of scope, by prior decomposition: the session rail (AI-2199), chat surfaces
(AI-2196), structured turn frames (AI-2197), the work-context sidebar (AI-2198),
and real search. `LaunchOutcome.AgentId` gains its first consumer here.

## Decisions

Settled with the owner during brainstorming, 2026-08-24:

1. **Terminal rendering: `SvcSystems.UI.Terminal`** (MIT, XTerm.NET emulation,
   Avalonia 12.1.1 / .NET 10 — exact match for this app). The renamed successor of
   AvaloniaTerminal (Avalonia trademark), same author, actively maintained.
   Trade-off accepted knowingly: a single-author, small-audience package renders
   untrusted PTY bytes. Rejected: an in-house VT emulator (a project of its own,
   not a slice — agents emit cursor addressing, alternate screen, SGR); a
   WebView + xterm.js embed (webview runtime + packaging cost on three OSes).
2. **Terminal-tab gate: additive `has_terminal` on `AgentStatusDto`, with vendor-map
   fallback.** The daemon's `IHostedAgentRuntime.EmitsTerminalOutput` is the
   authority and today never leaves the daemon; the app's vendor→family map guesses
   wrong exactly where it matters (a codex agent on the app-server transport maps
   "pty" but has no terminal). The issue's "no protocol change" is read as "no new
   frame family": attach rides the existing frames untouched, and `AgentStatusDto`'s
   own doc pins the additive-field compatibility rules this follows.
3. **Navigation: top-level surface swap.** MainWindow gets its first navigation
   seam — a `ContentControl` switching between the existing tabbed shell and the
   workspace surface. Rejected: a fourth `TabItem` per open session (bakes in a
   document-tabs model the canvas design does not have) and a window per session
   (departs from the single-window design, multiplies tray/close-to-hide concerns).

## 1. Wire change (additive only)

`AgentStatusDto` (`src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs`) gains one field:

- `HasTerminal` — declared as a **trailing `bool? HasTerminal = null`** member, so
  every existing positional construction stays valid; serialized `has_terminal`,
  always emitted (never conditionally omitted — `false` is a real value, not an
  absence). The daemon stamps `agent.Runtime.EmitsTerminalOutput` in
  `SnapshotAgentsForStatus`. `null` means "older daemon, unknown" and the app
  falls back to the vendor heuristic.

Serialization acceptance (extends `StatusIpcJsonTests`): old JSON without the
member deserializes to `null`; `true`, `false`, and `null` all serialize with
`has_terminal` present in the declared trailing order; `false` is never omitted.
The daemon-side test asserts the final **serialized** status payload, not just an
in-memory DTO.

Deliberately a bool, not a transport token: `IHostedAgentRuntime.RuntimeTransport`
defaults to `"pty"` even for ACP runtimes (only codex app-server overrides it), so
it is the wrong source of truth for this gate. Nothing else on the wire changes;
`FrameType` is untouched.

App-side projection: `HostedHarnessCatalog.TransportFamilies` is private, so the
catalog gains one shared lookup (family-for-vendor) rather than the workspace
duplicating the map. `has_terminal=false` cannot distinguish ACP / rpc /
app-server, so the correction rule is: keep the vendor family when it is already
non-PTY; only a *conflicting* PTY guess is overridden, to the generic "chat"
family.

## 2. Core attach client

New `src/Capacitor.Cli.Core/LocalIpc/AgentAttachClient.cs` — a long-lived,
bidirectional, per-agent client alongside the existing `LocalControlClient`
(status subscribe) and `LocalControlOps` (one-shot request/reply). BCL-only, no
Rx/JSON-reflection, per that surface's NativeAOT contract. It is a port of the
CLI's `LocalAgentClient.RunAsync` with the raw-tty plumbing removed:

- Dials `DaemonStore.SocketPath(daemonName)` — its own socket; the daemon routes
  each connection on its opening frame, and the app already runs three socket
  users, so a fourth is architecturally consistent.
- Sends `Attach` with the **full 32-hex agent id** (the daemon does not resolve
  prefixes on this path).
- First reply is exactly one of `Attached(snapshot)`,
  `AttachedReadOnly(reason, snapshot)`, or `Error(text)`; then `Stdout` frames
  stream until `Exited(code)`, `Error`, or EOF (whose meaning depends on who
  initiated it — see termination semantics below).
- Surface: `Task<AttachOutcome> RunAsync(initialCols, initialRows,
  CancellationToken)` — the initial surface size is a run argument, because the
  client itself owes the daemon the post-attach repaint nudge and has no other
  source for it. **Termination is the result, not a callback**:
  `AttachOutcome` is exactly one of `Detached`, `Exited(code)`,
  `Failed(message)` (daemon `Error`, refusal included), or `ConnectionLost`
  (uninitiated EOF) — observable identically by a Core caller and the VM.
  Caller cancellation surfaces as the standard `OperationCanceledException`.
  Streaming events are two **awaited async callbacks** only —
  `OnAttachedAsync(snapshot, readOnlyReason)` and `OnOutputAsync(bytes)` — the
  pump awaits each before reading the next frame, so delivery is serial and
  ordered (snapshot strictly before any output), and the daemon's slow-client
  overflow protection stays meaningful: a slow UI backs the socket up rather
  than ballooning an unbounded dispatcher queue. A callback exception faults the
  run (surfaces out of `RunAsync`). With termination folded into the result,
  "no events after termination" is structural, not a rule to police.
- Outbound methods: `SendInputAsync(bytes)`, `ResizeAsync(cols, rows)`,
  `DetachAsync()`. All writes serialize behind one lock (input and resize share
  the stream — same reason the CLI holds a write semaphore), and **the client —
  not its caller — owns the semantic invariants**: no input/resize before
  `Attached`; input/resize silently dropped after `AttachedReadOnly` (explicit
  calls included, not just the missing nudge); no writes after a terminal
  result; `DetachAsync` is idempotent and no input is written behind a queued
  detach. Dimensions are validated to `1..=ushort.MaxValue` per axis; invalid
  values are rejected locally, never sent.
- After a read-write `Attached`, the client sends one `Resize` at the initial
  size to nudge a clean repaint (CLI parity). After `AttachedReadOnly` it sends
  nothing: a read-only viewer must not influence the PTY clamp.
- Termination semantics, linearized in one place (the pump): `DetachAsync`
  records detach intent and sends the frame — it does not itself produce the
  outcome. The daemon sends no detach acknowledgement; it just closes the
  connection, and it can still emit `Exited` after receiving a `Detach` when the
  runtime exited concurrently. So the pump resolves the single `AttachOutcome`
  as: the first terminal frame read (`Exited`/`Error`) wins even when detach
  intent is pending; EOF with detach intent pending → `Detached`; EOF without it
  → `ConnectionLost`. Exactly one outcome, assigned at exactly one point.
- **Exceptional paths classify too** — the pump sees more than frames and clean
  EOF (`FrameCodec.ReadAsync` throws `EndOfStreamException` on truncation and
  `InvalidDataException` on malformed frames; dial/read/write can throw
  `SocketException`/`IOException`; closing the socket locally completes a
  blocked read *by exception*, not by null):
  - **local-close intent takes precedence at every phase, not just after
    attach**: once `DetachAsync`/`DisposeAsync` has recorded intent, any
    close-induced exception — during dial, while writing the opening `Attach`,
    awaiting the first reply, or streaming — settles as `Detached` (an
    `Exited`/`Error` frame the pump had already read still wins). Expected
    local teardown never faults `DisposeAsync` and never transiently publishes
    `Failed`. `DetachAsync`/`DisposeAsync` **before** `RunAsync` puts the
    client terminal immediately: a later `RunAsync` returns `Detached` without
    dialing;
  - failures before a successful attach **without local intent** (dial refused,
    handshake error) → `Failed(message)`;
  - after attach, **uninitiated** transport failure or truncation →
    `ConnectionLost`;
  - a malformed or protocol-unexpected frame → `Failed` (protocol failure);
  - caller cancellation surfaces as `OperationCanceledException` — **when
    cancellation is the recorded cause** (below);
  - a throwing callback still faults the run (exception out of `RunAsync`), and
    the client transitions terminal first, so later writes are rejected rather
    than racing a faulted pump.
- **One atomic terminal-cause slot resolves every race — and detach intent is
  not a cause.** `DetachAsync` only records *intent* and sends the frame; it
  never claims the slot, which is what lets a subsequently read `Exited`/`Error`
  frame still win after a detach (the daemon really does send `Exited` after
  reading a `Detach`). The causes that do compete for the slot, each claiming
  it exactly once by compare-exchange:
  - a terminal frame read → `Exited`/`Failed`;
  - EOF or close-induced read failure **with detach intent pending** →
    `Detached`;
  - uninitiated transport loss/truncation → `ConnectionLost`;
  - protocol failure (malformed/unexpected frame) → `Failed`;
  - an outbound input/resize write failure → `ConnectionLost`;
  - a callback fault → the faulted run;
  - observed cancellation → `OperationCanceledException`;
  - `DisposeAsync` → claims `Detached` immediately, before force-closing the
    socket (the one local action that terminalizes eagerly).

  Compare-exchange guarantees exactly one winner; actual event ordering
  determines *which* cause wins — the slot removes double-results, not
  nondeterminism. The app retires an attempt by cancelling *and* disposing;
  whichever claims first decides. `DisposeAsync` never rethrows the retired
  run's cancellation (or any expected-teardown exception).
- **Outbound write failures route to the pump, which stays the single
  linearization point.** `SendInputAsync`/`ResizeAsync`/`DetachAsync` write from
  caller tasks while the pump can be blocked in a read (the CLI being ported
  has the same shape). A transport failure in an input/resize write atomically
  terminalizes the client and closes the socket, so the blocked read completes
  and `RunAsync` settles `ConnectionLost`; the initiating outbound call
  **completes without rethrowing** — the failure is the run's to report, so
  there is never a second result or a hung pump. A `Detach` write failure with
  intent recorded resolves the same way every local close does: `Detached`.
- Disposal: the client is **`IAsyncDisposable`**. `DisposeAsync` records detach
  intent, closes the socket, and awaits the pump's completion — so "dispose"
  anywhere in this spec means an awaited async teardown, never a synchronous
  best-effort.

The CLI's `LocalAgentClient` stays untouched in this slice; folding it onto the
new client is an optional follow-up, not part of this change.

Daemon behaviors the client leans on (all existing, none modified): scrollback
snapshot (2 MiB ring) delivered inside the attached frame, atomically consistent
with the live stream; multiple concurrent attachers; read-only forced for
non-`Default` launch kinds with the human reason on the frame; slow-client
overflow force-detaches with an `Error`; tmux-style min-clamp of PTY dimensions
across all viewers — the workspace participates like any attacher, which is
accepted as-is.

## 3. App workspace

### Navigation

`MainWindowViewModel` gains `OpenSession(string agentId)` / `CloseWorkspace()`
and a `CurrentWorkspace` (`WorkspaceViewModel?`) the window binds through a
top-level `ContentControl`: null → the existing `TabControl` shell, non-null →
the workspace surface with a "← Home" affordance. Navigation state lives in the
view-model layer, never the view — `MainWindowCoordinator` discards the window
on real close on exactly that premise.

Entry points:

- **Session card click on Home** — the cards stop being inert. The card gains a
  click affordance; `HomeViewModel` exposes the chosen agent id upward (callback
  injected by the composition root, consistent with the app's plain-construction
  style).
- **Successful launch** — `LaunchOutcome.AgentId` finally gets its consumer:
  Start opens the workspace for the new id. The agent may not be attachable for
  a moment; the terminal state machine below absorbs that.

### WorkspaceViewModel (header + tabs)

Follows the settled canvas artboard:

- Header: session title (same `RepoLabel.Leaf(repo) · vendor` shape as the card),
  repo label, vendor + model chip with transport-family dot, and the two
  existing actions — Open in web and Stop — reused straight from
  `AgentActionService` (one code path with tray/grid, force-stop confirmation
  included).
- **No branch in this slice.** The canvas breadcrumb shows repo/branch, but the
  daemon runs owned launches in their own worktree on a `capacitor/agent-*`
  branch, and the status wire carries only the *requested* `RepoPath` — reading
  that checkout's `.git/HEAD` would show the user's branch, not the agent's.
  Rather than display a confidently wrong branch, the breadcrumb shows the repo
  alone; the execution worktree/branch arrives as an additive status field with
  AI-2199 (whose rail needs worktree structure anyway).
- Tab strip: this slice ships Terminal only. A PTY session (`has_terminal` true,
  or vendor-map fallback says pty) shows the Terminal tab; a non-PTY session
  shows a muted note — "This session has no terminal", suffixed with the
  transport family when it is reliably known (e.g. "— runs over ACP") — never a
  disabled tab, and never a hard-coded vendor name. Chat's slot stays empty
  until AI-2196.
- The VM tracks its agent in `IDaemonClientService.Agents` (ObserveOn main-thread
  scheduler before touching bound state, the codified rule) so status/title stay
  live, and shows a "session ended" state when the agent leaves the cache.

### TerminalTabViewModel (attach lifecycle)

Owns the attach lifecycle behind an app-side factory seam: **the factory creates
one client per attach attempt** (a client owns one socket and one run), the
previous run is fully cancelled and awaited-disposed before the next starts, and
reattach is single-flight. **Each attempt also gets a fresh terminal model and a
fresh incremental decoder, swapped in before its `Attached` snapshot is
accepted** — the daemon replays the full scrollback on every connection
(overflow recovery included), so feeding a replay into an emulator that already
consumed the pre-error live stream would duplicate history and smear cursor /
alternate-screen state; the view stays bound to the VM-owned property and just
sees the replacement. **Every UI-affine step runs awaited on the UI thread**,
not just output application: the terminal control model is an `AvaloniaObject`
(construction/use can acquire UI-thread affinity — the repo codifies this for
brushes already), and retry/reattach completions arrive from background pumps —
so model construction, event wiring, the bound-property swap, and every
terminal-state/outcome mutation dispatch to `RxSchedulers.MainThreadScheduler`.
VM tests script the factory. States:

`Connecting → Attached (read-write | read-only with reason) → Detached / Exited(code) / Failed(error)`

The terminal states map from the client's **entire completion surface**, not
just `AttachOutcome`: `ConnectionLost` maps to `Failed` with a lost-connection
message and the reattach affordance; a **non-cancellation `RunAsync` fault**
(the callbacks decode and feed a third-party engine untrusted PTY bytes — a
real failure path) is caught and rendered as `Failed` (local rendering error)
with the reattach affordance, applied on the UI scheduler; an
`OperationCanceledException` from a retired or disposed attempt generation is
swallowed silently and mutates nothing — a retired attempt never touches VM
state after its replacement begins.

- Output delivery: the VM awaits each `OnOutputAsync` by decoding through **one
  incremental `System.Text.Decoder` spanning the snapshot and every live
  frame** (PTY frames split multibyte UTF-8 at arbitrary byte boundaries, and
  the terminal control's `Feed(byte[])` does a fresh `GetString` per call —
  feeding raw frames would render replacement characters), flushed only at
  terminal completion, then applies the decoded text to the terminal **awaiting
  the UI thread** — no fire-and-forget dispatcher posts, so socket backpressure
  reaches the daemon's overflow protection. Terminal buffer lives in the VM,
  never the view (window-rebuild premise).
- Input: control input events → `SendInputAsync`; resize → `ResizeAsync`.
  **Terminal-generated protocol replies** (device-status and similar queries the
  emulator answers on its own) go through the same ordered input lane in
  read-write mode and are suppressed in read-only mode — the exact engine
  surface (XTerm.NET's data-received path; the Avalonia wrapper does not expose
  it directly) is verified at implementation and is an acceptance item. Read-only
  mode suppresses all input/resize and shows the daemon's reason banner (canvas:
  attach banner with Detach button).
- **The workspace opens in a `Resolving` state and no attach client exists
  until the session's first status DTO arrives.** `OpenSession` carries only an
  agent id; `HasTerminal`/vendor arrive with the first matching
  `AgentStatusDto` in the daemon cache. Attaching optimistically would race the
  gate — a fresh ACP/app-server session would show "no such agent" then the
  daemon's no-terminal refusal before the `has_terminal=false` note could
  render. So: `Resolving` waits (up to 10 seconds, `TimeProvider`-injected) for
  the first matching DTO, then applies the gate — `has_terminal=false` renders
  the no-terminal note with **zero socket or client attempts ever made**;
  `true`/`null`+PTY-fallback constructs the first attach client; timeout
  surfaces a not-found error. **Initial absence (`Resolving`) and
  "observed then left the cache" (session ended) are distinct states** — the
  second only exists after a first observation.
- **Resolving has its own disposal/linearization contract**, because it owns an
  asynchronous cache subscription plus a timer that race Back, replacement,
  close-to-hide, and shutdown: the workspace holds a resolve
  generation/cancellation, and **disposal wins permanently** — once navigation
  has left, no later DTO or timer callback constructs a client or mutates
  bound state. DTO-vs-timeout has one atomic winner (same compare-exchange
  shape as the client's cause slot). **Timeout drops the cache subscription**:
  a DTO arriving after timeout is ignored — the not-found state carries an
  explicit retry affordance rather than auto-recovering, so the state a user
  is looking at never changes underneath them without an action.
- Signal precedence: `Exited(code)` and daemon `Error` are more specific than
  the agent leaving the status cache; the generic "session ended" state never
  overwrites an already-terminal exited/failed state (the two signals arrive on
  independent background pumps).
- `Exited(code)` renders an exited banner in place; `Error` (including overflow
  force-detach) renders the daemon's message with a reattach affordance.

### Workspace ownership (one owner, every path)

Each `MainWindowViewModel` owns its `CurrentWorkspace`, and workspace teardown
is **asynchronous by nature** (`DetachAsync` + socket close + pump completion),
which the app's existing synchronous disposal pass cannot express. So the
composition root gains one small app-lifetime piece: a **workspace teardown
tracker** that every teardown registers with.

**The teardown algorithm is bounded and always reaches the socket close.**
The timeline, explicitly: wait at most **1 second** for the Detach write (it
can wait behind the write lock or a stalled socket); then **close the socket
immediately** — `DisposeAsync`'s contract closes before awaiting the pump, so
the local socket is force-closed by roughly the 1-second mark on every path;
the remainder of the **3-second** per-teardown budget is spent awaiting and
observing pump completion. The shutdown drain has its own **separate 5-second
total** across all pending teardowns concurrently; on expiry the drain stops
waiting and shutdown proceeds. **Expiry can leave a straggler *task*, never a straggler *socket*** — every
teardown closed its socket within its own first second, which is what the
clamp-release guarantee in Risks rests on; the deadlines govern how long we
wait for graceful detach frames and pump observation, not whether the clamp
releases. **A timed-out pump is abandoned by the await, never left
unobserved**: the wrapper attaches a continuation that consumes and logs its
eventual completion or fault (the callback/rendering fault is a real
possibility) without re-entering VM state — so a late fault cannot go
unobserved, and cannot retain the disposed VM/model past its logging. All three
durations flow through an injected `TimeProvider` so the
never-completing-write tests are deterministic, not real-time. The tracker
isolates per-teardown exceptions: one failed teardown logs and completes,
never poisoning the drain.

Exit paths:

- **Back / opening another session**: the VM starts the outgoing workspace's
  async teardown (tracked) and swaps `CurrentWorkspace`.
- **Intercepted close-to-hide**: the coordinator cancels every non-quit close
  and only hides the window — the window and its VMs stay alive, so without
  this an invisible terminal would stay attached and keep clamping the PTY for
  every other viewer. The close handler *starts* the tracked teardown and
  resets navigation to the shell; reopening from the tray lands on Home.
- **Real close**: the coordinator discards the window and builds a fresh
  window + VM on next show; the discarded VM's workspace teardown is started
  (tracked) as part of that close, so a rebuilt window never inherits or leaks
  an attach.
- **App shutdown**: the shutdown sequence sets quit-in-progress first and only
  closes the window *after* async service disposal — so a live workspace that
  never went through Back/close-to-hide would otherwise register its teardown
  after the drain, against already-disposed dependencies. Therefore the
  **first shutdown pass synchronously unhooks `CurrentWorkspace` from the
  current VM (when a window exists — the coordinator can also build one after
  shutdown began, see below) and registers its teardown before draining**. The
  drain then **seals** the tracker atomically: registration and seal cannot
  race past the final snapshot, and the later real close of the window is
  idempotent. Sealing is belt-and-braces, not the guard itself:
  - **the navigation seam closes with shutdown** — the first pass latches
    shutdown into the navigation generation, and `OpenSession` (card click and
    launch auto-open alike) rejects from then on, so no new workspace or attach
    can be created while quiesce/disposal runs, including inside a window the
    coordinator builds after shutdown began;
  - **a post-seal `Track` is not silently refused** — it immediately performs
    and observes the same bounded socket-close teardown, so even a path that
    slips past the latch cannot hold a socket open.

  The drain awaits pending teardowns (its own bounded deadline, above) before
  service disposal proceeds.

Detach never stops the agent; reopening reattaches and the scrollback replay
restores history. No app-side buffer persistence.

Entry-point guards:

- `LaunchOutcome.AgentId` is nullable. A `Started` outcome without a
  well-formed 32-hex id does not open a workspace (and cannot call
  `OpenSession(null!)`); it surfaces as a launch-succeeded-but-unopenable error.
- **Auto-open carries a navigation generation.** A launch captures the current
  generation at Start; close-to-hide, workspace disposal, and every explicit
  navigation bump it. A launch success arriving with a stale generation opens
  nothing — otherwise a delayed completion would attach an invisible terminal
  after close-to-hide (defeating the clamp mitigation) or silently replace a
  session the user opened while the launch was in flight.

Naming note: the app's existing `AttachStatus`/`AttachState` describe the
*status subscription* to the daemon. New types avoid "attach" bare — they are
`TerminalSessionState` etc. — so the two concepts cannot be confused.

## 4. Testing

TDD throughout (red-green per test):

- **Core `AgentAttachClient`**: against a scripted in-proc Unix-socket server in
  a `TempDir` (socket path budget: `GetResolvedPath` where needed). Pins: attach
  handshake for all three first replies; snapshot-before-stream ordering and
  serial awaited callback delivery; write serialization AND semantic ordering
  (no input/resize before `Attached`; explicit `SendInputAsync`/`ResizeAsync`
  dropped after `AttachedReadOnly`; no writes after a terminal result; no input
  behind a queued detach; idempotent `DetachAsync`); dimension validation
  (zero, negative, > ushort range rejected locally); resize nudge after
  read-write attach and its absence after read-only; **`AttachOutcome`
  linearization** — detach intent + EOF → `Detached`; a terminal frame read
  after detach intent wins (`Exited`/`Failed`, exactly one outcome); uninitiated
  EOF → `ConnectionLost`; the outcome observable by an ordinary awaiting caller;
  `DisposeAsync` awaits pump completion; **exceptional-path classification** —
  connect refusal → `Failed`; mid-header and mid-payload stream loss →
  `ConnectionLost`; an unexpected/malformed frame → `Failed`; **disposal at
  every pre-attach phase** — during a blocked connect, mid-opening-write, and
  awaiting the first reply — settles `Detached`, faults nothing, and never
  transiently publishes `Failed`; `DetachAsync`/`DisposeAsync` before `RunAsync`
  → a later run returns `Detached` without dialing; **outbound write failure
  with the read side held open** — the initiating call completes without
  rethrow, the pump settles `ConnectionLost` (or `Detached` for a failed
  Detach write), exactly one result, no hung pump; `DisposeAsync` closing a
  blocked read settles `Detached` and rethrows nothing; **cause-slot
  cross-races** — an input-write failure racing `DetachAsync`, and a callback
  fault racing `DisposeAsync`, each produce exactly one recorded cause; calls
  made before the run and after termination; a throwing callback faults the
  run after the client turns terminal (subsequent writes rejected).
- **Wire**: `StatusIpcJsonTests` — old JSON without `has_terminal` → `null`;
  `true`/`false`/`null` all serialize with the member present; `false` never
  omitted.
- **Daemon**: `has_terminal` stamped true for a PTY runtime, false for ACP and
  codex app-server runtimes, asserted on the **serialized** status payload.
- **App VMs**: scripted fake attach factory — state transitions, read-only
  suppression; **the `Resolving` gate**: launch success while the agent is
  absent from the cache followed by `has_terminal=false` renders the note with
  zero client/socket attempts; `true` and `null`+PTY-fallback proceed to
  attach; resolve timeout under a test `TimeProvider`; removal after first
  observation (session ended) is a different state than initial absence;
  **Resolving disposal** — Back, replacement, intercepted close, and shutdown
  while still Resolving each produce zero clients/sockets and no later
  DTO/timer callback mutates state; DTO-vs-timeout has one atomic winner;
  a DTO after timeout is ignored until the explicit retry;
  one-client-per-attempt and single-flight reattach; **cancellation vs
  local-close precedence**: cancel-before-dispose, dispose-before-cancel, and
  the simultaneous race each yield exactly one recorded cause, and a retired
  attempt mutates no VM state after its replacement begins; **a throwing
  output callback / background `RunAsync` fault** renders `Failed` with the
  reattach affordance on the UI scheduler; exited/error precedence over
  session-ended, null/malformed `LaunchOutcome.AgentId` guard; UTF-8 assembly with 2-, 3- and
  4-byte characters split across every frame boundary including the
  snapshot/live seam; a terminal query/response sequence forwarded read-write
  and suppressed read-only; **reattach freshness** — consume output, receive
  `Failed`, reattach with a snapshot containing that history, assert the final
  buffer reflects the snapshot exactly once (no old-buffer-plus-replay);
  navigation open/close on `MainWindowViewModel` including the **coordinator's
  actual intercepted-close path proving a Detach frame and socket teardown**
  (not just a direct `CloseWorkspace()` call), plus the **real-close /
  rebuilt-window path** (a fresh window inherits no attach) and **shutdown with
  a live workspace and no pre-existing tracked teardown** — the first pass
  registers it, the drain seals atomically, and its socket teardown completes
  before service disposal; a **never-completing Detach write on intercepted
  close** — the socket is force-closed within the teardown bound under a test
  `TimeProvider`; a **pump faulting after the 3-second teardown budget** — the
  fault is observed and logged exactly once with no late UI mutation; an
  **explicit `OpenSession` after the first shutdown pass/seal** and a **window
  constructed after shutdown began** — neither creates a client or socket; a
  **scheduler assertion** driving retry/reattach completion from a background
  thread that fails on any off-UI-thread model construction or bound-state
  mutation; **stale-generation launches** — a delayed launch success after
  intercepted close, and after the user opened a different session,
  opens/replaces nothing; card-click and launch-success entry paths.
- **Terminal rendering**: a headless recorded-transcript test feeding a captured
  ANSI/TUI byte stream (colors, cursor addressing, alternate screen) through the
  decode-and-feed path, with `ReflowOnResize = false` set per upstream's own TUI
  guidance — that setting is an acceptance criterion, not a suggestion.
- **Smoke**: headless resolution of the new named controls, per
  `HomeViewSmokeTests` convention.

## Risks

- **Supply chain**: `SvcSystems.UI.Terminal` (1.1.1) + `XTerm.NET` (1.0.16) are
  single-author, small-audience MIT packages rendering untrusted PTY bytes.
  Mitigation is concrete: this repo manages only *direct* references centrally
  and does not enable transitive pinning, so `XTerm.NET` (and its Unicode/width
  dependencies) get **direct pinned references** in `Directory.Packages.props` +
  the app csproj — a `PackageVersion` line alone would not pin a transitive.
  License/vulnerability audit of the full resolved graph is part of the PR.
  Accepted by the owner.
- **Emulation fidelity**: full-screen TUIs are the hard case. Gate: the headless
  recorded-transcript test (§4) plus `ReflowOnResize = false`; visual QA against
  a live claude/codex session on top, not instead.
- **Platform coverage**: CI runs Ubuntu and Windows legs only — there is no
  macOS leg — so CI smoke covers those two and macOS gets a manual QA pass
  before merge (this machine).
- **Dimension clamp**: an app viewer at a small window shrinks the PTY for all
  viewers (existing daemon semantics, tmux-style). Accepted; the read-only path
  never contributes, and on every exit path (close-to-hide included) clamp
  release is guaranteed by the **local socket close** in the teardown's bounded
  `finally` — the Detach frame is best-effort on top.
