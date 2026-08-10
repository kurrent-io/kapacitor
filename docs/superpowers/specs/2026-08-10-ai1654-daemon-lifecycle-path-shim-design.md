# AI-1654 — Daemon lifecycle management + PATH shim (desktop supervisor slice 3)

**Date:** 2026-08-10 (revised after spec-review rounds 1–4, Codex reviewer)
**Status:** Approved design.
**Issue:** AI-1654. Umbrella spec: [2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §4 "Lifecycle" and §11.
**Prior slices:** control IPC + consent (AI-1623/AI-1648), app shell (AI-1650), tray/agents/stop (AI-1651), consent prompts + Activity (AI-1652). AI-1653 (bundling/signing) has NOT landed — this slice keeps the dev-time CLI seam and records constraints on AI-1653.

## 1. Problem

The app can attach to a daemon and start one ad hoc (`kcap daemon start -d`), but nothing manages the daemon's *lifecycle*: nothing installs it as a LaunchAgent so agents survive logout/reboot, nothing notices an externally installed daemon on a different version, and the bundled CLI is invisible to terminals. Umbrella §4 assigns all three to the app; this slice delivers them pre-wizard (AI-1655 later folds them into onboarding).

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **Everything through the CLI (approach A).** The app shells the resolved `kcap` for every service operation and for detection. No plist/launchctl knowledge in the app. The CLI work in this slice: `daemon service status --json` plus the service-verb hardenings §3.4 enumerates — each required by a lifecycle guarantee, and each a correction of a contract the verb already claims to honor. **The service mutation, its ownership verification, and its rollback are one CLI transaction** (`--verify`, §3.4): an app crash or force-quit can never orphan a spinning unit, because the process that bootstrapped it also proves ownership or rolls it back. Rejected: moving service machinery into `Capacitor.Cli.Core` for in-proc use; hybrid in-proc reads; app-side verification with a persisted in-flight journal (recovers only if the app runs again, and cannot distinguish its own residue from a foreign unit as reliably as the CLI that just wrote it). |
| 2 | **Auto-install on startup attach failure** (Docker Desktop model): no unit + a valid profile + a known terminal PATH (§4.1) → the app silently installs the LaunchAgent via `service install --verify`. A stopped-but-installed unit auto-starts on relaunch via `service start --verify` (the umbrella's "the app starts a down daemon", E2E item 3). Auto-action eligibility exists only during the startup phase and closes permanently on the first terminal **attach** outcome (§3.2) — a daemon the user stops mid-session is never fought. |
| 3 | **Takeover offer on version mismatch only.** Same-version foreign setups keep working untouched. The offer is a dialog — never silent (umbrella §4). Every unit rewrite's dialog **discloses that the existing service unit will be replaced and its baked settings re-captured** (regardless of provenance, §4.3); the app does not attempt to restore a replaced unit on failure — a mid-sequence failure surfaces branch-specific recovery from re-queried evidence (§6). **No install sequence takes a destructive step before its preconditions hold** (§4.1): a takeover must never stop a working daemon and then discover the replacement cannot be installed. |
| 4 | **PATH shim only when no `kcap` resolves on the user's terminal PATH**, and never over an existing filesystem entry: install is `lstat`-checked and non-forcing (§5). An existing npm install is never shadowed or replaced. Denial is remembered; the app works without the shim (terminal features degrade, umbrella §11). |
| 5 | **The tray Start action becomes service-aware** (§4.4). Prevents a user-clicked Start from spawning a detached daemon that races a loaded label for the name lock. |
| 6 | **Zero daemon and zero wire-protocol changes.** One additive client-side change in `Capacitor.Cli.Core`: the hello reply's `DaemonVersion` is propagated end to end — `CycleOutcome` → `LocalControlEvent.Unreachable` → `AttachStatus` — with the client's transition dedupe keyed on `(reason, daemonVersion)` so a version change while incompatible re-emits (§4.3). Without this, the incompatible old daemon (the takeover offer's primary audience) could never receive the offer the umbrella promises. |
| 7 | **Every CLI child the app spawns runs with a pinned profile** (`KCAP_PROFILE` env overlay; `--profile` additionally on `service install`) **and, when known, the terminal PATH** (§3.6). Without the pin, the child's repo-aware profile resolution can capture a different tenant into the unit; without the PATH, `ServiceEnvironment.Capture` bakes the GUI app's minimal launchd PATH into the unit — a daemon that cannot find `claude`/`codex`. Because the baked PATH is load-bearing, **the known-PATH requirement applies exactly to unit-writing mutations** (unknown → no silent install; dialoged installs disclose the degraded PATH and proceed only with that consent). Starting an existing unit is exempt — it recaptures nothing — and an unknown probe neither blocks it nor closes the startup phase. |

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

- **One startup-phase state, shared by lifecycle and shim.** The phase ends — permanently, for this app run — at the first terminal **attach** outcome: `Connected`, or `Unreachable(daemon_incompatible)`, or the completed §4.2 `daemon_unreachable` branch, or the no-CLI determination. An unknown terminal-PATH probe is **not** a phase-closing outcome — it only vetoes the unit-writing action if §4.2 reaches one (decision 7). After the phase closes, unreachable events never mutate anything.
- **One reconciliation query per startup, on every path.** When the first attach outcome is an immediate `Connected` (which never enters §4.2), the controller still runs one `service status --json`: a loaded label that does not own the attached daemon — e.g. residue of a crash mid-mutation in a *previous* run — is surfaced via the §4.4 affordance. Never silent, never skipped because attach succeeded.
- **One async operation gate** serializes every lifecycle mutation — startup auto-install/start, takeover accept, the reinstall affordance, and the user Start action.
- **Evidence is revalidated inside the gate immediately before mutating**: fresh `service status --json`, current attach state, current version pair, install preconditions (§4.1). Stale consent aborts with a message.
- **A connection-generation token** invalidates stale continuations; §4.2's attach confirmation counts only events observed strictly **after** the mutation began — the `AttachStatus` behavior-subject's replayed value never satisfies it.
- **The once-per-run arm is claimed before the first await**; **the controller subscribes before the attach pump starts**.

### 3.3 `PathShimInstaller` (app, new)

Detection + one-prompt symlink install (§5). Small interface; macOS implementation now, no-op elsewhere (AI-1657 keeps Windows open).

### 3.4 CLI: `service status --json` + service-verb hardenings

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

- **Tri-state launchd classification (new, underpins everything below).** `launchctl print`/`bootout` results are classified `loaded` / `absent` / `unknown`: `absent` only on the positive could-not-find-service signature; permission errors, tool failures, and anything unrecognized are `unknown`. The `IServiceManager` status surface gains this query-outcome dimension — today `StatusFromPrint` maps every non-zero print to `NotInstalled`, conflating "not there" with "could not ask". **`status --json` fails non-zero on `unknown`** rather than emitting `not_installed`; the app treats that as §6 *unknown* (no mutations).
- `state` keeps the existing mapping, with the launchd query running **even when the plist is absent**, so an orphaned loaded label is reported (`unit_present=false`, job state visible) instead of an unconditional `not_installed`. All four (`unit_present`, job) combinations have defined §4 actions.
- `binary_path`: the unit's baked `ProgramArguments[0]` when `unit_present`, else null. `install_binary_path`: what an install by **this** CLI would bake — `ResolveDaemonBinary()` (correct through the npm launcher, `KCAP_APP_CLI_PATH`, and the future bundle alike); null when the sibling is missing. Comparisons on canonicalized paths.
- `job_pid`: from `launchctl print` when running, else null. `daemon_pid`: the name's PID-file owner, **live identity-validated** via the `IsOurDaemon` start-token check (rejects dead, recycled, foreign PIDs; name fallback only for legacy/uncomparable tokens) — null otherwise. `job_pid == daemon_pid` (both non-null) is the **ownership** predicate.
- **Per-label cross-process lock (new):** `service install/start/stop/uninstall` serialize on a per-label advisory flock (the `DaemonLock` pattern), so a terminal `service start` cannot interleave with an app-driven uninstall between its confirm and its delete. The app's async gate serializes only the app; this closes the cross-process TOCTOU.
- **`service uninstall` hardening:** on a non-zero `bootout` the label is re-queried under the lock. Confirmed `absent` → idempotent success, plist deleted. `loaded` or `unknown` → plist **retained**, non-zero exit. Uninstall success asserts both label absence and file removal — a deletion failure is never reported clean merely because the job is currently unloaded.
- **`service stop`/`service start` hardening:** stop performs `bootout` (label unloaded, plist retained — `launchctl kill SIGTERM` cannot stop a lock-losing KeepAlive job that is between short-lived incarnations when the signal lands); start bootstraps the label when unloaded, else kickstarts.
- **`--verify` on `service install` and `service start` (new):** after bootstrap/kickstart, the **same CLI process** polls (bounded, ~15 s) for ownership — the label's `job_pid` equal to the validated `daemon_pid`. Success → exit 0. Failure/timeout → restore the pre-operation state (install: bootout + delete the unit *it just wrote*; start: bootout, plist retained) and exit non-zero with a coded reason, verifying the restore (label `absent`; install also `unit_present=false`). One process owns mutation + verification + rollback, so neither an app crash nor a force-quit can orphan a spinning unit (the child is not killed by the parent's death; the app's §3.6 shutdown deferral covers the graceful path). The app always passes `--verify`; bare terminal semantics are unchanged.

snake_case via a source-generated JSON context (AOT rule). Without `--json` the human output is unchanged. Help text (`help-usage.txt`) and the README's daemon-service section update in the same PR.

### 3.5 App state store

`~/.config/kcap/app-state.json` (via `PathHelpers.ConfigPath`), app-owned. One **serialized store service** owns the file — controller and shim installer both go through it. Writes are **atomic** (temp file + rename); missing/corrupt → defaults, no crash. One-shot claims (shim offered, decline pairs) are **persisted before the dialog is shown**; if the persist fails, the claim holds in memory for this run (no re-offer now, possible re-offer next launch — accepted and logged). Nothing secret lives in it.

### 3.6 Process + shell-environment seams (app)

**`IProcessRunner`** grows to `(ExitCode, Stdout, Stderr)` with a per-call **environment overlay** (adds `KCAP_PROFILE`/`PATH` without replacing the rest), cancellation, and timeout:

- **Detach-on-cancel is scoped to `daemon start -d` and read-only queries**: cancellation abandons the *wait*, never the child (existing behavior, kept there and only there).
- **Service mutations are never abandoned mid-flight**: app shutdown defers — bounded by the CLI's own `--verify` window plus margin — until the in-flight mutation child exits. Even if deferral is violated (force-quit), the `--verify` transaction completes in the child (§3.4).
- **Internal timeout on mutating calls** — set strictly above the CLI's `--verify` window — kills the **entire process tree**, awaits its exit, then reconciles via a fresh status query before any further mutation.

**Terminal-PATH probe.** A GUI app inherits launchd's minimal PATH; what users mean by "the terminal" is an *interactive login* shell — `zsh -l -c` reads `.zprofile` but not `.zshrc`, where nvm/npm and agent paths commonly live. The probe runs `$SHELL -lic 'printf "<sentinel>%s<sentinel>" "$PATH"'` (stdin `/dev/null`, bounded timeout), parsing between sentinels; on failure/timeout it retries with `-lc`; `$SHELL` unset/unusable falls back to `/bin/zsh`. Both failing → **unknown**: unit-writing mutations are suppressed (silent) or disclosed (dialoged) per decision 7; starting an existing unit and all queries proceed. The same probe answers shim detection (§5).

## 4. Lifecycle state machine

### 4.1 Install preconditions

Every install sequence — silent auto-install, takeover accept, reinstall accept — revalidates, inside the gate and **before its first destructive step**:

- a named profile whose server URL is a valid absolute `http`/`https` URL (the daemon exits non-zero on an invalid URL; under `KeepAlive` an unstartable unit would spin), pinned via `--profile` + `KCAP_PROFILE`;
- a **non-null `install_binary_path`** from fresh status — a takeover must never stop a working daemon and then discover `ResolveDaemonBinary()` returns nothing;
- the terminal-PATH rule of decision 7 (known for silent; disclosed-and-consented for dialoged).

Silent auto-install additionally requires the startup phase to be open (§3.2). A dialoged offer whose preconditions fail is not shown as actionable — the dialog (or its trigger surface) states what is missing instead.

### 4.2 Startup branch (runs at most once, only while the startup phase is open)

On the **first** `Unreachable(daemon_unreachable)` while the phase is open, the controller (inside the gate, generation-checked) queries `service status --json` and acts on positive evidence only. Rows are keyed on the loaded-label/job state **before** plist presence, same as §4.3/§4.4:

| Label/job | Plist | Action |
|---|---|---|
| `running` | — | Nothing. launchd thinks its job is up; existing backoff keeps retrying. A wedged daemon stays a manual-UX case. |
| loaded, inactive (`installed`) | present, `daemon_pid` null | `kcap daemon service start --verify`. A non-null `daemon_pid` means some process owns the name while the unit's job is down — surface the coexistence (§4.4 affordance) instead. |
| loaded, inactive | absent (orphan label) | Nothing automatic. A loaded label without its unit file is a broken state — surfaced with the §4.4 affordance, never silently paved. |
| none | present (stopped unit — the normal state after the hardened `service stop`), `daemon_pid` null | `kcap daemon service start --verify` (bootstraps). This is the relaunch-after-stop path: stop the service in a terminal, relaunch the app, it starts again (E2E item 3; umbrella "the app starts a down daemon"). `daemon_pid` non-null → affordance. |
| none | none (§4.1 preconditions pass) | `kcap daemon service install --name <X> --profile <P> --verify` — silent auto-install. |
| none | none (preconditions fail) | Nothing. Today's Stopped UX plus the §6 honest reason. The wizard (AI-1655) owns fresh machines. |

**Verification split.** Ownership verification and rollback live in the CLI's `--verify` transaction (§3.4) — a manual daemon that holds or wins the name lock causes the CLI to roll back its own mutation and exit coded, with the manual daemon untouched. The app layers the UX confirmation on top: a **fresh** attach `Connected` (observed strictly after the mutation began; never the replayed value), at the expected version when the mutation installed a unit. CLI success + no fresh attach within the window → surfaced as §6 degraded state (no rollback — ownership is proven; the socket may simply be slow). CLI coded failure → §6 message lane, once, with manual recovery.

After any action the controller kicks `RestartLoopAsync()`. Crash residue from previous runs is caught by the §3.2 startup reconciliation query on every path, including immediate `Connected`.

The daemon name is resolved once via the existing `DaemonNameResolver` chain, so the watched, started, and installed daemon can never diverge.

### 4.3 Skew → restart/takeover

**Triggers** (both feed one code path):

- On every `Connected`: the snapshot's `Daemon.Version` differs from the cached CLI version.
- On `Unreachable(daemon_incompatible)` carrying a hello `DaemonVersion` (decision 6) that differs. The version propagates `CycleOutcome → Unreachable → AttachStatus`, deduped on `(reason, daemonVersion)` so null→v1 or v1→v2 while incompatible re-emits. `daemon_unreachable` never triggers offers — transport silence is not version evidence.

**Classification** (fresh `service status --json` inside the gate): `unit_present` && canonical `binary_path` == canonical `install_binary_path` → **same-binary target** ("Restart daemon to update"). Anything else → **different-binary** ("Take over management"). Path equality is *not* installer provenance (a terminal install with the same native CLI produces the same paths; no marker exists); **both** dialog copies carry the decision-3 replacement/recapture disclosure.

**Accept path** — preconditions (§4.1) first, then one sequence branched on who actually owns the name:

| Evidence | Sequence |
|---|---|
| Ownership holds (`job_pid == daemon_pid`, job `running`) | `service install --verify` — bootout → rewrite → bootstrap → ownership, one transaction. |
| A **loaded label** exists that does not own the reachable daemon — loaded-or-stale unit + manual daemon, or an orphan label | `service uninstall` **first** (clears the label whether or not a plist exists — benign-absence semantics; and a raw `daemon stop` would see the loaded label through the hardened status and no-op) → *if a validated live owner remains* (`daemon_pid` non-null): `daemon stop --name <X>` → **stop verification** → `service install --verify`. |
| **No loaded label** | *If a validated live owner exists* (`daemon_pid` non-null — a manual daemon): `daemon stop --name <X>` → stop verification → `service install --verify`. With no live owner (e.g. reinstall over a stopped unit with nothing running): straight to `service install --verify` — no `daemon stop` is issued against nothing (raw stop exits non-zero with no PID file and would abort the sequence spuriously). |

**Stop verification:** the CLI's stop reports success after a best-effort ≤5 s wait; the controller does not trust it. It polls (bounded, ~10 s) until the attach is gone and a fresh dial fails, before installing. Timeout → abort; **the daemon was not proven stopped** and the recovery surface says exactly that (§6).

**Decline** is remembered per `(daemonVersion, cliVersion)` pair (claim persisted before the dialog, §3.5): no nag on later launches; a new pair asks again. At most one skew dialog per app run; dialogs never stack (§5 ordering).

### 4.4 User actions

**Start** runs inside the gate with a fresh status query, branched **on the loaded label before the plist** (precedence is normative):

1. Job `running` → just kick reattach.
2. A loaded label exists (`state=installed`, plist or not): plist present and `daemon_pid` null → `service start --verify`; `daemon_pid` non-null (coexistence) or plist absent (orphan label) → the dialoged affordance — detached-starting past a loaded label would hand the name lock to a contender the label respawns against.
3. No loaded label: plist present and `daemon_pid` null → `service start --verify` (bootstraps the stopped unit — same as the startup row); plist present and `daemon_pid` non-null → affordance; nothing at all → today's detached `daemon start -d` (detach-on-cancel semantics, §3.6).

A Start click is never consent to rewrite a unit.

**Reinstall affordance** (orphan label, coexistence, or an explicitly chosen rewrite): a **dialoged** operation reusing the takeover machinery verbatim — same gate, in-gate revalidation, §4.1 preconditions, replacement + degraded-PATH disclosures, the same ownership-branched sequence (including the no-live-owner branch that skips `daemon stop`), stop verification, and `--verify` installs. It is takeover with a different entry point, not a second code path.

## 5. PATH shim (macOS)

**Detection.** Via the terminal-PATH probe (§3.6): `command -v kcap`. The shim is considered when the probe *positively* finds nothing (unknown → no auto-offer) **and** the resolver has an absolute CLI path to link to.

**Pre-flight (unprivileged).** `lstat /usr/local/bin/kcap`:

- absent → installable;
- a symlink already resolving to the selected CLI → already installed; success, no prompt;
- anything else (foreign symlink, regular file, directory, broken link) → **conflict**: never overwritten, surfaced with the path and what was found.

**Install.** One admin prompt; the target is passed as argv, never interpolated into script source:

```
osascript -e 'on run argv' \
          -e 'do shell script "mkdir -p /usr/local/bin && ln -s " & quoted form of item 1 of argv & " /usr/local/bin/kcap" with administrator privileges' \
          -e 'end run' -- <target>
```

`ln -s` is deliberately non-forcing: anything appearing at the destination between pre-flight and elevation fails as a conflict instead of being clobbered (decision 4). `quoted form of` handles POSIX quoting; paths containing CR or LF are rejected before prompting; spaces/quotes/backslashes must round-trip.

**Post-install verification.** Re-run the probe. If `kcap` still doesn't resolve (login PATH omits `/usr/local/bin`), say so and show the line to add. Never report success on the symlink alone.

**Cancel vs failure.** A user cancel surfaces as AppleScript error `-128`, detected by the `-128` code in stderr (locale-stable). Cancel → recorded, never auto-offered again. Non-cancel failure → show the error **with a copyable fallback rendered through a real POSIX single-quote escaper** (`'` → `'"'"'`): `sudo mkdir -p /usr/local/bin && sudo ln -s <escaped-target> /usr/local/bin/kcap`. The menu item stays.

**Offer surface.** The auto-offer happens at most **once ever** (claim persisted atomically before the dialog, §3.5) on first app run, after the startup phase (§3.2) closes — an immediate `Connected` closes the phase and releases the offer just like the completed unreachable branch. Dialogs are serialized: the shim offer never appears while the skew dialog is up. An **"Install command-line tool…"** tray-menu item stays available while the shim is applicable-but-absent.

**Constraint recorded for AI-1653:** the bundled CLI must live at a **stable path inside the .app bundle across auto-updates**, or the symlink breaks on every update.

## 6. Error handling

The controller acts only on positive evidence, degrades honestly, never loops — and **recovery surfaces are derived from re-queried evidence after the failure, never from an assumed normalized state**:

- **No CLI resolves** → lifecycle features off; tray keeps today's Stopped UX plus an honest status line ("kcap CLI not found").
- **`service status` fails, emits unparseable JSON, or reports launchd-query `unknown`** → *unknown*: no mutations, reason logged.
- **Terminal-PATH probe unknown** → unit-writing mutations suppressed (silent) or disclosed (dialoged); starting an existing unit is exempt (decision 7); the startup phase is not closed by it; queries unaffected.
- **§4.1 preconditions fail** → silent paths do nothing (honest status line); dialoged paths state what is missing; nothing has been stopped or removed.
- **CLI `--verify` fails (coded)** → the transaction rolled itself back and verified the restore; the app surfaces the coded reason through the `StartDaemonAsync` message lane, once (once-per-run guard on the silent path), with recovery derived from a fresh status query.
- **CLI `--verify` succeeds but no fresh attach arrives** → degraded-but-owned state: surfaced as "daemon started, app not yet attached — retrying"; no rollback.
- **Takeover/reinstall aborts mid-sequence** (stop-verification timeout; uninstall reporting `loaded`/`unknown`) → re-query attach + unit + label + ownership; render the branch-specific state (e.g. "daemon still running, unit removed", "stop could not be confirmed") with only the recoveries valid for it. The version pair is **not** marked declined.
- **Stale-consent abort** → nothing mutated; one-line explanation; re-offer on the next qualifying trigger.
- **`--version` query fails / malformed / `unknown`** → skew detection disabled for the run, logged.
- **App state store missing/corrupt** → defaults (offer again rather than never); write failure → in-memory claim for this run, logged (§3.5).

## 7. Testing

TUnit throughout; controller and shim are plain services driven through `IProcessRunner` fakes — no real launchctl/osascript in CI. Windows CI leg: build path assertions with `Path.Combine` (known separator trap).

- **CLI — status/classification:** the tri-state classifier (`absent` only on the could-not-find signature; permission/tool failures → `unknown`; `--json` exits non-zero on `unknown` instead of emitting `not_installed`); the full state grid incl. orphan labels (plist absent, label loaded) and present-but-unloaded (`not_installed`, `unit_present=true`, non-null `binary_path`); `job_pid` parsed; `daemon_pid` identity validation (absent/unusable file, dead PID, start-token mismatch/PID reuse, legacy token → fallback); `install_binary_path` present/missing; snake_case contract; human output unchanged.
- **CLI — mutations:** per-label lock serializes concurrent verbs (deterministic concurrent-bootstrap-between-confirm-and-delete test — the uninstall retains/fails instead of deleting under a fresh label); uninstall benign-absence (bootout non-zero + re-query `absent` → success, plist deleted) vs `loaded` and vs `unknown` (both → retained, non-zero), success requiring label absence AND file removal; stop = bootout retaining plist, post-stop status shows no label; start bootstraps when unloaded / kickstarts when loaded; `--verify` transactions: ownership success; lock-loser → rollback restores the exact pre-operation state (install: no label + no file; start: label absent, plist retained) with the manual daemon untouched, coded non-zero exit; rollback-restore verification failure → distinct coded failure; **parent-death test**: the mutation child completes verification/rollback after its parent is killed (real child process, the `ProcessRunnerTests` pattern).
- **Controller — startup:** the §4.2 matrix per row, including stop→relaunch (hardened-stop state auto-starts via bootstrap) and both `daemon_pid` non-null affordance rows; startup phase closes on `Connected` / `daemon_incompatible` / completed branch — and **not** on an unknown PATH probe; `Connected`-first → later unreachable mutates nothing; `daemon_incompatible`-first → likewise; the reconciliation query runs on immediate `Connected` and surfaces a non-owning loaded label (crash residue) via the affordance; §4.1 precondition failures (invalid/missing profile URL, null `install_binary_path`) → no mutation, honest surface; auto-start proceeds with the probe unknown.
- **Controller — skew/takeover:** trigger matrix (equal → nothing; snapshot mismatch; `daemon_incompatible` + hello version → offer; null→v1 and v1→v2 while incompatible re-emit through `CycleOutcome → Unreachable → AttachStatus`; `daemon_unreachable` → never); same-binary vs different-binary classification (npm-launcher shape, symlinked paths, equal-path terminal-installed unit still gets the recapture disclosure); accept sequences as exact argv order — ownership → install only; loaded non-owning label → uninstall first, `daemon stop` only when a validated owner remains; **no-label + no live owner → install directly, no `daemon stop` issued**; preconditions checked before the first destructive step (missing target / invalid profile → old daemon and unit untouched); stop verification (proceeds/timeout-aborts); decline persistence and stale-consent abort.
- **Races & shutdown:** status query resolving after `Connected` → discarded; user Start racing auto-install → serialized; dialog accept after generation change → abort; subscribe-before-pump; shutdown during each mutating operation defers until the child exits — only detached `daemon start -d` abandons the wait; internal mutation timeout (> `--verify` window) kills the tree, awaits, reconciles.
- **Process/probe seams:** stdout returned; env overlay adds `KCAP_PROFILE`/`PATH` without replacing the environment; `--version` parsing (prefix strip, multiline reject, `--no-update-check` in argv); probe `-lic` sentinel parse robust to chatter, `.zshrc`-only PATH found, `-lc` fallback, `$SHELL` unset → `/bin/zsh`, both fail → unknown semantics.
- **Shim:** pre-flight lstat shapes (absent / correct symlink / foreign symlink / file / dir / broken link); target passed as argv, hostile paths round-trip, CR/LF rejected; `-128` → cancel persisted; non-cancel failure → POSIX-escaped `sudo` fallback (round-trip: spaces, single/double quotes, backslashes); post-install probe re-run (not-on-PATH → guidance, no false success); offered-once claim persisted before dialog, held in memory on write failure; auto-offer waits for the startup phase incl. immediate `Connected`; menu-item visibility.
- **App state store:** serialized concurrent writes lose nothing; atomic replace; missing/corrupt → defaults.
- **E2E stays manual** (no bundle to automate against yet). Checklist for the PR:
  1. Fresh run, valid profile, no unit → unit appears in `~/Library/LaunchAgents`, daemon attaches; plist env carries the terminal PATH and pinned `KCAP_PROFILE`.
  2. Manual `kcap daemon start` racing first app launch → no spinning LaunchAgent remains (CLI `--verify` rolled back); manual daemon still alive; app attached. Force-quit the app during the install → same outcome.
  3. `kcap daemon service stop` in a terminal mid-session → app shows Stopped, does NOT auto-restart; relaunch app → auto-starts (bootstraps the stopped unit).
  4. npm-installed unit on an older version → takeover dialog (different-binary copy, disclosure shown); accept → unit rewritten, new version attaches; decline → no re-prompt after app restart.
  5. Loaded-stopped unit + manual daemon under the same name, older version → accept runs uninstall → stop → install; no spin; ends attached and ownership-verified.
  6. Old daemon that fails the hello capability gate → takeover offer still appears (incompatible trigger).
  7. Machine whose `kcap`/nvm paths live only in `.zshrc` → probe finds them; no spurious shim offer; installed unit's PATH resolves `claude`.
  8. Shim: no `kcap` on PATH → offer; accept → `/usr/local/bin/kcap` works in a new terminal; deny → no re-offer, menu item present. Pre-existing file at the destination → conflict message, file untouched.

## 8. Scope boundaries

- **AI-1653 keeps:** bundling, the bundle-relative resolver arm, the stable-in-bundle-path constraint (§5), auto-update atomicity, signing/notarization.
- **AI-1655 keeps:** the wizard, and the **consent default flip to `prompt`** — explicitly not this PR.
- **AI-1657 keeps:** Windows control channel; the shim interface and the already-cross-platform `IServiceManager` leave it open.
- Zero daemon and wire-protocol changes; CLI changes per decision 1/§3.4; one additive Core client propagation (decision 6). One PR (references AI-1654 and its GitHub issue per repo convention; README + help-text updates ride along).
