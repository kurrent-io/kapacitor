# AI-1654 — Daemon lifecycle management + PATH shim (desktop supervisor slice 3)

**Date:** 2026-08-10 (revised same day after spec-review round 1, Codex reviewer)
**Status:** Approved design.
**Issue:** AI-1654. Umbrella spec: [2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §4 "Lifecycle" and §11.
**Prior slices:** control IPC + consent (AI-1623/AI-1648), app shell (AI-1650), tray/agents/stop (AI-1651), consent prompts + Activity (AI-1652). AI-1653 (bundling/signing) has NOT landed — this slice keeps the dev-time CLI seam and records constraints on AI-1653.

## 1. Problem

The app can attach to a daemon and start one ad hoc (`kcap daemon start -d`), but nothing manages the daemon's *lifecycle*: nothing installs it as a LaunchAgent so agents survive logout/reboot, nothing notices an externally installed daemon on a different version, and the bundled CLI is invisible to terminals. Umbrella §4 assigns all three to the app; this slice delivers them pre-wizard (AI-1655 later folds them into onboarding).

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **Everything through the CLI (approach A).** The app shells the resolved `kcap` for every service operation (`daemon service install/start/uninstall`, `daemon stop`) and for detection (`daemon service status --json`, `--version`). No plist/launchctl knowledge in the app. Rejected: moving service machinery into `Capacitor.Cli.Core` for in-proc use (code churn, two callers to keep consistent, wrong growth axis — the app grows as an IPC client, not by absorbing operational internals); hybrid in-proc reads (splits plist layout knowledge across projects). |
| 2 | **Auto-install on startup attach failure** (Docker Desktop model): no unit file present + a *valid* profile (§4.1) → the app silently installs and starts the LaunchAgent, then **verifies the outcome and rolls back a unit that did not take ownership** (§4.2). Arms once per app run, at startup only — never on mid-session drops, so the app never fights a user who deliberately stopped or uninstalled the service. |
| 3 | **Takeover offer on version mismatch only.** Same-version foreign setups (npm unit, manual `daemon start`) keep working untouched. The offer is a dialog — never silent (umbrella §4). The dialog **discloses that the existing service unit will be replaced and its baked settings re-captured**; the app does not attempt to restore a replaced foreign unit on failure (reconstructing plists would require exactly the plist knowledge decision 1 excludes) — a mid-sequence failure surfaces an honest recoverable state instead (§6). |
| 4 | **PATH shim only when no `kcap` resolves on the user's login-shell PATH**, and never over an existing filesystem entry: install is `lstat`-checked and non-forcing (§5). An existing npm install is never shadowed or replaced. Denial is remembered; the app works without the shim (terminal features degrade, umbrella §11). |
| 5 | **The tray Start action becomes service-aware** (§4.4). Prevents a user-clicked Start from spawning a detached daemon that races the installed LaunchAgent for the name lock. |
| 6 | **Zero daemon and zero wire-protocol changes.** One additive client-side change in `Capacitor.Cli.Core`: `LocalControlEvent.Unreachable` gains an optional `DaemonVersion` carried from the already-received hello reply (§4.3) — without it, the incompatible old daemon (the takeover offer's primary audience) could never receive the offer the umbrella promises. |
| 7 | **Every CLI child the app spawns runs with a pinned profile and a terminal-grade PATH**: `KCAP_PROFILE=<resolved name>` in the child env, login-shell `PATH` (§3.6) injected, `--profile` additionally passed to `service install`. Without this, the child's repo-aware profile resolution can capture a different tenant into the unit, and `ServiceEnvironment.Capture` would bake the GUI app's minimal launchd PATH into the unit — producing a daemon that cannot find `claude`/`codex` (the exact failure the PATH capture exists to prevent). |

## 3. Components

### 3.1 `CliResolver` (app, new)

Extracts and extends the inline resolution in `DaemonClientService.CreateDefaultAsync`:

1. `KCAP_APP_CLI_PATH` env override (absolute path, dev seam — app-shell design decision 6);
2. *(future, AI-1653)* bundle-relative path — a one-line arm added when bundling lands;
3. `kcap` on PATH.

Shared by the daemon client, the lifecycle controller, and the shim installer. At startup it runs `<cli> --version --no-update-check` once and caches the result. Parsing is strict: output must be a single line matching `kcap <version>`; the prefix is stripped and the bare version compared against snapshot/hello versions (which carry the bare `AssemblyInformationalVersion`). Multiline, malformed, or `unknown` output → skew detection disabled for the run. If no CLI resolves at all, the resolver says so and every lifecycle feature degrades honestly (§6).

The app does **not** compute the daemon install target itself (the naïve "sibling of the resolved CLI" is wrong for the standard npm install, where PATH resolves a Node launcher and the native binary lives in a separate platform package). The CLI reports it (§3.4).

### 3.2 `DaemonLifecycleController` (app, new)

The state machine of this slice (§4). Subscribes to the existing `AttachStatus`/`Snapshots` streams, queries service state via the CLI, and shells the CLI for mutations. Concurrency contract (all load-bearing):

- **One async operation gate** serializes every lifecycle mutation — startup auto-install/start, takeover accept, and the user Start action all acquire it; none can interleave.
- **Evidence is revalidated inside the gate immediately before mutating**: fresh `service status --json`, current attach state, current version pair. A takeover acceptance whose evidence no longer matches (version changed, unit changed, daemon now unreachable/reachable differently) aborts with a message instead of acting on stale consent.
- **A connection-generation token** invalidates stale continuations: a status query started against generation *n* is discarded if the attach stream has moved to generation *n+1* (e.g. `Unreachable` → query in flight → `Connected`).
- **The once-per-run arm is claimed before the first await** on the startup path.
- **The controller subscribes before the attach pump starts** (constructed and wired before `DaemonClientService.Start()`), so an early `Unreachable`→`Connected` transition cannot be missed.

### 3.3 `PathShimInstaller` (app, new)

Detection + one-prompt symlink install (§5). Small interface; macOS implementation now, no-op elsewhere (AI-1657 keeps Windows open).

### 3.4 `kcap daemon service status --json` (CLI, the one addition)

```json
{
  "service_id": "default",
  "unit_present": true,
  "state": "not_installed" | "installed" | "running",
  "binary_path": "/path/baked/into/unit/kcap-daemon",
  "install_binary_path": "/path/this/cli/would/bake/kcap-daemon"
}
```

- `state` is the existing `ServiceState` mapping, unchanged — including its quirk that a present-but-unloaded plist reports `not_installed` (a failed `launchctl print` maps to `NotInstalled` regardless of the file).
- `unit_present` disambiguates exactly that quirk: `true` iff the plist file exists. The four reachable combinations and their §4 actions are all defined; `state=not_installed` alone never means "no unit".
- `binary_path`: the unit's baked `ProgramArguments[0]` when `unit_present`, else null.
- `install_binary_path`: what an install by **this** CLI would bake — `ResolveDaemonBinary()`, i.e. the `kcap-daemon` sibling of the running native binary (correct through the npm launcher, `KCAP_APP_CLI_PATH`, and the future bundle alike); null when the sibling is missing.
- Provenance comparisons between `binary_path` and `install_binary_path` are made on canonicalized paths (symlinks resolved), case-sensitivity per platform.

snake_case via a source-generated JSON context (AOT rule). Without `--json` the human output is unchanged. Errors stay non-zero exit + stderr. Help text (`help-usage.txt`) and the README's daemon-service section update in the same PR.

### 3.5 App state store

`~/.config/kcap/app-state.json` (via `PathHelpers.ConfigPath`), app-owned. One **serialized store service** owns the file — the lifecycle controller and shim installer both go through it, so concurrent read-modify-write cannot lose updates. Holds: takeover-decline pairs (§4.3), shim offer/denial state (§5). Missing/corrupt → defaults, no crash — same degradation philosophy as the daemon's `consent.json`. Nothing secret lives in it.

### 3.6 Process + login-shell seams (app)

- `IProcessRunner` grows to return `(ExitCode, Stdout, Stderr)` with a bounded timeout and cancellation per call (today it returns `(ExitCode, Stderr)` and discards stdout, which cannot serve `--version`, `status --json`, or `command -v`). All fakes and existing callers updated.
- A **login-shell probe** service runs `$SHELL -l -c '…'` with a bounded timeout, falling back to `/bin/zsh` (the macOS default) when `$SHELL` is unset or the probe fails to execute. Used once at startup to capture the user's terminal `PATH` (injected into every spawned CLI child, decision 7) and by shim detection (§5). A probe that fails or times out yields *unknown*: children are spawned without the injected PATH (an honest degradation, logged), and the shim auto-offer is suppressed (the menu item remains).

## 4. Lifecycle state machine

### 4.1 Auto-install gate: "a valid profile"

Auto-install requires a **durable, usable daemon configuration**, not merely a resolved profile object: a named profile whose server URL is a valid absolute `http`/`https` URL (the daemon rejects an empty/invalid URL and exits non-zero — under `KeepAlive SuccessfulExit=false` an unstartable unit would spin). The app resolves the profile name once, revalidates inside the mutation gate immediately before installing, and pins it explicitly (`--profile <name>` + `KCAP_PROFILE`, decision 7) so the spawned CLI cannot capture a different repo-bound profile.

### 4.2 Startup (arms once per app run)

The attach loop runs exactly as today. On the **first** `Unreachable(daemon_unreachable)`, the controller (inside the gate, generation-checked) queries `service status --json` and acts on positive evidence only:

| `unit_present` | `state` | Action |
|---|---|---|
| — | `running` | Nothing. launchd thinks its job is up; existing backoff keeps retrying. A wedged daemon stays a manual-UX case — no blind kickstart. |
| true | `installed` | `kcap daemon service start` |
| true | `not_installed` | Nothing automatic. A present-but-unloaded plist is a broken or foreign state; silently rewriting it is takeover territory. Surface it ("service unit present but not loaded — reinstall from the app menu or terminal") with a manual affordance. |
| false | — (valid profile, §4.1) | `kcap daemon service install --name <X> --profile <P>` — silent auto-install; `RunAtLoad` starts it — then **post-install verification** (below). |
| false | — (no valid profile) | Nothing. Today's Stopped UX. The wizard (AI-1655) owns fresh machines. |

**Post-install verification (closes the TOCTOU on the name lock).** "No unit file" does not prove no daemon owns the name: a manual daemon may be starting, wedged pre-IPC, or started between the query and the bootstrap. The freshly bootstrapped job would then exit non-zero on the held lock and `KeepAlive` would respin it silently. So after `service install`, the controller watches a bounded window (15 s) for the conjunction *attach `Connected`* AND *`service status` = `running`*:

- Both → healthy; done.
- Attach `Connected` but service job not `running` → the app attached to a daemon the unit does not own (a manual daemon won the lock): `kcap daemon service uninstall` (boots out and deletes only the unit — never touches the manual daemon's process), log the outcome, continue attached. Skew handling (§4.3) applies to the connected daemon as usual.
- No attach and the job is not settling into `running` → `service uninstall` (rollback), surface the install failure with the manual Start affordance. The rollback removes the spinning job — no retrying unit may remain.

After any action the controller kicks `RestartLoopAsync()` (the `StartDaemonAsync` pattern). Mid-session unreachable never auto-acts: crash recovery is launchd's job, and deliberate stops aren't fought.

The daemon name is resolved once via the existing `DaemonNameResolver` chain, so the watched, started, and installed daemon can never diverge.

### 4.3 Skew → restart/takeover

**Triggers** (both feed one code path):

- On every `Connected`: the snapshot's `Daemon.Version` differs from the cached CLI version.
- On `Unreachable(daemon_incompatible)` carrying a hello `DaemonVersion` (decision 6) that differs: an old external daemon that fails the capability gate is *exactly* the takeover audience — without this trigger the umbrella's promise ("hello detects the mismatch, app offers takeover") is unreachable. `daemon_unreachable` still never triggers lifecycle offers — transport silence is not version evidence.

**Provenance** (from a fresh `service status --json` inside the gate): `unit_present` && canonical `binary_path` == canonical `install_binary_path` → **app-managed** ("Restart daemon to update" — self-skew after an app update while the old daemon still runs). Anything else → **foreign** ("Take over management", with the replacement disclosure of decision 3).

**Accept path** — one sequence, branched on who actually owns the name (evidence revalidated first; §3.2):

| Evidence | Sequence |
|---|---|
| `state=running` (the unit's job owns the daemon) | `service install` — its bootout → rewrite → bootstrap stops the job it owns and starts the replacement. |
| `unit_present` but `state≠running` while a daemon is reachable (loaded-or-stale unit + manual daemon coexisting under one name) | `service uninstall` **first** (a raw `daemon stop` with any unit present is a documented no-op success — it prints "managed by launchd" and exits 0 without stopping anything) → `daemon stop --name <X>` → **verify the stop took effect** (below) → `service install`. |
| `unit_present=false` (manual daemon) | `daemon stop --name <X>` → verify → `service install`. |

**Stop verification:** the CLI's stop reports success after a best-effort ≤5 s wait; the controller does not trust it as proof. It polls (bounded, ~10 s) until the attach is gone and a fresh dial fails, before installing. Timeout → abort the takeover with an honest error; nothing has been installed over the live daemon (the foreign unit, if any, was already removed — disclosed in the dialog; the recovery surface is §6).

**Post-mutation verification:** same as §4.2 — the takeover install is also followed by the bounded attach+`running` check with `service uninstall` rollback, covering a concurrent manual start racing the bootstrap.

**Decline** is remembered per `(daemonVersion, cliVersion)` pair in the app state store: declining once means no nag on any later launch; a new pair (either side changed) asks again. At most one skew dialog per app run; the dialog never stacks with the shim offer (§5 ordering).

### 4.4 Service-aware Start action

The user Start action runs inside the same gate with a fresh status query: `unit_present && state=installed` → `service start`; `unit_present && state=not_installed` → no silent mutation — surface the §4.2 "present but not loaded" affordance (an explicit reinstall is one more click, but a Start click is not consent to rewrite a unit); `unit_present=false` → today's detached `daemon start -d`; `state=running` → just kick reattach.

## 5. PATH shim (macOS)

**Detection.** A GUI app inherits launchd's minimal PATH, not the user's terminal PATH — so detection uses the login-shell probe (§3.6): `command -v kcap`. The shim is considered when the probe *positively* finds nothing (probe failure = unknown = no auto-offer) **and** the resolver has an absolute CLI path to link to.

**Pre-flight (unprivileged).** `lstat /usr/local/bin/kcap`:

- absent → installable;
- a symlink already resolving to the selected CLI → already installed; success, no prompt;
- anything else (foreign symlink, regular file, directory, broken link) → **conflict**: never overwritten, surfaced with the path and what was found. Login-shell detection missing it does not prove the destination is free — the entry may be outside PATH or non-executable.

**Install.** One admin prompt; the target is passed as argv, never interpolated into script source:

```
osascript -e 'on run argv' \
          -e 'do shell script "mkdir -p /usr/local/bin && ln -s " & quoted form of item 1 of argv & " /usr/local/bin/kcap" with administrator privileges' \
          -e 'end run' -- <target>
```

`ln -s` is deliberately non-forcing: if anything appeared at the destination between pre-flight and elevation, creation fails as a conflict instead of clobbering it (decision 4). `quoted form of` handles POSIX quoting; paths with spaces/quotes/backslashes must round-trip, newline-containing paths are rejected outright before prompting.

**Post-install verification.** Re-run the login-shell probe. If `kcap` still doesn't resolve (login PATH omits `/usr/local/bin`), say so — the link exists but their shell config doesn't look there; show the line to add. Never report success on the symlink alone.

**Cancel vs failure.** A user cancel surfaces as AppleScript error `-128`; the controller distinguishes it by the `-128` code in stderr (locale-stable), not by exit code alone. Cancel → recorded, never auto-offered again. Non-cancel failure → show the error **with a copyable `sudo` equivalent** (`sudo mkdir -p /usr/local/bin && sudo ln -s '<target>' /usr/local/bin/kcap` — the osascript line is not reusable outside elevation); the menu item stays.

**Offer surface.** The auto-offer happens at most **once ever** (persisted as offered in the app state store regardless of outcome — accept, cancel, failure, or deferred), on first app run, after the startup attach has reached its **first terminal outcome**: `Connected`, or the §4.2 branch has completed (including verification), or the no-CLI/no-profile determination — each path signals the same "startup decision complete" latch, so an immediate `Connected` (which never enters §4.2) still releases the offer. Dialogs are serialized: the shim offer never appears while the skew dialog is up. An **"Install command-line tool…"** tray-menu item stays available while the shim is applicable-but-absent.

**Constraint recorded for AI-1653:** the bundled CLI must live at a **stable path inside the .app bundle across auto-updates**, or the symlink breaks on every update.

## 6. Error handling

The controller acts only on positive evidence, degrades honestly, never loops:

- **No CLI resolves** → lifecycle features off; tray keeps today's Stopped UX plus an honest status line ("kcap CLI not found").
- **`service status` fails or emits unparseable JSON** → treated as *unknown*: no auto-install, no takeover offer, reason logged. Unknown never triggers mutations.
- **Auto-install/start fails, or post-install verification rolls back** → stderr/outcome surfaces through the same message lane as `StartDaemonAsync` failures; the once-per-run guard means it shows once and stops; manual Start/retry remains.
- **Takeover aborts mid-sequence** (stop verification timeout, or install fails after the stop) → the dialog's disclosed worst case: daemon stopped, no service unit. Surfaced as Stopped with the error text and both recoveries offered (Start = detached start; retry takeover). The version pair is **not** marked declined — the next qualifying trigger re-offers.
- **Stale-consent abort** (evidence changed between dialog and accept) → nothing mutated; one-line explanation; re-offer on the next qualifying trigger.
- **`--version` query fails / malformed / `unknown`** → skew detection disabled for the run, logged. No dialogs on garbage.
- **Login-shell probe fails/times out** → children spawn without injected PATH (logged); shim auto-offer suppressed; menu item remains.
- **App state store missing/corrupt** → defaults (offer again rather than never).

## 7. Testing

TUnit throughout; controller and shim are plain services driven through `IProcessRunner` fakes — no real launchctl/osascript in CI. Windows CI leg: build path assertions with `Path.Combine` (known separator trap).

- **CLI:** `service status --json` — the full state grid: plist absent; plist present + `launchctl print` failing (→ `not_installed` with `unit_present=true` and non-null `binary_path`); loaded-inactive; running. `install_binary_path` from `ResolveDaemonBinary` present/missing. snake_case contract; human output unchanged without the flag.
- **Lifecycle controller — startup:** the §4.2 matrix including the present-but-unloaded row (no mutation); once-per-run arming claimed pre-await (second unreachable: nothing; mid-session unreachable: nothing; `daemon_unreachable` only — `daemon_incompatible` routes to §4.3, never to install); profile gate (§4.1): null/invalid server URL, missing profile, repo-bound child resolution defeated by `--profile`/`KCAP_PROFILE` (assert exact child argv+env); post-install verification: healthy, attach-without-running (→ uninstall, manual daemon untouched — assert no `daemon stop` issued), no-attach spin (→ uninstall rollback, error surfaced once).
- **Lifecycle controller — skew/takeover:** trigger matrix (equal versions → nothing; snapshot mismatch; `daemon_incompatible` + hello version mismatch → offer; `daemon_unreachable` → never); provenance via canonical `binary_path` vs `install_binary_path` (npm-launcher shape, symlinked paths, missing target); the three accept sequences as exact argv order, including uninstall-before-stop for the coexisting-unit case; stop verification (drops attach → proceeds; timeout → abort, no install); post-mutation verification rollback; decline persistence within/across runs, new pair re-offers; stale-consent abort (version/unit changed between offer and accept → no mutation).
- **Races (deterministic, via generation token + gate):** status query resolving after `Connected` → discarded; user Start racing auto-install → serialized, second sees fresh evidence; dialog accept after generation change → abort; controller subscribed before pump → synthetic immediate-`Unreachable` handled.
- **Process/probe seams:** `IProcessRunner` returns stdout; timeout kills and reports; `--version` parsing (strips `kcap ` prefix, rejects multiline/garbage, asserts `--no-update-check` in argv); login-shell probe argv, `$SHELL` unset → `/bin/zsh`, probe timeout → unknown semantics (no PATH injection, no auto-offer).
- **Shim:** pre-flight lstat shapes (absent / correct symlink / foreign symlink / file / dir / broken link — only absent proceeds, correct symlink short-circuits as success); osascript argv passes target as argv (never interpolated), hostile paths round-trip, newline path rejected; `-128` in stderr → cancel persisted; other failure → `sudo` fallback text; post-install probe re-run (not-on-PATH → guidance, no false success); offered-once latch across outcomes; auto-offer waits for the startup latch incl. the immediate-`Connected` path; menu-item visibility.
- **App state store:** serialized concurrent writes (controller + shim) lose nothing; missing/corrupt → defaults.
- **E2E stays manual** (no bundle to automate against yet). Checklist for the PR:
  1. Fresh run, valid profile, no unit → unit appears in `~/Library/LaunchAgents`, daemon attaches; plist env carries the login-shell PATH and pinned `KCAP_PROFILE`.
  2. Manual `kcap daemon start` racing first app launch → no spinning LaunchAgent remains; manual daemon still alive; app attached.
  3. `kcap daemon service stop` in a terminal mid-session → app shows Stopped, does NOT auto-restart; relaunch app → auto-starts.
  4. npm-installed unit on an older version → takeover dialog (foreign copy, disclosure shown); accept → unit rewritten, new version attaches; decline → no re-prompt after app restart.
  5. Loaded-stopped unit + manual daemon under the same name, older version → accept runs uninstall → stop → install; no spin; ends attached to the new daemon.
  6. Old daemon that fails the hello capability gate → takeover offer still appears (incompatible trigger).
  7. Shim: no `kcap` on PATH → offer; accept → `/usr/local/bin/kcap` works in a new terminal; deny → no re-offer, menu item present. Pre-existing file at the destination → conflict message, file untouched.

## 8. Scope boundaries

- **AI-1653 keeps:** bundling, the bundle-relative resolver arm, the stable-in-bundle-path constraint (§5), auto-update atomicity, signing/notarization.
- **AI-1655 keeps:** the wizard, and the **consent default flip to `prompt`** — explicitly not this PR.
- **AI-1657 keeps:** Windows control channel; the shim interface and the already-cross-platform `IServiceManager` leave it open.
- Zero daemon and wire-protocol changes; one additive Core client field (decision 6). One PR (references AI-1654 and its GitHub issue per repo convention; README + help-text updates ride along).
