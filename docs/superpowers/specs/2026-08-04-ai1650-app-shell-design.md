# App shell scaffold: Avalonia project + IPC client + attach

**Status:** Approved design (brainstormed 2026-08-03, approved section-by-section; revised
2026-08-04 after spec-review round 1), pending reviewer sign-off. Child of the
desktop-supervisor umbrella
([2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md), §9
slice 2 — first app PR). Produces one PR. Rides the implementation PR per convention.

**Deliverable:** an Avalonia app you can run from source that attaches to a running daemon over
the local control socket and shows live daemon state — daemon identity (name, version), server
URL, connection health, agent count — with a daemon-unreachable state machine that can start
the daemon via the CLI. (The umbrella's "server/profile" display reduces to server URL + daemon
identity this slice: the status wire carries no profile identity and the daemon name is not a
profile name — surfacing the active *profile* needs either local derivation or a wire
extension, deferred with the settings work.) No tray, no consent UX, no bundling.

## 1. Current state (grounding)

Facts this design builds on, verified against `main` after the AI-1649 merge (PR #442):

1. The daemon's local control socket (Unix socket, 0600, length-prefixed `[1B type][4B BE len]`
   frames) serves a versioned hello: `Hello = 15` → `HelloReply = 75`
   (`HelloReplyDto`: protocol/daemon version, daemon name, `capabilities`). The capability list
   is `["consent/1", "status/1"]`, assembled next to the routing switch
   (`LocalControlCapabilities.Current`). A pre-hello daemon cannot decode frame 15 at all and
   drops the connection — hello-then-EOF is the down-level *signal*, never an `Error` frame —
   but it is NOT uniquely diagnostic: a current daemon shutting down between accept and reply
   produces the same observation (§4 treats it as a retried heuristic, not a verdict).
2. `HelloReplyDto.Capabilities` is nullable by contract: null means "field absent (older
   daemon)" and MUST be treated as empty. `protocol_version` is informational only — nothing
   gates on it (slice-2 spec decision 2); additive DTO changes never bump it.
3. `StatusSubscribe = 16` opens a long-lived connection; the daemon pushes full
   `DaemonStatus = 76` snapshots (`DaemonStatusDto`, snake_case, nulls always written) — one
   immediately, then debounced re-pushes on every change generation. Hello is **one-shot** (the
   daemon answers and closes), so an attach needs two connections: hello first, then subscribe.
4. Wire semantics are pinned by spec §4.1 of
   [2026-08-01-slice2-prework-control-ipc-design.md](2026-08-01-slice2-prework-control-ipc-design.md)
   and by exact-JSON tests: lowercase `connection` vocabulary, verbatim PascalCase agent
   `status` (open vocabulary — clients treat unknown values as opaque), `KindText` spellings,
   agents ordered `created_at` asc / `id` ordinal, `active_agents` derived from the same array.
   Unknown JSON members must never break a client (STJ unmapped-member skip).
5. `active_agents` counts `Starting|Running` — a **display count**, NOT the daemon's admission
   gate (`EffectiveCount`, which includes kill-quarantine). The app must never render
   `active_agents < max_agents` as a launch-capacity promise.
6. `FrameCodec.ReadAsync` throws `InvalidDataException` on undecodable input and
   `EndOfStreamException` (an `IOException`) on truncation; returns null on clean EOF. These
   are expected client-side observations of a skewed or dying peer, not bugs (§4 classifies
   them).
7. The wire DTOs and codec live in `Capacitor.Cli.Core/LocalIpc` (`FrameCodec`, `LocalFrame`,
   `HelloIpc.cs`, `StatusIpc.cs`); `LocalSocketPaths.Socket(name)` resolves the socket path.
   The CLI's existing `LocalAgentClient` is a one-shot terminal-attach helper, not a reusable
   typed client — nothing to reuse there beyond the codec.
8. **Daemon-name resolution** is `DaemonNameResolver.Resolve` with precedence: explicit
   `--name` arg (not applicable in the app) > `KCAP_DAEMON_NAME` env > the active profile's
   `daemon.name` > the shared username/machine fallback. A bare `kcap daemon start -d`
   re-resolves this in the child against ITS environment/cwd — so a spawner that wants a
   specific daemon must pass `--name` explicitly (§5).
9. `Capacitor.Cli.Core` is compiled into the NativeAOT CLI and daemon; anything added there
   must be AOT-clean and dependency-free (BCL only).
10. Real-socket test harnesses exist in `test/Capacitor.Cli.Tests.Unit/Daemon/`
    (`LocalControlHelloTests`, `DaemonStatusIpcTests`) with the established discipline: Windows
    guard, `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]`, short
    daemon names (macOS `sockaddr_un` ~104-byte limit).
11. CI (`.github/workflows/ci.yml`) builds the solution but runs tests by INVOKING EACH TEST
    PROJECT EXPLICITLY — adding a project to the solution does not execute its tests (§8 adds
    the invocation).

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **The typed IPC client lives in `Capacitor.Cli.Core/LocalIpc`** — one home for the whole protocol (codec + DTOs + client). BCL-only (`IAsyncEnumerable`, no Rx types on its surface) so the shared AOT core gains no dependencies. Future CLI verbs (e.g. a live status watch) can adopt it. |
| 2 | **The client is self-healing**: it owns connect → hello gate → subscribe → reconnect-with-backoff, and surfaces everything through one event stream. The app renders states; it does not implement retry. |
| 3 | **MVVM framework: ReactiveUI (+ DynamicData), via `ReactiveUI.Avalonia`** — the MAINTAINED ReactiveUI↔Avalonia integration (the historical `Avalonia.ReactiveUI` package is marked legacy on NuGet, stopped at 11.3.8, and redirects to `ReactiveUI.Avalonia`, which versions on its own line). This AMENDS umbrella §6, which named CommunityToolkit.Mvvm (§7 below). The app is stream/collection-shaped (status pushes, agents list, activity feed, consent prompts all arrive as pushes); DynamicData's keyed diffing is exactly the full-snapshot→delta machinery AI-1651's list needs, and retrofitting a reactive substrate after ViewModels exist costs more than starting with it. |
| 4 | **App tests: TUnit on MTP driving `Avalonia.Headless` through `HeadlessUnitTestSession`** via a small shared helper — one test framework and one CI invocation shape across the repo, instead of Avalonia's xunit/NUnit adapters. Avalonia/Rx globals are process-wide, so every test touching them serializes (§8). |
| 5 | **The app is not NativeAOT** — plain framework-dependent build this slice; packaging/trimming decisions belong to distribution (AI-1653). |
| 6 | **Dev-time CLI resolution for "start daemon"**: `KCAP_APP_CLI_PATH` env override, else `kcap` on PATH. The bundled binary is AI-1653's concern. |
| 7 | **`Connected` means served, not dialed**: the client reports `Connected` (and resets backoff) only when the first valid `DaemonStatus` frame arrives — never on a successful connect or subscribe write. |
| 8 | **One attach-status value**: the app service publishes state + reason + capabilities as a single atomic value; split observables that can tear are forbidden. |

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
.github/workflows/ci.yml                                  ← modified: run the app test project (§8)
Directory.Packages.props                                  ← modified: central versions for the new packages
```

Dependencies: `Capacitor.App` → `Capacitor.Cli.Core`, `Avalonia`, `Avalonia.Desktop`,
`Avalonia.Themes.Fluent`, `ReactiveUI.Avalonia`, `DynamicData` (+ `Avalonia.Headless` in the
test project). **Central package management**: the repo has
`ManagePackageVersionsCentrally=true`, so `Directory.Packages.props` gains `PackageVersion`
entries for exactly these packages — the Avalonia family (`Avalonia`, `Avalonia.Desktop`,
`Avalonia.Themes.Fluent`, `Avalonia.Headless`) pinned to ONE identical latest-stable 11.3.x
version, `ReactiveUI.Avalonia` at ITS OWN latest stable (the integration package versions on
its own line — it is never forced to equal Avalonia's version; NuGet's dependency ranges
enforce Avalonia compatibility), and `DynamicData` at its latest stable. The legacy
`Avalonia.ReactiveUI` package (deprecated at 11.3.8) is deliberately NOT used. `ReactiveUI`
and `System.Reactive` are deliberately NOT direct references — they arrive transitively via
`ReactiveUI.Avalonia`/`DynamicData`, so they get no `PackageVersion` entries and no project can
silently pin a conflicting version. Bootstrap/base types (`UseReactiveUI()`,
`ReactiveWindow<>`) come from `ReactiveUI.Avalonia`'s namespaces. Acceptance: restore + build
green on both CI legs. Core
gains no packages. Both new projects join the solution; the ubuntu and windows CI legs gain an
explicit `dotnet run --project test/Capacitor.App.Tests.Unit/...` step (§1.11 — solution
membership alone runs nothing). The AOT-publish checks are untouched and must stay warning-free
(the client is BCL-only).

## 4. The IPC client (`LocalControlClient`)

```csharp
public sealed class LocalControlClient(string daemonName, TimeProvider? time = null) {
    // INTERNAL test seams (Core grants the unit suite internals access): production always
    // runs the defaults, so no public validation contract is needed — an invalid value is a
    // test-authoring bug, not a runtime surface. Defaults below are the shipped behavior.
    internal TimeSpan[] RetryDelays { get; set; } = [1s, 2s, 5s, 10s, 30s]; // TimeSpan values; sketch shorthand
    /// Per-phase deadlines (§4.1): a silent peer must classify, never hang the state machine.
    internal TimeSpan ConnectTimeout       { get; set; } = 5s; // applies to EACH dial: hello AND subscribe
    internal TimeSpan HelloReplyTimeout    { get; set; } = 5s;
    internal TimeSpan FirstSnapshotTimeout { get; set; } = 10s;
    public IAsyncEnumerable<LocalControlEvent> RunAsync(CancellationToken ct);
}

public abstract record LocalControlEvent {
    public sealed record Connecting : LocalControlEvent;
    /// Carries the FIRST validated snapshot: no consumer can observe "connected"
    /// while holding only stale data from a previous incarnation (§4.3).
    public sealed record Connected(
        IReadOnlyList<string>? Capabilities, DaemonStatusDto FirstSnapshot) : LocalControlEvent;
    public sealed record Unreachable(string Reason) : LocalControlEvent;
    public sealed record Status(DaemonStatusDto Snapshot) : LocalControlEvent;
}
```

All waits — backoff delays AND the three phase deadlines — run on the injected `TimeProvider`
(default `TimeProvider.System`) so tests drive them deterministically; no wall-clock races.

### 4.1 Attach cycle

Resolve `LocalSocketPaths.Socket(daemonName)` → **connection 1: hello** (frame 15 → 75; hello
is one-shot, the daemon answers and closes) → gate on the reply → **connection 2:
`StatusSubscribe`** → read frames. Each phase has a finite deadline (`ConnectTimeout` on the
socket connect, `HelloReplyTimeout` on the hello reply, `FirstSnapshotTimeout` on the first
snapshot after subscribing) — a peer that accepts and then stays silent (wedged daemon,
unrelated process squatting the socket) classifies and enters backoff instead of pinning the
state machine on `Connecting` forever. The cycle SUCCEEDS only when the first VALID snapshot
arrives: the client yields `Connected(capabilities, firstSnapshot)` — the first snapshot rides
inside the `Connected` event — resets the backoff schedule to its start, and yields `Status`
for every subsequent frame. A subscribe connection that opens and then EOFs, faults, or goes
silent before the first valid frame is a FAILED cycle — no `Connected`, no backoff reset.

**Snapshot validity** (what "valid `DaemonStatus`" means — the first frame and every later
one): deserialization succeeds AND every member `StatusIpc.cs` declares non-nullable is
actually non-null — the root, `Daemon` (with `Name`, `Version`, `ServerUrl`, `Connection`),
`Agents`, and each agent element's `Id`, `Kind`, `Vendor`, `Status` — AND every `Id` is
non-whitespace and unique within the snapshot (ordinal). Uniqueness is load-bearing: the app
feeds the array into a `SourceCache` keyed by `Id`, and a snapshot with duplicate keys has no
unambiguous keyed-diff meaning. Unknown vocabulary in `Kind`/`Status`/`Connection` remains
fine — open vocabularies, non-null is the only requirement. STJ source-gen does not enforce
non-nullable members at runtime, so the client validates structurally and NEVER yields an
unusable DTO — the app may dereference what it receives. An invalid snapshot is protocol
evidence (§4.2), not data.

**Explicit non-goal:** no idle timeout on the ESTABLISHED stream. After the first snapshot the
daemon is legitimately silent until something changes; detecting a wedged daemon at idle would
need a heartbeat, which is not designed here.

### 4.2 Failure classification (exhaustive)

Every cycle failure is contained and classified; nothing but cancellation escapes `RunAsync`:

- **`daemon_unreachable`** (transport/unresponsive): socket file absent, connect refused,
  `ConnectTimeout`/`HelloReplyTimeout`/`FirstSnapshotTimeout` expiry,
  `IOException`/`SocketException` (including `EndOfStreamException` truncation) at any point,
  clean EOF on the subscribe connection, and — as the catch-all — any other non-cancellation
  exception inside a cycle.
- **`daemon_incompatible`** (protocol evidence): hello-then-clean-EOF with no reply (pre-hello
  daemon — a HEURISTIC per §1.1: a dying current daemon looks the same, and retries
  self-correct it); a `HelloReply` whose capabilities (null ⇒ empty, §1.2) lack `"status/1"`;
  an `Error` frame or any unexpected frame type answering `Hello` or arriving on the subscribe
  connection; `InvalidDataException` from the codec; malformed `HelloReply` JSON
  (`JsonException`); a malformed or structurally invalid `DaemonStatus` payload (§4.1
  validity) — first frame or mid-stream (a mid-stream one ends the Connected streak as
  `Unreachable("daemon_incompatible")`).
  Incompatible is STILL retried on the same schedule — a daemon update/restart fixes it and
  the retry then succeeds; the reason string only changes what the UI says while waiting
  (neutrally: version skew, not a verdict about which side is old — §5). Unknown/extra
  capability strings are ignored (forward compat); `protocol_version` is not gated (§1.2).

### 4.3 Observable state machine (pinned sequence)

- On enumeration start (initial run or a manual restart — each `RunAsync` call is one
  enumeration): yield `Connecting`, then run attach cycles.
- Success path: `Connecting` → `Connected(caps, first)` → `Status`* — the first snapshot
  travels IN the `Connected` event, so no consumer can observe the connected state before the
  fresh data exists (§5 pins the service-side publication order that preserves this
  end-to-end).
- Failure path: yield `Unreachable(reason)` and begin backed-off retries. **Background retries
  are silent**: no `Connecting` is emitted for automatic re-attempts, and the externally
  observable state stays `Unreachable` until either a cycle succeeds (→ `Connected`) or a
  cycle fails with a DIFFERENT reason (→ `Unreachable(newReason)`). A persistent outage
  therefore produces exactly ONE `Unreachable` event per reason, however many cycles run.
- After a `Connected` streak ends (subscribe stream fails), the next event is
  `Unreachable(reason)` — again once, then silent retries.
- Cancellation ends the enumeration cleanly with no fabricated event.

`Connecting` thus appears exactly once per enumeration — it marks "an attach attempt the user
initiated is in flight", not "a socket dial happened". Manual Retry restarts the enumeration
(§5), which is what makes Retry visibly do something.

### 4.4 Tests

Kcap-cli unit suite, reusing the AI-1649 harness (Windows guard, `NotInParallel`, short daemon
names), controlled `TimeProvider` for backoff and phase deadlines:

- Gate pass → `Connecting`, then `Connected` carrying capabilities AND the first snapshot,
  then `Status` per push — in that order; nothing yields between `Connecting` and `Connected`.
- Capability-missing, hello-EOF, `Error`-reply, and undecodable-frame gates each classify as
  `daemon_incompatible`; socket-absent and connect-refused classify as `daemon_unreachable`.
- Silent-peer matrix (scripted socket server): accepts then stays silent during hello →
  `HelloReplyTimeout` expiry → `daemon_unreachable`; accepts `StatusSubscribe` then never
  pushes → `FirstSnapshotTimeout` expiry → `daemon_unreachable`; both enter backoff, no hang.
- Malformed/invalid status: unparseable JSON as the first frame → failed cycle,
  `daemon_incompatible`; structurally invalid payloads (`{"daemon":null,"agents":null}`, null
  daemon leaf fields like `version`/`connection`, an agents array containing null elements,
  null/whitespace-only ids, null `kind`/`vendor`/`status` leaves, and DUPLICATE ids) → same;
  each shape tested both as the first frame and mid-stream; a mid-stream one ends the streak
  with `Unreachable("daemon_incompatible")`; no invalid DTO is ever yielded.
- Subscribe-EOF-before-first-frame: no `Connected`, no backoff reset, one `Unreachable`.
- Persistent outage across ≥2 cycles: exactly one `Unreachable` event (transition-only pin);
  a reason CHANGE (unreachable daemon replaced by an incompatible one) yields a second event.
- Backoff schedule advances across failed cycles and resets after a proven `Connected`.
- Daemon stop mid-stream → `Unreachable`; daemon restart → `Connected` with a FRESH first
  snapshot (reconnect pin).
- Clean cancellation mid-backoff-wait and mid-stream.

## 5. App: service, ViewModel, window

**`DaemonClientService : IDaemonClientService`** (singleton; loop started at app launch with
the app-lifetime token; interface exists so ViewModel tests script the stream):

- `IObservable<AttachStatus> Status` where
  `sealed record AttachStatus(AttachState State, string? Reason, IReadOnlyList<string>? Capabilities)`
  and `enum AttachState { Connecting, Connected, Unreachable }` — both APP-side types
  (`Capacitor.App`; Core models state solely through its event records and needs no enum).
  ONE atomic value (decision 8), replay-1, initial value `AttachStatus(Connecting, null, null)`
  published synchronously at service start. **Event→status mapping (complete):**
  `Connecting` → `(Connecting, null, null)`; `Connected(caps, first)` →
  `(Connected, null, caps)`; `Unreachable(reason)` → `(Unreachable, reason, null)` —
  capabilities are CLEARED on every non-connected state (null), never retained from a previous
  incarnation: a consumer that needs capability gating while disconnected is making a
  category error, and the next `Connected` carries the fresh list. Projections (state text,
  reason text, command enablement) derive from this single stream — split state/reason
  observables that can tear are forbidden.
- `IObservable<DaemonStatusDto> Snapshots` — replay-1 that emits NOTHING until the first real
  snapshot (no fabricated seed; the UI renders placeholders until then).
- **Publication order on reconnect (no-stale pin):** on a `Connected(caps, first)` event the
  service applies the carried first snapshot FIRST — publish to `Snapshots`, `EditDiff` into
  the cache — and only THEN publishes `AttachStatus(Connected, …)`. Combined with §4.3 (the
  first snapshot rides inside `Connected`), a consumer that gates rendering on `Connected` can
  never observe the connected state alongside a previous incarnation's data. §8 has the
  reconnect assertion.
- `SourceCache<AgentStatusDto, string> Agents` (keyed by `Id`, `EditDiff` per snapshot).
  **Retained across disconnects**: neither the cache nor the last snapshot is cleared on
  `Unreachable` — staleness is a presentation concern (the VM stops showing the count when not
  `Connected`, §below). Rendered only as a count this slice; AI-1651 binds the list.
- `Task RestartLoopAsync()` — SINGLE-FLIGHT serialized restart: cancels the current client
  enumeration, AWAITS its completion, then starts the next one; at most one enumeration is
  ever live, so two publishers can never interleave into the subjects/cache. Concurrent calls
  coalesce onto the in-flight restart. After shutdown has begun it is a no-op. Backs the Retry
  button and the post-start kick (below).
- `Task<StartDaemonResult> StartDaemonAsync(CancellationToken ct)` with
  `sealed record StartDaemonResult(bool Ok, string? Message)` — spawns the CLI (decision 6)
  with the EXPLICIT resolved name: `kcap daemon start -d --name <resolvedName>` — never bare
  (§1.8: a bare start re-resolves in the child and can start a daemon on a different socket
  than the one the client watches). On exit 0 it immediately calls `RestartLoopAsync()` so the
  attach doesn't sit out a 30 s backoff. Spawn exception, non-zero exit, and empty stderr all
  produce a non-empty human-readable `Message`. `ct` (the app-lifetime token) abandons the
  WAIT, not the started daemon.
- **Daemon name resolved ONCE at service construction** using the same resolver the CLI uses
  (§1.8 precedence, no CLI-arg tier), and that one name feeds both `LocalControlClient` and
  the start-daemon argv — the watched daemon and the started daemon cannot diverge.
- **Shutdown** (app exit): cancel the lifetime token → await the client loop's completion →
  dispose subjects and the `SourceCache`. Pinned: no live socket read and no child-process
  wait survives app exit.

**`MainWindowViewModel`** (ReactiveObject, `IActivatableViewModel`): projections of `Status`
and `Snapshots` observed on `RxApp.MainThreadScheduler` — `DaemonName`, `DaemonVersion`,
`ServerUrl`, `ConnectionText`, `AgentCountText` (rendered as "n of m agents" ONLY while
`Connected`; "—" otherwise — no free-slots claim, §1.5), `State`, `Reason`, `StartMessage`.
Commands: `StartDaemonCommand` — enabled iff the current `AttachStatus` is
`(Unreachable, "daemon_unreachable")` and no start is in flight; `RetryCommand` — always
enabled outside `Connected`. `Unreachable("daemon_incompatible")` renders the NEUTRAL skew message
"app and daemon are incompatible — make sure both are up to date" with Retry only (§4.2 is a
broad heuristic — an unexpected frame can equally mean the APP is the older side, so the UI
must not prescribe an upgrade direction; Start stays disabled because the daemon is alive).
`StartMessage` (start-daemon failure text) clears on the next start attempt and on any
transition to `Connected`. All VM subscriptions are activation-scoped (`WhenActivated`);
the service outlives ViewModels and owns its subjects.

**`MainWindow`** (`ReactiveWindow<MainWindowViewModel>`, `WhenActivated`-scoped bindings): one
bare window — daemon identity block (name, version), server URL, connection state, agent
count, state-dependent Start/Retry actions, message line. First-run/onboarding, tray, and all
richer UI are later slices.

## 6. Error handling

- Daemon absence/incompatibility is DATA (the client's classified state stream, §4.2), never
  an exception; the app never blocks on the daemon (umbrella §10).
- `StartDaemonAsync` failures render as `StartMessage` text; the attach state machine is
  unaffected by them.
- Lifecycle: VM subscriptions die with view deactivation; the service's loop, subjects, and
  cache die with app shutdown (§5 shutdown pin); `RestartLoopAsync` can never produce
  overlapping publishers (single-flight pin).
- The `HeadlessUnitTestSession` helper pins `RxApp.MainThreadScheduler` to an immediate
  scheduler for the test body and RESTORES the prior scheduler in a `finally` (the scheduler
  is process-global).

## 7. Amendment to the umbrella

Umbrella §6 named CommunityToolkit.Mvvm as the MVVM framework. Amended 2026-08-03 (decision 3
above) to **ReactiveUI + DynamicData**, integrated via the MAINTAINED `ReactiveUI.Avalonia`
package (successor to the deprecated `Avalonia.ReactiveUI`): the ecosystem matches the app's
push-stream shape, and DynamicData is its native collection layer. The umbrella document's §6
line is updated alongside this spec; the AI-1650 issue text is corrected after spec approval.

## 8. Testing

- **Core client**: the §4.4 list — real-socket integration in `Capacitor.Cli.Tests.Unit`,
  existing harness discipline, `TimeProvider`-driven backoff (deterministic, no wall-clock
  races).
- **App — ViewModel/service tests** against a scripted `IDaemonClientService`: state/reason
  projections from the atomic `AttachStatus` (no torn intermediate states possible — pinned by
  construction, asserted by a state×reason command-enablement matrix); agent-count text shows
  values only in `Connected` and "—" after a disconnect WITHOUT the cache being cleared
  (disconnect test); **reconnect no-stale assertion**: script Connected(A) → Unreachable →
  Connected(B) and assert that at the moment status flips back to `Connected`, `Snapshots`
  and every rendered field already carry B's values — old identity/count are never visible as
  connected; `SourceCache` diffing add/update/remove across snapshots; `Snapshots` emits
  nothing before the first snapshot; initial `AttachStatus` replay is `Connecting`;
  **capability-clearing assertion**: script Connected(caps) → Unreachable and a manual restart
  → Connecting, asserting `Capabilities` is null in both non-connected statuses (never
  retained across incarnations);
  **deactivation-disposal test**: activate the VM, deliver events, deactivate (window close),
  deliver more events, assert no projection updates and the activation subscriptions are
  released.
- **App — service integration tests** (fake process-runner seam): `StartDaemonAsync` exact
  argv pin (`daemon start -d --name <resolved>` through the `KCAP_APP_CLI_PATH` binary);
  failure capture (spawn exception / non-zero exit / empty stderr → non-empty message);
  exit-0 triggers an immediate `RestartLoopAsync`. Rapid double `RestartLoopAsync` produces
  one live enumeration and no interleaved events (single-flight pin). Shutdown leaves no live
  loop (awaited-completion pin); **shutdown-during-start test**: begin `StartDaemonAsync`
  against a non-exiting fake process, trigger app shutdown, assert the wait is abandoned and
  shutdown completes (no child-process wait survives exit); **disposal assertions**: after
  shutdown the service's subjects and `SourceCache` are disposed and publish nothing.
- **App — headless UI**: one smoke test booting `MainWindow` and asserting the §5 fields
  render (incl. the deliverable's identity block — rendering acceptance). Every test touching
  Avalonia or `RxApp` globals carries `[NotInParallel("AvaloniaSession")]` — the session and
  scheduler are process-wide (decision 4).
- **CI**: ci.yml runs the app test project explicitly on ubuntu + windows (§1.11); AOT-publish
  checks unchanged and green.
- Onboarding-style e2e (real app against a real daemon) stays manual this slice (umbrella §10
  accepted risk).

## 9. Out of scope

Tray/menu-bar (AI-1651) · consent prompt window + activity feed (AI-1652) · bundling, signing,
PATH shim, auto-update (AI-1653) · daemon lifecycle install/takeover + version-skew takeover
offer (AI-1654) · onboarding wizard (AI-1655) · settings surfaces (AI-1656) · Windows named
pipe (AI-1657) · active-profile display (deferred with settings, see Deliverable). No README
change: nothing is distributed yet and no CLI surface moves.
