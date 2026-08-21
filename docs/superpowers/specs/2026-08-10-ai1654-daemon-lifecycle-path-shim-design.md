# AI-1654 — Daemon lifecycle management + PATH shim (desktop supervisor slice 3)

**Date:** 2026-08-10 (revised after spec-review rounds 1–6, Codex reviewer)
**Status:** Approved design.
**Issue:** AI-1654. Umbrella spec: [2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §4 "Lifecycle" and §11.
**Prior slices:** control IPC + consent (AI-1623/AI-1648), app shell (AI-1650), tray/agents/stop (AI-1651), consent prompts + Activity (AI-1652). AI-1653 (bundling/signing) has NOT landed — this slice keeps the dev-time CLI seam and records constraints on AI-1653.

## 1. Problem

The app can attach to a daemon and start one ad hoc (`kcap daemon start -d`), but nothing manages the daemon's *lifecycle*: nothing installs it as a LaunchAgent so agents survive logout/reboot, nothing notices an externally installed daemon on a different version, and the bundled CLI is invisible to terminals. Umbrella §4 assigns all three to the app; this slice delivers them pre-wizard (AI-1655 later folds them into onboarding).

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **Everything through the CLI (approach A), with every destructive sequence inside ONE CLI transaction.** The app shells the resolved `kcap` for queries and single-call mutations; it never orchestrates a multi-command destructive sequence — the app can die between commands, and no surviving process would own the remainder. The CLI work in this slice: `daemon service status --json`, the service-verb hardenings, and the transactional `--verify` / `--replace` modes §3.4 enumerates. Rejected: moving service machinery into `Capacitor.Cli.Core` for in-proc use; hybrid in-proc reads; app-side verification/rollback (not crash-safe); an app-side persisted mutation journal (recovers only if the app runs again; the CLI that performed the mutation is the only process that can guarantee its completion). |
| 2 | **Auto-install on startup attach failure** (Docker Desktop model): no unit + preconditions (§4.1) → the app silently installs via `service install --verify`. A stopped-but-installed unit auto-starts on relaunch via `service start --verify` (the umbrella's "the app starts a down daemon", E2E item 3). Auto-action eligibility exists only during the startup phase and closes permanently on the first terminal **attach** outcome (§3.2) — a daemon the user stops mid-session is never fought. |
| 3 | **Takeover offer on version mismatch only.** Same-version foreign setups keep working untouched. The offer is a dialog — never silent (umbrella §4). Every unit rewrite's dialog **discloses that the existing service unit will be replaced and its baked settings re-captured**, and that a failed replacement ends in the §3.4 verified-safe failure state (no unit) rather than the old unit restored — reconstructing plists is exactly the knowledge decision 1 keeps out of the app, and a half-restored unit is worse than an honest absence. **No transaction takes a destructive step before its viability is revalidated inside the transaction** (§3.4). |
| 4 | **PATH shim only when no `kcap` resolves on the user's terminal PATH**, and never over an existing filesystem entry: install is `lstat`-checked and non-forcing (§5). An existing npm install is never shadowed or replaced. Denial is remembered; the app works without the shim (terminal features degrade, umbrella §11). |
| 5 | **The tray Start action becomes service-aware** (§4.4). Prevents a user-clicked Start from spawning a detached daemon that races a loaded label for the name lock. |
| 6 | **Zero wire-protocol changes; one deliberate daemon exit-code change** (amended 2026-08-10 from "zero daemon changes"): a **deliberate refusal exits 0** — both a server `NameInUse` rejection and a **supervised** daemon's local name-lock refusal (supervised = the unit-baked `KCAP_DAEMON_SUPERVISED` matches its sanitized name; a manual `kcap daemon start` keeps its non-zero exit for scripts). Refusals are not crashes — and under `KeepAlive SuccessfulExit=false` the old non-zero exits made a unit respin forever against a name registered elsewhere or held locally (the plain, non-`--verify` `service start`/`install` verbs stay available to terminal users, so the daemon itself must refuse quietly; no verification window can cover them — for `NameInUse`, the daemon opens its local socket before `ServerConnection` connects, so no post-hello check can outwait it). Plus one additive client-side change in `Capacitor.Cli.Core`: the hello reply's `DaemonVersion` propagates `CycleOutcome` → `LocalControlEvent.Unreachable` → `AttachStatus`, deduped on `(reason, daemonVersion)` (§4.3). |
| 7 | **Every CLI child the app spawns runs with a pinned profile** (`KCAP_PROFILE` env overlay; `--profile` additionally on `service install`) **and, when known, the terminal PATH** (§3.6). The known-PATH requirement applies exactly to **unit-writing** mutations (unknown → no silent install; dialoged installs disclose the degraded PATH and proceed only with that consent). Starting an existing unit is exempt — it recaptures nothing — and an unknown probe neither blocks it nor closes the startup phase. |

## 3. Components

### 3.1 `CliResolver` (app, new)

Extracts and extends the inline resolution in `DaemonClientService.CreateDefaultAsync`:

1. `KCAP_APP_CLI_PATH` env override (absolute path, dev seam — app-shell design decision 6);
2. *(future, AI-1653)* bundle-relative path — a one-line arm added when bundling lands;
3. `kcap` on PATH.

Shared by the daemon client, the lifecycle controller, and the shim installer. At startup it runs `<cli> --version --no-update-check` once and caches the result. Parsing is strict: single line `kcap <version>`; prefix stripped; compared against snapshot/hello versions (bare `AssemblyInformationalVersion`). Multiline/malformed/`unknown` → skew detection disabled for the run. No CLI → every lifecycle feature degrades honestly (§6). The app does **not** compute the daemon install target itself (wrong for the npm install) — the CLI reports it (§3.4).

### 3.2 `DaemonLifecycleController` (app, new)

The state machine of this slice (§4). Subscribes to the existing `AttachStatus`/`Snapshots` streams, queries service state via the CLI, and performs each mutation as **one** CLI call. Concurrency contract (all load-bearing):

- **One startup-phase state, shared by lifecycle and shim.** The phase ends — permanently, for this app run — at the first terminal **attach** outcome: `Connected`, `Unreachable(daemon_incompatible)`, the completed §4.2 `daemon_unreachable` branch, or the no-CLI determination. An unknown terminal-PATH probe is **not** phase-closing (decision 7). After the phase closes, unreachable events never mutate anything.
- **One reconciliation query per startup, on every path** — including immediate `Connected`. It surfaces (never silently mutates) every inconsistent combination: a loaded label that does not own the attached daemon; a **stale** transaction marker (§3.4 — marker present, service flock free; an *active* transaction, `txn_active`, is waited out and re-queried, not offered as repair); a present-but-unloaded plist while a manual daemon owns the name. Each routes to the §4.4 affordance.
- **One async operation gate** serializes every lifecycle mutation.
- **Evidence is revalidated inside the gate immediately before mutating**: fresh `service status --json`, current attach state, current version pair, §4.1 preconditions. Stale consent aborts with a message. (The transaction revalidates viability again internally — the gate check exists for honest dialogs, not as the safety boundary.)
- **A connection-generation token** invalidates stale continuations; the §4.2 attach confirmation counts only events observed strictly **after** the mutation began.
- **The once-per-run arm is claimed before the first await**; **the controller subscribes before the attach pump starts**.

### 3.3 `PathShimInstaller` (app, new)

Detection + one-prompt symlink install (§5). Small interface; macOS implementation now, no-op elsewhere (AI-1657 keeps Windows open).

### 3.4 CLI: `service status --json` + hardenings + the `--verify`/`--replace` transaction

```json
{
  "service_id": "default",
  "unit_present": true,
  "state": "not_installed" | "installed" | "running",
  "binary_path": "...",
  "install_binary_path": "...",
  "job_pid": 1234,
  "daemon_pid": 1234,
  "txn_marker": false,
  "txn_active": false
}
```

**Classification and fields:**

- **Tri-state launchd classification** underpins everything: `launchctl print`/`bootout` results classify `loaded` / `absent` / `unknown` — `absent` only on the positive could-not-find-service signature; permission errors, tool failures, anything unrecognized → `unknown`. The `IServiceManager` surface gains this dimension. **`status --json` fails non-zero on `unknown`.**
- `state` keeps the existing mapping; the launchd query runs **even when the plist is absent**. `binary_path`: the unit's baked `ProgramArguments[0]`, else null. `install_binary_path`: `ResolveDaemonBinary()`; null when missing. Canonicalized-path comparisons. `job_pid`: from `launchctl print` when running. `daemon_pid`: the PID-file owner, **live identity-validated** via `IsOurDaemon` — null otherwise. `job_pid == daemon_pid` (both non-null) is **ownership**. `txn_marker`/`txn_active`: below.
- **Per-label cross-process lock:** all **mutating** service verbs serialize on a **distinct** per-label advisory flock — read-only `service status` is exempt and instead **probes the lock non-blockingly** (try-acquire/release) to report `txn_active`; a status that queued on the flock could only ever observe `txn_active=false` after the holder finished, making the wait-not-repair behavior unobservable. The flock is its own file, never the daemon's `DaemonLockPaths.LockPath` (that one is held for the daemon's entire lifetime: reusing it would deadlock replacement while a daemon runs, and install would hold the very lock the bootstrapped daemon needs). It lives under the **fixed** `DaemonLockPaths.Directory` namespace, NOT `PathHelpers.ConfigPath` — `KCAP_CONFIG_DIR` moves the config root while the launchd label is global, so a config-scoped lock or marker is split-brain across config dirs. **Owned at the command layer**: acquired before pre-state capture, held through classification, every destructive step, write, bootstrap, the full readiness poll, rollback, and restore verification. Internal rollback never re-acquires; the lock file is never unlinked; contention → bounded wait, then coded failure. *Mixed-version residual risk:* an older terminal CLI does not honor this lock — the classifier before every write and the final on-disk recheck are the defensive postconditions; accepted and documented.
- **`service uninstall`:** on non-zero `bootout`, re-query under the lock: `absent` → idempotent success, plist deleted; `loaded`/`unknown` → plist **retained**, non-zero exit. Success asserts label absence AND file removal.
- **`service stop`/`start`:** stop performs `bootout` (label unloaded, plist retained); start bootstraps when unloaded, else kickstarts.

**The `--verify` transaction (`service install [--replace] --verify`, `service start --verify`):** one CLI process owns mutation, verification, and rollback end to end.

- **Marker first, durable, and phase-recording:** before the first destructive step the transaction writes a versioned marker — `{DaemonLockPaths.Directory}/{label}.service-txn` (fixed namespace, same split-brain rule as the lock) — recording operation kind, current phase, captured pre-state, the intended verified-safe failure state, and (once written) the **fingerprint of the plist this transaction wrote** (program arguments + baked environment). The marker file *and its directory* are flushed before the first destructive step — rename atomicity prevents torn publication, not persistence ordering, and a marker that evaporates in the same power loss as the mutation it guards is no marker. Phase updates, the commit record, and removal follow the same ordering discipline. **Recovery authority is scoped by content:** a later transaction or the repair affordance may delete/normalize **only artifacts matching the recorded fingerprint**; anything else — e.g. a plist replaced by a lock-unaware older CLI after the death — surfaces for explicit repair, never silent healing. A marker whose phase is *committed* (death between commit and marker removal) authorizes exactly one action: clearing the marker — never touching the healthy unit. `status --json` reports `txn_marker` plus **`txn_active`** (the service flock is currently held): an active transaction — e.g. the orphaned npm grandchild still completing after a GUI force-quit — is waited out, not offered as repair; only a stale marker is.
- **Viability revalidated inside install/replace, before the first destructive step:** non-null `ResolveDaemonBinary()`, usable pinned profile. `service start --verify` performs **no** viability check — it writes nothing, and a valid stopped unit must not be stranded because the invoking CLI's sibling target or current profile is unavailable.
- **Install's own initial bootout obeys the classifier:** the plist is rewritten only after the label is positively `absent`; `loaded`/`unknown` → coded abort, nothing written.
- **`--replace` executes the explicit ownership matrix**, inside the same process and lock: **owning loaded label** (the common skew case — the label's job *is* the daemon) → classifier-gated bootout FIRST, then termination confirmed (label `absent`, validated PID gone); **non-owning loaded label + manual owner** → boot out the label first, then kill the validated owner via an **internal raw-kill helper below `StopByName`** — no console I/O, no service-installed check (the public guard would no-op exactly here), no nested lock acquisition (the flock is already held) — then termination confirmed (PID gone AND a fresh socket dial fails); **no live owner** → no stop step. Then install. Without `--replace`, a contended name is a coded abort with nothing touched.
- **Success predicate — ownership AND readiness AND disk:** ownership (`job_pid == daemon_pid`); a **successful hello** on the daemon's socket (the PID file lands immediately after lock acquisition, long before the host builds — ownership alone blesses doomed incarnations); then a **final recheck**: ownership again (same incarnation) AND the on-disk unit — `unit_present` with the plist matching the transaction's recorded fingerprint — because the mixed-version postcondition must inspect *disk*, not the process: a lock-unaware old CLI can overwrite the plist under a healthy job, and PID checks would commit a unit whose next login runs the old binary. The race after this final check is the documented residual. One window remains between hello and server registration (the local socket opens before `ServerConnection` connects): a `NameInUse` rejection can land after commit — decision 6's daemon change makes that exit **0**, so the unit comes to rest *stopped*, not respinning, and the normal attach UX surfaces it.
- **The hello probe is a bounded one-shot, per-verb contract** — not `LocalControlClient`'s gated attach (which rejects hello replies lacking `status/1` and discards name/protocol/version — exactly wrong here). Install/replace validates protocol, daemon name, and version (mismatch → rollback). `service start --verify` accepts any **well-formed** hello — including a capability-incompatible older daemon: a successfully started old unit is precisely what lets the app then emit `daemon_incompatible` and offer takeover. Malformed/absent hello → not ready. The probe obeys the phase deadline.
- **Two-phase internal deadline:** a forward-phase cutoff plus a **separately reserved rollback budget** — one shared bound cannot be exhaustible by forward work and still promise time to restore. Every child `launchctl` invocation gets the remaining phase time (`ServiceProcess` gains timeout support — today it blocks unboundedly in `ReadToEnd`/`WaitForExit`); a hung child is tree-killed, awaited, and launchd **reclassified** before proceeding (the request may have applied before the kill). Forward cutoff → abort into rollback with the full reserve; rollback exhausting its reserve → retain the marker, exit with the attention code. Callers set their kill-timeout strictly above forward + reserve (§3.6).
- **Verified-safe failure states** (deliberately not "the exact pre-operation state" — two shapes legitimately differ): fresh install → label `absent` + file removed; replacement install → label `absent` + file removed (the replaced unit is **not** restored — decision 3's disclosed contract); bootstrap-start → label `absent`, plist retained; kickstart-start → label booted out, plist retained (pre-op was loaded-inactive; unloaded is the safe reachable state, own code). Each restore is itself verified; restore-verification failure exits with a distinct code (§6 attention state).
- **Closed-stdio tolerance:** in the npm topology the native binary is a *grandchild* blocking under Node's `execFileSync(stdio: "inherit")` (the launcher's "exec" comment is wrong — it blocks), sharing the GUI's pipes; when the GUI dies those pipes close. Console writes in the transaction are best-effort — a broken pipe never aborts verification or rollback.

snake_case via a source-generated JSON context (AOT rule). Human output unchanged without `--json`. Help text + README's daemon-service section update in the same PR.

### 3.5 App state store

`~/.config/kcap/app-state.json` (via `PathHelpers.ConfigPath` — app UX state only; nothing lifecycle-safety-bearing lives here, that is the fixed-namespace marker's job). One **serialized store service**; atomic temp+rename writes; missing/corrupt → defaults. One-shot claims (shim offered, decline pairs) **persisted before the dialog is shown**; persist failure → in-memory claim for the run, logged. Nothing secret.

### 3.6 Process + shell-environment seams (app)

**`IProcessRunner`** grows to `(ExitCode, Stdout, Stderr)` with per-call **environment overlay** (adds `KCAP_PROFILE`/`PATH` without replacing the rest), cancellation, and timeout:

- **Detach-on-cancel is scoped to `daemon start -d` and read-only queries**: cancellation abandons the *wait*, never the child.
- **Mutation children are never killed inside the CLI's transaction bound (forward + rollback reserve)**: the app's kill-timeout sits strictly above it, and app shutdown defers until the child exits. Even a force-quit is safe — the transaction completes in the child (§3.4). If the child exceeds the bound anyway (pathological), the app kills the **tree**, awaits it, re-queries, and surfaces an **attention state** from the evidence (`txn_marker`/`txn_active` make residue visible) — it never auto-mutates after a kill it had to force.

**Terminal-PATH probe.** What users mean by "the terminal" is an *interactive login* shell — `zsh -l -c` reads `.zprofile` but not `.zshrc` (nvm/npm/agent paths). The probe runs `$SHELL -lic 'printf "<sentinel>%s<sentinel>" "$PATH"'` (stdin `/dev/null`, bounded timeout), sentinel-parsed against chatter; fallback `-lc`; `$SHELL` unset → `/bin/zsh`. Both failing → **unknown**: unit-writing mutations suppressed (silent) or disclosed (dialoged); starting an existing unit and all queries proceed. The same probe answers shim detection (§5).

## 4. Lifecycle state machine

### 4.1 Preconditions (dialog-honesty layer)

Before offering or silently starting any unit-writing action, the app checks: a named profile with a valid absolute `http`/`https` server URL (pinned via `--profile` + `KCAP_PROFILE`); non-null `install_binary_path` from fresh status; the decision-7 PATH rule. Silent auto-install additionally requires the startup phase open. A dialog whose preconditions fail states what is missing instead of offering the action. The transaction re-checks viability internally (§3.4) — the app layer exists for honest UX, not safety.

### 4.2 Startup branch (runs at most once, only while the startup phase is open)

On the **first** `Unreachable(daemon_unreachable)` while the phase is open, the controller (inside the gate, generation-checked) queries `service status --json` and acts on positive evidence only. Rows are keyed on the loaded-label/job state **before** plist presence, same as §4.3/§4.4:

| Label/job | Plist | Action |
|---|---|---|
| `running` | — | Nothing. launchd thinks its job is up; existing backoff keeps retrying. |
| loaded, inactive | present, `daemon_pid` null | `service start --verify`. `daemon_pid` non-null → §4.4 affordance (coexistence). |
| loaded, inactive | absent (orphan label) | Nothing automatic — §4.4 affordance. |
| none | present (stopped unit — normal after the hardened stop), `daemon_pid` null | `service start --verify` (bootstraps). The relaunch-after-stop path (E2E item 3). `daemon_pid` non-null → affordance. |
| none | none (§4.1 passes) | `service install --verify` — silent auto-install. |
| none | none (§4.1 fails) | Nothing. Today's Stopped UX + the §6 honest reason. The wizard (AI-1655) owns fresh machines. |

The CLI transaction owns verification and rollback. The app layers the UX confirmation: a **fresh** attach `Connected` (observed strictly after the mutation began), at the expected version when a unit was installed. Transaction success + no fresh attach → degraded-but-owned surface ("daemon started, app not yet attached — retrying"), no rollback. Coded transaction failure → §6 message lane, once. After any action the controller kicks `RestartLoopAsync()`. Crash residue from prior runs is caught by the §3.2 reconciliation on every path. The daemon name is resolved once via the existing `DaemonNameResolver` chain.

### 4.3 Skew → restart/takeover

**Triggers** (one code path): on every `Connected`, snapshot `Daemon.Version` ≠ cached CLI version; on `Unreachable(daemon_incompatible)` carrying a differing hello `DaemonVersion` (decision 6, deduped on `(reason, daemonVersion)` so null→v1/v1→v2 re-emit). `daemon_unreachable` never triggers offers.

**Classification** (fresh status inside the gate): `unit_present` && canonical `binary_path` == canonical `install_binary_path` → **same-binary target** ("Restart daemon to update"); anything else → **different-binary** ("Take over management"). Path equality is *not* installer provenance; **both** copies carry the decision-3 replacement/recapture disclosure.

**Accept path — one CLI call:** `service install --replace --verify`. The transaction internally: revalidates viability → executes the §3.4 ownership matrix (owning label: bootout-first + confirm; non-owning label + manual owner: bootout, then the internal raw-kill helper + confirm; no live owner: no stop) → rewrites only on positive label absence → bootstraps → ownership + hello + version + final disk recheck → or rolls back to the verified-safe failure state. The app contributes dialogs, the §4.2 fresh-attach UX confirmation, and §6 surfaces — nothing destructive.

**Decline** is remembered per `(daemonVersion, cliVersion)` pair (claim persisted before the dialog, §3.5); a new pair asks again. At most one skew dialog per app run; dialogs never stack (§5 ordering).

### 4.4 User actions

**Start** runs inside the gate with a fresh status query, branched **on the loaded label before the plist**:

1. Job `running` → just kick reattach.
2. A loaded label exists: plist present and `daemon_pid` null → `service start --verify`; `daemon_pid` non-null (coexistence) or plist absent (orphan label) → the dialoged affordance — detached-starting past a loaded label hands the name lock to a contender the label respawns against.
3. No loaded label: plist present and `daemon_pid` null → `service start --verify`; plist present and `daemon_pid` non-null → affordance; nothing at all → today's detached `daemon start -d`.

A Start click is never consent to rewrite a unit.

**Reinstall/repair affordance** (orphan label, coexistence, **stale** `txn_marker`, or an explicitly chosen rewrite): a **dialoged** entry into the same single-call transaction — §4.1 dialog checks, replacement + degraded-PATH disclosures, then `service install --replace --verify`. Repair honors the marker's recovery authority (§3.4): fingerprint-matching residue is cleaned; anything else is presented, not paved. One code path with takeover.

## 5. PATH shim (macOS)

**Detection.** Via the terminal-PATH probe (§3.6): `command -v kcap`. Considered only when the probe *positively* finds nothing (unknown → no auto-offer) **and** the resolver has an absolute CLI path to link to.

**Pre-flight (unprivileged).** `lstat /usr/local/bin/kcap`: absent → installable; a symlink already resolving to the selected CLI → success, no prompt; anything else → **conflict**, never overwritten, surfaced with what was found.

**Install.** One admin prompt; the target passed as argv, never interpolated:

```
osascript -e 'on run argv' \
          -e 'do shell script "mkdir -p /usr/local/bin && ln -s " & quoted form of item 1 of argv & " /usr/local/bin/kcap" with administrator privileges' \
          -e 'end run' -- <target>
```

`ln -s` non-forcing (a race lands as a failed creation, not a clobber). CR/LF-containing paths rejected pre-prompt; spaces/quotes/backslashes round-trip.

**Post-install verification.** Re-run the probe; if `kcap` still doesn't resolve (login PATH omits `/usr/local/bin`), say so and show the line to add. Never report success on the symlink alone.

**Cancel vs failure.** Cancel = AppleScript `-128` in stderr (locale-stable) → recorded, never auto-offered again. Other failures → error + copyable fallback through a real POSIX single-quote escaper (`'` → `'"'"'`): `sudo mkdir -p /usr/local/bin && sudo ln -s <escaped-target> /usr/local/bin/kcap`. Menu item stays.

**Offer surface.** At most **once ever** (claim persisted before the dialog), on first app run, after the startup phase closes (immediate `Connected` closes it too). Dialogs serialized — never during the skew dialog. An **"Install command-line tool…"** tray-menu item stays while applicable-but-absent.

**Constraint recorded for AI-1653:** the bundled CLI must live at a **stable path inside the .app bundle across auto-updates**, or the symlink breaks on every update.

## 6. Error handling

Positive evidence only; honest degradation; no loops; **recovery surfaces derive from re-queried evidence, never an assumed normalized state**:

- **No CLI** → lifecycle off; Stopped UX + "kcap CLI not found".
- **Status fails / unparseable / launchd `unknown`** → *unknown*: no mutations, logged.
- **PATH probe unknown** → unit-writing suppressed (silent) or disclosed (dialoged); start-existing exempt; phase unaffected.
- **§4.1 preconditions fail** → silent paths do nothing (honest line); dialogs state what is missing; nothing touched.
- **Transaction coded failure** → it rolled back and verified the restore; the app surfaces the coded reason (once on silent paths) with recovery from fresh evidence. Distinct codes: contended-without-`--replace`, viability failure, bootout-unknown, stop-unconfirmed, readiness timeout, hello-validation failure, rollback-budget exhausted (marker retained), restore-verification failure (→ attention state with terminal guidance).
- **Transaction success, no fresh attach** → degraded-but-owned: "daemon started, app not yet attached — retrying"; no rollback. (Includes the post-commit `NameInUse` exit-0 residual — the unit rests stopped and the attach UX shows it.)
- **`txn_active`** → transaction still running (e.g. orphaned grandchild after force-quit): wait and re-query; no repair offer while the flock is held.
- **Forced kill after bound overrun** (pathological) → attention state from re-queried evidence + marker; never auto-mutated.
- **Stale-consent abort** → nothing mutated; one line; re-offer on next trigger.
- **`--version` garbage** → skew off for the run, logged.
- **State store missing/corrupt** → defaults; write failure → in-memory claim, logged.

## 7. Testing

TUnit throughout; the controller and shim are plain services on `IProcessRunner` fakes; CLI transaction tests drive real processes where the guarantee is process-lifetime-shaped. Windows CI leg: `Path.Combine` in path assertions.

- **CLI — classification/status:** tri-state classifier (`absent` only on could-not-find; permission/tool → `unknown`; `--json` exits non-zero on `unknown`); the full state grid incl. orphan labels and present-but-unloaded; `job_pid`; `daemon_pid` identity (absent/unusable, dead PID, token mismatch/PID reuse, legacy fallback); `install_binary_path`; `txn_marker` + `txn_active` exposure; snake_case; human output unchanged.
- **CLI — marker & recovery:** versioned content (operation, phase, pre-state, intended safe state, plist fingerprint); durability fault injection at write/flush/rename boundaries (not only SIGKILL); process-death matrix — before mutation / after label bootout / after owner stop / after plist write / after bootstrap / after verification-before-marker-removal — each recovering per the recorded phase; *committed*-phase marker → clears the marker only, unit untouched; fingerprint mismatch (foreign plist written after the death) → surfaced for explicit repair, never healed; cross-`KCAP_CONFIG_DIR`: a transaction under one config dir is visible to status/recovery under another (fixed namespace).
- **CLI — transaction sequencing:** viability revalidated inside install/replace before any destructive step (missing sibling / unusable profile → coded abort, nothing touched — even with `--replace`); start-existing exempt from viability (unusable install target/current profile + valid plist → start proceeds); install's initial bootout classifier-gated (`loaded`/`unknown` → abort, no write); the `--replace` ownership matrix as exact internal order — owning label: bootout first, termination confirmed; non-owning + manual owner: bootout, then the raw-kill helper (below `StopByName`: no console I/O, no service check, no lock re-acquisition), termination confirmed (PID gone + dial fails); no live owner: no stop step; contended without `--replace` → coded abort.
- **CLI — verify predicate:** lock-acquired-then-exit-before-socket → readiness never satisfied → rollback; repeated-crash loop → forward cutoff → rollback; hello-succeeds-then-server-`NameInUse` → daemon exits **0**, unit at rest stopped, no respin; supervised local lock-refusal → exit **0** (plain non-`--verify` `service start` against a manual lock holder comes to rest, no respin) while a manual `daemon start` lock-refusal keeps its non-zero exit (both decision-6 tests); status under a held transaction flock → non-blocking probe reports `txn_active=true` (never queues); per-verb hello: malformed/null hello and wrong name/protocol/version → install rolls back; start accepts a well-formed capability-incompatible hello (stopped-old-unit → successful start → the app's `daemon_incompatible` takeover offer follows); final recheck: incarnation swap between hello and recheck → fail; old-writer-replacing-the-plist between bootstrap and final check → rollback (disk fingerprint, not PID); verified-safe failure states per verb, each with its own code; restore-verification failure → distinct code.
- **CLI — deadline & lock:** never-exiting forward child → tree-kill, await, launchd reclassified, rollback completes within the reserved budget; rollback-budget exhaustion → marker retained + attention code; the service flock is distinct from the daemon lock (replacement acquires it while a daemon runs and holds `DaemonLockPaths.LockPath`); held from pre-state capture through restore verification (a concurrent verb blocks the whole way); rollback does not re-acquire; lock file never unlinked; bounded contention → coded failure; mixed-version residual documented with its postconditions asserted.
- **CLI — parent death & stdio:** direct-native AND npm-launcher (grandchild) topologies — parent killed during verification; the native child completes readiness/rollback, releases the flock, survives writes to closed pipes.
- **Controller — startup:** the §4.2 matrix per row incl. stop→relaunch auto-start and both `daemon_pid` non-null affordance rows; phase closes on `Connected`/`daemon_incompatible`/completed branch and **not** on unknown PATH; `Connected`-first → later unreachable inert; reconciliation on immediate `Connected` surfaces: non-owning loaded label, stale marker (and waits on `txn_active` — immediate-relaunch-while-the-orphan-still-runs test), present-unloaded plist + connected manual daemon; §4.1 failures → no mutation, honest surface; auto-start proceeds under unknown probe.
- **Controller — skew/takeover:** trigger matrix (equal → nothing; snapshot mismatch; incompatible + hello version → offer; null→v1, v1→v2 re-emit through `CycleOutcome → Unreachable → AttachStatus`; unreachable → never); same-binary vs different-binary (npm shape, symlinks, equal-path terminal unit still discloses recapture); accept = exactly one `service install --replace --verify` invocation (argv asserted; no app-side uninstall/stop on the path); decline persistence; stale-consent abort.
- **Races & shutdown:** stale query discarded; Start vs auto-install serialized; accept-after-generation-change aborts; subscribe-before-pump; shutdown defers past forward + reserve; kill-timeout strictly above it; pathological kill → attention state, no auto-mutation.
- **Process/probe seams:** stdout returned; env overlay; `--version` parsing (`--no-update-check` in argv); probe `-lic` sentinel robust to chatter, `.zshrc`-only PATH found, `-lc` fallback, `/bin/zsh` fallback, both-fail → unknown semantics.
- **Shim:** lstat shapes; argv-passed target, hostile-path round-trips, CR/LF rejected; `-128` cancel persisted; POSIX-escaped sudo fallback round-trip; post-install probe re-run; offered-once claim before dialog; auto-offer waits for the phase incl. immediate `Connected`; menu-item visibility.
- **App state store:** serialized writes; atomic replace; corrupt → defaults.
- **E2E stays manual.** Checklist:
  1. Fresh run, valid profile, no unit → unit in `~/Library/LaunchAgents`, daemon attaches; plist env carries terminal PATH + pinned `KCAP_PROFILE`.
  2. Manual `kcap daemon start` racing first launch → transaction rolls back, no spinning unit, manual daemon alive, app attached. Force-quit mid-install (dev binary and npm-launcher invocations) → same outcome. `kill -9` the CLI mid-install → stale marker → next app start surfaces the repair affordance; relaunching *immediately* while the orphaned transaction still runs shows waiting, not repair.
  3. Terminal `service stop` mid-session → Stopped, no auto-restart; relaunch → auto-starts (bootstrap).
  4. npm unit on an older version → takeover dialog (different-binary + disclosure); accept → single transaction replaces and verifies; decline → no re-prompt after restart.
  5. Loaded-stopped unit + manual daemon, older version → accept: one transaction clears label, stops owner, installs; ends attached and ownership-verified.
  6. Hello-incompatible old daemon → offer still appears.
  7. `.zshrc`-only PATH machine → probe finds it; no spurious shim offer; unit PATH resolves `claude`.
  8. Shim: offer/accept/deny flows; pre-existing destination file → conflict, untouched.

## 8. Scope boundaries

- **AI-1653 keeps:** bundling, the bundle-relative resolver arm, the stable-in-bundle-path constraint (§5), auto-update atomicity, signing/notarization.
- **AI-1655 keeps:** the wizard, and the **consent default flip to `prompt`**.
- **AI-1657 keeps:** Windows control channel; the shim interface and cross-platform `IServiceManager` leave it open.
- Zero wire-protocol changes; one daemon exit-code change (decision 6); CLI changes per decision 1/§3.4; one additive Core client propagation (decision 6). One PR (references AI-1654 and its GitHub issue; README + help-text updates ride along).
