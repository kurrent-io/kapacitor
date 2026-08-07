# Tray presence, agents list, and stop — desktop supervisor slice 2 (AI-1651)

Linear: AI-1651 (parent AI-1622). Umbrella spec:
[2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §6.
Builds directly on the AI-1650 app shell
([2026-08-04-ai1650-app-shell-design.md](2026-08-04-ai1650-app-shell-design.md)) and the AI-1649
supervision IPC. One PR.

## 1. Problem & goal

The app shell attaches to the daemon and shows a status panel, but the supervisor's actual value —
ambient visibility and control — doesn't exist yet: no menu-bar presence, no agent list, no way to
stop an agent or pause launches without a terminal. This slice adds the tray icon with a native
menu, a real Agents area in the main window, per-agent Stop and web deep links, and a
pause-new-launches toggle.

## 2. Decisions (settled in brainstorming, 2026-08-06)

1. **Native menu, not a custom popover.** Avalonia `TrayIcon` + `NativeMenu` (NSStatusItem menu on
   macOS): native look, dismissal, and keyboard access for free. A custom popover window (own
   positioning, focus-loss dismissal, multi-display) is noted as a possible future upgrade, not
   built. Rich, live-updating UI lives in the main window.
2. **Hide-to-tray lifecycle.** Closing the main window hides it; the app lives in the tray
   (`ShutdownMode.OnExplicitShutdown` becomes the steady-state mode). Quit is explicit — tray menu
   item or Cmd+Q — and runs the existing deferred-dispose shutdown machine. Close and quit are
   deliberately different verbs (Docker Desktop / Slack pattern).
3. **Pause = wildcard deny rule, not a default flip.** The issue text says pause "flips the consent
   default to deny, and back", but "back" requires the app to remember the prior default across
   restarts and races with CLI edits. Instead, pause inserts `{action: "deny"}` with all four
   matchers null (a wildcard — `LaunchConsentRule` doc: null field = wildcard) at `rules[0]`;
   unpause removes exactly that rule. Rules are evaluated first-match-wins before the default
   (`LaunchConsentEngine.Evaluate`), so this denies every **non-owner** server-driven launch while
   it exists (owner exemption below). The
   rule IS the state: the default is untouched, restore is correct by construction, the toggle
   survives app restarts, and `kcap daemon consent show` tells the truth. This is a deliberate
   deviation from the issue's letter.
   **Owner exemption:** `LaunchConsentEngine.Evaluate` admits an owner-originated launch
   (verdict Allow, source `owner`) before consulting rules or the default — owner-always-allowed
   is a consent-engine invariant (umbrella §10 matrix). Pause therefore stops **non-owner**
   server-driven launches only. The issue's default-flip wording has the identical property (the
   owner check precedes the default too), so the rule mechanism changes nothing here; §6 pins the
   behavior and its acceptance test. Local-socket launches (`kcap agent start`) never consult the
   gate at all.
4. **VM-owned tray state, thin native adapter.** `TrayViewModel` projects everything
   decision-shaped (state, menu model, commands) from `IDaemonClientService`; a dumb
   `TrayIconManager` renders it into `TrayIcon`/`NativeMenu`. All logic is headless-testable; only
   the adapter needs the manual macOS verification the issue budgets.
5. **Stop is graceful-only in the app this slice** (`StopV2 force:false`). The daemon refuses to
   stop a protected review/flow participant without force, replying an `Error` frame whose text is
   display-quality; the app surfaces that text verbatim and does not offer a force path (CLI
   escape hatch: `kcap agent stop --force <id>`). A confirm-then-force UX is deferred.

## 3. Scope & non-goals

In scope: tray icon + native menu, tray state machine, pause toggle, per-agent Stop and
open-in-web (tray + main window), main-window Agents grid, hide-to-tray lifecycle, `Capacitor.Cli.Core`
one-shot IPC ops, transient error banner, headless VM tests + scripted-server ops tests.

Non-goals (explicitly out):

- Consent prompt window, notifications, Activity feed, pending-consent attention state — AI-1652.
  (The tray state enum leaves room; this slice does not subscribe to `ConsentSubscribe`.)
- Onboarding, distribution, dock-icon hiding (`LSUIElement` is an app-bundle concern) — AI-1653.
- Settings surfaces — AI-1654.
- Force-stop UX in the app (decision 5).
- Migrating the CLI's `AgentCommand` onto the new Core ops — it keeps its own socket code.
- `ClientHelloDto` self-identification for the app's subscribe connection — remains a ledgered
  nicety (AI-1657 cross-cutting).
- Windows/Linux tray polish. The code path is cross-platform by construction (Avalonia `TrayIcon`),
  but only macOS is manually verified this slice.

## 4. Tray state machine & icon

`TrayState` is a pure projection of the latest `AttachStatus` and (when connected) the latest
`DaemonStatusDto`, computed in `TrayViewModel`:

| Precedence | Condition | State |
|---|---|---|
| 1 | `Unreachable` + reason `daemon_unreachable` | **Stopped** |
| 2 | `Unreachable` + reason `daemon_incompatible` | **Attention** |
| 3 | `Connecting` | **Connecting** |
| 4 | `Connected`, `daemon.connection == "connecting"` | **Connecting** |
| 5 | `Connected`, `daemon.connection` ∈ `reconnecting`, `disconnected` | **Attention** |
| 6 | `Connected`, `daemon.connection == "connected"`, `active_agents < 0` (malformed count — `DaemonStatusValidator` does not reject it, and this slice deliberately leaves that pinned validator untouched) | **Attention** (conservative) |
| 7 | `Connected`, `daemon.connection == "connected"`, `active_agents == 0` | **Idle** |
| 8 | `Connected`, `daemon.connection == "connected"`, `active_agents > 0` | **Running(n)**, n = `active_agents` |
| 9 | `Connected`, any other `connection` value (future daemon) | **Attention** (conservative) |
| 10 | `Unreachable`, any other reason (future client — today the client pins exactly two) | **Attention** (conservative) |

The projection is total: rows 6 and 9–10 make every input the client can deliver map to a state,
and §5 gives those fallback rows a neutral header. `AttachStatus` precedence is absolute: a retained stale snapshot
never produces Idle/Running while the client is Connecting or Unreachable. `n` is `active_agents` — the display count derived
server-side from the same array (never treated as launch capacity). Between `Connected` and the
first snapshot there is no gap: the AI-1650 client only reports Connected together with a valid
first snapshot.

**Icon.** A single brand-mark base (the product mark, `Assets/kcap-icon.png`, 32px) with a small
state overlay in the bottom-right corner (~12px), rendered via a `TrayIconRenderer` that returns a
`WindowIcon` for `(state, count)` with per-value caching — not one glyph asset per state. Running
overlays `CountBadge(count)` on a filled dark-green circle (legible against the burgundy mark,
`9+` cap); every other state overlays a plain color dot from the same status-dot palette
MainWindow's status line uses (`StatusColors`), so the window and the tray icon can never disagree
about what a color means. Avalonia does not expose NSImage template-image behavior, so light/dark
menu-bar contrast and the dynamic overlay bitmap are exactly the "known Avalonia platform
variance" the issue assigns to manual macOS verification. **Pinned fallback** if the overlay
misrenders: glyph-only base (brand mark, no overlay), count moves to the menu header line — a
one-line change in the renderer, not a redesign.

## 5. Tray menu & adapter

The menu is projected from a plain menu model (`TrayViewModel`) and rebuilt by `TrayIconManager`
at menu-display time (`NativeMenu` has no ItemsSource; full rebuild is cheap at these sizes — see
the rebuild cadence below).

```
{daemonName}: connected — 2 agents running      (disabled header line)
────────────
review-flow · codex · kcap-cli             ▸    Stop
                                                Open in web
agent · claude · kcap-server               ▸    Stop
                                                Open in web
────────────
Pause new launches                              (checkable; enabled iff consent/1)
Open Kurrent Capacitor
────────────
Quit
```

- **Header copy per state** (prefix `{daemonName}: ` except incompatible): Stopped → `not running`;
  Connecting → `connecting…`; Attention/skew → the existing neutral copy ("app and daemon are
  incompatible — make sure both are up to date", no daemon-name prefix); Attention/server-link →
  `reconnecting to server` / `disconnected from server`; Idle → `connected — no agents`;
  Running → `connected — {n} agent(s) running`; any other Attention case (§4 rows 6 and 9–10) →
  `needs attention` (neutral fallback).
- **Agent entries** appear only while `Connected`; they are the agents whose `status` is
  `Starting` or `Running` (same predicate as `active_agents`), labeled
  `{kind} · {vendor} · {repo last path segment}` (`—` when `repo_path` null), ordered `created_at`
  asc, id ordinal tiebreak (the wire order pin). Requester stays in the main window.
- **Stop** sends `StopV2 force:false` for that id; the entry's Stop item disables while in flight.
- **Open in web** opens `{ServerUrl trimmed of trailing /}/agents/{Uri.EscapeDataString(id)}` in
  the default browser (`Process.Start` with `UseShellExecute` — via an injected opener seam for
  tests). An opener exception surfaces in the banner (§11).
- **Pause new launches** — §6. Disabled when the connected daemon's capabilities lack
  `consent/1`, while not `Connected`, while a toggle operation is in flight, and while the state
  is unverified (§6); the checkmark always shows the last successfully fetched state.
- **Open Kurrent Capacitor** shows/creates the main window (§9). **Quit** calls
  `desktop.TryShutdown()`, entering the existing deferred shutdown pass.
- **Rebuild cadence.** `NativeMenu.NeedsUpdate` — the synchronous pre-display hook (Avalonia's
  docs forbid mutating the menu from `Opening`) — is the ONLY place the adapter rebuilds menu
  items, from the cached menu model. Model changes (snapshots, op completions, §6 refresh
  results) update the cache and set a dirty flag consumed at the next `NeedsUpdate`; a change
  arriving while the menu is open becomes visible at the next open, never mid-display. The tray
  **icon** (glyph + count) is not part of the menu and updates immediately on model change.
  `NeedsUpdate` also fire-and-forgets the §6 pause-state refresh, kicked immediately before the
  rebuild it triggers (macOS status-item menus never raise `Opening` at all — found in manual
  acceptance — so `NeedsUpdate` is the only pre-display hook that reliably fires) — the refresh
  starts async work only and never touches the menu itself, and it is subject to §6's
  serialization (dropped while a consent op is in flight). The adapter's open/dirty tracking is a
  small testable state machine; manual macOS acceptance includes updates arriving while the menu
  is open.

## 6. Pause-launches toggle

The pause rule is exactly `ConsentRuleDto("deny", null, null, null, null)` at index 0.

- **Semantics:** pause stops non-owner server-driven launches (§2 decision 3 — the engine's
  owner exemption precedes rules and default alike). Acceptance test: a policy with the pause
  rule at index 0 still allows a `RequesterIsOwner` input (engine-level, alongside the existing
  consent-engine tests).
- **Displayed state:** checked iff the latest fetched policy has an all-wildcard deny rule at
  index 0. An all-wildcard deny at any other index, or narrower deny rules, do not check the
  toggle — `kcap daemon consent show` is the full truth. A passive refresh (`ConsentRulesGet`)
  is kicked fire-and-forget from the menu's `NeedsUpdate` event (macOS status-item menus never
  raise `Opening`, so `NeedsUpdate` — the pre-display hook that does fire — is the kick site
  instead), as the trailing step of every toggle, and once more, edge-triggered, on the VM's own
  attach-state transition into `Connected` (so the toggle is usually verified before the first
  menu open rather than the second); its result lands in the cached model, so it becomes visible
  at the next menu open (§5 rebuild cadence). Accepted staleness: the checkmark reflects the policy as of the most
  recent **completed** refresh; a CLI-side edit becomes visible on the open after the next
  successfully started-and-completed refresh — usually one open behind, but more under rapid
  reopen or a busy lane (drops, §Serialization). A refresh failure keeps the last-known
  checkmark but marks the state **unverified**,
  which disables the item (§5) until a later refresh succeeds; passive refresh failures log to
  stderr, no banner.
- **Serialization (one lane for ALL consent-policy operations):** passive refreshes and toggle
  operations (Get → modify → Put → trailing refresh Get) share a single single-flight lane — at
  most one consent socket operation is in flight at any time, so results apply in start order
  and an older read can never overwrite a newer write's outcome (each op is a one-shot
  connection whose result cannot arrive after the op returns). A passive refresh requested
  while the lane is busy is **dropped**, not queued — a busy toggle ends in its own trailing
  refresh, and a busy passive refresh IS the refresh. The inverse direction — a toggle clicked
  while a passive refresh owns the lane, which is possible because the open menu is frozen and
  cannot disable the item mid-display — uses the lane's **one-slot queue, reserved exclusively
  for a user toggle**: the toggle is marked in-flight immediately (further clicks are ignored
  per single-flight) and runs exactly once when the passive op completes (success or failure).
  The slot stores a **desired checked value**, not a generic inversion — and the adapter pins
  how it is captured: Avalonia's native click path (`RaiseClicked`) never mutates
  `NativeMenuItem.IsChecked`, so reading the item inside the handler yields the pre-click value.
  The adapter therefore computes `desired = !displayedChecked` **at menu-rebuild time** and
  freezes it into the item's command parameter; the click handler dispatches that frozen value
  and never reads `IsChecked`. The queued operation applies pause/unpause toward the desired
  state against its own fresh Get, which may make it an idempotent no-op (the pause rule
  appeared or vanished externally while the passive read was held) — it never produces the
  opposite of what the user selected. A toggle while a toggle owns the lane is ignored
  (clicks-while-disabled rule; the slot never holds more than one). The toggle item is disabled
  while a toggle operation runs or is queued; it re-enables when the trailing refresh
  completes. If the Put's outcome is ambiguous (transport failure or timeout after send) and
  the trailing refresh also fails, the state is unverified as above.
- **Pause:** `ConsentRulesGet` → if the pause rule is already at index 0, no-op (idempotent);
  otherwise insert it at index 0 → `ConsentRulesPut` with the full policy (`default` and
  `prompt_timeout_seconds` passed through unchanged).
- **Unpause:** `ConsentRulesGet` → remove the index-0 all-wildcard deny if present (only that one
  rule) → `ConsentRulesPut`.
- **Concurrency:** `ConsentRulesPut` replaces the whole policy; a CLI edit between Get and Put is
  lost (last-write-wins). Accepted — the daemon-side store is the single writer and the window is
  sub-second; noted, not mitigated.
- **Failure:** a `ConsentAck` with `ok:false` (or transport failure) surfaces its error in the
  banner (§11) — neutral fallback copy ("the daemon rejected the change") when `error` is
  null/empty. The checkmark then follows the conditional contract above: a **successful**
  trailing refresh reconciles it with the daemon's actual state; a failed refresh preserves the
  last-known display and disables the item as unverified — safe, and honest about being
  unverified, rather than claimed-current.

## 7. Stop & open-in-web semantics

One code path for both surfaces (tray menu item, main-window row button):

- Send `StopV2(force:false, id)` via `LocalControlOps` (§10). Reply timeout is long (§10) because
  the daemon acks only after the stop completes — graceful wait plus terminate can take ~25s.
- Reply handling: `StopAck` line `{id}\tstopped` → success, no banner (the agent's disappearance
  from the next snapshot is the confirmation); `{id}\tfailed` → banner "Couldn't stop {label}";
  `{id}\tskipped` → banner "The daemon declined to stop {label}" (not expected on the per-id
  path today — protection refusals are `Error` frames — but `skipped` is StopAck vocabulary, so
  it gets defined presentation rather than `unexpected_reply`); `Error` frame (protected agent,
  unknown id) → banner with the daemon's text verbatim (it is display-quality and already names
  the `--force` escape hatch); transport failure → §10 classification into the banner.
- **Observable sequence** (pinned to the daemon's actual behavior — there is no `Stopping`
  status): `StopAgentCoreAsync` sets the agent's status to `Completed` **before** the graceful
  wait, so the next snapshot already shows `Completed` — the agent leaves the Starting/Running
  predicate, the tray entry disappears, and the count drops **before** `StopAck` lands. The main
  grid's row shows `Completed` until the agent leaves the snapshot entirely, then disappears. The
  app never fakes a status; everything visible comes from snapshots.
- In-flight gating is per agent id (a second Stop for the same id no-ops while one is pending;
  different ids run concurrently). Gating is keyed by id, so the tray entry or grid row
  vanishing mid-flight is harmless; the pending op just completes into a no-longer-rendered row.
- Open-in-web from a row uses the same link builder as the tray.

## 8. Main window Agents area

The status panel gains an **Agents** grid bound to the existing
`SourceCache<AgentStatusDto, string>` (DynamicData `Connect` → transform to row VMs → bind),
sorted `created_at` asc, id ordinal tiebreak.

- **Columns:** Kind · Vendor · Repo · Requester · Status · Uptime · actions (Stop, Open in web).
  Status (verbatim daemon string, opaque display text) is an addition beyond the issue's minimum
  list — it is how the daemon's actual stop sequence becomes visible (`Completed` at stop
  initiation, §7; there is no `Stopping` status).
- **Presentation rules:** `requester` null → `unknown` (the DTO doc assigns this to presentation);
  `repo_path` → last path segment, full path as tooltip, `—` when null; `model` appended to the
  vendor cell as `vendor (model)` when non-null.
- **Uptime:** `now − created_at` (`created_at` treated as UTC), formatted compactly: `42s` under a
  minute, `4m` under an hour, `2h 13m` under a day, `3d 4h` above. One shared 1-second ticker
  (`Observable.Timer` on `RxSchedulers.MainThreadScheduler`) drives all rows; tests pin formatting
  through a `TimeProvider` seam and the existing immediate-scheduler swap.
- **Cache staleness:** rows stay visible when not `Connected` (the cache is retained by design),
  but Stop/Open-in-web actions disable and the grid dims; the existing status header already says
  why. The grid shows "No agents running" when the cache is empty.
- Rows disappear when the agent leaves the snapshot (EditDiff) — no local removal on stop.

## 9. App lifecycle changes

- `ShutdownMode.OnExplicitShutdown` is set once at startup (framework-init), replacing the
  error-path-only pin. The startup-error window's explicit `Shutdown(1)` path is unchanged and
  loses an edge case (its mode pin becomes redundant but stays — harmless and self-documenting).
- A `MainWindowCoordinator` service owns the main window: `ShowMainWindow()` re-shows the hidden
  window or builds a fresh one from the live service (no state lives in the view);
  `QuitInProgress` gates close interception. The window's `Closing` handler cancels-and-hides
  while `QuitInProgress` is false; `App.OnShutdownRequested` sets `QuitInProgress = true` on its
  first (deferring) pass, so the second pass's real window teardown is never cancelled.
- On normal startup the main window opens as today, and the tray icon is created in the same
  success path (`StartAsync`, after the service starts). On startup failure no tray icon exists —
  the error window is the only surface.
- The tray icon is disposed during the deferred shutdown pass, before `TryShutdown(exitCode)`, so
  quit never strands a dead icon in the menu bar. Quit from the tray and Cmd+Q both route through
  `TryShutdown()` → the existing `OnShutdownRequested` machine, unchanged.
- macOS dock icon behavior (hide when windowless) is explicitly not addressed (§3).

## 10. Core one-shot ops (`Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs`)

Three one-shot operations, each on a fresh socket connection (mirroring the CLI's existing frame
usage), behind an interface for VM tests:

```csharp
public interface ILocalControlOps {
    Task<StopAgentResult>   StopAgentAsync(string agentId, bool force, CancellationToken ct);
    Task<ConsentPolicyDto>  GetConsentPolicyAsync(CancellationToken ct);
    Task<ConsentAckDto>     PutConsentPolicyAsync(ConsentPolicyDto policy, CancellationToken ct);
}

public sealed class LocalControlOps(string daemonName, TimeProvider? time = null) : ILocalControlOps;

/// Status: "stopped" | "failed" | "skipped" (from StopAck), or the terminal outcome below.
public sealed record StopAgentResult(bool Ok, string Status, string? Error);
```

- **Wire:** `StopAgentAsync` writes `FrameCodec.StopV2(force, agentId)`, expects `StopAck`
  (parses the `{id}\t{status}` line for the requested id) or `Error` (returned as
  `StopAgentResult(false, "error", text)`). Consent ops write `ConsentRulesGet` /
  `ConsentRulesPut` and expect `ConsentRules` / `ConsentAck`. Hello-less, like the CLI's stop
  path — the app already knows capabilities from `AttachStatus`, and gates the pause toggle on
  `consent/1` (§5) instead of probing.
- **Timeouts** (phase pattern from `LocalControlClient`: `CancellationTokenSource(timeout, time)`
  linked to the caller's token; internal seams for tests): connect 5s; reply 10s for consent ops;
  reply 40s for stop (the ack lands only after the graceful-stop sequence).
- **Failure classification** (thrown as `LocalControlOpsException(Reason, Message)`), with
  pinned precedence because `EndOfStreamException` derives from `IOException`: clean EOF
  (`FrameCodec.ReadAsync` returns null) and `EndOfStreamException` (truncated header/payload) →
  `unexpected_reply`, checked **before** the transport branch; any other post-connect
  IOException / SocketException / reset during write or read → `daemon_unreachable` (the same
  mapping `LocalControlClient` uses), as are connect/socket-missing failures; a decodable
  `Error` frame is not an exception (it is a result, see above) except for consent ops, where it
  becomes `daemon_rejected` with the frame text; undecodable frame or unexpected frame type →
  `unexpected_reply`; phase timeout → `timed_out`. **Caller-token cancellation is checked before classifying** (the
  `LocalControlClient` pattern): it propagates as `OperationCanceledException`, never as
  `timed_out` or a transport reason, and app commands absorb it quietly — no banner, no error
  log, lane and in-flight/queued-slot state cleaned up. The app maps reasons to banner copy;
  reasons are stable identifiers, not user copy.
- **Structural validation** (STJ source-gen does not enforce non-nullable members — the daemon's
  own receive path guards for exactly this, `LaunchConsentIpc.HandleRulesPutAsync`; the ops
  mirror it): a `ConsentRules` reply must have a non-null root, non-null `default` ∈
  `allow|deny|prompt`, `prompt_timeout_seconds ≥ 1`, non-null `rules`, and every rule element
  non-null with non-null `action` ∈ `allow|deny`. A `ConsentAck` reply must have a non-null
  root; note `{}` decodes as `ok:false, error:null` (STJ default bool), which is handled by §6's
  failure path with neutral fallback copy — an `ok:false` with a null/empty `error` banners "the
  daemon rejected the change" rather than an empty message, and `ok:true` with a non-null
  `error` (the DTO's partial-failure warning) is treated as success and logged to stderr. A
  `StopAck` must contain **exactly one** line for the requested id, with exactly two
  tab-separated fields and a status ∈ `stopped|failed|skipped`; a missing, duplicated, or
  malformed line or an unknown status token → `unexpected_reply`. Malformed JSON or any
  violation above → `unexpected_reply` — parseable-but-invalid payloads never escape as success.
- Scripted-server unit tests in `Capacitor.Cli.Tests.Unit` alongside the `LocalControlClient`
  harness (macOS sockaddr limit + Windows guard + NotInParallel conventions apply).

## 11. Error surfacing

- A minimal `AppNotifier` (replay-0 subject behind an interface) carries one-line failure
  messages; `MainWindowViewModel` renders the latest as a transient banner (auto-dismiss ~6s via
  `TimeProvider`, latest-wins single slot). Sources: stop failures (§7), pause toggle failures
  (§6), unexpected op failures (§10 reasons mapped to neutral copy, e.g. `daemon_unreachable` →
  "The daemon is not reachable").
- Every banner message is also written to stderr (the only channel when the window is hidden).
  A missed banner while the window is hidden is an accepted limitation of this slice — the tray
  state self-corrects from snapshots, and real notifications are AI-1652's job.
- Menu truthfulness over optimism: no optimistic checkbox flips, no locally-faked agent status;
  the push stream and the refresh-on-open are the sources of truth, and a pause state that
  cannot be verified disables the item rather than guessing (§6).

## 12. Testing

Headless (`Capacitor.App.Tests.Unit`, existing `AvaloniaSession` + immediate-scheduler swap):

- **Tray state matrix:** all ten mapping rows of §4, including skew, server-link-lost,
  negative-`active_agents`, unknown-connection-value, unknown-unreachable-reason, and
  stale-snapshot-while-unreachable.
- **Menu model:** entry projection (filter to Starting/Running, label format, ordering), header
  copy per state including the neutral fallback, agent section hidden when not Connected, pause
  item enablement (capability gating + disconnected + in-flight + unverified).
- **Adapter state machine:** rebuild only on `NeedsUpdate` from the cached model; a model change
  while open sets dirty and is consumed at the next `NeedsUpdate`; `Opening` kicks the refresh
  without touching menu structure; the pause item's frozen command parameter — an unchecked
  native item dispatches desired `true`, a checked item dispatches desired `false` (§6 capture
  rule; the handler never reads `IsChecked`).
- **Pause logic** against a scripted `ILocalControlOps`: exact Put payloads for pause/unpause
  (rule inserted/removed at index 0, default and timeout passed through), idempotent double-pause,
  detection strictness (wildcard deny at index ≠ 0 does not check the toggle), single-flight
  (rapid double-click runs one operation, second click ignored), lane serialization both ways —
  deterministic ordering tests, not timing-based: a passive `Opening` refresh requested during a
  toggle is dropped and never applies; and the mirror, hold the `Opening` refresh, click the
  toggle, release the refresh **returning a changed policy** → the queued intent runs exactly
  once, after the passive, applying the captured desired state against its own fresh Get
  (asserting the resulting Put, or the idempotent no-op when the desired state already holds —
  never an inversion) — disconnect mid-toggle (→ `daemon_unreachable` banner + unverified) and
  shutdown-token cancellation mid-toggle (→ absorbed quietly: no banner, no unverified marking,
  lane and queued-slot state cleaned up), and the ack branches as separate deterministic cases
  pinning **both sides** of the conditional trailing-refresh contract: `ok:false` (error text or
  the `error:null` neutral fallback copy) + **successful** trailing Get → banner AND reconciled
  checkmark, verified/enabled; `ok:false` or ambiguous transport failure + **failed** trailing
  Get → banner, last-known checkmark, unverified/disabled until a later successful `Opening`
  refresh re-enables it; `ok:true, error!=null` → success, stderr warning, NO banner,
  verified/enabled after its successful trailing refresh.
- **Stop command:** per-id in-flight gating, concurrent stops for different ids, completion into
  a vanished row is a no-op, `failed`/`Error` → banner, `skipped` → the "declined to stop"
  banner, no local cache mutation.
- **Agents grid:** row projection and sort order (`created_at` asc, id ordinal), presentation
  rules (`requester` null → `unknown`, repo last-segment + tooltip + `—`, `vendor (model)`),
  action disablement + dimming when not Connected, empty state, rows follow EditDiff removal.
- **Deep links:** exact URI (trailing-slash trim + `Uri.EscapeDataString(id)`), opener invoked
  through the seam, opener exception → banner.
- **AppNotifier/banner:** latest-wins single slot, auto-dismiss expiry via `TimeProvider`,
  stderr mirroring.
- **Uptime formatting:** boundary cases via `TimeProvider`.
- **Lifecycle** (fake `IClassicDesktopStyleApplicationLifetime` via the existing DispatchProxy
  harness): close-hides vs quit-closes ordering (`QuitInProgress` set on first deferred pass),
  tray-icon disposal before `TryShutdown`, startup-failure path creates no tray icon.
- **`TrayIconRenderer`:** state→asset selection and count-cap logic as pure-function tests
  (bitmap output is manual verification).

`Capacitor.Cli.Tests.Unit`: `LocalControlOps` scripted-server tests — success, `Error`-frame
result vs exception per op, clean EOF AND truncated header/payload (`EndOfStreamException`) →
`unexpected_reply` (precedence over the IOException transport branch), connect failure AND
post-connect reset/transport failure → `daemon_unreachable`, phase timeout → `timed_out`,
caller-token cancellation → `OperationCanceledException` (never `timed_out`), and
parseable-but-invalid payloads (§10 structural validation:
null `rules`, null rule element, unknown `default`, missing/duplicated/malformed `StopAck` line,
unknown `StopAck` status token) → `unexpected_reply`; `{}` as `ConsentAck` → the op returns
`ConsentAckDto(false, null)` (wire-shape assertion only — the resulting neutral banner is the
app suite's assertion above, via the fake ops seam). Plus one engine-level acceptance test alongside the existing
consent-engine tests (`test/Capacitor.Cli.Tests.Unit/Daemon/LaunchConsentEngineTests.cs`): a
policy with the pause rule at index 0 still allows a `RequesterIsOwner` input (owner exemption,
§6 semantics).

Manual (macOS, per the issue): tray icon rendering across the five states + count overlay,
light/dark menu bar, menu interaction, deep links, real stop against a live daemon.

## 13. Risks & notes

- **Avalonia tray variance on macOS** — the acknowledged risk; the pinned fallback (§4) bounds it.
- **Menu staleness while open** — the `NeedsUpdate`-only rebuild cadence (§5) means content is
  frozen while the menu is displayed and the pause checkmark can trail a CLI-side edit by one
  open (or more, under rapid reopen/busy-lane drops — §6's stated bound); both are accepted
  (native menus are static while open anyway) and covered by manual macOS acceptance.
- **Last-write-wins on consent policy** (§6) — accepted for a sub-second window against a
  single-writer store.
- **The `attention` state will gain consent-pending input in AI-1652**; the mapping table's
  precedence rows are designed to absorb an additional OR-condition without renumbering.
