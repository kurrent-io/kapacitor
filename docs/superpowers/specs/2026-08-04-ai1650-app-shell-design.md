# App shell scaffold: Avalonia project + IPC client + attach

**Status:** Approved design (brainstormed 2026-08-03, approved section-by-section), pending user
spec review. Child of the desktop-supervisor umbrella
([2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md), §9
slice 2 — first app PR). Produces one PR. Rides the implementation PR per convention.

**Deliverable:** an Avalonia app you can run from source that attaches to a running daemon over
the local control socket and shows live daemon state — server/profile, connection health, agent
count — with a daemon-unreachable state machine that can start the daemon via the CLI. No tray,
no consent UX, no bundling.

## 1. Current state (grounding)

Facts this design builds on, verified against `main` after the AI-1649 merge (PR #442):

1. The daemon's local control socket (Unix socket, 0600, length-prefixed `[1B type][4B BE len]`
   frames) serves a versioned hello: `Hello = 15` → `HelloReply = 75`
   (`HelloReplyDto`: protocol/daemon version, daemon name, `capabilities`). The capability list
   is `["consent/1", "status/1"]`, assembled next to the routing switch
   (`LocalControlCapabilities.Current`). A pre-hello daemon cannot decode frame 15 at all and
   drops the connection — **hello-then-EOF IS the down-level signal**, never an `Error` frame.
2. `StatusSubscribe = 16` opens a long-lived connection; the daemon pushes full
   `DaemonStatus = 76` snapshots (`DaemonStatusDto`, snake_case, nulls always written) — one
   immediately, then debounced re-pushes on every change generation. Hello is **one-shot** (the
   daemon answers and closes), so an attach needs two connections: hello first, then subscribe.
3. Wire semantics are pinned by spec §4.1 of
   [2026-08-01-slice2-prework-control-ipc-design.md](2026-08-01-slice2-prework-control-ipc-design.md)
   and by exact-JSON tests: lowercase `connection` vocabulary, verbatim PascalCase agent
   `status` (open vocabulary — clients treat unknown values as opaque), `KindText` spellings,
   agents ordered `created_at` asc / `id` ordinal, `active_agents` derived from the same array.
   Unknown JSON members must never break a client (STJ unmapped-member skip).
4. `active_agents` counts `Starting|Running` — a **display count**, NOT the daemon's admission
   gate (`EffectiveCount`, which includes kill-quarantine). The app must never render
   `active_agents < max_agents` as a launch-capacity promise.
5. A client that disconnects mid-push currently triggers a daemon-side clean absorb (fixed on
   PR #442 round: `IOException`/`SocketException` are normal termination in the status handler).
6. The wire DTOs and codec live in `Capacitor.Cli.Core/LocalIpc` (`FrameCodec`, `LocalFrame`,
   `HelloIpc.cs`, `StatusIpc.cs`); `LocalSocketPaths.Socket(name)` resolves the socket path.
   The CLI's existing `LocalAgentClient` is a one-shot terminal-attach helper, not a reusable
   typed client — nothing to reuse there beyond the codec.
7. `Capacitor.Cli.Core` is compiled into the NativeAOT CLI and daemon; anything added there
   must be AOT-clean and dependency-free (BCL only).
8. Real-socket test harnesses exist in `test/Capacitor.Cli.Tests.Unit/Daemon/`
   (`LocalControlHelloTests`, `DaemonStatusIpcTests`) with the established discipline: Windows
   guard, `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]`, short
   daemon names (macOS `sockaddr_un` ~104-byte limit).

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **The typed IPC client lives in `Capacitor.Cli.Core/LocalIpc`** — one home for the whole protocol (codec + DTOs + client). BCL-only (`IAsyncEnumerable`, no Rx types on its surface) so the shared AOT core gains no dependencies. Future CLI verbs (e.g. a live status watch) can adopt it. |
| 2 | **The client is self-healing**: it owns connect → hello gate → subscribe → reconnect-with-backoff, and surfaces everything through one event stream. The app renders states; it does not implement retry. |
| 3 | **MVVM framework: ReactiveUI (+ DynamicData), via `Avalonia.ReactiveUI`** — this AMENDS umbrella §6, which named CommunityToolkit.Mvvm (§7 below). The app is stream/collection-shaped (status pushes, agents list, activity feed, consent prompts all arrive as pushes); DynamicData's keyed diffing is exactly the full-snapshot→delta machinery AI-1651's list needs, and retrofitting a reactive substrate after ViewModels exist costs more than starting with it. |
| 4 | **App tests: TUnit on MTP driving `Avalonia.Headless` through `HeadlessUnitTestSession`** via a small shared helper — one test framework and one CI invocation shape across the repo, instead of Avalonia's xunit/NUnit adapters. |
| 5 | **The app is not NativeAOT** — plain framework-dependent build this slice; packaging/trimming decisions belong to distribution (AI-1653). |
| 6 | **Dev-time CLI resolution for "start daemon"**: `KCAP_APP_CLI_PATH` env override, else `kcap` on PATH. The bundled binary is AI-1653's concern. |

### Approaches considered

- **A. Self-healing client in Core — chosen** (decisions 1–2). Reconnect logic lands in the
  most testable place (Core's real-socket suite) and is reusable.
- **B. Dumb client + smart app-side attach service.** Rejected: puts the retry state machine in
  the least-testable project and forecloses CLI reuse.
- **C. Rx/DynamicData substrate.** Adopted app-side (decision 3) — but deliberately NOT on the
  Core client's surface (decision 1): `System.Reactive` must not enter the shared AOT core.

## 3. Project structure

```
src/Capacitor.Cli.Core/LocalIpc/LocalControlClient.cs   ← new (client, §4)
src/Capacitor.App/                                       ← new Avalonia project (net10.0)
  Program.cs, App.axaml(.cs)                              — bootstrap, UseReactiveUI()
  ViewModels/MainWindowViewModel.cs                       — ReactiveObject (§5)
  Views/MainWindow.axaml(.cs)                             — ReactiveWindow<MainWindowViewModel>
  Services/IDaemonClientService.cs, DaemonClientService.cs — client loop + Rx adapter (§5)
test/Capacitor.App.Tests.Unit/                           ← new TUnit/MTP project
  AvaloniaSession.cs                                      — HeadlessUnitTestSession helper
  MainWindowViewModelTests.cs, MainWindowSmokeTests.cs, DaemonClientServiceTests.cs
```

Dependencies: `Capacitor.App` → `Capacitor.Cli.Core`, `Avalonia`, `Avalonia.ReactiveUI`,
`DynamicData` (+ `Avalonia.Themes.Fluent`, `Avalonia.Headless` in the test project). Core gains
no packages. Both new projects join the solution and the existing ubuntu/windows CI legs; the
AOT-publish checks are untouched and must stay warning-free (the client is BCL-only).

## 4. The IPC client (`LocalControlClient`)

```csharp
public sealed class LocalControlClient(string daemonName) {
    /// Backoff schedule between failed attach cycles (stays at the last entry). Settable for tests.
    public TimeSpan[] RetryDelays { get; set; } = [1s, 2s, 5s, 10s, 30s];
    public IAsyncEnumerable<LocalControlEvent> RunAsync(CancellationToken ct);
}

public abstract record LocalControlEvent {
    public sealed record StateChanged(AttachState State, string? Reason,
        IReadOnlyList<string>? Capabilities) : LocalControlEvent;
    public sealed record Status(DaemonStatusDto Snapshot) : LocalControlEvent;
}
public enum AttachState { Connecting, Connected, Unreachable }
```

Cycle: resolve `LocalSocketPaths.Socket(daemonName)` → `Connecting` → **connection 1: hello**
→ gate → **connection 2: `StatusSubscribe`** → `Connected` (carrying the reply's capabilities)
→ one `Status` event per pushed frame. Contract points:

- **Down-level vs absent vs incompatible are distinct reasons.** Socket missing / connect
  refused / IO failure → `Unreachable("daemon_unreachable")`. Hello-then-EOF (pre-hello daemon,
  §1.1) or a `HelloReply` without `"status/1"` → `Unreachable("daemon_incompatible")` — the
  daemon is alive but too old; retrying won't fix it and neither will starting it.
- **Transition-only state events**: consecutive identical (state, reason) pairs are not
  re-yielded — a long outage produces one `Unreachable`, not one per retry.
- **Backoff** advances through `RetryDelays` on consecutive failures and resets on a successful
  attach (a `Connected` yield).
- **Failure containment**: EOF/`IOException`/`SocketException` anywhere in a cycle → next cycle
  after backoff. The client never throws for daemon absence; cancellation ends the enumeration
  cleanly with no fabricated event. Unknown/extra capabilities are ignored (forward compat).
- **No Windows named-pipe support** — same platform posture as the rest of the socket surface;
  AI-1657 owns the Windows channel.

Tests (kcap-cli unit suite, reusing the AI-1649 harness; millisecond `RetryDelays`): gate pass →
`Connected` + first snapshot; capability-missing and hello-EOF gates yield
`daemon_incompatible`; no socket yields `daemon_unreachable`; daemon stop → `Unreachable` →
daemon restart → reconnect + fresh snapshot; transition-only yielding (two failed cycles, one
event); backoff reset after success; clean cancellation mid-wait and mid-stream.

## 5. App: service, ViewModel, window

**`DaemonClientService : IDaemonClientService`** (singleton; loop started at app launch with the
app-lifetime token; interface exists so ViewModel tests script the stream):

- `IObservable<AttachState> State`, `IObservable<string?> UnreachableReason`,
  `IObservable<DaemonStatusDto> Snapshots` — behavior-subject semantics (late subscribers get
  the latest value immediately).
- `SourceCache<AgentStatusDto, string> Agents` (keyed by `Id`, updated via `EditDiff` per
  snapshot) — rendered only as a count this slice; AI-1651 binds the list. This is the
  full-snapshot→delta pattern later PRs build on.
- `RestartLoop()` — cancel + restart the client loop (backs the Retry button; makes "I fixed
  it, try now" immediate instead of waiting out the 30 s backoff cap).
- `Task<StartDaemonResult> StartDaemonAsync()` — spawns `kcap daemon start -d` per decision 6;
  returns success or the captured stderr. No post-start wiring: the client loop picks the
  daemon up on its next retry.
- Daemon name comes from the same Core code path the CLI uses for the default profile's daemon
  — no app-side duplicate resolution.

**`MainWindowViewModel`** (ReactiveObject): `ObservableAsPropertyHelper` projections —
`DaemonName`, `DaemonVersion`, `ServerUrl`, `ConnectionText`, `ActiveAgents`, `MaxAgents` (from
`Snapshots`), `State`, `UnreachableReason`, plus `StartDaemonCommand` (enabled only in
`Unreachable("daemon_unreachable")`, disabled while running) and `RetryCommand`. All observed on
`RxApp.MainThreadScheduler`. Capacity renders as "n of m agents" with no free-slots claim
(§1.4). `Unreachable("daemon_incompatible")` renders "daemon is too old — update kcap" with
Retry only.

**`MainWindow`** (`ReactiveWindow<MainWindowViewModel>`, `WhenActivated`-scoped bindings): one
bare window — daemon identity block, connection state, agent count, state-dependent
Start/Retry actions, and a message line for start-daemon failures. First-run/onboarding, tray,
and all richer UI are later slices.

## 6. Error handling

- Daemon absence/incompatibility is DATA (the client's state stream), never an exception; the
  app never blocks on the daemon (umbrella §10).
- `StartDaemonAsync` failures (spawn error, non-zero exit, missing binary) render as message
  text; the state machine is unaffected.
- Subscriptions are `WhenActivated`-scoped (window close disposes them); the service loop ends
  only with app shutdown.
- The `HeadlessUnitTestSession` helper pins `RxApp.MainThreadScheduler` to an immediate
  scheduler inside tests so ViewModel assertions are synchronous and deterministic.

## 7. Amendment to the umbrella

Umbrella §6 named CommunityToolkit.Mvvm as the MVVM framework. Amended 2026-08-03 (decision 3
above) to **ReactiveUI + DynamicData**: `Avalonia.ReactiveUI` is a first-party integration,
the ecosystem matches the app's push-stream shape, and DynamicData is its native collection
layer. The umbrella document's §6 line is updated alongside this spec; the AI-1650 issue text
is corrected after spec approval.

## 8. Testing

- **Core client** (§4 list): real-socket integration in `Capacitor.Cli.Tests.Unit`, existing
  harness discipline (Windows guard, `NotInParallel`, short names).
- **App**: ViewModel tests against a scripted `IDaemonClientService` (state projections,
  command enablement per state × reason, `SourceCache` diffing add/update/remove across
  snapshots); one headless smoke test booting `MainWindow` and asserting bindings render;
  service tests for `EditDiff` behavior and `StartDaemonAsync` failure capture (fake process
  runner seam).
- **CI**: both new projects on ubuntu + windows legs; AOT-publish checks unchanged and green.
- Onboarding-style e2e (real app against a real daemon) stays manual this slice (umbrella §10
  accepted risk).

## 9. Out of scope

Tray/menu-bar (AI-1651) · consent prompt window + activity feed (AI-1652) · bundling, signing,
PATH shim, auto-update (AI-1653) · daemon lifecycle install/takeover + version-skew takeover
offer (AI-1654) · onboarding wizard (AI-1655) · settings surfaces (AI-1656) · Windows named
pipe (AI-1657). No README change: nothing is distributed yet and no CLI surface moves.
