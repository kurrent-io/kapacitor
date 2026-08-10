# AI-1654 — Daemon lifecycle management + PATH shim (desktop supervisor slice 3)

**Date:** 2026-08-10
**Status:** Approved design.
**Issue:** AI-1654. Umbrella spec: [2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §4 "Lifecycle" and §11.
**Prior slices:** control IPC + consent (AI-1623/AI-1648), app shell (AI-1650), tray/agents/stop (AI-1651), consent prompts + Activity (AI-1652). AI-1653 (bundling/signing) has NOT landed — this slice keeps the dev-time CLI seam and records one constraint on AI-1653.

## 1. Problem

The app can attach to a daemon and start one ad hoc (`kcap daemon start -d`), but nothing manages the daemon's *lifecycle*: nothing installs it as a LaunchAgent so agents survive logout/reboot, nothing notices an externally installed daemon on a different version, and the bundled CLI is invisible to terminals. Umbrella §4 assigns all three to the app; this slice delivers them pre-wizard (AI-1655 later folds them into onboarding).

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **Everything through the CLI (approach A).** The app shells the resolved `kcap` for every service operation (`daemon service install/start`, `daemon stop`) and for detection (`daemon service status --json`, `--version`). No plist/launchctl knowledge in the app; a unit installed by the app is byte-identical to a terminal-installed one. Rejected: moving service machinery into `Capacitor.Cli.Core` for in-proc use (code churn, two callers to keep consistent, wrong growth axis — the app grows as an IPC client, not by absorbing operational internals); hybrid in-proc reads (splits plist layout knowledge across projects for the price of one `--json` flag). |
| 2 | **Auto-install on startup attach failure** (Docker Desktop model): no unit installed + a profile resolves → the app silently installs and starts the LaunchAgent. Guarded to fire **once per app run**, at startup only — never on mid-session drops, so the app never fights a user who deliberately stopped or uninstalled the service. |
| 3 | **Takeover offer on version mismatch only.** Same-version foreign setups (npm unit, manual `daemon start`) keep working untouched. The offer is a dialog — never silent (umbrella §4). |
| 4 | **PATH shim only when no `kcap` resolves on the user's login-shell PATH.** An existing npm install is never shadowed or replaced. Denial is remembered; the app works without the shim (terminal features degrade, umbrella §11). |
| 5 | **The tray Start action becomes service-aware**: unit installed → `daemon service start`; no unit → today's detached `daemon start -d`. Prevents a user-clicked Start from spawning a detached daemon that holds the name lock against the installed LaunchAgent. |
| 6 | **Zero daemon/IPC changes.** The daemon is untouched. |

## 3. Components

### 3.1 `CliResolver` (app, new)

Extracts and extends the inline resolution in `DaemonClientService.CreateDefaultAsync`:

1. `KCAP_APP_CLI_PATH` env override (absolute path, dev seam — app-shell design decision 6);
2. *(future, AI-1653)* bundle-relative path — a one-line arm added when bundling lands;
3. `kcap` on PATH.

Shared by the daemon client, the lifecycle controller, and the shim installer. At startup it runs `<cli> --version` once and caches the result — the app's reference version for skew detection. Both `kcap --version` and the daemon's status snapshots report `AssemblyInformationalVersion`, so string equality is the comparison; if either side reports the `unknown` fallback (or the query fails), skew detection is disabled for the run. If no CLI resolves at all, the resolver says so and every lifecycle feature degrades honestly (§6).

The resolver also exposes the **install target**: the `kcap-daemon` sibling of the resolved CLI — the same path `kcap daemon service install` bakes into a unit (`ResolveDaemonBinary`). Used only to classify unit provenance (§4.2), never to write anything.

### 3.2 `DaemonLifecycleController` (app, new)

The state machine of this slice (§4). Subscribes to the existing `AttachStatus`/`Snapshots` streams from `IDaemonClientService`, queries service state via the CLI, and shells the CLI for mutations. All spawns go through the existing `IProcessRunner` seam.

### 3.3 `PathShimInstaller` (app, new)

Detection + one-prompt symlink install (§5). Small interface; macOS implementation now, no-op elsewhere (AI-1657 keeps Windows open).

### 3.4 `kcap daemon service status --json` (CLI, the one addition)

```json
{"service_id": "default", "state": "not_installed" | "installed" | "running", "binary_path": "/path/to/kcap-daemon" }
```

snake_case via a source-generated JSON context (AOT rule); `binary_path` null when not installed. Without `--json` the human output is unchanged. Errors stay non-zero exit + stderr. Help text (`help-usage.txt`) and the README's daemon-service section update in the same PR.

### 3.5 App state file

`~/.config/kcap/app-state.json` (via `PathHelpers.ConfigPath`), app-owned, single writer = the app. Holds the takeover-decline memory (§4.2) and the shim-denial memory (§5). Missing/corrupt → defaults, no crash — same degradation philosophy as the daemon's `consent.json`. Nothing secret lives in it.

## 4. Lifecycle state machine

### 4.1 Startup (arms once per app run)

The attach loop runs exactly as today. On the **first** `Unreachable(daemon_unreachable)`, the controller queries `service status --json` and acts on positive evidence only:

| Service state | Profile | Action |
|---|---|---|
| `running` | — | Nothing. launchd thinks it's up; existing backoff keeps retrying. A wedged daemon stays a manual-UX case — no blind kickstart. |
| `installed` (stopped) | — | `kcap daemon service start` |
| `not_installed` | resolves | `kcap daemon service install --name <X>` — silent auto-install; `RunAtLoad` starts it. |
| `not_installed` | none | Nothing. Today's Stopped UX. A LaunchAgent without a profile exits non-zero and would spin under `KeepAlive`; the wizard (AI-1655) owns fresh machines. |

After any action the controller kicks `RestartLoopAsync()` (the `StartDaemonAsync` pattern). Mid-session unreachable never auto-acts: crash recovery is launchd's job (`KeepAlive SuccessfulExit=false`), and deliberate stops aren't fought. `daemon_incompatible` never triggers lifecycle actions — it is protocol evidence, not absence.

The daemon name is resolved once via the existing `DaemonNameResolver` chain (already done in `CreateDefaultAsync`), so the watched, started, and installed daemon can never diverge.

### 4.2 Skew → restart/takeover (checked on every `Connected`)

Compare the snapshot's `Daemon.Version` to the cached CLI version. On mismatch, one dialog, two copies by provenance (from `service status`'s `binary_path` vs. the resolver's install target):

- **App-managed unit** (paths equal) → **"Restart daemon to update"** — self-skew after an app update while the old daemon is still running.
- **Foreign unit or no unit** (manual daemon) → **"Take over management"** — the umbrella's takeover case, offering to replace the unit with (a unit pointing at) the bundled binary.

Both funnel into one accept path:

1. If **no unit exists** (manual daemon): `kcap daemon stop --name <X>` first — the running daemon holds the per-name lock; installing without stopping would leave the LaunchAgent spinning against it.
2. `kcap daemon service install --name <X>` — its bootout → rewrite → bootstrap sequence is already idempotent for the existing-unit cases.
3. Kick reattach.

Decline is remembered per `(daemonVersion, cliVersion)` pair in `app-state.json`: declining once means no nag on any later launch, but a *new* pair (either side changed) asks again. At most one skew dialog per app run.

### 4.3 Service-aware Start action

Decision 5: the tray/main-window Start action queries `service status --json` first; `installed` → `service start`, `not_installed` → `daemon start -d` (unchanged today's path). `running` → just kick reattach.

## 5. PATH shim (macOS)

**Detection.** A GUI app inherits launchd's minimal PATH, not the user's terminal PATH — so detection runs the user's login shell: `$SHELL -l -c 'command -v kcap'`. The shim is offered only when that finds nothing **and** the resolver has an absolute CLI path to link to (a PATH-resolved `kcap` is both a non-target and proof no shim is needed).

**Install.** One admin prompt via:

```
osascript -e 'do shell script "mkdir -p /usr/local/bin && ln -sf <target> /usr/local/bin/kcap" with administrator privileges'
```

(`/usr/local/bin` may not exist on Apple Silicon, hence `mkdir -p`.) The embedded command is built programmatically with strict quoting of the target path — paths with spaces/quotes must round-trip.

**Offer surface.** Once, on first app run, after the startup lifecycle branch (§4.1) has run to completion — whatever its outcome — so it is the only first-run interruption (the daemon auto-install is dialog-free). A **"Install command-line tool…"** tray-menu item stays available while the shim is applicable-but-absent.

**Denial.** osascript exits non-zero on "User canceled" → recorded in `app-state.json`, never re-offered automatically, app fully functional. A non-cancel failure (e.g. `mkdir` denied by policy) shows the error **with the exact shell command included** so the user can run it themselves; the menu item stays.

**Constraint recorded for AI-1653:** the bundled CLI must live at a **stable path inside the .app bundle across auto-updates**, or the symlink breaks on every update.

## 6. Error handling

The controller acts only on positive evidence, degrades honestly, never loops:

- **No CLI resolves** → lifecycle features off; tray keeps today's Stopped UX plus an honest status line ("kcap CLI not found").
- **`service status` fails or emits unparseable JSON** → treated as *unknown*: no auto-install, no takeover offer, reason logged. Unknown never triggers mutations.
- **Auto-install/start fails** → stderr surfaces through the same message lane as `StartDaemonAsync` failures; the once-per-run guard means it shows once and stops; manual Start/retry remains.
- **Takeover fails mid-sequence** (stop succeeded, install failed) → daemon down; standard Stopped UX with the error text. The version pair is **not** marked declined — the next successful attach re-offers.
- **`--version` query fails / `unknown`** → skew detection disabled for the run, logged. No dialogs on garbage.
- **`app-state.json` missing/corrupt** → defaults (offer again rather than never).

## 7. Testing

TUnit throughout; controller and shim are plain services driven through `IProcessRunner` fakes — no real launchctl/osascript in CI. Windows CI leg: build path assertions with `Path.Combine` (known separator trap).

- **CLI:** `service status --json` rendering — three states × `binary_path` present/absent, snake_case contract, human output unchanged without the flag.
- **Lifecycle controller:** startup matrix (§4.1 table → no-op/start/install/no-op); once-per-run arming (second unreachable: nothing; mid-session unreachable: nothing; `daemon_incompatible`: nothing); skew matrix (equal → nothing; mismatch × app-managed/foreign-unit/no-unit → correct dialog variant); takeover sequencing asserted as exact argv order (stop → install → kick); decline persistence within and across runs, new pair re-offers; every §6 branch (unknown status → no mutation, install failure → message once, version-query failure → skew off).
- **Shim:** login-shell detection argv; offer gating (found-on-PATH → never; no absolute target → never); osascript command construction with hostile paths; cancel → persisted denial; menu-item visibility.
- **App state:** missing/corrupt → defaults, no crash.
- **E2E stays manual** (no bundle to automate against yet). Checklist for the PR:
  1. Fresh run, profile present, no unit → unit appears in `~/Library/LaunchAgents`, daemon attaches.
  2. `kcap daemon service stop` in a terminal mid-session → app shows Stopped, does NOT auto-restart; relaunch app → auto-starts.
  3. npm-installed daemon on an older version → takeover dialog; accept → unit rewritten, new version attaches; decline → no re-prompt after app restart.
  4. Manual `kcap daemon start -d` on an older version → takeover stops it first, installs, attaches.
  5. Shim: no `kcap` on PATH → offer; accept → `/usr/local/bin/kcap` works in a new terminal; deny → no re-offer, menu item present.

## 8. Scope boundaries

- **AI-1653 keeps:** bundling, the bundle-relative resolver arm, the stable-in-bundle-path constraint (§5), auto-update atomicity, signing/notarization.
- **AI-1655 keeps:** the wizard, and the **consent default flip to `prompt`** — explicitly not this PR.
- **AI-1657 keeps:** Windows control channel; the shim interface and the already-cross-platform `IServiceManager` leave it open.
- Zero daemon/IPC changes. One PR (references AI-1654 and its GitHub issue per repo convention; README + help-text updates ride along).
