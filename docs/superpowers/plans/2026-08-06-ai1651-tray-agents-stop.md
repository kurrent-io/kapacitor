# Tray presence, agents list, stop (AI-1651) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the desktop supervisor its slice-2 value surface: a menu-bar tray icon with a native menu (agents, per-agent Stop, open-in-web, pause-launches toggle), a real Agents grid in the main window, and hide-to-tray lifecycle.

**Architecture:** A `TrayViewModel` projects everything decision-shaped (tray state, menu model, commands) from the existing `IDaemonClientService`; a dumb `TrayIconManager` adapter renders it into Avalonia `TrayIcon`/`NativeMenu` with a `NeedsUpdate`-only rebuild cadence. New Core one-shot socket ops (`LocalControlOps`) carry StopV2 and consent Get/Put. A `PauseController` serializes all consent-policy operations on one lane with a one-slot desired-state queue.

**Tech Stack:** .NET 10, Avalonia 12.1.1 + ReactiveUI.Avalonia 12.0.3 + DynamicData 9.4.33 (already referenced — NO new packages), TUnit + Avalonia.Headless.

**Authoritative requirements:** `docs/superpowers/specs/2026-08-06-ai1651-tray-agents-stop-design.md` (reviewer-signed-off). Where this plan and the spec disagree, the spec governs — stop and flag it.

## Global Constraints

- **No new NuGet packages.** No direct `ReactiveUI`/`System.Reactive` references — only `ReactiveUI.Avalonia` 12.0.3 + `DynamicData` 9.4.33 via CPM (the flavor rule from the AI-1650 spec §7).
- **No Linear issue IDs (`AI-####`) anywhere in C# source** — a CI job fails the PR. Use spec-section references (e.g. "spec §6") in comments instead.
- **`RxSchedulers.MainThreadScheduler`**, never `RxApp` (ReactiveUI 23.x in the System.Reactive flavor has no `RxApp`).
- **Wire DTOs are pinned:** `StatusIpc.cs` and `ConsentIpc.cs` must NOT change (no attributes, no members, no serializer options).
- **`DaemonStatusValidator` stays untouched** (AI-1650's pinned client contract; the tray projection absorbs malformed counts instead — spec §4 row 6).
- App test classes carry `[NotInParallel("AvaloniaSession")]` and drive VMs through `AvaloniaSession.DispatchAsync` / `WithImmediateRxScheduler` (see `test/Capacitor.App.Tests.Unit/AvaloniaSession.cs`).
- Socket tests: copy the harness conventions from `test/Capacitor.Cli.Tests.Unit/LocalControlClientTests.cs` — short socket paths (macOS sockaddr_un ~104 bytes), Windows guard, `[NotInParallel]`.
- Run single test classes with `--treenode-filter "/*/*/ClassName/*"` (bare `"*Name*"` matches nothing).
- **AOT:** after any `Capacitor.Cli.Core` change run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` — must print nothing.
- No README change (no CLI surface changes in this slice).
- Commit after every green test cycle; small commits.

**Interface ownership map (who defines → who consumes):**
- Task 1: `ILocalControlOps`, `LocalControlOps`, `StopAgentResult`, `LocalControlOpsException` (Core) → Tasks 3, 4.
- Task 2: `TrayState`, `TrayAgentEntry`, `TrayPauseItem`, `TrayMenuModel`, `IPauseController` + `PauseState` (interface only), `TrayViewModel` (projections) → Tasks 3, 4, 6.
- Task 3: `PauseController` (implements Task 2's `IPauseController`), `TogglePauseCommand`/`RequestPauseRefresh` on `TrayViewModel`.
- Task 4: `IAppNotifier`/`AppNotifier`, `IUrlOpener`/`ShellUrlOpener`, `AgentActionService`, `StopAgentCommand`/`OpenInWebCommand` on `TrayViewModel` → Tasks 5, 7.
- Task 5: `UptimeFormat`, `AgentRowViewModel`, `MainWindowViewModel` additions (Agents collection, Banner).
- Task 6: `TrayIconRenderer`, `TrayMenuBuilder`, `TrayMenuSync`, `TrayIconManager` → Task 7.
- Task 7: `MainWindowCoordinator`, `App.axaml.cs` lifecycle wiring.

---

### Task 1: `LocalControlOps` one-shot IPC operations (Core) + engine acceptance test

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/LocalControlOpsTests.cs`
- Modify: `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentEngineTests.cs` (add one test)

**Interfaces produced:**
```csharp
namespace Capacitor.Cli.Core.LocalIpc;

/// Status: "stopped" | "failed" | "skipped" (StopAck vocabulary) or "error" (daemon Error
/// frame; Error carries its display text). Ok is true only for "stopped".
public sealed record StopAgentResult(bool Ok, string Status, string? Error);

/// Reason ∈ daemon_unreachable | daemon_rejected | unexpected_reply | timed_out (stable
/// identifiers, not user copy — spec §10).
public sealed class LocalControlOpsException(string reason, string message) : Exception(message) {
    public string Reason { get; } = reason;
}

public interface ILocalControlOps {
    Task<StopAgentResult>  StopAgentAsync(string agentId, bool force, CancellationToken ct);
    Task<ConsentPolicyDto> GetConsentPolicyAsync(CancellationToken ct);
    Task<ConsentAckDto>    PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct);
}

public sealed class LocalControlOps(string daemonName, TimeProvider? time = null) : ILocalControlOps {
    // Internal seams for tests (same pattern as LocalControlClient):
    internal TimeSpan ConnectTimeout      = TimeSpan.FromSeconds(5);
    internal TimeSpan ConsentReplyTimeout = TimeSpan.FromSeconds(10);
    internal TimeSpan StopReplyTimeout    = TimeSpan.FromSeconds(40); // StopAck lands only after graceful stop (~25s worst case)
}
```

**Requirements (spec §10, verbatim-binding):**
- One fresh socket per operation: `Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)` + `UnixDomainSocketEndPoint(LocalSocketPaths.Socket(daemonName))` + `NetworkStream(socket, ownsSocket: false)` — mirror `AgentCommand.SendStopAsync` (`src/Capacitor.Cli/Commands/AgentCommand.cs:218`). Hello-less.
- Phase timeouts via `new CancellationTokenSource(timeout, _time)` linked with the caller token (`CancellationTokenSource.CreateLinkedTokenSource`) — the `LocalControlClient` pattern. Never `WaitAsync` (abandons the socket op).
- Failure classification with pinned catch precedence (spec §10): caller-token cancellation is checked FIRST (`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }`) and propagates as OCE — never `timed_out`. Then: other OCE → `timed_out`; clean EOF (`FrameCodec.ReadAsync` returns null) and `EndOfStreamException` (truncated header/payload — it DERIVES from IOException, so its catch clause must come first) → `unexpected_reply`; any other post-connect `IOException`/`SocketException` → `daemon_unreachable`; connect-phase socket failures → `daemon_unreachable`.
- `StopAgentAsync`: write `LocalFrame.StopV2(force, agentId)`; `Error` reply → `StopAgentResult(false, "error", reply.Text)` (NOT an exception); `StopAck` → the reply must contain EXACTLY ONE line whose first tab-field equals `agentId`, that line must have exactly two tab-separated fields, and field two ∈ `stopped|failed|skipped` — any violation → `unexpected_reply`. Return `StopAgentResult(status == "stopped", status, null)`.
- `GetConsentPolicyAsync`: write a `ConsentRulesGet` frame (mirror the existing consent CLI command's frame construction — find it with `rg -n "ConsentRulesGet" src/Capacitor.Cli`); expect `ConsentRules` → deserialize `ConsentPolicyDto` via `ConsentIpcJsonContext.Default` and STRUCTURALLY VALIDATE (STJ does not enforce non-nullable members): non-null root, `Default` ∈ `allow|deny|prompt`, `PromptTimeoutSeconds >= 1`, non-null `Rules`, every element non-null with `Action` ∈ `allow|deny`. Violation or `JsonException` → `unexpected_reply`. `Error` reply → `daemon_rejected` with the frame text.
- `PutConsentPolicyAsync`: write `ConsentRulesPut` with the serialized policy; expect `ConsentAck` → non-null root required (note: `{}` deserializes to `ConsentAckDto(false, null)` — that IS returned; presentation is the app's job). `Error` reply → `daemon_rejected`.
- Any unexpected frame type → `unexpected_reply`.

**Steps:**

- [ ] **Step 1: Write the failing tests.** Build a `ScriptedOpsServer` modeled directly on the `ScriptedServer` in `LocalControlClientTests.cs` (bind a real Unix socket at a short temp path, accept one connection, run a script). Because `LocalControlOps` derives its socket path from the daemon name via `LocalSocketPaths.Socket`, use the SAME daemon-name/socket-path arrangement `LocalControlClientTests` uses — do not invent a new mechanism. Test list (one test each):
  - `Stop_ok`: server replies `StopAck` `"{id}\tstopped"` → `(true, "stopped", null)`.
  - `Stop_failed` / `Stop_skipped`: `(false, "failed"|"skipped", null)`.
  - `Stop_error_frame`: server replies `Error("x is protected")` → `(false, "error", "x is protected")`.
  - `Stop_missing_line`, `Stop_duplicate_line`, `Stop_three_fields`, `Stop_unknown_status`: → `LocalControlOpsException` with `Reason == "unexpected_reply"`.
  - `Get_policy_ok`: valid `ConsentPolicyDto` JSON round-trips.
  - `Get_policy_invalid` (four cases in one parameterized test): `{"default":"allow","prompt_timeout_seconds":45,"rules":null}`, a null rule element, `"default":"bogus"`, `prompt_timeout_seconds:0` → `unexpected_reply`.
  - `Get_policy_error_frame` → `daemon_rejected`.
  - `Put_ack_ok` / `Put_ack_empty_object`: `{}` → returns `ConsentAckDto(false, null)` (wire-shape assertion ONLY — banner behavior is the app suite's).
  - `Clean_eof` (server closes without reply) and `Truncated_frame` (server writes 2 bytes of a header then closes) → `unexpected_reply`.
  - `Post_connect_reset` (server accepts then aborts the socket mid-read) → `daemon_unreachable`.
  - `Connect_failure` (no listener at the path) → `daemon_unreachable`.
  - `Reply_timeout` (server accepts, never replies; seam-shortened timeout + a fake `TimeProvider` if the harness supports it, else a 100ms real timeout) → `timed_out`.
  - `Caller_cancellation` (server never replies; cancel the caller token) → `OperationCanceledException`, NOT `LocalControlOpsException`.
- [ ] **Step 2: Run to verify they fail** (`dotnet run --project test/Capacitor.Cli.Tests.Unit/... -- --treenode-filter "/*/*/LocalControlOpsTests/*"`): compile error (type missing) then assertion failures.
- [ ] **Step 3: Implement `LocalControlOps.cs`** per the requirements above. Suggested shape: one private `ExchangeAsync(LocalFrame request, TimeSpan replyTimeout, CancellationToken ct)` doing connect + write + single read with the full classification, and three thin public methods parsing the reply.
- [ ] **Step 4: Run the test class to green.**
- [ ] **Step 5: Add the engine acceptance test** to `test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentEngineTests.cs` (spec §6 Semantics): a policy `new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45, [new("deny", null, null, null, null)])` evaluated with `RequesterIsOwner: true` → verdict `Allow`, source `"owner"`. Match the file's existing test style exactly.
- [ ] **Step 6: Run that class; run the full Cli unit suite; AOT publish grep (must be empty).**
- [ ] **Step 7: Commit** (`feat: LocalControlOps one-shot stop/consent IPC operations`).

---

### Task 2: Tray state projection + menu model + `TrayViewModel` (projections only)

**Files:**
- Create: `src/Capacitor.App/ViewModels/TrayModels.cs`
- Create: `src/Capacitor.App/ViewModels/TrayViewModel.cs`
- Create: `src/Capacitor.App/Services/IPauseController.cs`
- Test: `test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs`

**Interfaces produced:**
```csharp
namespace Capacitor.App.Services;

public sealed record PauseState(bool Checked, bool Verified, bool Busy);

/// Implemented in a later task; this task consumes only the contract. State is replay-1,
/// seeded with (Checked: false, Verified: false, Busy: false) — unverified until the first
/// successful refresh.
public interface IPauseController {
    IObservable<PauseState> State { get; }
    void RequestRefresh();            // passive; DROPPED while the lane is busy (spec §6)
    void RequestToggle(bool desired); // desired checked value, single-flight + one queued slot (spec §6)
}
```
```csharp
namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

public sealed record TrayAgentEntry(string Id, string Label, bool StopEnabled); // StopEnabled: always true in this task; Task 4 wires in-flight gating
public sealed record TrayPauseItem(bool Enabled, bool Checked);
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause);

public sealed class TrayViewModel : ReactiveObject, IDisposable {
    public TrayViewModel(IDaemonClientService service, IPauseController pause);
    public TrayMenuModel MenuModel { get; }  // OAPH on RxSchedulers.MainThreadScheduler
    public void RequestPauseRefresh();       // adapter's Opening hook → pause.RequestRefresh() (wired now; trivially delegating)
}
```

**Requirements (spec §4, §5 — copy these exactly):**
- The state mapping is the ten-row table in spec §4, precedence top-down. Implement as a pure static function `Project(AttachStatus status, DaemonStatusDto? snap) → (TrayState State, int Count)`:
  rows 1–2: `Unreachable` + reason `daemon_unreachable` → Stopped; reason `daemon_incompatible` → Attention; row 10: any other reason → Attention. Row 3: `Connecting` → Connecting. Rows 4–9 (Connected, on `snap.Daemon`): `connection == "connecting"` → Connecting; `"reconnecting"`/`"disconnected"` → Attention; `"connected"` && `ActiveAgents < 0` → Attention; `== 0` → Idle; `> 0` → Running with n = `ActiveAgents`; any other `connection` → Attention. Defensive only (cannot happen per the AI-1650 client pin): Connected with null snapshot → Connecting.
- Header copy (spec §5): prefix `{DaemonName}: ` EXCEPT the skew case. Stopped → `not running`; Connecting → `connecting…`; Attention + reason `daemon_incompatible` → exactly the existing copy `app and daemon are incompatible — make sure both are up to date` (no prefix — reuse the constant from `MainWindowViewModel` if it is one, else duplicate the string verbatim); Attention + `reconnecting` → `reconnecting to server`; + `disconnected` → `disconnected from server`; Idle → `connected — no agents`; Running → `connected — {n} agent(s) running`; ANY other Attention (rows 6, 9, 10) → `needs attention`.
- Agent entries: only while `Connected`; source is the latest snapshot's `Agents` filtered to `Status` ∈ `Starting|Running` (ordinal compare), sorted `CreatedAt` asc then `Id` ordinal; label `{Kind} · {Vendor} · {repoLeaf}` where repoLeaf = last path segment of `RepoPath` (`Path.GetFileName(Path.TrimEndingDirectorySeparator(p))`) or `—` when null.
- Pause item: `Enabled = connected && capabilities.Contains("consent/1") && pauseState.Verified && !pauseState.Busy`; `Checked = pauseState.Checked` (last-known, shown even while disabled).
- Combine `service.Status` + `service.Snapshots` + `pause.State` with `Observable.CombineLatest`; snapshots start with `(DaemonStatusDto?)null` (`.StartWith`) so the VM emits before the first snapshot. `ObserveOn(RxSchedulers.MainThreadScheduler)` before `ToProperty`.

**Steps:**

- [ ] **Step 1: Write failing tests** in `TrayViewModelTests.cs` using `FakeDaemonClientService` (existing) + a scripted `FakePauseController` (`BehaviorSubject<PauseState>` + recorded calls, define it in this test file). Cases — drive with `AvaloniaSession.DispatchAsync` + `WithImmediateRxScheduler` like `MainWindowViewModelTests`:
  - All ten §4 rows → expected `TrayState` + count (parameterized where natural). Include: stale snapshot retained while `Unreachable` still yields Stopped/Attention (push a snapshot, then push Unreachable).
  - Header copy per state incl. the no-prefix skew case and the `needs attention` fallback (feed `connection: "weird"`, `ActiveAgents: -1`, unreachable reason `"future_reason"`).
  - Entries: filtering (`Completed` agent excluded), ordering (two agents out of creation order), label format incl. null repo → `—`; entries EMPTY when not Connected despite retained cache.
  - Pause item enablement matrix: no `consent/1` → disabled; disconnected → disabled; `Busy` → disabled; `!Verified` → disabled; all-good → enabled with `Checked` mirrored.
  - `RequestPauseRefresh()` delegates to the fake controller (recorded call).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** `TrayModels.cs`, `IPauseController.cs`, `TrayViewModel.cs`.
- [ ] **Step 4: Run the class to green; run the full app suite.**
- [ ] **Step 5: Commit** (`feat: tray state projection and menu model`).

---

### Task 3: `PauseController` — serialized consent lane

**Files:**
- Create: `src/Capacitor.App/Services/PauseController.cs`
- Modify: `src/Capacitor.App/ViewModels/TrayViewModel.cs` (add `TogglePauseCommand`)
- Test: `test/Capacitor.App.Tests.Unit/PauseControllerTests.cs`

**Interfaces produced:**
```csharp
public sealed class PauseController : IPauseController, IDisposable {
    // notify: failure banners (Task 4 introduces AppNotifier; THIS task takes Action<string>
    // to stay decoupled — Task 4 passes notifier.Notify when composing).
    public PauseController(ILocalControlOps ops, Action<string> notify, CancellationToken shutdownToken);
}
// TrayViewModel gains:
public ReactiveCommand<bool, Unit> TogglePauseCommand { get; } // parameter: desired checked value (frozen by the adapter, spec §6)
```

**Requirements (spec §6 — the contract is precise; read that section in full before coding):**
- The pause rule is EXACTLY `ConsentRuleDto("deny", null, null, null, null)` at index 0. Detection: `Rules.Count > 0 && Rules[0] is { Action: "deny", Requester: null, Kind: null, Repo: null, Vendor: null }`.
- ONE lane for all consent ops. Passive refresh while lane busy → dropped silently. Toggle while a PASSIVE op owns the lane → store desired value in the one-slot queue, mark `Busy` immediately, run exactly once when the passive completes (success OR failure). Toggle while a TOGGLE owns the lane (running or queued) → ignored.
- Toggle operation: fresh `Get` → apply toward the DESIRED state (desired true + rule already at 0 → idempotent no-op, no Put; desired false + no rule at 0 → no-op; otherwise insert-at-0/remove-index-0 and `Put`, passing `Default` and `PromptTimeoutSeconds` through unchanged) → ack handling → trailing refresh `Get`.
- Ack: `Ok == false` → `notify(...)` with the ack `Error` text, or the neutral copy `The daemon rejected the change` when null/empty; `Ok == true` with non-null `Error` → success, `Console.Error.WriteLine` the warning, NO notify.
- Trailing refresh success → reconcile `Checked` from the fetched policy, `Verified = true`. Trailing refresh failure (or passive refresh failure) → keep last-known `Checked`, `Verified = false` (item disables), log to stderr, no notify for the passive path.
- `LocalControlOpsException` from Get/Put → `notify` mapped copy: `daemon_unreachable` → `The daemon is not reachable`; anything else → `Couldn't update launch pause: {e.Message}`; then attempt the trailing refresh anyway (it decides Verified).
- `OperationCanceledException` (shutdown token) anywhere → absorbed QUIETLY: no notify, no stderr-error, no Verified change; lane and queued slot cleared.
- `State` is a `BehaviorSubject<PauseState>` seeded `(false, false, false)`. `Busy == toggle running || toggle queued` (a passive-only lane occupancy is NOT Busy — the item stays enabled and a click queues).
- Implementation note: guard all lane/slot/state transitions with one `lock`; run socket work unlocked on the thread pool (`Task.Run` from the request methods — both return `void` and must never throw).

**Steps:**

- [ ] **Step 1: Write failing tests** with a `ScriptedOps : ILocalControlOps` fake (per-call `TaskCompletionSource` gates so tests deterministically hold/release each Get/Put — no timing). Cases:
  - `First_refresh_sets_verified_checked` (policy with pause rule at 0 → `(true, true, false)`); and without → `(false, true, false)`.
  - `Refresh_failure_marks_unverified` (Get throws `unexpected_reply` → Checked retained, Verified false).
  - `Passive_dropped_while_busy`: hold refresh #1's Get; call `RequestRefresh` again; release → exactly ONE Get issued.
  - `Toggle_pause_puts_rule_at_zero`: desired true against `{default: "prompt", timeout: 45, rules: [narrower]}` → Put payload has the wildcard deny at index 0, original rule at 1, default/timeout unchanged.
  - `Toggle_unpause_removes_only_index_zero`; `Toggle_idempotent_no_put` (desired true, rule already present → NO Put call).
  - `Toggle_during_passive_queues_desired` (the spec §12 mirror test): hold the passive Get; `RequestToggle(true)`; assert `Busy` immediately; release the passive returning a policy WHERE THE RULE ALREADY EXISTS (changed externally) → the queued toggle runs once, its fresh Get sees the rule, NO Put (idempotent no-op toward desired) — never an inversion. Variant: release returning no-rule policy → Put with rule at 0.
  - `Toggle_during_toggle_ignored` (second `RequestToggle` while first holds the lane → no second op, slot empty).
  - `Ack_error_notifies` (`ok:false` + text → notify with text; `ok:false, error:null` → the neutral copy) and `Ack_warning_success` (`ok:true, error:"w"` → NO notify).
  - `Trailing_refresh_reconciles` (ack ok:false but trailing Get succeeds → Verified true, Checked from fetched policy) and `Trailing_refresh_failure_unverified`.
  - `Shutdown_cancellation_quiet`: cancel the shutdown token while a toggle's Get is held; release → no notify, Verified unchanged, lane free (a subsequent `RequestRefresh` issues a Get).
  - Plain C# tests (no Avalonia session needed — the controller is scheduler-free); use `WaitUntilAsync`-style polling helpers from `DaemonClientServiceTests` for the async settles.
- [ ] **Step 2: Run to verify failure. Step 3: Implement. Step 4: Green + full app suite.**
- [ ] **Step 5: Wire `TogglePauseCommand`** into `TrayViewModel` (`ReactiveCommand.Create<bool>(pause.RequestToggle)` — fire-and-forget by design; the controller serializes) and add one VM test that executing the command reaches the fake controller with the parameter value.
- [ ] **Step 6: Green; commit** (`feat: pause-launches controller with serialized consent lane`).

---

### Task 4: Stop + open-in-web commands, `AppNotifier`, `AgentActionService`

**Files:**
- Create: `src/Capacitor.App/Services/AppNotifier.cs`
- Create: `src/Capacitor.App/Services/UrlOpener.cs`
- Create: `src/Capacitor.App/Services/AgentActionService.cs`
- Modify: `src/Capacitor.App/ViewModels/TrayViewModel.cs` (commands + in-flight gating into `MenuModel`)
- Test: `test/Capacitor.App.Tests.Unit/AgentActionServiceTests.cs`, additions to `TrayViewModelTests.cs`

**Interfaces produced:**
```csharp
public interface IAppNotifier { IObservable<string> Messages { get; } void Notify(string message); }
public sealed class AppNotifier : IAppNotifier; // Subject (replay-0); Notify ALSO writes the message to Console.Error (spec §11)

public interface IUrlOpener { void Open(string url); }
public sealed class ShellUrlOpener : IUrlOpener; // Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })

/// Shared by the tray menu and the main-window rows (spec §7: one code path).
public sealed class AgentActionService {
    public AgentActionService(ILocalControlOps ops, IAppNotifier notifier, IUrlOpener opener,
                              IObservable<DaemonStatusDto> snapshots, CancellationToken shutdownToken);
    public IObservable<IReadOnlySet<string>> StopsInFlight { get; } // replay-1, starts empty
    public void RequestStop(string agentId, string label);          // never throws
    public void OpenInWeb(string agentId);                          // never throws
}
// TrayViewModel gains:
public ReactiveCommand<string, Unit> StopAgentCommand  { get; } // parameter: agent id
public ReactiveCommand<string, Unit> OpenInWebCommand  { get; }
```

**Requirements (spec §5, §7, §11):**
- `RequestStop`: per-id gating — if id already in flight, no-op; else add to set, `ops.StopAgentAsync(id, force: false, shutdownToken)` on the pool. Result mapping: `stopped` → nothing; `failed` → `Notify($"Couldn't stop {label}")`; `skipped` → `Notify($"The daemon declined to stop {label}")`; `error` → `Notify(result.Error!)` (daemon text verbatim). `LocalControlOpsException` → `daemon_unreachable` → `Notify("The daemon is not reachable")`, else `Notify($"Couldn't stop {label}: {e.Message}")`. OCE → quiet. Always remove from set (finally). Completion into a vanished row/entry is naturally a no-op.
- `OpenInWeb`: URL = `{ServerUrl.TrimEnd('/')}/agents/{Uri.EscapeDataString(agentId)}` from the LATEST snapshot's `Daemon.ServerUrl` (hold it via a subscription; if none yet, `Notify("Not connected to a daemon yet")` — cannot happen from live UI but the method must not throw). Opener exception → `Notify($"Couldn't open the browser: {e.Message}")`.
- `TrayViewModel`: combine `StopsInFlight` into the `MenuModel` projection — `TrayAgentEntry.StopEnabled = !inFlight.Contains(id)`. Commands delegate to the service (label = the entry's label).
- No local cache mutation anywhere — visible status changes come only from snapshots (spec §7).

**Steps:**

- [ ] **Step 1: Write failing tests.** `AgentActionServiceTests` (plain C#, `ScriptedOps` fake reused from Task 3, recording fake notifier + opener): stop result mapping (all four statuses + both exception reasons + OCE-quiet), per-id gating (second request while first held → one op), different ids concurrent (two ops in flight), in-flight set add/remove observable, URL exact-match test `https://x.kcap.ai/agents/a%2Fb` from `ServerUrl: "https://x.kcap.ai/"` + id `a/b`, opener-throw → banner. `TrayViewModelTests` additions: entry `StopEnabled` flips while id in flight; commands reach the service.
- [ ] **Step 2: Fail. Step 3: Implement. Step 4: Green + full app suite. Step 5: Commit** (`feat: agent stop and open-in-web actions with banner surfacing`).

---

### Task 5: Main-window Agents grid + banner

**Files:**
- Create: `src/Capacitor.App/ViewModels/AgentRowViewModel.cs`
- Create: `src/Capacitor.App/UptimeFormat.cs` (namespace `Capacitor.App`)
- Modify: `src/Capacitor.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/Capacitor.App/Views/MainWindow.axaml` (+ `.cs` only if new named controls need asserts)
- Test: `test/Capacitor.App.Tests.Unit/AgentGridTests.cs`, additions to `MainWindowSmokeTests.cs`

**Interfaces produced:**
```csharp
public static class UptimeFormat {
    // <1m → "42s"; <1h → "4m"; <1d → "2h 13m"; ≥1d → "3d 4h" (spec §8). Negative input clamps to "0s".
    public static string Format(TimeSpan uptime);
}

public sealed class AgentRowViewModel : ReactiveObject {
    public AgentRowViewModel(AgentStatusDto dto, AgentActionService actions,
                             IObservable<long> ticker, TimeProvider time, IObservable<bool> connected,
                             IObservable<IReadOnlySet<string>> stopsInFlight);
    public string Id, Kind, VendorDisplay, RepoLeaf, RepoFull, Requester, StatusText { get; } // static per DTO revision
    public string Uptime { get; }        // OAPH, recomputed on ticker
    public bool ActionsEnabled { get; }  // OAPH: connected && !inFlight(Id)
    public ReactiveCommand<Unit, Unit> StopCommand, OpenInWebCommand { get; }
}
// MainWindowViewModel ctor CHANGES to:
public MainWindowViewModel(IDaemonClientService service, AgentActionService actions,
                           IAppNotifier notifier, CancellationToken shutdownToken,
                           TimeProvider? time = null)
// and gains:
public ReadOnlyObservableCollection<AgentRowViewModel> Agents { get; }
public string? Banner { get; }        // OAPH; auto-clears 6s after the LAST message (latest-wins)
public bool GridEnabled { get; }      // OAPH: attach state == Connected
```

**Requirements (spec §8, §11):**
- Presentation: `Requester` null → `unknown`; `RepoLeaf` = last path segment (same helper as Task 2 — extract `RepoLabel.Leaf(string?)` into `TrayModels.cs` and reuse), `RepoFull` = full path for the tooltip, leaf `—` when null; `VendorDisplay` = `Vendor` or `Vendor (Model)` when `Model` non-null; `StatusText` = `Status` verbatim.
- Rows: `service.Agents.Connect()` → `Transform` to row VMs → `SortAndBind` comparer `CreatedAt` asc then `Id` `StringComparer.Ordinal` → `ObserveOn(RxSchedulers.MainThreadScheduler)` (DynamicData: `ObserveOn` BEFORE `Bind`). Rows persist across disconnects (cache is retained); `GridEnabled=false` disables actions and the XAML dims the list (`Opacity 0.5` on the ItemsControl via a bound style).
- ONE shared ticker for all rows: `Observable.Interval(TimeSpan.FromSeconds(1), RxSchedulers.MainThreadScheduler).StartWith(0L)` created in `MainWindowViewModel`, passed to rows. Uptime = `time.GetUtcNow().UtcDateTime - DateTime.SpecifyKind(dto.CreatedAt, DateTimeKind.Utc)` (treat wire value as UTC, spec §8).
- Banner: subscribe `notifier.Messages` `ObserveOn` main; each message sets `Banner` and (re)arms a 6s clear (`Observable.Timer(6s, RxSchedulers.MainThreadScheduler)` per message with `Switch()` — latest-wins single slot; a new message during the window replaces text AND restarts the 6s).
- XAML: keep the existing status panel; below it a `Border` banner (`IsVisible="{Binding Banner, Converter={x:Static ObjectConverters.IsNotNull}}"`) then an Agents `ItemsControl` with a header `Grid` (columns: Kind, Vendor, Repo, Requester, Status, Uptime, actions) and a row `DataTemplate` mirroring the columns; Repo cell sets `ToolTip.Tip="{Binding RepoFull}"`; per-row `Stop` + `Open in web` buttons bound to the row commands; empty-state `TextBlock "No agents running"` visible when the collection is empty AND `GridEnabled`. Column widths: `Grid` with shared-size scope is fine; no DataGrid package.
- Composition wiring in `App.axaml.cs` `StartAsync` changes ctor args — Task 7 finalizes composition; in THIS task update `BuildAndShowMainWindow` and every existing test constructing `MainWindowViewModel` (they get `AgentActionService` with fakes and a real `AppNotifier`).

**Steps:**

- [ ] **Step 1: Failing tests.** `UptimeFormat` boundary table (59s/60s/59m59s/1h/23h59m/24h/25h→"1d 1h") as a parameterized test; `AgentGridTests`: projection fields (incl. `unknown`, `—`, `vendor (model)`), sort order, rows-follow-EditDiff (remove agent from fake cache → row leaves), `ActionsEnabled` false when disconnected and while its id is stopping, uptime text advances when a `TestScheduler`-free tick is simulated (drive the ticker via a `Subject<long>` passed as the ticker — the ctor takes `IObservable<long>`), banner latest-wins + 6s expiry via fake `TimeProvider`? — NO: the banner timer runs on the Rx scheduler; under `WithImmediateRxScheduler` a 6s timer fires immediately, which breaks the test. Instead pass the banner-clear delay as an internal seam (`internal TimeSpan BannerLifetime = TimeSpan.FromSeconds(6);`) and in tests set it to `TimeSpan.Zero` for the expiry case / assert text-replacement for latest-wins with the default. `MainWindowSmokeTests`: window builds with the new ctor, empty state visible.
- [ ] **Step 2: Fail. Step 3: Implement (VM + XAML). Step 4: Green + full app suite. Step 5: Commit** (`feat: agents grid with uptime, actions and transient banner`).

---

### Task 6: Tray icon renderer + native-menu adapter

**Files:**
- Create: `src/Capacitor.App/Views/TrayIconRenderer.cs`
- Create: `src/Capacitor.App/Views/TrayMenuBuilder.cs`
- Create: `src/Capacitor.App/Views/TrayMenuSync.cs`
- Create: `src/Capacitor.App/Views/TrayIconManager.cs`
- Test: `test/Capacitor.App.Tests.Unit/TrayAdapterTests.cs`

**Interfaces produced:**
```csharp
public static class TrayIconRenderer {
    // Cached per (state, cappedCount). Running draws the count (cap "9+") onto the glyph.
    public static WindowIcon Get(TrayState state, int count);
    internal static string CountBadge(int count); // "1".."9", "9+" — pure, tested
}
public sealed class TrayMenuBuilder {
    public TrayMenuBuilder(TrayViewModel vm);
    public void Rebuild(NativeMenu menu, TrayMenuModel model); // clears + repopulates Items
}
/// The open/dirty state machine (spec §5 rebuild cadence) — pure, no Avalonia types.
public sealed class TrayMenuSync {
    public void OnModelChanged(TrayMenuModel model);
    public void OnNeedsUpdate(Action<TrayMenuModel> rebuild); // invokes rebuild(latest) iff dirty; clears dirty
    public bool Dirty { get; }
}
public sealed class TrayIconManager : IDisposable {
    public TrayIconManager(Application app, TrayViewModel vm); // creates TrayIcon, subscribes MenuModel
    public void Dispose();                                     // detaches + disposes the TrayIcon
}
```

**Requirements (spec §4 icon, §5 cadence, §6 capture rule):**
- Renderer: draw programmatically into a `RenderTargetBitmap` (32×32 px, i.e. 2x for a 16pt item) from `StreamGeometry` glyph resources defined in a new `Assets/TrayGlyphs.axaml` `ResourceDictionary` merged in `App.axaml` — five keys `TrayGlyphStopped|Connecting|Idle|Running|Attention`. Monochrome mid-gray `#808080` (readable on light and dark menu bars — manual macOS verification decides; the pinned fallback if the count overlay misrenders is glyph-only Running + count in the header, a renderer-local change). For Running, draw `CountBadge(count)` with `FormattedText` bottom-right. Keep glyph shapes trivial: circle outline (Stopped), half-filled circle (Connecting), filled circle (Idle/Running), triangle-bang (Attention).
- Builder: header = disabled `NativeMenuItem` (`IsEnabled = false`) with the model header text; separator; per agent a `NativeMenuItem` with a `Menu` (submenu) containing `Stop` (`Command = vm.StopAgentCommand`, `CommandParameter = entry.Id`, `IsEnabled = entry.StopEnabled`) and `Open in web` (`vm.OpenInWebCommand`, id param) — agent section only when the model has entries; separator; `Pause new launches` (`ToggleType = NativeMenuItemToggleType.CheckBox`, `IsChecked = model.Pause.Checked`, `IsEnabled = model.Pause.Enabled`, `Command = vm.TogglePauseCommand`, **`CommandParameter = !model.Pause.Checked`** — the frozen desired value, spec §6: the click handler must NEVER read `IsChecked` because Avalonia's native click path does not mutate it); `Open Kurrent Capacitor` (command injected via `TrayViewModel` — see Task 7's `OpenMainWindowCommand`/`QuitCommand` note below); separator; `Quit`.
  - This task adds the two remaining commands to `TrayViewModel` as injected delegates with no-op defaults: `public ReactiveCommand<Unit, Unit> OpenMainWindowCommand { get; }`, `QuitCommand` — ctor gains optional `Action? openMainWindow = null, Action? quit = null`; Task 7 supplies the real ones.
- Sync: `OnModelChanged` stores latest + sets dirty; `OnNeedsUpdate` rebuilds from latest iff dirty. (While the native menu is open the OS shows a static snapshot; our rebuild happens on the NEXT `NeedsUpdate` — that's the whole state machine.)
- Manager: `new TrayIcon { Icon = TrayIconRenderer.Get(...), Menu = menu, ToolTipText = "Kurrent Capacitor" }`; register via `TrayIcon.SetIcons(app, new TrayIcons { trayIcon })`; subscribe `vm.MenuModel` (already on the UI scheduler): icon updates IMMEDIATELY on change, menu content goes through `TrayMenuSync`; wire `menu.NeedsUpdate` → `sync.OnNeedsUpdate(m => builder.Rebuild(menu, m))` and the menu's opening event → `vm.RequestPauseRefresh()`. **Verify the exact member names (`NeedsUpdate`, `Opening`/`Оpening` equivalent, `TrayIcons`, `SetIcons`) against the installed Avalonia 12.1.1 binaries before coding — decompile, do not guess.** `Dispose` → `TrayIcon.SetIcons(app, null)` (or remove from the collection) + `trayIcon.Dispose()`.

**Steps:**

- [ ] **Step 1: Failing tests** (headless — `NativeMenu`/`NativeMenuItem` are plain Avalonia objects, constructible under the headless session): `CountBadge` (0→"0" is unreachable but define "0"; 1→"1"; 9→"9"; 10→"9+"); `TrayMenuSync` state machine (change→dirty; NeedsUpdate rebuilds once + clears; NeedsUpdate w/o change → no rebuild; change-while-open → rebuild only at next NeedsUpdate); `TrayMenuBuilder` structure asserts on a built `NativeMenu`: header disabled + text, agent submenu items with correct `CommandParameter`s and `IsEnabled`, pause item `ToggleType`/`IsChecked`/**`CommandParameter == !Checked` for BOTH checked states** (the §12 adapter capture test: unchecked item dispatches `true`, checked dispatches `false`), quit/open items present, no agent section when empty. Renderer `Get` returns non-null and caches (same reference for same key) — bitmap pixels are manual macOS verification.
- [ ] **Step 2: Fail. Step 3: Implement (verify Avalonia API names first — decompile `Avalonia.Controls` from the NuGet cache). Step 4: Green + full app suite. Step 5: Commit** (`feat: tray icon and native menu adapter`).

---

### Task 7: Lifecycle — hide-to-tray, coordinator, composition, disposal

**Files:**
- Create: `src/Capacitor.App/Services/MainWindowCoordinator.cs`
- Modify: `src/Capacitor.App/App.axaml.cs`
- Modify: `src/Capacitor.App/Views/MainWindow.axaml.cs` (Closing interception)
- Test: additions to `test/Capacitor.App.Tests.Unit/AppStartupTests.cs`

**Interfaces produced:**
```csharp
public sealed class MainWindowCoordinator {
    public MainWindowCoordinator(Func<MainWindow> windowFactory);
    public bool QuitInProgress { get; set; }
    public void ShowMainWindow();                 // re-shows the hidden window or builds a fresh one
    public bool OnWindowClosing();                // returns true → cancel close + Hide (called from MainWindow.Closing)
}
```

**Requirements (spec §9):**
- `ShutdownMode.OnExplicitShutdown` is set ONCE in `OnFrameworkInitializationCompleted` (desktop branch), before `StartAsync` — the steady-state mode. The startup-error path's own pin stays (harmless, self-documenting).
- `MainWindow.Closing` → `if (coordinator.OnWindowClosing()) { e.Cancel = true; }`; `OnWindowClosing` returns `!QuitInProgress` and calls `Hide()` on the tracked window when cancelling. `ShowMainWindow` on a hidden window calls `Show()` + `Activate()`; after a window was truly closed (quit path only) a fresh one is built from the factory — no view state to lose (spec §9).
- `App.OnShutdownRequested` FIRST pass (before `e.Cancel = true`): set `coordinator.QuitInProgress = true` — so the second pass's real teardown is never cancelled by the Closing handler.
- Composition in `StartAsync` success path (after `service.Start()`): `ops = new LocalControlOps(service.DaemonName)`; `notifier = new AppNotifier()`; `pause = new PauseController(ops, notifier.Notify, _shutdown.Token)`; `actions = new AgentActionService(ops, notifier, new ShellUrlOpener(), service.Snapshots, _shutdown.Token)`; `coordinator = new MainWindowCoordinator(() => BuildMainWindow(service, actions, notifier, _shutdown.Token))` (refactor of `BuildAndShowMainWindow` — still Shows); `trayVm = new TrayViewModel(service, pause, actions, openMainWindow: coordinator.ShowMainWindow, quit: () => desktop.TryShutdown())`; `_tray = new TrayIconManager(this, trayVm)`. Startup FAILURE path: unchanged — no tray is ever created there (tray creation is the last step of the success path).
- Disposal: in `DisposeAndShutdownAsync`, dispose `_tray` (and `trayVm`/`pause` as `IDisposable`s) BEFORE the service-dispose/`TryShutdown` helper runs — extend the existing flow without changing `DisposeAndConfirmShutdownAsync`'s signature: dispose the tray synchronously at the top of `DisposeAndShutdownAsync` (UI thread), then proceed exactly as today. Quit never strands a menu-bar icon.
- Cmd+Q and tray-Quit both arrive as `TryShutdown()` → the existing deferred machine — no new shutdown path.

**Steps:**

- [ ] **Step 1: Failing tests** in `AppStartupTests` (existing `FakeClassicDesktopLifetime` + DispatchProxy harness): `Close_hides_window` (coordinator with QuitInProgress false → `OnWindowClosing` true and window hidden — use a real `Window` under headless); `Quit_lets_close_through` (QuitInProgress true → false, window not hidden); `ShowMainWindow_reshows_same_instance` (hide then show → same reference, visible); `Startup_failure_creates_no_tray` (drive `HandleStartupFailureAsync` — assert no `TrayIcon` attached to the app / the manager field is null); `Tray_disposed_before_confirm` (spy `IDisposable` order via a small seam: `DisposeAndShutdownAsync` calls tray-dispose then the confirm helper — assert call order with a recording list).
- [ ] **Step 2: Fail. Step 3: Implement.** Careful in `App.axaml.cs`: `_exitCode`/`_shutdownStarted`/`_shutdownConfirmed` semantics and the startup-failure ordering comments are load-bearing — extend, don't restructure.
- [ ] **Step 4: Green; FULL test run** (app + cli unit + integration untouched but run cli unit anyway), AOT publish grep, `dotnet build` the solution.
- [ ] **Step 5: Manual macOS acceptance** (human partner, per spec §12): five tray states + count overlay, light/dark menu bar, menu interaction incl. updates-while-open, pause toggle against `kcap daemon consent show`, real stop, deep links, hide-to-tray/quit/Cmd+Q, startup-failure window. Record outcomes in the PR description.
- [ ] **Step 6: Commit** (`feat: hide-to-tray lifecycle and tray composition`).

---

## Self-review notes (writing-plans checklist)

- **Spec coverage:** §4 → T2/T6; §5 → T2 (model) + T6 (adapter/cadence); §6 → T3 (+T6 capture rule); §7 → T1 (wire) + T4 (UX); §8 → T5; §9 → T7; §10 → T1; §11 → T4 (notifier) + T5 (banner); §12 mapped test-by-test into the task test lists; §2 decision 3's engine acceptance test → T1 step 5.
- **Type consistency:** `IPauseController`/`PauseState` defined T2, implemented T3; `TrayAgentEntry.StopEnabled` defined T2 (constant true), wired T4; `AgentActionService` defined T4, consumed T5/T7; `TrayViewModel` ctor grows across T2→T4→T6 — each task states its final signature; `MainWindowViewModel` ctor change is owned by T5 including updating existing tests.
- **Known judgment points for implementers:** exact Avalonia member names in T6 must be decompiler-verified (the plan says so); the `ConsentRulesGet` frame construction in T1 mirrors the existing CLI consent command.
