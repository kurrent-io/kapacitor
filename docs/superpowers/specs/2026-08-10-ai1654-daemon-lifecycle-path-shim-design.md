# AI-1654 — Daemon lifecycle management + PATH shim (desktop supervisor slice 3)

**Date:** 2026-08-10 (revised after spec-review rounds 1–2, Codex reviewer)
**Status:** Approved design.
**Issue:** AI-1654. Umbrella spec: [2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §4 "Lifecycle" and §11.
**Prior slices:** control IPC + consent (AI-1623/AI-1648), app shell (AI-1650), tray/agents/stop (AI-1651), consent prompts + Activity (AI-1652). AI-1653 (bundling/signing) has NOT landed — this slice keeps the dev-time CLI seam and records constraints on AI-1653.

## 1. Problem

The app can attach to a daemon and start one ad hoc (`kcap daemon start -d`), but nothing manages the daemon's *lifecycle*: nothing installs it as a LaunchAgent so agents survive logout/reboot, nothing notices an externally installed daemon on a different version, and the bundled CLI is invisible to terminals. Umbrella §4 assigns all three to the app; this slice delivers them pre-wizard (AI-1655 later folds them into onboarding).

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **Everything through the CLI (approach A).** The app shells the resolved `kcap` for every service operation and for detection. No plist/launchctl knowledge in the app. The CLI changes in this slice: (a) `daemon service status --json` (§3.4); (b) two behavioral hardenings the lifecycle guarantees require — `service status` queries launchd even when the plist is absent (an orphaned loaded job must be visible), and `service uninstall` propagates a `bootout` failure (non-zero exit, plist retained) instead of deleting the file over a still-loaded job (§3.4). Rejected: moving service machinery into `Capacitor.Cli.Core` for in-proc use; hybrid in-proc reads. |
| 2 | **Auto-install on startup attach failure** (Docker Desktop model): no unit file present + a valid profile + a known terminal PATH (§4.1) → the app silently installs and starts the LaunchAgent, then runs **ownership-verified post-mutation verification with rollback** (§4.2). Auto-action eligibility exists only during the startup phase and closes permanently on the first terminal attach outcome of *any* kind (§3.2) — a daemon the user stops mid-session is never fought. |
| 3 | **Takeover offer on version mismatch only.** Same-version foreign setups keep working untouched. The offer is a dialog — never silent (umbrella §4). Every unit rewrite's dialog **discloses that the existing service unit will be replaced and its baked settings re-captured** (regardless of provenance, §4.3); the app does not attempt to restore a replaced unit on failure (reconstructing plists would require exactly the plist knowledge decision 1 excludes) — a mid-sequence failure surfaces branch-specific recovery from re-queried evidence (§6). |
| 4 | **PATH shim only when no `kcap` resolves on the user's terminal PATH**, and never over an existing filesystem entry: install is `lstat`-checked and non-forcing (§5). An existing npm install is never shadowed or replaced. Denial is remembered; the app works without the shim (terminal features degrade, umbrella §11). |
| 5 | **The tray Start action becomes service-aware** (§4.4). Prevents a user-clicked Start from spawning a detached daemon that races the installed LaunchAgent for the name lock. |
| 6 | **Zero daemon and zero wire-protocol changes.** One additive client-side change in `Capacitor.Cli.Core`: the hello reply's `DaemonVersion` is propagated end to end — `CycleOutcome` → `LocalControlEvent.Unreachable` → `AttachStatus` — with the client's transition dedupe keyed on `(reason, daemonVersion)` so a version change while incompatible re-emits (§4.3). Without this, the incompatible old daemon (the takeover offer's primary audience) could never receive the offer the umbrella promises, and the app could never see the value. |
| 7 | **Every CLI child the app spawns runs with a pinned profile** (`KCAP_PROFILE` env overlay; `--profile` additionally on `service install`) **and, when known, the terminal PATH** (§3.6). Without the pin, the child's repo-aware profile resolution can capture a different tenant into the unit; without the PATH, `ServiceEnvironment.Capture` bakes the GUI app's minimal launchd PATH into the unit — a daemon that cannot find `claude`/`codex`. Because the baked PATH is load-bearing, **silent mutations require a known terminal PATH** (unknown → no auto-install/auto-start); dialoged mutations disclose a degraded PATH and proceed only with that consent. |

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

- **One startup-phase state, shared by lifecycle and shim.** The phase ends — permanently, for this app run — at the first terminal attach outcome: `Connected`, or `Unreachable(daemon_incompatible)`, or the completed §4.2 `daemon_unreachable` branch (including its verification), or the no-CLI/no-profile/unknown-PATH determination. Auto-actions are eligible only while the phase is open; the shim auto-offer waits for it to close. A `Connected → daemon_unreachable` or `daemon_incompatible → daemon_unreachable` transition after the phase closed performs **zero** lifecycle mutation.
- **One async operation gate** serializes every lifecycle mutation — startup auto-install/start, takeover accept, the reinstall affordance, and the user Start action all acquire it; none can interleave.
- **Evidence is revalidated inside the gate immediately before mutating**: fresh `service status --json`, current attach state, current version pair. A takeover acceptance whose evidence no longer matches aborts with a message instead of acting on stale consent.
- **A connection-generation token** invalidates stale continuations: a status query started against generation *n* is discarded if the attach stream has moved on. Verification (§4.2) counts only attach events observed strictly **after** the mutation began — the `AttachStatus` behavior-subject's replayed current value never satisfies it.
- **The once-per-run arm is claimed before the first await** on the startup path.
- **The controller subscribes before the attach pump starts**, so an early transition cannot be missed.

### 3.3 `PathShimInstaller` (app, new)

Detection + one-prompt symlink install (§5). Small interface; macOS implementation now, no-op elsewhere (AI-1657 keeps Windows open).

### 3.4 CLI: `service status --json` + two hardenings

```json
{
  "service_id": "default",
  "unit_present": true,
  "state": "not_installed" | "installed" | "running",
  "binary_path": "/path/baked/into/unit/kcap-daemon",
  "install_binary_path": "/path/this/cli/would/bake/kcap-daemon",
  "job_pid": 1234,
  "daemon_pid": 1234
}
```

- `state` keeps the existing `ServiceState` mapping, with one hardening: **the launchd query runs even when the plist is absent**, so an orphaned loaded job (bootout failed, file deleted) is reported as `unit_present=false, state=running/installed` instead of an unconditional `not_installed`. A present-but-unloaded plist still reports `not_installed` with `unit_present=true` — the four (`unit_present`, job) combinations are all representable and all have defined §4 actions.
- `binary_path`: the unit's baked `ProgramArguments[0]` when `unit_present`, else null.
- `install_binary_path`: what an install by **this** CLI would bake — `ResolveDaemonBinary()` (correct through the npm launcher, `KCAP_APP_CLI_PATH`, and the future bundle alike); null when the sibling is missing. Comparisons are made on canonicalized paths.
- `job_pid`: the pid from `launchctl print` (present alongside `state = running`), null otherwise.
- `daemon_pid`: the name's PID-file owner (same read `daemon stop` uses), null when absent/unusable. `job_pid == daemon_pid` (both non-null) is the **ownership** predicate: the launchd job *is* the daemon holding the name.
- **`service uninstall` hardening:** the `bootout` result is checked; on failure the plist is **retained** and the command exits non-zero with the label still loaded — never a deleted file over a live KeepAlive job silently reported as success.

snake_case via a source-generated JSON context (AOT rule). Without `--json` the human output is unchanged. Help text (`help-usage.txt`) and the README's daemon-service section update in the same PR.

### 3.5 App state store

`~/.config/kcap/app-state.json` (via `PathHelpers.ConfigPath`), app-owned. One **serialized store service** owns the file — controller and shim installer both go through it. Writes are **atomic** (temp file + rename); a crash mid-write can only yield the old or the new file, and missing/corrupt → defaults, no crash. One-shot claims (shim offered, decline pairs) are **persisted before the dialog is shown**; if the persist itself fails, the claim holds in memory for this run (no re-offer now, possible re-offer next launch — accepted and logged). Nothing secret lives in it.

### 3.6 Process + shell-environment seams (app)

**`IProcessRunner`** grows to `(ExitCode, Stdout, Stderr)` with per-call **environment overlay** (adds/overrides specific variables — `KCAP_PROFILE`, `PATH` — without replacing the rest), cancellation, and timeout. Two distinct policies, preserving today's semantics where they are load-bearing:

- **External cancellation** (app shutdown) abandons the *wait*, never the child — `daemon start -d` must survive the app quitting (existing behavior, kept).
- **Internal timeout on mutating calls** kills the **entire process tree** (the npm launcher synchronously owns a native child which spawns `launchctl`; killing the top process is not enough), then **awaits its exit**, then reconciles actual service state via a fresh status query before any further mutation — a late child must never complete a mutation after the gate has moved on.

**Terminal-PATH probe.** A GUI app inherits launchd's minimal PATH; what users mean by "the terminal" on macOS is an *interactive login* shell — `zsh -l -c` reads `.zprofile` but not `.zshrc`, where nvm/npm and agent paths commonly live. The probe therefore runs `$SHELL -lic 'printf "<sentinel>%s<sentinel>" "$PATH"'` (stdin `/dev/null`, bounded timeout), parsing between sentinels so startup chatter cannot corrupt the value; on failure/timeout it retries with `-lc`; `$SHELL` unset/unusable falls back to `/bin/zsh`. Both failing → **unknown**: silent mutations are suppressed (decision 7), dialoged mutations disclose the degraded PATH, queries still run. The same probe answers shim detection (`command -v kcap`, §5) and is re-run for post-install verification there.

## 4. Lifecycle state machine

### 4.1 Silent-mutation gate

Silent auto-install requires **all** of: a named profile whose server URL is a valid absolute `http`/`https` URL (the daemon rejects an empty/invalid URL and exits non-zero — under `KeepAlive SuccessfulExit=false` an unstartable unit would spin); a known terminal PATH (decision 7); the startup phase still open (§3.2). The profile is revalidated inside the operation gate immediately before installing and pinned explicitly (`--profile` + `KCAP_PROFILE`) so the spawned CLI cannot capture a different repo-bound profile.

### 4.2 Startup branch (runs at most once, only while the startup phase is open)

On the **first** `Unreachable(daemon_unreachable)` while the phase is open, the controller (inside the gate, generation-checked) queries `service status --json` and acts on positive evidence only:

| `unit_present` | job | Action |
|---|---|---|
| — | `running` | Nothing. launchd thinks its job is up; existing backoff keeps retrying. A wedged daemon stays a manual-UX case. |
| true | `installed`, `daemon_pid` null | `kcap daemon service start` (kickstart), then **post-start verification** (below). A non-null `daemon_pid` means some process owns the name while the unit's job is down — kickstarting would spin against the lock: surface the coexistence instead (§4.4 affordance). |
| true | `not_installed` (present-but-unloaded plist) | Nothing automatic. Surfaced with the **reinstall affordance** (§4.4) — a dialoged operation, because the unit may be foreign and the name may be lock-held. |
| false | — (silent-mutation gate §4.1 passes) | `kcap daemon service install --name <X> --profile <P>` → **post-install verification**. |
| false | — (gate fails) | Nothing. Today's Stopped UX (plus the §6 honest reason). The wizard (AI-1655) owns fresh machines. |

**Post-mutation verification (closes the name-lock TOCTOU).** "No unit file" does not prove no daemon owns the name: a manual daemon may be starting, wedged pre-IPC, or started between query and bootstrap; the bootstrapped job would exit non-zero on the held lock and `KeepAlive` would respin it silently. Worse, *two liveness signals are not ownership*: the app can be `Connected` to a manual daemon while the launchd contender is transiently `running` before losing the lock. Verification therefore requires, within a bounded window (15 s), the conjunction of:

1. a **fresh** attach `Connected` — observed strictly after the mutation began (never the replayed value), and, when the mutation installed a unit, at the expected version (the shelled CLI's own); and
2. **ownership**: `job_pid == daemon_pid`, both non-null, from a fresh status query.

Outcomes:

- Conjunction holds → healthy; done.
- Fresh `Connected` but ownership fails (a manual daemon won the lock) → for a unit **this operation just wrote**: `service uninstall` (rollback; the hardened uninstall makes a failed bootout visible instead of orphaning the job), log, stay attached to the manual daemon; skew handling (§4.3) applies to it as usual. For a **pre-existing** unit (post-start verification): do not destroy it silently — `service stop` the job and surface the coexistence with the dialoged affordance.
- Deadline with anything else — including *no fresh attach while the job still reads `running`* (a spin sampled between crashes) → same rollback/surface split as above; the error shows once (once-per-run guard) with manual recovery offered. After rollback, a re-queried status must show the label gone — a failed rollback is surfaced as such (§6), never reported as clean.

After any action the controller kicks `RestartLoopAsync()`. Once the startup phase closes, unreachable events never mutate anything: crash recovery is launchd's job, and deliberate stops aren't fought.

The daemon name is resolved once via the existing `DaemonNameResolver` chain, so the watched, started, and installed daemon can never diverge.

### 4.3 Skew → restart/takeover

**Triggers** (both feed one code path):

- On every `Connected`: the snapshot's `Daemon.Version` differs from the cached CLI version.
- On `Unreachable(daemon_incompatible)` carrying a hello `DaemonVersion` (decision 6) that differs: an old external daemon that fails the capability gate is *exactly* the takeover audience. The version propagates `CycleOutcome → Unreachable → AttachStatus`, and the client's transition dedupe keys on `(reason, daemonVersion)` so null→v1 or v1→v2 while incompatible re-emits. `daemon_unreachable` never triggers offers — transport silence is not version evidence.

**Classification** (fresh `service status --json` inside the gate): `unit_present` && canonical `binary_path` == canonical `install_binary_path` → **same-binary target** — the unit already runs the binary this app would install, so the dialog reads "Restart daemon to update". Anything else → **different-binary** — "Take over management". Path equality is *not* installer provenance (a terminal install with the same native CLI produces the same paths, and `ServiceInstall` records no marker); therefore **both** dialog copies carry the decision-3 disclosure that the unit will be rewritten and its baked settings re-captured.

**Accept path** — one sequence, branched on who actually owns the name (evidence revalidated first; §3.2):

| Evidence | Sequence |
|---|---|
| Ownership holds (`job_pid == daemon_pid`, job `running`) | `service install` — its bootout → rewrite → bootstrap stops the job it owns and starts the replacement. |
| `unit_present` but the unit's job does not own the reachable daemon (loaded-or-stale unit + manual daemon coexisting) | `service uninstall` **first** (a raw `daemon stop` with any unit present is a documented no-op success) → `daemon stop --name <X>` → **stop verification** → `service install`. |
| `unit_present=false` (manual daemon) | `daemon stop --name <X>` → stop verification → `service install`. |

**Stop verification:** the CLI's stop reports success after a best-effort ≤5 s wait; the controller does not trust it. It polls (bounded, ~10 s) until the attach is gone and a fresh dial fails, before installing. Timeout → abort; **the daemon was not proven stopped** and the recovery surface says exactly that (§6).

**Post-mutation verification:** identical to §4.2 (fresh attach at expected version + ownership), covering a concurrent manual start racing the bootstrap.

**Decline** is remembered per `(daemonVersion, cliVersion)` pair in the app state store (claim persisted before the dialog, §3.5): declining once means no nag on any later launch; a new pair asks again. At most one skew dialog per app run; dialogs never stack (§5 ordering).

### 4.4 User actions

**Start** runs inside the gate with a fresh status query: `unit_present && state=installed && daemon_pid null` → `service start` + post-start verification (§4.2); `unit_present=false` → today's detached `daemon start -d` (abandon-wait semantics preserved, §3.6); job `running` → just kick reattach; anything else → the reinstall affordance below (a Start click is not consent to rewrite a unit).

**Reinstall affordance** (present-but-unloaded plist, or surfaced coexistence): a **dialoged** operation reusing the takeover machinery verbatim — same operation gate, same in-gate revalidation, same decision-3 replacement disclosure (plus the degraded-PATH disclosure when the probe is unknown), same ownership-branched sequence, same stop and post-mutation verification. It is takeover with a different entry point, not a second code path.

## 5. PATH shim (macOS)

**Detection.** Via the terminal-PATH probe (§3.6): `command -v kcap`. The shim is considered when the probe *positively* finds nothing (unknown → no auto-offer) **and** the resolver has an absolute CLI path to link to.

**Pre-flight (unprivileged).** `lstat /usr/local/bin/kcap`:

- absent → installable;
- a symlink already resolving to the selected CLI → already installed; success, no prompt;
- anything else (foreign symlink, regular file, directory, broken link) → **conflict**: never overwritten, surfaced with the path and what was found. Detection missing it does not prove the destination is free — the entry may be outside PATH or non-executable.

**Install.** One admin prompt; the target is passed as argv, never interpolated into script source:

```
osascript -e 'on run argv' \
          -e 'do shell script "mkdir -p /usr/local/bin && ln -s " & quoted form of item 1 of argv & " /usr/local/bin/kcap" with administrator privileges' \
          -e 'end run' -- <target>
```

`ln -s` is deliberately non-forcing: anything appearing at the destination between pre-flight and elevation fails as a conflict instead of being clobbered (decision 4). `quoted form of` handles POSIX quoting; paths containing CR or LF are rejected before prompting; spaces/quotes/backslashes must round-trip.

**Post-install verification.** Re-run the probe. If `kcap` still doesn't resolve (login PATH omits `/usr/local/bin`), say so and show the line to add. Never report success on the symlink alone.

**Cancel vs failure.** A user cancel surfaces as AppleScript error `-128`, detected by the `-128` code in stderr (locale-stable). Cancel → recorded, never auto-offered again. Non-cancel failure → show the error **with a copyable fallback rendered through a real POSIX single-quote escaper** (`'` → `'"'"'`): `sudo mkdir -p /usr/local/bin && sudo ln -s <escaped-target> /usr/local/bin/kcap`. The menu item stays.

**Offer surface.** The auto-offer happens at most **once ever** — the offered claim is persisted (atomically, before the dialog; §3.5) regardless of outcome — on first app run, after the startup phase (§3.2) closes; an immediate `Connected` closes the phase and releases the offer just like the completed unreachable branch. Dialogs are serialized: the shim offer never appears while the skew dialog is up. An **"Install command-line tool…"** tray-menu item stays available while the shim is applicable-but-absent.

**Constraint recorded for AI-1653:** the bundled CLI must live at a **stable path inside the .app bundle across auto-updates**, or the symlink breaks on every update.

## 6. Error handling

The controller acts only on positive evidence, degrades honestly, never loops — and **recovery surfaces are derived from re-queried evidence after the failure, never from an assumed normalized state** (a stop-verification timeout means the daemon was *not* proven stopped; a bootstrap failure leaves a written plist with an unloaded job; a failed bootout leaves a loaded job the hardened status can now see):

- **No CLI resolves** → lifecycle features off; tray keeps today's Stopped UX plus an honest status line ("kcap CLI not found").
- **`service status` fails or emits unparseable JSON** → *unknown*: no auto-install, no takeover offer, reason logged. Unknown never triggers mutations.
- **Terminal-PATH probe unknown** → silent mutations suppressed with an honest status line; dialoged mutations disclose and require consent; queries unaffected.
- **Auto-install/start fails, or post-mutation verification rolls back** → outcome surfaces through the same message lane as `StartDaemonAsync` failures; the once-per-run guard means it shows once and stops; manual recovery reflects the re-queried state.
- **Takeover/reinstall aborts mid-sequence** → re-query attach + unit + job + ownership; render the branch-specific state (e.g. "daemon still running, unit removed", "unit written but not started", "stop could not be confirmed") with the recoveries that are actually valid for it. The version pair is **not** marked declined — the next qualifying trigger re-offers.
- **Rollback itself fails** (hardened uninstall reports the label still loaded) → surfaced as exactly that, with terminal guidance; never reported as clean.
- **Stale-consent abort** (evidence changed between dialog and accept) → nothing mutated; one-line explanation; re-offer on the next qualifying trigger.
- **`--version` query fails / malformed / `unknown`** → skew detection disabled for the run, logged. No dialogs on garbage.
- **App state store missing/corrupt** → defaults (offer again rather than never); write failure → in-memory claim for this run, logged (§3.5).

## 7. Testing

TUnit throughout; controller and shim are plain services driven through `IProcessRunner` fakes — no real launchctl/osascript in CI. Windows CI leg: build path assertions with `Path.Combine` (known separator trap).

- **CLI:** `service status --json` — the full grid: plist absent (with and without an orphaned loaded job — the hardened launchd query); plist present + print failing (`not_installed`, `unit_present=true`, non-null `binary_path`); loaded-inactive; running with `job_pid` parsed; `daemon_pid` from the PID file (absent/unusable → null); `install_binary_path` present/missing. Hardened `uninstall`: injected bootout failure → non-zero exit, plist retained. snake_case contract; human output unchanged without `--json`.
- **Startup phase & arming:** `Connected` first → later `daemon_unreachable` mutates nothing; `daemon_incompatible` first → later `daemon_unreachable` mutates nothing; the completed unreachable branch closes the phase; arm claimed pre-await; mid-session unreachable inert; the §4.2 matrix including the `daemon_pid`-gated kickstart row and the present-but-unloaded row (no silent mutation).
- **Verification & ownership:** replayed `Connected` never satisfies verification; fresh `Connected` at wrong version fails it; fresh `Connected` + pid mismatch (manual daemon B owns lock, launchd job A transiently running) → correct rollback branch, manual daemon untouched (no `daemon stop` issued); deadline with job still `running` and no fresh attach → rollback; post-rollback re-query shows label gone, and an injected rollback failure is surfaced not swallowed; post-start (pre-existing unit) failure → `service stop` + surface, never uninstall.
- **Skew/takeover:** trigger matrix (equal → nothing; snapshot mismatch; `daemon_incompatible` + hello version → offer; version change while incompatible re-emits — null→v1, v1→v2 — through `CycleOutcome → Unreachable → AttachStatus`; `daemon_unreachable` → never); same-binary vs different-binary classification (npm-launcher shape, symlinked paths, equal-path terminal-installed unit gets the recapture disclosure too); the three accept sequences as exact argv order including uninstall-before-stop; stop verification (drops attach → proceeds; timeout → abort, no install, honest "not proven stopped" state); post-mutation verification; decline persisted before dialog, across runs, new pair re-offers; stale-consent abort.
- **Reinstall affordance:** enters the same gate/revalidation/disclosure/sequence as takeover (shared code path asserted); triggered from present-but-unloaded and coexistence surfaces.
- **Races:** status query resolving after `Connected` → discarded; user Start racing auto-install → serialized; dialog accept after generation change → abort; subscribe-before-pump.
- **Process/probe seams:** stdout returned; env overlay adds `KCAP_PROFILE`/`PATH` without replacing the environment; external cancellation abandons wait (detached start survives; shutdown during `daemon start -d`); internal timeout kills the whole tree, awaits exit, and forces a state reconcile before the next mutation (late-child-mutation test); `--version` parsing (prefix strip, multiline reject, `--no-update-check` in argv); probe `-lic` sentinel parse robust to chatter, `.zshrc`-only PATH found, `-lc` fallback, `$SHELL` unset → `/bin/zsh`, both fail → unknown semantics (silent mutations off, dialogs disclose).
- **Shim:** pre-flight lstat shapes (absent / correct symlink / foreign symlink / file / dir / broken link); target passed as argv, hostile paths round-trip, CR/LF rejected; `-128` → cancel persisted; non-cancel failure → POSIX-escaped `sudo` fallback (round-trip test with spaces, single/double quotes, backslashes); post-install probe re-run (not-on-PATH → guidance, no false success); offered-once claim persisted before dialog, held in memory on write failure; auto-offer waits for the startup phase incl. the immediate-`Connected` path; menu-item visibility.
- **App state store:** serialized concurrent writes lose nothing; atomic replace (truncated temp never corrupts the live file); missing/corrupt → defaults.
- **E2E stays manual** (no bundle to automate against yet). Checklist for the PR:
  1. Fresh run, valid profile, no unit → unit appears in `~/Library/LaunchAgents`, daemon attaches; plist env carries the terminal PATH and pinned `KCAP_PROFILE`.
  2. Manual `kcap daemon start` racing first app launch → no spinning LaunchAgent remains; manual daemon still alive; app attached.
  3. `kcap daemon service stop` in a terminal mid-session → app shows Stopped, does NOT auto-restart; relaunch app → auto-starts (unit present, no lock owner).
  4. npm-installed unit on an older version → takeover dialog (different-binary copy, disclosure shown); accept → unit rewritten, new version attaches; decline → no re-prompt after app restart.
  5. Loaded-stopped unit + manual daemon under the same name, older version → accept runs uninstall → stop → install; no spin; ends attached and ownership-verified.
  6. Old daemon that fails the hello capability gate → takeover offer still appears (incompatible trigger).
  7. Machine whose `kcap`/nvm paths live only in `.zshrc` → probe finds them; no spurious shim offer; installed unit's PATH resolves `claude`.
  8. Shim: no `kcap` on PATH → offer; accept → `/usr/local/bin/kcap` works in a new terminal; deny → no re-offer, menu item present. Pre-existing file at the destination → conflict message, file untouched.

## 8. Scope boundaries

- **AI-1653 keeps:** bundling, the bundle-relative resolver arm, the stable-in-bundle-path constraint (§5), auto-update atomicity, signing/notarization.
- **AI-1655 keeps:** the wizard, and the **consent default flip to `prompt`** — explicitly not this PR.
- **AI-1657 keeps:** Windows control channel; the shim interface and the already-cross-platform `IServiceManager` leave it open.
- Zero daemon and wire-protocol changes; CLI changes per decision 1; one additive Core client propagation (decision 6). One PR (references AI-1654 and its GitHub issue per repo convention; README + help-text updates ride along).
