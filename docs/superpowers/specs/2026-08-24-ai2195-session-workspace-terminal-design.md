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

- `HasTerminal` — `bool?`, serialized `has_terminal`, always emitted by a daemon
  that knows it. The daemon stamps `agent.Runtime.EmitsTerminalOutput` when
  building status payloads. `null` means "older daemon, unknown" and the app
  falls back to `HostedHarnessCatalog.TransportFamilies` (vendor heuristic).

Deliberately a bool, not a transport token: `IHostedAgentRuntime.RuntimeTransport`
defaults to `"pty"` even for ACP runtimes (only codex app-server overrides it), so
it is the wrong source of truth for this gate. Nothing else on the wire changes;
`FrameType` is untouched.

The header's transport label derives from the vendor map, corrected to the "chat"
family whenever `has_terminal` is authoritatively false.

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
  stream until `Exited(code)`, `Error`, or EOF. EOF without `Exited`/`Error` is
  "lost connection to the daemon", mirroring the CLI's interpretation.
- Surface: a `RunAsync(CancellationToken)` pump with callbacks
  (`OnAttached(snapshot, readOnlyReason)`, `OnOutput(bytes)`, `OnExited(code)`,
  `OnError(text)`) plus `SendInputAsync(bytes)`, `ResizeAsync(cols, rows)`,
  `DetachAsync()`. All writes serialize behind one lock (input and resize share
  the stream — same reason the CLI holds a write semaphore).
- After a read-write `Attached`, the client sends one `Resize` at the current
  surface size to nudge a clean repaint (CLI parity). After `AttachedReadOnly`
  it sends nothing: a read-only viewer must not influence the PTY clamp.

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
  repo/branch breadcrumb, vendor + model chip with transport-family dot, and the
  two existing actions — Open in web and Stop — reused straight from
  `AgentActionService` (one code path with tray/grid, force-stop confirmation
  included).
- Branch: nothing carries it on the wire, so a best-effort local read of
  `.git/HEAD` under the agent's `RepoPath` (worktree-aware — a `.git` *file* is
  followed like `GitRepository.ResolveMainRepoRoot` does); empty on any failure.
- Tab strip: this slice ships Terminal only. A PTY session (`has_terminal` true,
  or vendor-map fallback says pty) shows the Terminal tab; a non-PTY session
  shows the design's muted note ("No terminal — Gemini runs over ACP") — never a
  disabled tab. Chat's slot stays empty until AI-2196.
- The VM tracks its agent in `IDaemonClientService.Agents` (ObserveOn main-thread
  scheduler before touching bound state, the codified rule) so status/title stay
  live, and shows a "session ended" state when the agent leaves the cache.

### TerminalTabViewModel (attach lifecycle)

Owns one `AgentAttachClient` behind an app-side interface seam
(`IAgentAttachClient`-shaped factory) so VM tests script it. States:

`Connecting → Attached (read-write | read-only with reason) → Detached / Exited(code) / Failed(error)`

- Feeds `OnAttached` snapshot then `OnOutput` bytes into the XTerm.NET terminal
  the `SvcSystems.UI.Terminal` control renders; terminal buffer lives in the VM,
  never the view (window-rebuild premise).
- Control input/resize events → `SendInputAsync`/`ResizeAsync`; suppressed
  entirely in read-only mode, which also shows the daemon's reason banner
  (canvas: attach banner with Detach button).
- Launch race: if attach fails with "no such agent" while the agent is not yet
  (or no longer) in the status cache, retry with backoff for up to 10 seconds
  before surfacing the error — this is the `LaunchOutcome.AgentId` path booting.
- Navigating back or closing the window disposes the VM → `DetachAsync` and
  socket teardown. Detach never stops the agent; reopening reattaches and the
  scrollback replay restores history. No app-side buffer persistence.
- `Exited(code)` renders an exited banner in place; `Error` (including overflow
  force-detach) renders the daemon's message with a reattach affordance.

Naming note: the app's existing `AttachStatus`/`AttachState` describe the
*status subscription* to the daemon. New types avoid "attach" bare — they are
`TerminalSessionState` etc. — so the two concepts cannot be confused.

## 4. Testing

TDD throughout (red-green per test):

- **Core `AgentAttachClient`**: against a scripted in-proc Unix-socket server in
  a `TempDir` (socket path budget: `GetResolvedPath` where needed). Pins: attach
  handshake for all three first replies; snapshot-before-stream ordering; write
  serialization; resize nudge after read-write attach and its absence after
  read-only; detach frame on `DetachAsync`; EOF-without-`Exited` surfaced as
  connection loss.
- **Daemon**: `has_terminal` stamped true for a PTY runtime, false for ACP and
  codex app-server runtimes, on the status payloads.
- **App VMs**: scripted fake attach client — state transitions, read-only
  suppression, retry-on-boot, session-ended tracking; navigation open/close on
  `MainWindowViewModel`; card-click and launch-success entry paths.
- **Smoke**: headless resolution of the new named controls, per
  `HomeViewSmokeTests` convention.

## Risks

- **Supply chain**: `SvcSystems.UI.Terminal` + `XTerm.NET` are single-author,
  small-audience MIT packages rendering untrusted PTY bytes. Pinned versions in
  `Directory.Packages.props`; accepted by the owner.
- **Emulation fidelity**: full-screen TUIs are the hard case; the package's own
  samples cover them, but visual QA against a live claude/codex session is part
  of acceptance.
- **Dimension clamp**: an app viewer at a small window shrinks the PTY for all
  viewers (existing daemon semantics, tmux-style). Accepted; the read-only path
  never contributes.
