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
   (`LaunchConsentEngine.Evaluate`), so this denies every server-driven launch while it exists. The
   rule IS the state: the default is untouched, restore is correct by construction, the toggle
   survives app restarts, and `kcap daemon consent show` tells the truth. This is a deliberate
   deviation from the issue's letter.
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
| 6 | `Connected`, `daemon.connection == "connected"`, `active_agents == 0` | **Idle** |
| 7 | `Connected`, `daemon.connection == "connected"`, `active_agents > 0` | **Running(n)**, n = `active_agents` |
| 8 | `Connected`, any other `connection` value (future daemon) | **Attention** (conservative) |

`AttachStatus` precedence is absolute: a retained stale snapshot never produces Idle/Running while
the client is Connecting or Unreachable. `n` is `active_agents` — the display count derived
server-side from the same array (never treated as launch capacity). Between `Connected` and the
first snapshot there is no gap: the AI-1650 client only reports Connected together with a valid
first snapshot.

**Icon.** One monochrome glyph asset per state (16px + 32px for 2x), rendered via a
`TrayIconRenderer` that returns a `WindowIcon` for `(state, count)` with per-value caching. For
Running, the count is drawn onto the glyph bitmap (`RenderTargetBitmap`, 2x, `9+` cap). Avalonia
does not expose NSImage template-image behavior, so light/dark menu-bar contrast and the dynamic
count bitmap are exactly the "known Avalonia platform variance" the issue assigns to manual macOS
verification. **Pinned fallback** if that verification fails: glyph-only Running asset, count moves
to the menu header line — a one-line change in the renderer, not a redesign.

## 5. Tray menu & adapter

The menu is projected from a plain menu model (`TrayViewModel`) and rebuilt by `TrayIconManager`
whenever the model changes (`NativeMenu` has no ItemsSource; full rebuild is cheap at these sizes).

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
  Running → `connected — {n} agent(s) running`.
- **Agent entries** appear only while `Connected`; they are the agents whose `status` is
  `Starting` or `Running` (same predicate as `active_agents`), labeled
  `{kind} · {vendor} · {repo last path segment}` (`—` when `repo_path` null), ordered `created_at`
  asc, id ordinal tiebreak (the wire order pin). Requester stays in the main window.
- **Stop** sends `StopV2 force:false` for that id; the entry's Stop item disables while in flight.
- **Open in web** opens `{ServerUrl trimmed of trailing /}/agents/{id}` in the default browser
  (`Process.Start` with `UseShellExecute` — via an injected opener seam for tests).
- **Pause new launches** — §6. Disabled (unchecked) when the connected daemon's capabilities lack
  `consent/1`, and while not `Connected`.
- **Open Kurrent Capacitor** shows/creates the main window (§9). **Quit** calls
  `desktop.TryShutdown()`, entering the existing deferred shutdown pass.
- On menu open (`NativeMenu` opening event), the VM refreshes the pause state (§6). Menu content
  otherwise tracks the push stream — no polling.

## 6. Pause-launches toggle

The pause rule is exactly `ConsentRuleDto("deny", null, null, null, null)` at index 0.

- **Displayed state:** checked iff the latest fetched policy has an all-wildcard deny rule at
  index 0. An all-wildcard deny at any other index, or narrower deny rules, do not check the
  toggle — `kcap daemon consent show` is the full truth. State is refreshed via `ConsentRulesGet`
  on menu open and after every toggle write; a refresh failure keeps the last-known state and logs
  (no banner for a passive refresh).
- **Pause:** `ConsentRulesGet` → if the pause rule is already at index 0, no-op (idempotent);
  otherwise insert it at index 0 → `ConsentRulesPut` with the full policy (`default` and
  `prompt_timeout_seconds` passed through unchanged).
- **Unpause:** `ConsentRulesGet` → remove the index-0 all-wildcard deny if present (only that one
  rule) → `ConsentRulesPut`.
- **Concurrency:** `ConsentRulesPut` replaces the whole policy; a CLI edit between Get and Put is
  lost (last-write-wins). Accepted — the daemon-side store is the single writer and the window is
  sub-second; noted, not mitigated.
- **Failure:** a `ConsentAck` with `ok:false` (or transport failure) surfaces its error in the
  banner (§11); the checkbox reverts on the follow-up refresh, so the menu never lies.

## 7. Stop & open-in-web semantics

One code path for both surfaces (tray menu item, main-window row button):

- Send `StopV2(force:false, id)` via `LocalControlOps` (§10). Reply timeout is long (§10) because
  the daemon acks only after the stop completes — graceful wait plus terminate can take ~25s.
- Reply handling: `StopAck` line `{id}\tstopped` → success, no banner (the agent's disappearance
  from the next snapshot is the confirmation); `{id}\tfailed` → banner "Couldn't stop {label}";
  `Error` frame (protected agent, unknown id) → banner with the daemon's text verbatim (it is
  display-quality and already names the `--force` escape hatch); transport failure → §10
  classification into the banner.
- In-flight gating is per agent id (a second Stop for the same id no-ops while one is pending;
  different ids run concurrently). The row/menu item re-enables on reply; the visible status
  transition (`Stopping`, then row removal) comes exclusively from daemon snapshots — the app
  never fakes a status.
- Open-in-web from a row uses the same link builder as the tray.

## 8. Main window Agents area

The status panel gains an **Agents** grid bound to the existing
`SourceCache<AgentStatusDto, string>` (DynamicData `Connect` → transform to row VMs → bind),
sorted `created_at` asc, id ordinal tiebreak.

- **Columns:** Kind · Vendor · Repo · Requester · Status · Uptime · actions (Stop, Open in web).
  Status (verbatim daemon string, opaque display text) is an addition beyond the issue's minimum
  list — it is how Stopping becomes visible.
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
- **Failure classification** (thrown as `LocalControlOpsException(Reason, Message)`):
  connect/socket-missing failures → `daemon_unreachable`; a decodable `Error` frame is not an
  exception (it is a result, see above) except for consent ops, where it becomes
  `daemon_rejected` with the frame text; EOF, undecodable frame, or unexpected frame type →
  `unexpected_reply`; phase timeout → `timed_out`. The app maps reasons to banner copy; reasons
  are stable identifiers, not user copy.
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
  the push stream and the refresh-on-open are the sources of truth.

## 12. Testing

Headless (`Capacitor.App.Tests.Unit`, existing `AvaloniaSession` + immediate-scheduler swap):

- **Tray state matrix:** all eight mapping rows of §4, including skew, server-link-lost,
  unknown-connection-value, and stale-snapshot-while-unreachable.
- **Menu model:** entry projection (filter to Starting/Running, label format, ordering), header
  copy per state, agent section hidden when not Connected, pause item enablement (capability
  gating + disconnected).
- **Pause logic** against a scripted `ILocalControlOps`: exact Put payloads for pause/unpause
  (rule inserted/removed at index 0, default and timeout passed through), idempotent double-pause,
  detection strictness (wildcard deny at index ≠ 0 does not check the toggle), ack-failure →
  banner + state revert on refresh.
- **Stop command:** per-id in-flight gating, concurrent stops for different ids, `failed`/`Error`
  → banner, no local cache mutation.
- **Uptime formatting:** boundary cases via `TimeProvider`.
- **Lifecycle** (fake `IClassicDesktopStyleApplicationLifetime` via the existing DispatchProxy
  harness): close-hides vs quit-closes ordering (`QuitInProgress` set on first deferred pass),
  tray-icon disposal before `TryShutdown`, startup-failure path creates no tray icon.
- **`TrayIconRenderer`:** state→asset selection and count-cap logic as pure-function tests
  (bitmap output is manual verification).

`Capacitor.Cli.Tests.Unit`: `LocalControlOps` scripted-server tests — success, `Error`-frame
result vs exception per op, EOF → `unexpected_reply`, connect failure → `daemon_unreachable`,
phase timeout → `timed_out`.

Manual (macOS, per the issue): tray icon rendering across the five states + count overlay,
light/dark menu bar, menu interaction, deep links, real stop against a live daemon.

## 13. Risks & notes

- **Avalonia tray variance on macOS** — the acknowledged risk; the pinned fallback (§4) bounds it.
- **Menu rebuild cadence** — full `NativeMenu` rebuild per model change is O(agents) and rare
  (snapshot-driven); if a platform quirk surfaces (flicker, focus), rebuilding only on
  menu-open + snapshot-arrival is the noted fallback.
- **Last-write-wins on consent policy** (§6) — accepted for a sub-second window against a
  single-writer store.
- **The `attention` state will gain consent-pending input in AI-1652**; the mapping table's
  precedence rows are designed to absorb an additional OR-condition without renumbering.
