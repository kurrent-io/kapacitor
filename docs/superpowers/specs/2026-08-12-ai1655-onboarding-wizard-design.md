# AI-1655 — First-run onboarding wizard (desktop supervisor slice 3)

**Date:** 2026-08-12 (revised after spec-review rounds 1–3, Codex reviewer)
**Status:** Approved design.
**Issue:** AI-1655. Umbrella spec: [2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §6–7.
**Prior slices:** control IPC + consent (AI-1623/AI-1648), app shell (AI-1650), tray/agents/stop (AI-1651), consent prompts + Activity (AI-1652), daemon lifecycle + PATH shim (AI-1654). AI-1653 (bundling/signing) has NOT landed — the wizard keeps the `KCAP_APP_CLI_PATH` dev seam and works from source.

## 1. Problem

A fresh desktop machine has no profile, no token, no hooks, no daemon — and the app today shows a
main window that can only say "daemon unreachable". AI-1654's lifecycle controller deliberately
does nothing on fresh machines (its §4.2: "The wizard (AI-1655) owns fresh machines"). Umbrella §7
assigns first-run onboarding to a wizard: PATH shim, connect, login, harness hook setup, historical
import, daemon enablement — and moving the app-managed daemon's consent default off the
upgrade-safe `allow`, which is what actually answers the silent-launch complaint on desktop
machines. The 2026-08-10 issue amendment folds **create a workspace** into scope: the branch
umbrella decision 6 reserved, riding the existing WorkOS self-service provisioning backend
(AI-914/915/916) through the CLI's existing create-tenant flow (AI-1110) — no new server-side work.

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **Trigger: the wizard opens only when setup is incomplete.** The gate is a local, side-effect-free eligibility check (`OnboardingGate`, §4) returning an explicit reason: no resolvable profile, no canonical `http(s)` server URL, or no usable token. "Usable" is **provider-aware, matching `TokenStore`'s real refresh rules**: the token file must exist for the resolved profile, be stamped for the profile's canonical server, and be unexpired OR refresh-capable — where GitHubApp is *always* refresh-capable (server `/auth/refresh`; its `RefreshToken` is normally null) and WorkOS requires both `RefreshToken` and `ClientId`. A profile whose persisted provider stamp is `none` **for the profile's current server identity** (§4) needs no token at all. No server round-trip, no refresh side effects. Configured machines never see the wizard. Rejected: always-on-first-run (annoys existing npm-CLI users); a persisted "wizard done" claim (derived state re-opens the wizard until setup is actually complete, and stops the moment it is). |
| 2 | **Wizard-first startup: when the gate fires, `App.StartAsync` builds NO daemon graph** — no `DaemonClientService`, no tray, no `DaemonLifecycleController`, no `ShimOfferCoordinator`. The wizard is the only window. Forced, not just simpler: everything in the graph pins identity at construction (`KcapCli` pins the profile, `DaemonClientService` pins the daemon name) and on a gate-failing machine the wizard is what *decides* those identities. On wizard close — finished or abandoned — startup re-resolves the profile fresh and builds the normal graph, **with one carve-out: if the gate still fails at close, the lifecycle controller starts with its startup auto-action arm permanently closed and the post-close auto shim offer suppressed** (user-clicked actions and the tray shim item keep working). Without the carve-out, a URL-valid/token-less machine whose user closes the wizard before sign-in would flow straight into AI-1654's silent auto-install — §4.1 preconditions require a profile URL but no token. Gate and graph must also agree on what "valid URL" means: both use the same `ServerIdentity` canonicalization (today `App.ValidProfileName` accepts any absolute URI, e.g. `file://`, that the gate would reject). Graph construction after wizard close additionally **defers until both wizard lanes have quiesced** — the service-operation lane (§6a) and the auth lane's commit boundary (§5) — subject to §6a's documented post-cap exception: past the bounded cap the graph is built with auto-actions closed while the still-live action remains owned by the app-lifetime `DaemonMutationLane` (§4), which is what keeps a later user-clicked mutation queued behind it. A close after the auth boundary begins detaches the UI, but the app awaits the operation's terminal `Committed`/`Failed` before resolving configuration, building the graph, or completing shutdown. |
| 3 | **Hybrid drive: auth/discovery/provisioning run IN-PROC via a GUI-neutral Core façade; steps with tested non-interactive CLI surfaces are shelled.** This amends umbrella §7's "every step shells the bundled CLI" the way AI-1650 amended the MVVM choice — the rationale (reuse existing tested flows, never reimplement) is preserved: `OAuthLoginFlow`/`WorkOSDiscovery`/`WorkOSTokenSource`/`TokenStore` ARE the tested flow, in Core. Shelling login is impossible for exactly the cases that matter: multi-tenant discovery and create-workspace are Spectre prompts that hard-fail on redirected stdin, and the tenant pick happens mid-auth-session (a shelled two-phase `login --list`/`login --tenant` forces re-auth between list and pick). The Core seams as they exist today are NOT app-consumable — sync `ITenantPicker.Pick`, no cancellation anywhere, `Console` writes inside `WorkOSDiscovery`/`OAuthLoginFlow`, GitHub discovery orchestration living in `SetupCommand` with internal helpers — so this slice reshapes them into an explicit onboarding façade (§5) that the CLI's `setup`/`login` re-plumb onto, behavior-preserving. Rejected: growing a non-interactive CLI surface (large public surface in an app slice; the mid-session pick problem stands); PTY-driving the interactive commands (screen-scraping Spectre redraws has no contract). |
| 4 | **App-managed daemons are born `prompt`: the DAEMON seeds its own policy at boot, behind a unit-baked directive.** The app sets `KCAP_CONSENT_SEED_DEFAULT=prompt` in the env overlay of its `service install [--replace] --verify` calls; `ServiceEnvironment`'s baked-env allowlist gains the key, so it becomes **deliberate unit content** — a property of the app-managed unit consumed at every daemon boot, not transaction control (and, being plist content, it is already covered by AI-1654's `TxnMarker` fingerprint and rollback: a failed install rolls the directive back with the unit; no separate seed artifact, phases, or flush ordering exist). At boot, with the directive present, the daemon — under its own `DaemonLock`, as the sole writer of `consent.json`, strictly BEFORE `ServerConnection` and before the launch gate serves anything — classifies its policy file with its own parser rules (§6). Nothing outside the daemon ever writes the file, which is what closes the offline-writer race: a manual `kcap daemon start` does not hold the service flock, so a CLI-side seed write could interleave with a live daemon's in-memory policy; a boot-time self-seed cannot. This is the causal barrier: the policy is committed before the daemon can register, so *no* post-attach flip has to beat a server-issued launch. If the directive is present and the seed/quarantine write fails, the daemon logs a stable coded token (`consent_seed_unwritable`) and **exits 0** — never runs with an uncommittable policy (per-verb surfacing in §9). **The directive's value contract is exact**: this slice defines only the literal `prompt`; any other value (empty, `allow`, `deny`, case variants, unknown) fails closed — the daemon refuses (coded token, exit 0) rather than honoring or ignoring it — and daemon boot and the §3 start gate share one parser. **The directive rides EVERY app-initiated daemon spawn, and every such spawn runs the pinned, fail-closed CLI**: the unit-writing verbs, the gated `service start --verify`, and ONE detached-start module — the main-window Start/Retry path (`DaemonClientService.StartDaemonAsync`) is re-routed through the same pinned `KcapCli.DetachedStartAsync` the lifecycle controller uses, because as it stands it resolves `CliResolver` and then **falls back to bare `"kcap"`** and starts with no pinned profile: an older PATH CLI would spawn an older, directive-unaware daemon (defeating the seed), and an unpinned start after a concurrent `kcap use` would start the pinned name against a different server. App-managed starts get no bare fallback — no resolvable current CLI means an honest "kcap CLI not found", never a legacy spawn. **`daemon start` itself, when the directive is present in its environment, validates the sibling daemon against the CLI's embedded digest BEFORE spawning** — without this, the detached path would be the one spawn that skips the digest gate entirely (it has no service transaction), and a floor-compatible CLI beside legacy daemon bytes would pass the app's version check and then boot a directive-unaware `allow` daemon. The refusal is a pinned machine-readable result, symmetric with the service verbs: **exit 43** with exactly one MATCHING `daemon_start_reason=` line (`package_inconsistent`) — additional unrelated stderr is allowed and never affects routing (decision 9's prefix-parsing rule); zero, duplicate, or conflicting matching lines fail closed. Both app surfaces map it to the reinstall guidance (never takeover — same bytes), and any unrecognized/future `daemon_start_reason` value maps to fail-closed attention, never a destructive action. A configured npm-CLI user who never armed a claim — or a machine whose claims were quarantined or already consumed — cannot mint an `allow` daemon through any app action (one qualified exception: the §3 lock-unaware-writer residual); terminal/manual starts remain unchanged because they receive no overlay. **The directive is boot-local, with one deliberate carrier**: the daemon captures the validated directive into `DaemonConfig` at startup and removes it from the ambient process environment before anything can spawn a child, and it joins the existing PTY/ACP env-scrub lists as defense in depth — hosted agents inherit the daemon's environment, and a hosted agent running `kcap daemon start` or `service install` must not unknowingly seed or bake it. The one place it is re-injected explicitly: `DetachedRespawnStrategy` places the captured directive into the restart-after-update SUCCESSOR's `ProcessStartInfo.Environment` — the successor is spawned with inherited (scrubbed) env and reconstructed argv, so without explicit re-injection a detached daemon's update successor would boot directive-less and a missing/corrupt policy at that boot would fall back to `allow`. Headless daemons are untouched (no directive → today's behavior exactly; the upgrade-safe `allow` default stands). This generalizes umbrella §5's "the app's onboarding flips the daemon it manages to `prompt`" to *every* daemon the app spawns — deliberate: an app-managed desktop daemon has a UI for prompts (AI-1652), and a prompt-with-no-UI resolves as the designed fail-closed deny. |
| 5 | **The wizard includes visibility and daemon name** (setup's step 3/6 and 5/6, which umbrella §7's list omitted): a Defaults step with the visibility picker (default `org_public`) and daemon-name field (default: lowercased username). |
| 6 | **Import step: vendor checkboxes + scope choice** (Everything / one org / specific repo — mirroring `ImportScopePrompt`'s vocabulary), running `kcap import --all|--org <o>|--repo <r> --yes` plus selected vendor flags. Setup's own embedded import is current-repo-scoped, which is meaningless for a GUI launch. |
| 7 | **The consent-flip claim covers what seeding cannot: PRE-EXISTING daemons.** With decision 4, a daemon the app installs is born `prompt`; the claim's only remaining job is a daemon that already exists at onboarding time (running, or installed-stopped with an existing policy file). Claims live in their own store — `{config}/consent-flip-claims.json`, a collection keyed by canonical `{profile, server URL}` with merge semantics (arm upserts; apply/clear removes only its key) — mutated under `ConfigFileLock` and **flushed (file and directory) before the sign-in commit proceeds**, AI-1654-marker-grade. Deliberately NOT in `app-state.json` (UX-grade by contract). **The daemon NAME is not part of the key — it is resolved from config at application time** (round-5 correction): a name is an address, not the protected principal (owner × profile × server), and keying on it made claim validity depend on a cross-file dance — the wizard's Defaults write and any terminal `kcap config set daemon.name` would each have to re-key a second store crash-consistently. Resolving the name at application time means daemon renames need no claim writes at all: the §6 conditional put still verifies BOTH the currently-resolved name and the claim's server identity against the live daemon, so the guard is undiminished. The façade's async **before-commit hook** carries the full identity SET the boundary is about to make gate-complete — GitHub discovery publishes a token per discovered tenant, so one claim per published identity — and **hook failure prevents the commit** (retryable sign-in error). "Sign-in completion" means the façade's commit boundary (§5), NOT `SetupFunnel.SigninCompleted`. Step 7 applies/consumes the claim; `ConsentFlipCoordinator` applies it to pre-existing daemons on a later attach (§6). Abandoning before sign-in arms nothing (decision 2's carve-out covers that population). **Store corruption fails safe, not open** (§4): the corrupt file is quarantined and surfaced, the coordinator goes inert — and no `allow` daemon can be minted meanwhile (subject to §3's one qualified exception), because every app install seeds `prompt` regardless of claim-store health. |
| 8 | **Harness detection moves to `Capacitor.Cli.Core` as `AgentDetection`, composing the existing per-vendor rules through PURE inputs** (§8). `AgentDetector` (today in the CLI project) moves to Core; the env-reading vendor helpers (`KiroPaths` `KIRO_HOME`, `PiPaths` `PI_CODING_AGENT_DIR`, `OpenCodePaths` `OPENCODE_CONFIG_DIR`/XDG) gain pure overloads/input records so PATH, PATHEXT, home, and every relevant override are passed as values — an injected accessor around an aggregate that still reads globals inside would be composition theater, and parity tests must run in parallel without mutating process env. The app feeds the terminal PATH from `LoginShellProbe`. No new CLI verb. |
| 9 | **The app emits NO telemetry this slice; desktop-onboarding funnel coverage is explicitly deferred** to a follow-up issue. The app never calls `CliTelemetry.Initialize`, so Core's embedded `SetupFunnel` emissions no-op (`Capture`/`CaptureNow` are guarded on `Enabled`/`_client`). Reversed from the pre-review draft, which was unsafe on three verified counts: `Initialize` hardcodes `source: "cli"` and prints the one-time privacy disclosure to `Console.Error` — invisible in a WinExe, silently consuming `notice_shown`; `CaptureNow` is deliberately sync-over-async, safe only in a console app without a SynchronizationContext, and can deadlock on Avalonia's UI context; and app-emitted fragments would corrupt the funnel while mislabeled as CLI traffic. The follow-up owns: an app source label, a visible disclosure surface, async delivery, and the desktop funnel sequence. **The guarantee extends to shelled children**: every CLI process the app spawns still executes `Program.cs`, whose `CliTelemetry.Initialize` runs before dispatch — the compatibility probe, status, plugin, import, and mutation children would each send CLI-labeled telemetry AND the very first one would print the one-time privacy disclosure to an invisible stderr and consume `notice_shown`. So every app-spawned CLI child carries a **dedicated, genuinely process-local marker** — `KCAP_APP_SPAWN_NO_TELEMETRY=1`, which `Program.cs` consumes for `CliTelemetry.Initialize` and REMOVES from the process environment before command dispatch — NOT a `KCAP_TELEMETRY=0` overlay: `daemon start -d` constructs the detached daemon's `ProcessStartInfo` from the inherited environment, so a plain overlay would propagate into the daemon, its respawn successors, and every hosted child, silently opting hosted agents' nested `kcap` commands out of telemetry for the daemon's lifetime — and it would clobber a user's own pre-existing `KCAP_TELEMETRY` choice, which the marker never touches. Consumed-and-removed means nothing the CLI child spawns can observe it. This also keeps the machine-readable stderr contracts clean: reason lines (`start_gate_reason=`, `daemon_start_reason=`) are parsed by prefix, not by assuming stderr contains nothing else. |
| 10 | **`config.json` gains ONE Core mutation API and every writer migrates to it.** `AppConfig.SaveProfileConfig` (lock-free, fixed `config.json.tmp`) is replaced by a field-scoped mutate call: acquire `ConfigFileLock` → re-read under the lock → apply the caller's mutation to the fresh snapshot → publish via unique temp + rename — as a **synchronous critical section** (the lock is a thread-affine named `Mutex`: no await while holding it; async callers wrap in `Task.Run`). The re-read uses a **pure load/parse/migrate-in-memory primitive** — today `LoadProfileConfig` *writes* the v1→v2 migration back during load, which under the mutation API would recursively acquire the same thread-affine mutex; instead, migration is applied in memory inside the critical section and persists through the same publication as the caller's mutation. Locking only the wizard's writes cannot work: `ConfigFileLock` requires every writer to participate, and today `ConfigCommand`, `ProfileCommand`, `UseCommand`, `UpdateCommand`, `IgnoreCommand`, `RemapCommand`, `ImportCommand`, `SetupCommand`, `Program`, `WorkOSDiscovery`, `MachineIdProvider`, and `LoadProfileConfig`'s migration path all write lock-free. Deleting the old method makes the migration compiler-enforced. Accepted residual, documented: the app has no single-instance guard — with claims in their own locked, keyed store (decision 7) this is a pure UX gap (two tray icons), not a safety one. |

## 3. Wizard flow

Linear steps, Back/Next, every step individually skippable and retryable (umbrella §7). One
window (`OnboardingWindow`), step content switched by template.

1. **Command-line tool** — the PATH shim. Shown only when applicable: macOS + `CliResolver`
   resolved an absolute path + the login-shell probe positively found no `kcap` on the terminal
   PATH. Reuses `PathShimInstaller` (AppleScript sudo, non-forcing symlink, post-install re-probe);
   claims `ShimOffered` in app-state so the post-wizard `ShimOfferCoordinator` never re-offers.
2. **Connect** — gathers *intent only*; no network, nothing written. Three choices: paste a
   workspace slug/URL (Core-moved `ToServerOrigin`/`ResolveTenantArg` rules), "find my
   workspaces" (discovery), or **Create a workspace**. Auth, tenant enumeration, and provisioning
   all happen inside the Sign-in step — both discovery paths must authenticate *before* tenants
   can be listed, and WorkOS creation runs inside discovery after org-less sign-in.
3. **Sign in** — executes ONE façade operation (§5) for the chosen intent, rendering its
   structured progress inline: browser-open notice with the fallback URL, GitHub device code +
   verification URL when that flow applies, the tenant picker list when discovery finds several,
   and the create-workspace sub-flow (org name → slug with live availability → confirm →
   provisioning progress mirroring the CLI's 4 s × 150 poll contract, `WorkOSTokenSource` keeping
   the org-less token alive per AI-1171, its `CurrentRefreshToken` used for the final org switch).
   The operation ends at the §5 **commit boundary**: claims armed for every identity about to be
   published (decision 7), then all durable publications run to completion. Pasted-URL joins to
   an `AuthProvider.None` server run the same boundary (claim + profile + provider stamp, no
   token) and the step auto-satisfies.
   **Step transitions:** while the operation is in flight *before the boundary*, Back/Skip/close
   cancel it through the §5 cancellation lane (no durable write); once the boundary is entered,
   the operation runs to `Committed` regardless, and a close hands the wait to the app per
   decision 2. After the commit, Back returns to Connect for a *different* intent, and re-entry
   shows the step satisfied. Retry re-runs the operation only after the previous lane has
   quiesced.
4. **Defaults** — visibility picker + daemon-name field (decision 5), written per decision 10 to
   the active profile. No claim maintenance: claims key on `{profile, server}` and resolve the
   daemon name at application time (decision 7), so a rename here — or a later terminal
   `kcap config set daemon.name` — needs no second-store write.
5. **Coding agents** — `AgentDetection` results (§8) as pre-checked checkboxes →
   `kcap plugin install --<vendor>` per selection (Claude Code is the flagless default install).
6. **Import history** — vendor checkboxes + scope choice (decision 6); output streams into a live
   log pane per the §7 contract; Cancel kills the child tree. A failed import never blocks
   finishing onboarding.
7. **Enable daemon** — through the §6a service lane, on **AI-1654's full state matrix** (not a
   reduced one), from a fresh `service status --json`:
   - *No unit, no live owner* → `service install --verify` (bakes the seed directive, decision 4).
   - *Unit present, loaded-inactive or stopped, `daemon_pid` null* → `service start --verify`,
     which — **when invoked with the directive in its environment** (every app-initiated start
     is; a plain terminal `service start` carries none and behaves exactly as today) — enforces
     two gates **inside the transaction, re-read under the per-label lock immediately before
     bootstrap** (a read-only status check cannot carry them: status deliberately takes no
     lock, so a concurrent rewrite between the app's read and the start would boot evidence the
     app never approved). Failing either gate → the stable coded verify failure
     **`verify_start_gate` = 28** (additive to `VerifyExit` 20–27; Phase-A failures occur
     before any mutation, so the safe state is "nothing touched" — no rollback arm), carrying
     **exactly one MATCHING machine-readable reason line on stderr** (unrelated stderr allowed
     and never affects routing; zero/duplicate/conflicting matching lines fail closed) —
     `start_gate_reason=<directive_missing|directive_invalid|identity_mismatch|foreign_binary|package_inconsistent|evidence_unreadable>`
     — because one code must drive different recoveries and the app must not parse human
     prose. The enum is TOTAL over the gate's failure paths: `directive_invalid` covers the
     exact-value contract's rejections (empty/`allow`/`deny`/case-variant/unknown values),
     `evidence_unreadable` covers unreadable/malformed/ambiguous unit data. The
     controller/wizard mapping is pinned: `directive_missing`, `directive_invalid`,
     `identity_mismatch`, `foreign_binary` → the dialoged takeover (`--replace` bakes the
     directive and the current binary); `package_inconsistent` → the reinstall surface, never
     takeover (it would re-point at the same bad bytes); `evidence_unreadable` AND any
     unrecognized/future reason → fail-closed attention/repair, never takeover — forward
     safety: an older app must not destructively interpret a newer CLI's reason:
     **(a) directive + current binary CONTENT, against BUILD-TIME trusted evidence** — the
     unit bakes `KCAP_CONSENT_SEED_DEFAULT` with the exact supported value AND the SHA-256 of
     the bytes at the unit's `binary_path` equals the **expected daemon digest embedded in the
     CLI at build time** (an MSBuild step hashes the packaged `kcap-daemon` artifact and
     generates the constant into the CLI — CLI and daemon ship as one package, so the CLI can
     carry its sibling's identity). Comparing against the bytes at `install_binary_path` would
     be vacuous: `ResolveDaemonBinary` returns the same fixed sibling path, so an in-place
     downgrade or partial package replacement puts legacy bytes under BOTH reads and they
     necessarily match — equality proves nothing about seed capability. And never
     `--version`: the unit's binary is the daemon binary, whose argv handling would BOOT a
     daemon — executing the artifact under test is the failure mode, while a pre-consent
     binary (which ignores both seed and policy file, `start --verify` deliberately accepting
     capability-incompatible hellos per AI-1654 §3.4) must be caught before bootstrap. Digest
     mismatch against a DIFFERENT path → takeover (rewrite the unit to the current sibling);
     digest mismatch AT the canonical sibling path → the package itself is inconsistent
     (gate-capable CLI over legacy daemon bytes) — surfaced as "kcap installation is
     inconsistent — reinstall", because a takeover would re-point at the same bad bytes.
     **Install/replace verify the same embedded digest against the sibling before writing any
     unit** (a new viability arm, with its own machine-readable
     `viability_reason=package_inconsistent` so the app can show the reinstall surface instead
     of a generic viability failure), AND **re-check it immediately before bootstrap and at the
     final post-readiness recheck** — the viability check alone leaves the same
     check-to-exec window the start gate closes (a concurrent package replacement can swap the
     daemon to legacy bytes between viability and `WriteAndBootstrap`, and AI-1654's final
     install recheck fingerprints unit content only, not binary bytes); drift found at either
     re-check resolves through the marker-backed rollback. So a unit for legacy bytes can never
     be created — or knowingly bootstrapped — by a gate-capable CLI. Unknown/unreadable
     evidence → fail closed.
     **(b) effective identity** — the unit's *complete* effective identity, derived inside the
     transaction the same way the daemon itself would resolve it (a baked `KCAP_URL` takes
     precedence; else the baked profile under the baked `KCAP_CONFIG_DIR`'s config root),
     must match the invoking environment's expectation (the pinned `KCAP_PROFILE` + the
     captured `KCAP_EXPECT_SERVER_URL` — NOT the profile's config-resolved server at gate
     time, which a concurrent `server_url` edit could have changed to match, §4). **The
     unit's own baked expectation participates**: the gate parses the unit's baked
     `KCAP_EXPECT_SERVER_URL` and requires it to equal BOTH the unit's resolved server and
     the invocation expectation — otherwise a unit installed for server S whose profile was
     later re-pointed to T would resolve to T, match a fresh T-expecting invocation, pass
     the gate, and only fail as the daemon's post-bootstrap refusal (a deterministic
     takeover case degraded into a readiness timeout after mutation). A pre-existing
     mismatch is pre-mutation exit 28 / `identity_mismatch` → takeover; the daemon's own
     boot check remains for the irreducible post-gate race. `service status --json`
     additionally exposes `unit_expected_server` so the stale pin is explainable in UX
     (`unit_server_url` alone would show the resolved T while the unit is constrained to S).
     `unit_profile` alone is NOT identity: two units can bake the same
     profile name yet resolve different servers. Malformed, duplicate, unreadable, or
     ambiguous relevant env entries → fail closed.
     **The gated start launches the validated artifact, not a memory of one — in two pinned
     phases**: *Phase A (pre-mutation)*: both gates evaluated under the lock; any failure →
     exit 28, no marker written, nothing touched. *Phase B (mutation, marker-backed per the
     start verb's existing AI-1654 transaction)*: instead of kickstarting a loaded definition
     (launchd may have cached one BEFORE the plist on disk last changed — `QueryCore` reads
     the file, `StartCore` kickstarts the loaded job, and nothing guarantees they agree), the
     gated path boots out any loaded label, then re-checks both fingerprints (plist + binary
     digest) immediately before bootstrap. Drift detected HERE is not exit 28 — the label was
     already mutated, so "nothing touched" would be a lie: it is the distinct
     **`verify_start_gate_drift` = 29**, resolving through the marker-backed rollback to the
     start verb's verified-safe failure state (label unloaded, plist retained). The app maps
     29 as an attention state: surface the coded reason, re-query fresh evidence, offer the
     repair affordance — never an automatic retry (something is actively rewriting the unit). **Accepted
     residual, stated here and NOT attributed to AI-1654** (whose final-recheck seam is
     install-only; today's `StartVerifiedAsync` has no unit recheck at all), **generalized to
     every spawn verb**: a concurrent writer swapping the unit or the daemon bytes in the
     irreducible window between a verb's final pre-exec re-check and the exec itself — the
     gated start's recheck→bootstrap, install/replace's digest-recheck→bootstrap, the
     detached start's digest-check→spawn, and the §4 floor probe's approval→invocation of the
     pinned CLI executable — can run unvalidated bytes. **The terminal state differs by path
     and is specified honestly, not uniformly promised as rollback**: *gated
     start/install/replace* → the post-readiness recheck detects drift and the marker-backed
     rollback runs (bounded exposure); *detached start* → there is no transaction and no
     rollback — a swapped legacy daemon is a PERSISTENT running unseeded daemon; *swapped CLI
     executable* → none of the new gates execute at all, and the old CLI can successfully
     complete a mutation, leaving a persistent unseeded daemon or unit. For the two
     non-rollback outcomes the app's contract is **post-result reconciliation, owned by the
     §4 `DaemonMutationLane`** — one deep module, because reconciliation cannot live on "the
     controller": decision 2 builds no controller or client service while the wizard is open,
     and the main-window path's `RestartLoopAsync` kick makes an immediate status read prove
     nothing about a fresh attach. Its outcome algebra and success predicates are defined in
     §4; the key rule here: a swapped old CLI's residue (a persistent unseeded daemon, or an
     installed-but-stopped unit, or legacy `status --json` lacking the new unit fields)
     always terminates in an attention/repair classification, never silence — delivered through
     the lane's single-consumer channel (§4) and presented by whichever consumer owns it
     (wizard step UI while open, else the composition root's surfaces), so no controller
     internals are imported anywhere and no classification is ever presented twice. This whole family is the **one qualified exception** to
     decision 4's and §4's "no app action / no automatic path mints an `allow` daemon"
     guarantees, and those statements carry it; the acceptance tests force the order
     deterministically per path (a test seam pauses after the final check, swaps the
     artifact, resumes) and assert each path's REAL terminal state and reconciliation
     surface — no timing measurement pretends to bound what the OS does not.
     `service status --json` gains additive `unit_profile`, `unit_server_url` (derived as
     above, nullable), and `unit_consent_seed` (the value, not a boolean) — **UX evidence
     only**; the transaction re-derives everything under the lock. The same gates apply to the
     lifecycle controller's silent auto-start row: gate failure → no silent start, affordance
     surfaced.
   - *Positive ownership* (`job_pid` == `daemon_pid`, both non-null) **AND identity match** (§6
     probe: expected daemon name + canonical server URL) → already enabled; apply a pending
     claim via the §6 conditional put (pre-existing daemon, decision 7). Ownership *without*
     identity match (a unit owning a daemon configured for another profile/server) is NOT
     "enabled" — the step offers the dialoged takeover.
   - *Manual daemon (no unit or non-owning loaded unit), identity-matched* → NOT enablement (a
     manual daemon dies at logout; a Connected post-wizard graph closes AI-1654's startup phase
     and will never silently repair it): dialoged takeover with AI-1654's disclosures; decline →
     enablement stays visibly incomplete (skippable), and a pending claim still applies via
     conditional put — the flip protects the owner regardless of which process hosts the daemon.
   - *Manual daemon, identity MISMATCH* (status probe answers a different server/name) → no
     mutation of any kind and no claim application; the step surfaces what it found with the
     dialoged takeover as the only offered action (explicit row — a wrong-server daemon must
     neither be flipped nor silently replaced).
   - *Orphan label / stale `txn_marker`* → AI-1654's repair affordance semantics; `txn_active` →
     wait, never a parallel mutation; *launchd `unknown` / status unparseable* → no mutation,
     honest message.
8. **Done** — summary of what was set up and what was skipped (and why).

Abandonment: config writes are incremental, so a re-entered wizard finds steps pre-satisfied.
Accepted residual, documented: a user who signs in and quits never re-enters (the gate now
passes), so hooks/import stay undone until `kcap setup`/`kcap import` or the AI-1656 settings
surface — consent is covered by decision 4 (any later app install is born `prompt`) plus the
claim for pre-existing daemons, and pre-sign-in abandonment by the decision-2 carve-out.

## 4. Components (app)

```
src/Capacitor.App/Views/Onboarding/OnboardingWindow.axaml(.cs)
src/Capacitor.App/ViewModels/Onboarding/OnboardingViewModel.cs      — step state machine
src/Capacitor.App/ViewModels/Onboarding/<Step>StepViewModel.cs      — one per step
src/Capacitor.App/Services/Onboarding/OnboardingGate.cs             — decision 1 trigger
src/Capacitor.App/Services/Onboarding/WizardAuthService.cs          — façade driver + cancellation lane
src/Capacitor.App/Services/Onboarding/ConsentFlipClaims.cs          — decision 7 durable claim store
src/Capacitor.App/Services/Onboarding/ConsentFlipCoordinator.cs     — pending-claim application (§6)
src/Capacitor.App/Services/DaemonMutationLane.cs                    — probe→mutation→reconciliation (below)
src/Capacitor.Cli.Core/LocalIpc/LocalControlProbe.cs                — bounded one-shot hello + status snapshot
```

- **`DaemonMutationLane`** — the singleton every daemon mutation runs through (wizard step 7,
  lifecycle auto-actions, main-window Start). **One app-lifetime instance**: `App` constructs
  it once, BEFORE the gate, and disposes it only at app shutdown — never rebuilt at the
  wizard-to-graph transition, because a second instance would lose the owned task and channel
  at exactly the §6a post-cap moment where a wizard action may still be live and a
  user-clicked normal-graph action MUST queue behind it (one lane per mode would break the
  global serialization silently). The observation adapter is therefore not fixed at
  construction: the lane holds a current-adapter slot, starting as the one-shot
  `LocalControlProbe` adapter and **atomically swapped to the live-graph adapter when the
  graph is built** — but **each owned action pins its observation strategy ONCE, at action
  start, and keeps it for the action's whole lifetime** (an action spanning the post-cap
  graph build must not switch observers mid-reconciliation), and the live adapter is used
  ONLY when the graph's full identity (the client's daemon name + its profile/server)
  matches the request identity — `DaemonClientService` is constructed for one immutable
  name and cannot observe an arbitrary target; a mismatched request gets an
  identity-specific one-shot probe regardless of the slot, or the lane would classify
  evidence from the graph's daemon instead of the daemon it mutated. **Execution is
  action-scoped, not instance-pinned**: the lane owns an executor factory that binds
  `{pinned absolute CLI path (from this action's fresh probe), profile, daemon name,
  canonical server}` ONCE per owned action; the action's floor probe and its mutation run
  through that single binding (`KcapCli` becomes this action-scoped executor — no
  long-lived identity-pinned instance participates in mutations, resolving the earlier
  contradiction between "the wizard constructs its own `KcapCli`" and "one instance runs
  probe and mutations"; the graph's own `KcapCli` remains for non-mutating queries only).
  **The canonical server is an enforced EXPECTATION, not a resolution override**: the
  executor overlays `KCAP_EXPECT_SERVER_URL=<the request's canonical server>` alongside
  `KCAP_PROFILE` — deliberately NOT `KCAP_URL`, which round-20 review showed would be a
  credential-confusion hole: `ProfileResolver` checks the env URL FIRST and returns a
  profile-NULL result without consulting `KCAP_PROFILE`, so `TokenStore` would fall back to
  the on-disk ACTIVE profile — a request for profile A while B is active would boot against
  A's server while reading/refreshing B's token (and a pre-upgrade unbound token would pass
  `BoundToTarget` even for another server's credential), with the unit's baked
  `KCAP_PROFILE=A` masking the mismatch. With the expectation variable, profile/token
  resolution is untouched (always the pinned profile's own identity, settings, and token
  file); instead, the CLI's §3 gate (b) compares the unit's derived identity against the
  expectation, and **the daemon itself, on every boot path (unit or detached), compares its
  RESOLVED canonical server against a present expectation before `ServerConnection` and
  before any token use — mismatch is a coded exit-0 refusal** (`server_expectation_mismatch`
  token; comes to rest, no respin), which is what closes the concurrent
  `kcap config set server_url` redirect on ALL surfaces including gate-less detached start:
  the redirected resolution no longer matches the captured expectation, so the daemon
  refuses instead of authenticating anywhere. `ServiceEnvironment`'s allowlist gains the
  variable, so install bakes it — the unit stays pinned to the server it was enabled for,
  and a deliberate later server change surfaces as a visible refusal + attention (reinstall
  re-captures), never a silent wrong-server or wrong-credential boot. **The expectation gets
  the seed directive's exact boot-local carrier lifecycle** (it is equally an internal app
  control): the daemon captures it into `DaemonConfig` at boot and removes it from ambient
  process state before any descendant exists; it joins the PTY/ACP/RPC scrub lists;
  `DetachedRespawnStrategy` re-injects the captured value into the restart successor (which
  would otherwise lose the safety check); unit restarts receive it from the unit. Left
  ambient, every hosted child would inherit it — a hosted agent running
  `kcap daemon start`/`service install` for another profile would hit an unexplained
  mismatch refusal and `ServiceEnvironment` would persist the inherited expectation into a
  NEW unit, contradicting the terminal-actions-unchanged rule. **Refusal propagation — the
  daemon's exit-0 refusals must be app-observable**: the detached wrapper redirects and
  closes the daemon's stdio and its short liveness check either returns a generic failure
  (fast exit) or success (late exit), so no coded token reaches the lane through process
  results; service verbs surface only a readiness timeout. So the daemon, on EVERY coded
  exit-0 refusal (`server_expectation_mismatch`, `consent_seed_unwritable`, invalid
  directive), atomically writes a **per-daemon-name** marker,
  `{stateDir}/boot-refusal.json` (the state dir is already the sanitized-name directory —
  stated, not implied), whose record carries verifiable identity, not just a timestamp:
  `{schema, daemon name, token, expectation, resolved value, pid, instance_id, attempt_id?,
  timestamp}`. **Causality is established per verb, never by wall-clock comparison** (clocks
  move; a retained marker can share a coarse timestamp; a foreign boot can write during a
  wait): *detached start* — the executor overlays a per-action `KCAP_BOOT_ATTEMPT` GUID
  (boot-local carrier lifecycle like its siblings; deliberately NOT unit-bakeable), the
  daemon echoes it into the marker, and the lane's reconciliation attributes ONLY a marker
  whose `attempt_id` equals this action's — it is also that marker's single consumer
  (deletes on attribution); *service verbs* — launchd launches from unit env, so no
  per-spawn id can pass through, and the service flock does NOT exclude other boots (it is
  its own lock; detached/manual starts and a direct `launchctl kickstart` take different or
  no locks) — so service attribution rests on **verified pre-clear + positive process
  correlation**, never on lock-implied exclusivity: the transaction clears the marker before
  bootstrap and VERIFIES the clear (an undeletable stale marker would otherwise be
  attributed to the new action — verification failure disables coded attribution for this
  action, which then degrades to the generic timeout, and is logged; the mutation itself
  proceeds); during the readiness window it attributes ONLY a marker whose daemon name and
  baked expectation match AND whose `pid` equals a job PID **positively observed via
  `launchctl print` for this label** in that window — a manual same-name boot is never this
  label's job, so its pid never matches, while a concurrent kickstart of the same unit IS
  this unit booting and is legitimately attributable. The transaction is that marker's
  single consumer and passes the coded reason to the app as the `refusal_reason=` line
  under the standard prefix rules; the lane uses the line, never a second read. The daemon
  publishes the marker only AFTER acquiring its daemon-name lock (two same-name daemons
  cannot interleave marker writes); leftover deletion (passing boot, consuming reader) is
  HYGIENE, not the safety property — attribution's identity/pid correlation is what
  protects against a stale survivor, so a failed deletion is logged and tolerated. A marker failing identity
  validation against the `MutationRequest` is NOT attributed (foreign evidence → attention,
  not `Refused`). Lifecycle: a daemon that passes its boot checks deletes any leftover
  marker before `ServerConnection`; a corrupt/unknown-schema marker is renamed aside,
  ignored for attribution, and logged. **Marker publication is contained best-effort,
  never recursive**: `consent_seed_unwritable` means the state dir may be exactly as
  unwritable for the marker — a marker-write failure is swallowed, the original exit-0
  safety refusal is preserved unconditionally (never a nonzero crash-respin loop), and the
  observer side degrades honestly: no attributable marker → the generic outcome
  (`UnconfirmedNoAttach` / readiness timeout) with log-pointing guidance, never a fabricated
  coded result and never an unbounded wait. **The marker token enum is TOTAL and its routing pinned** (one recovery per token, forward
  safe): `server_expectation_mismatch` → the dialoged takeover (the unit re-capture — ONE
  action; "reinstall kcap" remains reserved for `package_inconsistent`);
  `consent_seed_unwritable` → attention + storage guidance (disk/permissions — takeover
  cannot fix it); `consent_seed_invalid` (the directive value-contract rejections, now a
  stable token rather than prose) → the dialoged takeover (rewrites the bad unit content);
  any unknown/future token → fail-closed attention, never destructive. The `refusal_reason=`
  line obeys the standard prefix rules (exactly one matching line; zero/duplicate/
  conflicting → fail closed to the generic outcome); the verify exit code is retained
  (`verify_readiness_timeout`) with the reason line as auxiliary evidence. The lane maps an
  attributed refusal to `Refused(reason)` with that token's pinned surface —
  distinguishable from unrelated startup failure on every verb, including detached-start
  refusals on both sides of the wrapper's liveness window; without an attributable marker,
  the outcome is the generic one with log-pointing guidance. External interface:
  `Task<MutationOutcome> RunAsync(MutationRequest, CancellationToken)`, where
  `MutationRequest` names the verb (install/replace/start-verify/detached-start) and the
  expected identity `{profile, canonical server, daemon name}` — nothing else: a
  surfacing-caller field would make request equality (and therefore coalescing) ambiguous,
  and callers don't need it: presentation flows through the single-consumer channel
  (below), and `RunAsync` results are state-only. **The action's
  lifetime is lane-owned, never caller-owned**: the lane runs probe → child → terminal
  process result → reconciliation as ONE internal task under its own lifetime token — the
  child is started under that token, not a caller's, because the runner's `AbandonWait`
  cancellation throws with NO `ProcessResult` while the child runs on, and a lane that let a
  caller's token drive the shared work could neither reconcile that invocation nor protect a
  second coalesced waiter. Each `RunAsync` caller merely AWAITS the owned task with
  per-waiter cancellation: cancelling detaches THAT waiter only (caller A's Back/close never
  aborts classification for waiter B, and never releases the lane while the transaction
  runs). **The owned action is observable at the module seam** — the promises above are
  unimplementable through a lone `RunAsync` task a cancelled waiter has lost: the lane also
  exposes `QuiescedAsync()` (what decision 2's wizard-to-graph handoff and §6a's shutdown
  sequencing actually await, bounded by their existing caps) and a **FIFO of pending
  actionable outcomes** — a single cell cannot be lossless: action A can finish waiterless
  with `AttentionSkew` and queued action B finish waiterless with `AttentionRepair` before
  any drain, and overwrite/drop/block are each wrong. Retention/presentation policy: **EVERY actionable
  outcome (`AttentionSkew`, `AttentionRepair`, `Failed`, `Refused`, `UnconfirmedNoAttach`)
  travels through this channel exactly once, waiters or not, and `RunAsync` results are
  STATE-ONLY** — a waiter uses its result to update its own state (enable buttons, show
  step status) but never to open prompts or takeover dialogs. Without this rule, two
  coalesced waiters receiving one shared `AttentionSkew` would each open a takeover prompt,
  and two accepted prompts would enqueue a second destructive request. A
  `Succeeded`/`SucceededAfterTimeout` is delivered to waiters as a state-only result and,
  when waiterless, logged — success must not consume an attention surface. Delivery is a **live single-consumer channel, not a
  one-shot drain**: §6a permits graph construction after its cap while the owned child is
  still live, so an outcome can be enqueued AFTER the handoff's initial take — a drain-once
  contract would silently lose exactly the late attention the seam exists to carry. The
  consumer holds a persistent subscription (channel/async-enumerable of actionable
  envelopes): every enqueue wakes the active consumer; consumer ownership (wizard while
  open → composition root after handoff) transfers atomically with respect to producers, so
  an enqueue racing the transfer is delivered to exactly one of them. **Envelopes are LEASED,
  not removed, at dequeue** — a read that deletes before presentation completes would lose an
  envelope the wizard dequeued but never showed (teardown between dequeue and UI dispatch),
  while blind re-enqueue could duplicate one whose presentation completed concurrently. The
  consumer acks the lease when presentation reaches the user; a lease cancelled BEFORE
  presentation (wizard teardown mid-dispatch) requeues the envelope exactly once for the
  next consumer. Disposition of a dialog VISIBLE at wizard shutdown is pinned: it WAS
  presented — its close resolves through the dialog's own rules (close-without-answer =
  decline) and the envelope is acked, never requeued. Each envelope carries
  its `MutationRequest` identity (verb + target `{profile, server, name}`) so the consumer
  can route outcomes from different profiles/actions. A subsequent `RunAsync` NEVER returns
  a prior outcome — it performs its own request; prior outcomes reach the user only through
  the channel. A retry or queued request always waits for the owned action to quiesce first.
  **Every mutation has a finite lane-owned deadline** — including detached start, which today
  is the ONE unbounded child (`DetachedStartAsync` sets no `Timeout`): under lane-owned
  lifetime a hung `daemon start -d` would otherwise own the global lane forever and starve
  every queued mutation of its fresh probe. Service verbs keep the §6a bound
  (kill-timeout strictly above forward + reserve; pathological kill → attention). Detached
  start gets a bounded internal `Timeout` whose expiry kills the CLI wrapper via a **new
  process-only kill mode** (`RunOptions` gains it; today's internal timeout ALWAYS calls
  `Process.Kill(entireProcessTree: true)`, and the detached daemon is a direct, un-reparented
  child of the wrapper — `PreventInheritedHandles` changes descriptor inheritance, not
  parentage, and there is no setsid/double-fork on this path — so a tree kill would kill the
  daemon this contract promises to spare). The mode is tested with real processes. The lane
  then reconciles the possible partial effect through the instance-bound probes: a FULLY
  verified daemon (`ProcessResult.TimedOut == true`, identity + instance evidence complete) is the
  distinct outcome **`SucceededAfterTimeout`** — deliberately not `Succeeded`, whose
  successful-`ProcessResult` predicate stays intact; the runner result the lane consumes is
  the existing `TimedOut: true` `ProcessResult`, never a widened/nullable shape; a timeout
  with incomplete evidence is `UnconfirmedNoAttach` or attention. The lane records the outcome, quiesces, and admits the
  next request.
  **One global action lane covers probe → mutation → reconciliation**: an identical
  concurrent request coalesces into the in-flight action's single mutation and outcome; a
  DIFFERENT request queues and performs its OWN fresh §4 floor probe after acquiring the
  lane — a probe shared across two distinct mutations would leave the second running under a
  stale approval, which is exactly the indefinite-authorization hole the fresh-probe rule
  closes. Outcome algebra (exhaustive):
  `Succeeded` — requires ALL mandatory predicates: successful process result, canonical
  identity match, and (for service enablement verbs) positive ownership
  (`job_pid` == `daemon_pid`) — a Connected daemon alone is never success;
  `SucceededAfterTimeout` — detached-start only: the runner returned its EXISTING result
  shape with `ProcessResult.TimedOut == true` (the runner's contract is unchanged — its
  internal timeout kills, awaits, and returns a non-nullable result; the new kill mode
  changes kill scope only) under the process-only kill mode, AND fresh identity + instance
  evidence is complete; mapped to success UX with a logged note. A normal exit-0 remains
  `Succeeded`;
  `AttentionSkew` (Connected/hello evidence below floor, missing `consent/3`, or
  incompatible — the channel consumer routes it into the takeover offer, and the coordinator
  applies any pending claim under its factory guard);
  `AttentionRepair` (stale marker, orphan/unseeded residue, legacy status evidence);
  `UnconfirmedNoAttach` (bounded wait expired with no ownership evidence — surfaced as
  "started, not yet confirmed"; deliberately NOT "degraded-but-owned": no ownership may be
  claimed without the `job_pid` == `daemon_pid` evidence);
  `Refused(reason)` (floor/gate/resolver refusals, coded);
  `Failed(code, reason)` (verify exit codes and reason lines, mapped per §3).
  **Observation adapters** (two, selected via the current-adapter slot above): the live-graph adapter wraps
  `DaemonClientService`'s attach stream; the one-shot adapter uses `LocalControlProbe` — a
  NEW public Core seam (bounded one-shot hello + one status snapshot; today
  `LocalControlClient` is a retrying stream and the CLI's `HelloProbe` is internal, so
  wizard-first mode has no probe to call without this extraction, enumerated in §11).
  **The live adapter's instance binding is enforced IN the client, not hoped for above it**:
  today `LocalControlClient` discards the hello reply's identity after extracting
  capabilities/version, and the app publishes capabilities (`AttachStatus`) and snapshots on
  separate subjects — merely adding the DTO members would give the lane no hello instance id
  to compare. So `LocalControlClient` compares hello `pid`+`instance_id` against the FIRST
  status snapshot BEFORE emitting `Connected` (mismatch → classified incompatible, the cycle
  retries — never `Connected`), and the `Connected` event carries the correlated identity as
  an additive field so downstream consumers observe one atomic hello+snapshot fact. This
  Core client/app-shell seam change is enumerated in §11.
  **Fresh-generation semantics, bound to ONE daemon process**: a lane generation token
  rejects pre-mutation events, but that alone cannot stop evidence from DIFFERENT daemon
  processes being combined — the control server dispatches exactly one opening frame per
  connection, so hello (capabilities) and the status snapshot arrive on separate sockets,
  and service ownership (`job_pid`/`daemon_pid`) comes from yet another query; a daemon
  replaced between those reads could contribute `consent/3` + version from one process and
  identity + ownership from another, falsely satisfying `Succeeded` exactly in the
  swapped-legacy-daemon case reconciliation exists to catch. So `HelloReplyDto` and
  `DaemonInfoDto` gain additive `pid` + `instance_id` (a per-boot GUID) members — additive
  DTO members, no protocol bump, skipped by old clients — and the lane requires ALL evidence
  contributing to one classification to carry the SAME instance id, with the socket-side
  `pid` correlated against the service query's `daemon_pid`; any inconsistency (including
  absent fields from a pre-slice daemon) fails closed into `AttentionSkew`/re-probe, never
  `Succeeded`. Bounded waits are lane-owned (§6a rules unchanged: cancellation abandons
  waiters, never the child).

- **`OnboardingGate`** answers the decision-1 predicate once at startup, returning
  `(Complete | Incomplete(reason))` with reasons: `no_profile`, `invalid_server_url`,
  `no_token`, `token_unusable(binding|expired_unrefreshable)`. Provider-aware refresh capability
  per decision 1. **Provider persistence:** the profile gains an optional `auth_provider` stamp
  recording *provider + the canonical server identity it was learned for* (additive config
  schema), written inside the §5 commit boundary (never on a bare `/auth/config` read — a
  later-cancelled login must not leave a stamp). The gate honors `none` only when the stamp's
  server identity equals the profile's current canonical `server_url` — a stale stamp from a
  previous URL never bypasses the token requirement. Legacy profiles without the stamp are
  token-required — documented residual: a pre-existing `AuthProvider.None` server profile sees
  the wizard until a sign-in/setup pass stamps it (the stamp is written only inside a commit
  boundary, so the recovery is running the wizard's Sign-in step or `kcap setup`/`kcap login`
  against that server — a bare `kcap config set` cannot mint it). A server that changes provider
  in place (same URL, `none` → authenticated) passes the gate wrongly until requests 401 —
  documented; recovery identical. Unreadable/corrupt token files → `token_unusable` (the wizard
  is the recovery path). The gate and `App.ValidProfileName` share one `ServerIdentity`-based
  validator (decision 2).
- **`WizardAuthService`** drives the §5 façade: single-flight — one auth operation at a time, a
  new attempt only after the previous lane has quiesced (cancelled-before-boundary or terminal);
  cancellation is a distinct outcome, never rendered as failure. Owns the decision-2 close
  handoff: pre-boundary close cancels and awaits quiesce; post-boundary close detaches the UI
  and exposes the terminal-result task the app awaits before graph construction or shutdown.
- **`ConsentFlipClaims`**: the decision-7 store, with a defined quarantine lifecycle — not an
  open-ended inert state. Corruption → the file is renamed aside
  (`consent-flip-claims.quarantined-<n>.json`, preserved for diagnostics) and a fresh empty
  store takes its place. **Arming is never rejected** (Sign-in must not wedge on old
  corruption): new claims land in the fresh store and the coordinator operates on it normally.
  What is lost is the quarantined file's unknown claims, so the quarantine surfaces ONCE as an
  attention state naming the preserved path, with the recovery guidance ("pre-existing daemons
  may need `kcap daemon consent set-default prompt`, or re-run onboarding"); acknowledging it
  (persisted, app-state) resolves the quarantine. Safe by decision 4 regardless — no automatic
  path can mint an `allow` daemon while claims are unreadable, because app-written units seed
  `prompt` independently of this store — subject to the same single qualified exception every
  decision-4 guarantee carries (§3's lock-unaware-writer residual).
- **`KcapCli`** gains `PluginInstallAsync(vendor)` (bounded timeout), the §7 streaming import
  call, and the decision-4 `KCAP_CONSENT_SEED_DEFAULT=prompt` env overlay on **every daemon
  spawn it performs**: install/replace (baked into the unit), `service start --verify` (arms
  the in-transaction gates), and the detached `daemon start -d` fallback (the spawned daemon
  seeds from its own env). For daemon mutations, `KcapCli` is the **action-scoped executor the
  `DaemonMutationLane` creates per owned action** (identity + pinned CLI path bound once per
  action, `KCAP_EXPECT_SERVER_URL` + `KCAP_PROFILE` overlaid — see the lane bullet); long-lived instances
  (the graph's, or the wizard's own post-Sign-in instance for `plugin install`/import) serve
  non-mutating and non-daemon shelling only.
- **CLI compatibility floor — an enforced precondition, not an assumption**: every
  daemon-mutating app action (wizard step 7, lifecycle auto-actions, main-window detached
  Start) runs a **fresh** `--version --no-update-check` probe **immediately before each
  mutation** — a one-time cached approval would turn the probe→exec window into an indefinite
  stale authorization (replace the pinned file once after startup and every later
  Start/install/takeover would invoke the old CLI under the cached pass; the reconciliation
  lane would then re-invoke the same downgraded CLI). Probes are never shared across DISTINCT
  mutations: the `DaemonMutationLane` coalesces only an identical concurrent request into the
  in-flight action's single mutation and outcome, and a queued different request performs its
  own fresh probe after acquiring the lane; no mutation runs until its own probe resolves,
  then it proceeds or refuses on that result; missing, malformed, or below-floor → fail closed with
  update/reinstall guidance, no spawn; reinstalling a compatible CLI lets the next action
  recover without restarting the app. **The floor is the literal `0.12.0-beta.1`**
  (`KcapCliCompatibility.Floor`) — NOT `0.12.0`: this repo's release invariant ships
  internal-first releases as SemVer prereleases on the beta channel, so a plain-release floor
  would classify the first beta carrying this slice (and the app bundled beside it) as
  below-floor, refusing its own CLI. Coupling to release engineering is asserted, not
  assumed: the first `v0.12.0-beta.1` tag is cut at-or-after this slice's merge, and the
  app-package build FAILS if its sibling CLI's version does not satisfy the floor
  (a build-time assertion, since merging alone bumps nothing under MinVer). Pre-tag source
  E2E publishes the dev CLI with `-p:MinVerVersionOverride=0.12.0-beta.1` (the same
  mechanism the release workflow already uses) behind `KCAP_APP_CLI_PATH`. A build-generated
  floor was rejected because it drifts upward with every app build and would wrongly refuse
  older-but-capable CLIs. Comparison: a **strict parse-success predicate first** — `PrereleaseSemver` is an
  ordering helper that accepts invalid SemVer (leading-zero core numbers like `01.2.3`,
  illegal prerelease identifiers), so a strict compatibility parser rejects those BEFORE the
  `PrereleaseSemver` comparison (chosen over `SemverCompare`, which strips prerelease suffixes
  and would pass a below-floor beta at the release boundary); parse failure → fail closed;
  build metadata ignored; a prerelease of the floor version is below the floor. **The probe
  pins the executable it approved, through a defined resolver seam**: `ILoginShellProbe` gains
  a path-returning question (today `KcapOnPathAsync` deliberately discards the `command -v
  kcap` output and returns a bool — that bool cannot pin anything) whose answer is VALIDATED
  before use: it must be an absolute rooted path to an existing regular file — alias/function
  definitions and relative/word-only output (all things `command -v` legitimately emits) are
  invalid, yielding no pin and fail-closed mutations; symlinks are followed at invocation
  time; caching and force-refresh mirror the existing probe's rules. The pinned path is held
  by the ONE `KcapCli` instance both the version probe and every mutation run through, so
  version and mutations provably receive the identical filename. An in-place replacement of
  that pinned file between probe and mutation remains — part of §3's one qualified exception,
  with its real terminal state specified there (a successfully-mutating old CLI is NOT a
  rolled-back window). Bare-`kcap` establishes no capability by itself: `CliResolver` returns `"kcap"`
  unconditionally when the override is absent, and today's version cache is fire-and-forget
  skew telemetry the mutation paths never await — an older PATH CLI would ignore the seed
  directive and carry none of gates 28/29 or the embedded digest, on every routed path.
  Non-daemon shelling (`plugin install`, `import`) keeps its existing lenient classification.
- **App state** (`app-state.json`) keeps only UX claims (`ShimOffered`/`ShimDenied`) — nothing
  safety-bearing (restored to its AI-1654 contract; the flip claim lives in `ConsentFlipClaims`).

## 5. Core onboarding façade

The load-bearing Core work of this slice. A GUI-neutral module in `Capacitor.Cli.Core/Auth/`
(BCL-only, AOT-clean, source-generated serialization; no Avalonia/Rx/Spectre types) exposing the
existing flows through app-consumable seams. The CLI's `setup`/`login` re-plumb onto the same
façade through thin Spectre adapters — one tested implementation, two front-ends,
behavior-preserving for the CLI (asserted by its existing command tests).

- **Operations** (each cancellable up to the commit boundary): *login to known server* (wraps
  `OAuthLoginFlow.LoginWithDiscoveryAsync`), *discover-and-join* (WorkOS and GitHub — the GitHub
  discovery orchestration moves out of `SetupCommand.RunDiscoveryAsync` into the façade, making
  `AcquireGitHubTokenAsync` reachable), *create workspace* (the `ITenantProvisioner` path).
- **Structured progress, not console output**: an injected progress/notice sink replaces the
  `Console.Out/Error` writes on these paths (browser-open notice + fallback URL, device code +
  verification URL, poll ticks, error text). The CLI adapter prints them exactly as today; the
  app renders them in the Sign-in step.
- **Cancellation propagated everywhere before the boundary**: `WorkOSDiscovery`
  `RunWithLiveAuthAsync`/`RunAsync`, `IAuthProxyClient`, the browser wait, the GitHub device poll
  (today an unbounded loop), provisioning polls/refreshes, and the final org switch all gain
  `CancellationToken` parameters; `RunAsync` passes the token it already receives into
  `ITenantProvisioner.OfferCreateAsync` (today dropped). Single-use refresh-token rotation is why
  the single-flight lane (§4) is mandatory: two overlapping WorkOS attempts can invalidate each
  other's sessions.
- **The commit boundary is an ordered state machine, not a single write** — the existing flows
  durably publish in multiple files (WorkOS: config then token; GitHub discovery: config then one
  token per tenant), so "cancelled ⇒ no durable write" can only hold BEFORE the boundary:
  1. Before-commit hook (decision 7): the caller persists flip claims for **every identity the
     boundary will publish** (all discovered tenants on the GitHub path, each with its profile's
     resolved daemon name); hook failure aborts the operation (nothing durable written,
     retryable).
  2. Boundary entered: the remaining publications — profile creation/activation
     (`TenantDiscovery.MergeProfiles` / explicit profile write via the decision-10 mutation
     API), provider stamp (§4), `TokenStore.SaveAsync` (per tenant where applicable) — run
     under `CancellationToken.None` to completion; a cancel arriving mid-boundary is answered
     with `Committed`, never a torn stop; a close hands the wait to the app (decision 2).
  3. Result: `Committed` | `Cancelled` (strictly pre-boundary, nothing durable) | `Failed`.
  Crash residue inside the boundary is defined and harmless by ordering: claim-without-profile
  and profile-without-token both leave the gate failing, so the wizard returns; the dangerous
  inverse (token without claim) cannot occur because the claims are step 1.
- **Async picker**: `ITenantPicker.Pick` (sync) gains an async, cancellable counterpart the
  façade consumes; the Spectre picker adapts trivially, the app's picker awaits the ViewModel.
- **Helper moves**: `SetupCommand.ToServerOrigin` and `ResolveTenantArg` become public Core
  helpers; `AppConfig.ValidVisibilities` becomes public. §11's scope statement enumerates the
  CLI-project churn this causes.

## 6. Consent: seeding, conditional put, coordinator

- **Seeding (decision 4)** covers every daemon booting from an app-written unit. "Seed only when
  absent" is NOT sufficient — today's `LaunchConsentStore.Load` degrades a missing file, a null
  doc, an unreadable file, AND any unrecognized `default` value to `allow` (its `_ =>` arm), so a
  stale or malformed file would silently resurrect `allow` under a fresh unit. With the directive
  present, boot-time classification therefore handles every state, before `ServerConnection`:
  - *absent* → seed `{default: prompt}`;
  - *unreadable or malformed* (null doc, parse failure, unrecognized `default` — every arm that
    silently meant `allow` today) → quarantine the file aside (preserved for diagnostics) and
    seed `prompt`;
  - *valid `prompt`/`deny`* → respect;
  - *valid `allow` with rules* → respect (deliberately configured; §6 provenance stance);
  - *valid `allow`, zero rules* → respect only when stamped `default_source: "operator"`, else
    (seed/legacy/unstamped) rewrite to `prompt`. **`default_source`** is a daemon-internal
    provenance stamp `LaunchConsentStore` writes on every persist — IPC put → `operator`,
    boot seed → `seed` — never on the wire; it is what lets a boot re-seed distinguish a stale
    factory-looking file from an operator who explicitly chose `allow` on an app-managed daemon
    (the operator's choice survives every restart).
  The adversarial ordering — server issues a launch the instant the daemon registers — is
  answered structurally: the launch meets `prompt`, and with no UI attached the consent gate's
  existing fail-closed path denies it.
- **Identity-conditional put — a NEW frame, not new fields (the round-3 correction):**
  `consent/2` is ALREADY taken (AI-1652's identity-checked prompt resolution:
  ConsentSubscribeV2/ConsentResolveV2), and adding optional members to the existing
  `ConsentRulesPut` (frame 14) payload cannot fail closed — STJ skips unknown members, so a
  pre-change daemon would pass a `consent/2` capability check AND perform the unguarded write.
  Instead, per AI-1652's own pattern: a **`ConsentRulesPutV2` frame** (next free `FrameType`)
  whose payload carries **mandatory** `expected_name` + `expected_server_url`; the daemon
  compares them against its own identity at dispatch, on the same connection that carries the
  write, answering a coded `identity_mismatch` ack without mutating on mismatch. An old daemon
  cannot decode the new frame type at all — structural rejection is the fail-closed floor, with
  a new **`consent/3`** capability advertised for discovery (entry next to the routing switch,
  per the AI-1648 rule). The flip uses ONLY the v2 frame; a daemon without `consent/3` gets no
  put and the claim stays pending (wizard-installed daemons are current by construction; the
  coordinator retries after the daemon updates). Status reads use
  `DaemonStatusDto.Daemon.ServerUrl` (nested under `DaemonInfoDto`).
- **Step-7 application** (explicit user action): conditional put with the claim's identity. No
  factory guard — the user is looking at the step that says what it does.
- **`ConsentFlipCoordinator`** (normal post-wizard startup, sibling of `ShimOfferCoordinator`):
  when a claim matching the *current* resolved `{profile, server}` is pending and attach reaches
  `Connected`, it resolves the target daemon name from current config (decision 7), then runs
  capability check → get → factory guard → conditional put (expecting that name + the claim's
  server). **Clearing is itself conditional, inside ONE two-lock critical section** (step 7
  and coordinator alike): the clear acquires the `config.json` mutation lock FIRST,
  re-reads/resolves identity under it, then — still holding it — acquires the claims lock and
  removes the key only if `{profile, server, resolved daemon name}` still equal the captured
  target; fixed global lock order (config → claims), both held across the compare-and-delete;
  otherwise the claim is retained for the next graph. **The whole compare-and-delete is one
  synchronous Core/store operation on one thread** — `ConfigFileLock` is a thread-affine named
  `Mutex` (`WaitOne`/`ReleaseMutex`), so no `await` may occur between acquisition and release
  (decision 10's rule, restated here because this operation holds TWO such locks); async
  callers wrap the entire call in `Task.Run`. **Failure semantics are phase-defined** (atomic
  publication has a commit point, so "any failure leaves it pending" is not physically
  satisfiable): a second-lock timeout or any failure BEFORE the rename leaves the claim
  pending; a durability failure AFTER the rename (target/directory flush) is
  committed-but-durability-uncertain — safe, because a crash can only resurrect the deleted
  key, and a resurrected claim is idempotent (re-applied, factory-guarded, re-cleared). Either
  way, no path loses a claim that was not deliberately consumed. A plain re-read-then-delete would not
  do: `ConfigFileLock` hashes the exact path it is given, so holding the claims-file lock does
  not exclude a `config.json` writer — a rename landing between the re-read and the deletion
  recreates the race. With the config lock held, a racing rename can only land before the
  re-read (claim retained by the compare) or after the clear completes (claim correctly
  consumed for the then-current target). Without any of this, a concurrent
  `kcap config set daemon.name B` would let the coordinator flip daemon A and delete the sole
  `{profile, server}` key while B — possibly a pre-existing `allow` daemon the current graph
  cannot even attach to (the client pins its name at construction) — became the configured
  target unprotected. An extra inert claim is safer than a missing one. A claim whose `{profile, server}` no longer matches the resolved profile
  stays pending and inert. **Scope note:** the coordinator exists for
  pre-existing daemons only (decision 7); its attach-then-put ordering leaves those daemons'
  pre-flip window open — documented residual: such a daemon predates onboarding and was already
  operating under `allow`; the coordinator narrows, not creates, that exposure.
- **Factory guard** (coordinator path only): apply only when the live policy is default `allow`
  with zero rules. Two deliberate weakenings, accepted and documented rather than mechanized:
  (a) *provenance*: an explicit operator-chosen `allow` + zero rules is indistinguishable from
  factory state over the wire — the flip errs toward the safer `prompt` and the operator can
  revert. (b) *policy race*: between the guard's get and the put, a concurrent consent edit can
  be overwritten — same window as the CLI's own `consent set-default`; accepted. (The *identity*
  race is NOT accepted — that is what the v2 frame closes.)

## 6a. Service-operation lane (step 7)

One service operation at a time, app-side, complementing the CLI's own per-label flock:

- The install/replace child runs with `AbandonWait` semantics — **never `KillTree` inside the
  transaction bound** (AI-1654 §3.6's rule survives verbatim). Back/Skip/close during a running
  operation detach the *UI* from the wait; the lane still owns the child.
- Retry (or re-entering the step) first awaits the prior child's exit, then re-queries
  `service status --json` and acts on the evidence: `txn_active` → keep waiting; a stale
  `txn_marker` → the step surfaces AI-1654's repair affordance semantics rather than blindly
  re-installing.
- Wizard close hands the lane to the app's sequencing with **explicit post-cap rules**: graph
  construction waits for the lane up to the CLI transaction's own bound (forward + rollback
  reserve — a finite, known cap); if the child is somehow still live past it (pathological), the
  graph is built with the lifecycle controller's auto-action arm closed and an attention state
  surfaced from re-queried evidence — **auto-mutation stays forbidden while the transaction may
  be live**. App shutdown past its bounded cap may exit with the child detached — safe by
  AI-1654 §3.6's own contract (the transaction completes in the child; force-quit is a tested
  path) — and the next launch reconciles marker/status before any mutation (AI-1654 §3.2's
  startup reconciliation, unchanged).

## 7. Import streaming contract

`IProcessRunner` gains a streaming variant: channel-tagged lines (`stdout` | `stderr`, each
stream line-buffered independently — no cross-stream ordering promise), callbacks invoked on the
pump (never the UI) thread and marshaled by the ViewModel to the UI scheduler, callback
exceptions logged and swallowed (never kill the pump), and a bounded in-memory tail (last 500
lines; older lines drop, the pane says so). **The streaming result is NOT a full-capture
`ProcessResult`**: it carries exit code, `TimedOut`, and the same bounded per-stream tails —
full `Stdout`/`Stderr` captures are explicitly empty, because `ReadToEndAsync`-style retention
would duplicate the entire import output and defeat the bound. Cancel/close = `KillTree` + await
exit — no orphaned children. The CLI exits 0 even with per-session errors and reports them only
in human-formatted output, so the wizard does **not** parse for them: the step *unconditionally*
shows "if anything failed, run `kcap import` in a terminal to retry" alongside the CLI's own
final summary lines.

## 8. AgentDetection contract (Core move)

`AgentDetection` composes the **existing per-vendor rules through pure inputs** (decision 8):
`AgentDetector` moves from the CLI project into Core (PATH walk: PATH + PATHEXT + platform
executable rules, all passed as values); the vendor path helpers are consumed through pure
overloads/input records — `CursorPaths.IsInstalled`'s `~/.cursor`-plus-Electron-directory rules;
`GeminiPaths`' marker-file rules (bare `~/.gemini` deliberately NOT installed — the Antigravity
false-positive guard); `AntigravityPaths.IsInstalled(home, geminiCliHome)` with the `agy` +
`antigravity` binary probes; the `kiro-cli` probe and `KiroPaths`' `KIRO_HOME`; `PiPaths`'
`PI_CODING_AGENT_DIR`; `OpenCodePaths`' data-dir + `OPENCODE_CONFIG_DIR`/XDG overrides; Claude
and Codex binary-only. Every environment override those helpers read internally today becomes a
passed value on the pure overload (the existing global-reading entry points remain for current
callers, delegating to the pure form). Output: per-vendor `{binary_found, install_signal_found}`.
Edge behavior: unreadable PATH entries/dirs → not detected (no throw); symlinks followed;
duplicate PATH entries deduped. Parity tests assert the composition reproduces `SetupCommand`'s
current detection matrix and run fully parallel — no process-global environment mutation.
Documented residual: override values reflect the *app process's* environment — a GUI launch does
not see exports living only in shell rc files (the terminal-PATH probe covers PATH, nothing
covers arbitrary env). NativeAOT publish must stay warning-free with no new Core dependency.

## 9. Error handling

Uniform rule: every failure is a message + Retry/Skip on its step; nothing wedges the wizard.

- **No CLI resolved** (pre-AI-1653, no npm install): steps 1/5/6/7 show "kcap CLI not found" and
  stay skippable; Connect/Sign-in/Defaults still work fully (in-proc). Done lists what was skipped.
- **Sign-in:** browser timeout (the CLI's 5 minutes) → Retry through the quiesced lane. WorkOS
  has no device fallback (loopback-bind failure → retry); the GitHub device flow renders in-step
  and is now bounded by cancellation (§5). Cancellation ≠ failure in every surface. Claim-write
  (before-commit hook) failure → retryable sign-in error, nothing durable written.
- **Create workspace:** availability-check and provisioning errors surface with retry;
  provisioning timeout → "still provisioning — finish later by joining `<slug>` from the Connect
  step" (the CLI's own message, GUI-shaped); `WorkspaceFailed` reasons verbatim.
- **Coding agents:** per-vendor exit codes; a failed vendor gets ⚠ + retry, successes stand.
- **Import:** §7. Cancel = `KillTree`; failure never blocks Next.
- **Enable daemon:** skipped-login users see "requires sign-in". CLI below the compatibility
  floor (§4) → fail closed with update/reinstall guidance, no spawn. §3 step-7 matrix + §6a
  lane semantics for navigation; install failures surface the AI-1654 verify exit codes 20–27
  plus the two additive codes — `verify_start_gate` = 28 (with its `start_gate_reason` routing:
  takeover vs reinstall, §3) and `verify_start_gate_drift` = 29 (attention state + repair, §3)
  — with the same wording the lifecycle controller uses. A daemon that cannot commit its
  seeded policy (decision 4) logs the stable `consent_seed_unwritable` token and exits 0 —
  **what the app then observes is per verb**, because the daemon never satisfies the
  transaction's readiness predicate: on `install`/`replace` the transaction times out and
  rolls back to the verified-safe failure state (unit REMOVED), surfacing
  `verify_readiness_timeout`; on `start` the rollback is a bootout with the plist retained,
  same code. The EXIT CODE cannot distinguish seed-failure from any other never-ready
  daemon — no `VerifyExit` code exists for seed failure itself (the CLI writes no policy
  file) — but the §4 boot-refusal marker can, when attributable: the readiness-failure path
  then adds the auxiliary `refusal_reason=` line and the app routes per the pinned token
  enum; without an attributable marker the timeout guidance points at the daemon log, where
  the token is the diagnosis. `identity_mismatch` ack or missing `consent/3` → no write, claim pending, repair
  guidance.
- **Gate edge cases:** unreadable token file → wizard (it is the recovery path); config
  unreadable → wizard with `no_profile`; corrupt claims file → quarantined with the §4
  lifecycle (fresh store, arming never rejected, one-time attention + ack) — safe because
  app-written units seed `prompt` regardless (decision 4).

## 10. Testing

TUnit throughout, existing disciplines (`[NotInParallel("AvaloniaSession")]`, real-socket harness
rules, `Path.Combine` in path assertions for the Windows CI leg).

- **`OnboardingGate` matrix:** no profile / invalid URL (incl. `file://` — shared-validator
  assertion) / no token file / WorkOS expired with+without `RefreshToken`+`ClientId` / GitHubApp
  expired (usable — server-refreshable) / wrong-server token / legacy unbound token / corrupt
  token file / provider stamp `none` matching current server / stale `none` stamp after
  `server_url` change (token required again) / legacy profile without the stamp.
- **Decision-2 carve-out:** gate-still-failing close → graph builds with auto-actions closed and
  shim auto-offer suppressed; **zero service mutation** asserted for: valid URL + no token +
  abandon; invalid/non-HTTP URL + abandon; close during an in-flight pre-boundary auth operation.
- **Seeding (decision 4):** the full boot-classification matrix, each row with an **adversarial
  immediate-launch assertion** (server issues work the instant the daemon registers; it must
  meet the classified policy, never a silent `allow`): absent → seeded `prompt`; valid `allow`
  stamped `operator` → respected; valid `allow` unstamped/`seed`, zero rules → rewritten to
  `prompt`; valid `allow` with rules → respected (documented residual); valid `prompt`/`deny` →
  respected; malformed / null-doc / unrecognized-`default` (today's silent-`allow` arms) →
  quarantined + seeded; unreadable → quarantined + seeded; **directive value matrix** (empty /
  `allow` / `deny` / case variants / unknown → coded refusal, exit 0, never honored — only the
  literal `prompt` seeds); seed-write failure → `consent_seed_unwritable` log token + exit 0,
  with the per-verb §9 outcomes asserted (install/replace → transaction rollback, unit removed,
  `verify_readiness_timeout`; start → bootout, plist retained). `default_source` stamping: IPC
  put → `operator`, boot seed → `seed`, operator `allow` surviving restarts. **Boot-local
  capture**: after startup the variable is absent from the daemon's ambient env and from every
  child — PTY and each ACP/RPC adapter enumerated — so a hosted agent running
  `kcap daemon start`/`service install` cannot unknowingly seed or bake it. **Detached-start
  coverage through the ONE routed module** — main-window Start/Retry re-routed through the
  pinned `KcapCli.DetachedStartAsync`: configured-user-no-claim,
  acknowledged-quarantine-no-claim, and previously-cleared-claim scenarios each with an
  immediate server launch meeting `prompt`; **no bare fallback** (unresolvable app CLI + an
  old `kcap` on PATH → honest "kcap CLI not found", no legacy spawn); **detached digest gate**
  (floor-compatible CLI + legacy sibling bytes → `daemon start` with the directive refuses
  pre-spawn with the package-inconsistent result, on both the lifecycle and main-window
  surfaces); **profile pinning** (a
  `kcap use` after graph construction does not redirect the app's start — the pinned profile
  is used). **Respawn successor**: a
  detached daemon's restart-after-update successor receives the captured directive via
  `DetachedRespawnStrategy`'s explicit re-injection — fixture removes/corrupts the policy file
  before successor startup and asserts no `allow`-capable registration — while the same
  successor's hosted children still see the variable scrubbed. No directive (headless unit
  / manual terminal start) → every arm behaves exactly as today. Directive baked into the unit
  is covered by the existing TxnMarker fingerprint tests (rolls back with the unit).
- **Compatibility floor & resolver seam:** below/equal/above the concrete `0.12.0-beta.1`
  literal, including this repo's beta shapes (`0.11.x` below; `0.12.0-beta.N` ≥ beta.1 passes;
  plain `0.12.0` passes); **fresh probe per mutation** — CLI replaced after startup and
  between two mutations → the second is refused; reinstalling a compatible CLI → the next
  action recovers without an app restart; concurrent callers share only the in-flight
  action's probe; strict
  parse rejections that `PrereleaseSemver` alone would accept (`01.2.3`, leading-zero and
  illegal prerelease identifiers) → fail closed; malformed/unknown `--version` → fail closed;
  a mutation requested while the probe is in flight performs NO mutation until the probe
  resolves, then proceeds or refuses on its result; **resolver validation** — alias/function
  definitions, relative and word-only `command -v` output → no pin, mutations fail closed;
  absolute paths with spaces and symlinks round-trip; probe failure → fail closed; version
  probe and every mutation provably invoke the IDENTICAL pinned filename (PATH mutated
  between probe and mutation → pinned path still used); valid-probe-then-swapped-bytes → the
  §3 per-path terminal state (successful old-CLI mutation + post-result reconciliation
  surfacing), not a rollback claim — on all three mutation surfaces (wizard step 7, lifecycle
  auto-actions, main-window Start). **Exit-43 routing**: both surfaces map
  `daemon_start_reason=package_inconsistent` to reinstall guidance and any unknown reason to
  fail-closed attention.
- **Telemetry suppression (process-local marker):** with a fresh telemetry state, an
  app-spawned probe/status/plugin/import/mutation child creates NO notice marker, device id,
  spool entry, or network event — the disclosure is not swallowed; the marker is
  consumed-and-removed, so the **app-detached daemon, its respawn successor, and every hosted
  child adapter (PTY and each ACP/RPC)** observe no marker and keep normal telemetry; a
  user's pre-existing `KCAP_TELEMETRY` value is preserved untouched; unit-launched daemons
  and terminal invocations keep today's behavior. **Reason-line parsing:** unrelated stderr
  lines never affect routing; zero, duplicate, or conflicting MATCHING
  `start_gate_reason=`/`daemon_start_reason=` lines fail closed.
- **`DaemonMutationLane` (through its interface, all three callers):** wizard step 7 (no
  graph — the `LocalControlProbe` one-shot adapter), lifecycle controller, and main-window
  Start each: fresh generation armed at lane acquisition; **success predicates** — mutation
  failure beside an already-Connected daemon is NOT `Succeeded`; wrong-server Connected →
  `AttentionSkew`/no success; manual/non-owning Connected → not `Succeeded` (ownership
  evidence required); unreachable with no owner → `UnconfirmedNoAttach`, no ownership claim;
  Connected-below-floor / missing-`consent/3` / incompatible-hello → `AttentionSkew` +
  caller-routed takeover + coordinator claim application; swapped old CLI leaving only an
  unseeded stopped unit, and legacy `status --json` without the new unit fields →
  `AttentionRepair`, never silence. **Instance binding:** forced daemon swaps at
  hello→snapshot and snapshot→ownership → inconsistent `instance_id`/`pid` correlation →
  fail closed, never `Succeeded`; a pre-slice daemon without the fields → same; the swap
  asserted through BOTH adapters (the one-shot probe, and the live client which must refuse
  to emit `Connected` on a hello↔snapshot mismatch and classify it incompatible). **Lane
  concurrency & lifetime:** lifecycle+main-window and service+detached concurrent requests —
  an identical pair coalesces into ONE mutation and outcome (never two mutations from one
  approval); a different queued request re-probes fresh after acquiring the lane; waiter A
  cancels → B still receives the terminal outcome (and vice versa); **coalesced actionable
  fan-out** — an identical lifecycle+main-window request returning `AttentionSkew` (and
  separately `AttentionRepair`) → one mutation, ONE prompt/attention surface (both waiters'
  results are state-only), and no second takeover request enqueued; ALL waiters cancel with
  no immediate retry → the owned action runs to its terminal state and its actionable
  outcome lands in the FIFO; **two queued actions whose waiters all detach, producing
  DIFFERENT attention outcomes before any drain** → both surface, in FIFO order, exactly
  once (no overwrite, no drop); **late enqueue after the handoff's initial take** — the root
  completes its first drain empty, the post-cap action finishes and enqueues → prompt
  delivery to the live consumer, exactly once; an enqueue racing the atomic
  consumer-ownership transfer → delivered to exactly one consumer, no loss or duplicate;
  **lease disposition** — pause after dequeue but before UI dispatch, close the wizard → the
  envelope requeues and surfaces exactly once under the root; transfer while a prompt is
  already visible → presented (close-without-answer = decline), acked, never requeued;
  envelopes carry their `MutationRequest` identity for routing; **app-lifetime lane** — a
  wizard action outliving the graph-build cap, then a main-window mutation: both run through
  the SAME lane instance, the latter queues behind the live action, the adapter slot swaps
  atomically at graph build, and the late actionable envelope reaches the normal consumer;
  **action-scoped bindings** — a wizard action, the graph handoff, then a queued
  second-identity action: each action's floor probe and mutation use ONE
  executable/name/profile/server binding (asserted at the executor seam); a reconciliation
  straddling the slot swap keeps its action-start observation strategy; a queued request
  whose daemon/profile differs from the newly built graph gets an identity-specific one-shot
  probe, never the mismatched live adapter; **server expectation** — a forced-order
  `kcap config set server_url` on the pinned profile after the lane captured its request,
  raced against install, gated start, AND detached start: the daemon's resolved server no
  longer matches the captured `KCAP_EXPECT_SERVER_URL` → coded exit-0 refusal
  (`server_expectation_mismatch`), never a wrong-server or wrong-credential boot;
  **credential identity under the expectation** — active profile B, requested profile A:
  (a) A and B share the canonical server with different tokens → A's token used; (b) B holds
  a pre-upgrade unbound token for another server → never read (B is never resolved); (c) a
  WorkOS refresh writes A's token file only; (d) A's profile daemon settings survive the
  overlay (profile resolution untouched by the expectation variable); **expectation carrier
  lifecycle** — the unit/detached successor keeps enforcing (successor re-injection; unit
  restarts from the unit) while hosted children (PTY + each ACP/RPC) observe no expectation
  and a hosted `kcap daemon start`/`service install` for another profile hits no inherited
  mismatch and bakes no inherited expectation; **stale-pin gate** — profile re-pointed
  after install: the gate returns pre-mutation 28/`identity_mismatch` (nothing booted,
  takeover offered, `unit_expected_server` explains the pin), while a post-final-recheck
  race still ends in the daemon's refusal; **refusal propagation & marker protocol** — expectation mismatch
  before AND after the detached wrapper's liveness window, plus install/start readiness
  timeouts: each yields the exact ProcessResult shape, the marker is attributed per its
  verb's causality rule, the coded `refusal_reason=` line rides the failure, and the lane
  maps `Refused(server_expectation_mismatch)` with takeover/reinstall guidance —
  distinguishable from unrelated startup failure; back-to-back refusal→success→refusal
  (success deletes the leftover marker; each refusal freshly attributed);
  clock-shift/equal-timestamp cases (attribution never uses wall-clock comparison); an
  external same-name writer during a service wait → identity validation rejects it (no
  false `Refused`, no takeover prompt from foreign evidence); different daemon names →
  per-name markers never cross-attribute; single-consumer discipline (transaction consumes
  for service verbs and the lane uses only the reason line; detached reconciliation
  consumes directly) → no starvation, no stale attribution, no duplicate presentation;
  corrupt marker → renamed aside, ignored, logged; **service attribution correlation** —
  pre-clear verification failure → coded attribution disabled, generic timeout, mutation
  proceeds; an external same-name manual boot with the SAME expectation and absent
  attempt_id during the readiness window → pid never matches the label's observed job PID →
  not attributed, no false `Refused`/takeover; a concurrent kickstart of the same unit →
  attributed (it is this unit booting); **reason-enum totality** — per-verb routing for
  each pinned token (`server_expectation_mismatch` → takeover; `consent_seed_unwritable` →
  attention + storage guidance; `consent_seed_invalid` → takeover) plus unknown, duplicate,
  and malformed `refusal_reason=` evidence → fail-closed generic outcome; **marker-write containment** — fault
  injection at dir-create/temp-write/flush/rename while provoking
  `consent_seed_unwritable` → the exit-0 refusal is preserved (no respin), no
  registration/token use, the lane quiesces, and the degraded surface is the generic
  outcome with log-pointing guidance; a timed-out wrapper yields
  the exact existing runner shape (`ProcessResult.TimedOut == true`), asserted at the runner
  seam, not only the final `MutationOutcome`; a waiterless `Succeeded` is logged and does
  NOT enter the FIFO; graph construction awaits `QuiescedAsync` over a still-live action; a
  retry waits for quiesce; wizard close hitting the cap while the shared child is live →
  §6a post-cap rules. **Deadlines (real processes, not runner fakes):** a detached CLI that
  never exits, and one that spawns the daemon then hangs — the lane's bounded timeout kills
  the wrapper via the process-only kill mode (the un-reparented daemon child demonstrably
  survives), reconciliation classifies the partial effect (fully verified daemon →
  `SucceededAfterTimeout`; incomplete evidence → `UnconfirmedNoAttach`), the lane quiesces,
  and the next queued request is admitted.
- **Digest pipeline (release acceptance):** per-RID publish asserts the packaged daemon's
  digest equals the CLI's embedded constant; a mismatched or missing build input fails the
  production publish closed; dev/source builds hash the co-built daemon output, and an absent
  one produces the fail-closed placeholder.
- **Claim store & protocol:** arm flushed before boundary (fault injection at write/flush/rename,
  then proceed through a successful token commit + quit + relaunch → coordinator still holds the
  claim); hook failure blocks the commit; **multi-tenant GitHub discovery arms one claim per
  published identity**; keyed merge under two concurrent writers (two identities → two keys, no
  clobber); `AuthProvider.None` profile-only commit still arms; **daemon renames need no claim
  write** — a wizard Defaults rename AND a terminal `kcap config set daemon.name` (raced against
  an already-running `allow` daemon) both leave the `{profile, server}` claim applicable, with
  the coordinator resolving the new name at application time and the conditional put verifying
  name + server against the live daemon; **conditional clearing under the two-lock critical section** — a rename injected at each
  point of the consume sequence (after resolve / after get / after put) leaves the claim
  RETAINED whenever the re-read `{profile, server, name}` no longer equals the captured
  target; a rename poised WHILE deletion is in flight blocks on the held config lock and lands
  only after the clear (correctly consumed for the then-current target); the renamed
  identity's pre-existing `allow` daemon is protected on the next graph; **clear-failure
  semantics, per phase** — pre-commit injections (second-lock timeout, temp write, temp
  flush, the rename itself failing) leave the claim pending; post-commit injections
  (target-file flush, directory flush AFTER a successful rename) leave the store showing the
  key removed, and a crash/reload at that point may resurrect the key — asserted as the safe
  idempotent re-apply (factory-guarded, re-cleared), never as "pending"; concurrent
  arm/clear/config mutations observe the global lock order with no lost claim; terminal `kcap use` to another (claimed) identity →
  that identity's claim applies on its own attach; **quarantine lifecycle**: corruption before Sign-in (arming lands in the fresh store,
  never rejected) AND after a successful commit (existing claims quarantined, attention surfaced
  once, ack persists, coordinator operates on the fresh store), quarantined file preserved, and
  a subsequent app install still seeds `prompt` either way (fails safe, not open).
- **Conditional put (v2 frame):** against the real-socket harness — match applies; mismatch
  answers the coded ack and mutates nothing; **pre-change daemon** (advertises `consent/2`, no
  `consent/3`, cannot decode the v2 frame) → no put attempted (capability gate) AND structural
  rejection if sent anyway; daemon **replaced between get and put** → v2 frame lands on the new
  daemon, identity mismatch, no write; factory-guard matrix; the two documented §6 weakenings
  asserted as behavior.
- **Commit boundary:** cancellation on both sides of every publication boundary — pre-boundary
  cancel = nothing durable; mid-boundary cancel = `Committed` with all publications present;
  close AND process-shutdown requests on both sides (post-boundary close → app awaits terminal
  result before graph/shutdown); crash inside the boundary → defined residue and the gate still
  fails; provider stamp absent after a cancelled login.
- **Step-7 matrix:** every §3 row — install / start-existing passing both in-transaction gates /
  **stopped unit with a pre-consent binary** (no directive baked, or content digest ≠ current
  install target: exit 28 `verify_start_gate`, no start, takeover offered — fixture: a stopped
  legacy daemon whose server sends work immediately, asserting the gate **never executes the
  unit's binary or creates a socket/registration**) / **same-path/legacy-bytes** (gate-capable
  CLI with old daemon bytes at the exact canonical sibling path: digest ≠ the CLI's embedded
  build-time constant → inconsistent-install surface, no start, no takeover-to-same-bad-bytes;
  plus the install/replace viability arm refusing to write a unit over those bytes) /
  **loaded-definition-vs-disk drift** (launchd holds a stale loaded definition while the
  on-disk plist was validated: the gated start boots out and bootstraps from disk, never
  kickstarts the stale definition) / **phase split** (loaded-inactive unit whose evidence is
  swapped during bootout: exit 29 `verify_start_gate_drift`, marker lifecycle asserted, final
  state label-unloaded/plist-retained — vs a pre-mutation gate failure's exit 28 with no
  marker and nothing touched) / **check-to-exec window, per verb** (deterministic forced order via
  a test seam that pauses after the final recheck, swaps unit + binary, resumes — for the
  gated start AND for install/replace, whose digest re-checks before bootstrap and after
  readiness are asserted here: drift detected → marker-backed rollback — the accepted §3
  residual asserted as behavior, no timing measurement) / **unit rewritten between the app's status read and the transaction's
  lock acquisition** (the under-lock re-read catches it: gate outcome reflects the rewritten
  unit, never the stale read) / **effective-identity fixtures** (same
  `unit_profile` + baked `KCAP_URL` pointing elsewhere; same profile name under a different
  baked `KCAP_CONFIG_DIR`; duplicate/malformed relevant env keys → fail closed) / owning +
  identity (skip + claim apply) / owning wrong-server (takeover offered, NOT "enabled") /
  manual owner no unit, identity match / manual owner + non-owning unit / **manual owner
  wrong-server** (no mutation, no claim application, takeover the only offer) / orphan label /
  stale marker (repair) / `txn_active` (wait) / `unknown` (no mutation) — against real
  `service status --json` fixtures, incl. the new
  `unit_profile`/`unit_server_url`/`unit_consent_seed` fields (UX evidence only).
- **§6a lane:** close/Back during install → child unkilled, lane owned; retry waits child exit +
  re-query; **child outliving the cap** → graph built with auto-arm closed + attention state, no
  auto-mutation while the transaction may be live; next-launch reconciliation sees the marker.
- **Façade:** cancellation at every pre-boundary await — picker, availability check, each
  provisioning poll/refresh, final org switch, browser wait, device poll — each asserting *no
  durable write*; Retry-after-cancel only after quiesce; single-flight; CLI behavior parity for
  `setup`/`login` through the Spectre adapters.
- **Step ViewModels** via `Avalonia.Headless`: the §3 transition table — pasted URL / GitHub
  discovery / WorkOS discovery / zero-tenant create / "I already have a workspace" retarget /
  `AuthProvider.None` auto-satisfy — plus Back/Skip on both sides of the boundary, and re-entry
  recognizing satisfied states.
- **Config mutation API:** the wizard racing REAL `ConfigCommand`/`ProfileCommand`/`UseCommand`
  entry points — unrelated fields preserved both directions; unique temp names; lock held across
  a synchronous critical section only (no await-under-mutex); **legacy v1 config migrated
  in-memory under the lock while racing a real command writer** (no recursive acquisition, no
  lost migration).
- **`AgentDetection`:** §8 parity matrix against the composed helpers, PATHEXT/Windows rules,
  Gemini marker rules (bare `~/.gemini` NOT detected), override values as pure inputs — parity
  suite runs in parallel with zero process-env mutation.
- **Streaming runner** against a real child process: interleaved stdout/stderr tagging, bounded
  tail, callback exception swallowed, mid-stream `KillTree` cancel with no orphaned child, and a
  large-output run asserting BOTH the ViewModel tail and the runner-retained result stay bounded
  (full captures empty).
- **App startup:** gate-fires → wizard-first (asserting no service/tray/lifecycle/shim
  coordinator built); wizard-close → normal graph with freshly resolved profile.
- **AOT:** `dotnet publish` warning-free (Core façade + AgentDetection are BCL-only).
- **E2E stays manual** (umbrella §10): fresh machine full pass; abandon-after-sign-in → later
  auto-install is born `prompt`; abandon-before-sign-in → no auto-install; multi-tenant pick;
  real create-workspace against staging (`KCAP_SIGNUP_URL`); import cancel mid-run;
  manual-daemon takeover on step 7; shim step on a `.zshrc`-only-PATH machine.

## 11. Scope boundaries

- **AI-1653 keeps:** bundling, signing/notarization, auto-update, the bundle-relative
  `CliResolver` arm. The wizard runs from source via `KCAP_APP_CLI_PATH`.
- **AI-1656 keeps:** the settings surfaces that edit everything the wizard set.
- **Follow-up issue (new):** desktop app telemetry — source label, disclosure surface, async
  delivery, desktop onboarding funnel (decision 9).
- **Zero server-side work** (signup rides the existing provisioning backend) and **zero new CLI
  verbs or flags**. Enumerated protocol/daemon changes: **one new IPC frame pair**
  (`ConsentRulesPutV2` with mandatory expected-identity + its ack), advertised as `consent/3`
  (§6) — new frame type, structurally rejected by old daemons, no protocol bump; **additive
  `pid` + `instance_id` members on `HelloReplyDto` and `DaemonInfoDto`** (§4's evidence
  binding — additive DTO members, no protocol bump, skipped by old clients), with
  `LocalControlClient` enforcing the hello↔first-snapshot correlation BEFORE emitting
  `Connected` and the `Connected` event carrying the correlated identity (Core client +
  app-shell seam change, §4); **the decision-4
  boot seed** — directive-gated policy classification before `ServerConnection`, the exact-value
  contract (only `prompt`; anything else is a coded refusal), the `default_source` provenance
  stamp in `consent.json` (daemon-internal, never on the wire), the coded exit-0 refusal
  (`consent_seed_unwritable`) when the seed cannot be committed, and **boot-local capture** — the
  directive is removed from the daemon's ambient environment at startup and added to the
  PTY/ACP env-scrub lists, so hosted children never inherit it. Enumerated CLI changes:
  `ServiceEnvironment`'s baked-env allowlist gains `KCAP_CONSENT_SEED_DEFAULT` (deliberate unit
  content — decision 4) and `KCAP_EXPECT_SERVER_URL` (§4's server expectation; the daemon
  enforces it on every boot path with the coded exit-0 `server_expectation_mismatch`
  refusal — one further daemon-side boot check, enumerated with the seed); `service start --verify` enforces the §3 step-7 gates in-transaction
  when invoked with the directive (embedded-digest + effective-identity evidence, bootout +
  bootstrap-from-validated-disk), with the additive coded failures `verify_start_gate` = 28
  (pre-mutation) and `verify_start_gate_drift` = 29 (post-bootout, marker-backed rollback);
  install/replace gain the embedded-digest viability arm; **one build/packaging change, with
  its pipeline pinned against the ACTUAL build graph** (the CLI and daemon projects are
  independent, the release workflow publishes CLI before daemon today, and AOT CI publishes
  them as separate matrix entries — so "an MSBuild step" alone has no final daemon artifact to
  hash): per RID, (1) publish and finalize — including AI-1653 signing, recorded as an ordering
  constraint: sign the daemon BEFORE hashing or signing invalidates the digest — the daemon
  artifact first, (2) compute its SHA-256, (3) pass it as a required RID-scoped build input to
  the CLI publish (generated `obj` source), (4) publish/sign the CLI, (5) assert the packaged
  daemon still matches. A missing/placeholder digest fails production publish closed. Ordinary
  source/dev builds hash the co-built daemon project output via a build-target dependency;
  when absent, the placeholder makes the gate fail closed (never open);
  the app's main-window Start/Retry re-routes through the pinned `KcapCli.DetachedStartAsync`
  (no bare `"kcap"` fallback for app-managed starts); `daemon start` gains the
  directive-gated pre-spawn digest check with pinned exit 43 +
  `daemon_start_reason=package_inconsistent`; `ILoginShellProbe` gains the validated
  path-returning resolver question (§4); the app gains `KcapCliCompatibility.Floor` =
  `0.12.0-beta.1` with the strict-parse-then-`PrereleaseSemver` comparison, a fresh probe per
  mutation, and the `DaemonMutationLane` singleton (§4); `Program.cs` consumes-and-removes
  the `KCAP_APP_SPAWN_NO_TELEMETRY` marker before dispatch (decision 9 — one CLI change);
  Core gains the public `LocalControlProbe` one-shot seam (§4); the app-package build asserts
  its sibling CLI satisfies the floor;
  `service status --json` gains additive `unit_profile`, `unit_server_url`,
  `unit_expected_server`, and `unit_consent_seed` fields derived from the unit's baked env
  (UX evidence only — the transaction re-derives under the lock); the daemon writes the
  atomic per-name `{stateDir}/boot-refusal.json` marker on every coded exit-0 refusal
  (identity-bearing record incl. `attempt_id` echoed from the per-action
  `KCAP_BOOT_ATTEMPT` carrier; contained best-effort write; success deletes leftovers),
  consumed per verb — the verify transaction (emitting the `refusal_reason=` line) or the
  detached reconciliation (§4);
  `AgentDetection` composition move + `AgentDetector` to Core + pure-input overloads on
  `KiroPaths`/`PiPaths`/`OpenCodePaths` (decision 8); `setup`/`login` re-plumbed onto the §5
  façade with Spectre adapters (behavior-preserving); `ToServerOrigin`/`ResolveTenantArg` moved
  to Core; every `config.json` writer migrated to the decision-10 mutation API (`ConfigCommand`,
  `ProfileCommand`, `UseCommand`, `UpdateCommand`, `IgnoreCommand`, `RemapCommand`,
  `ImportCommand`, `SetupCommand`, `Program`, `WorkOSDiscovery`, `MachineIdProvider`, and
  `LoadProfileConfig`'s migration path via the pure in-memory primitive). Core gains the façade
  seams (§5), the `auth_provider` server-scoped stamp (§4), the mutation API (decision 10), and
  public `ValidVisibilities`. User-visible CLI behavior is unchanged, so no README/help churn.
- One PR (references AI-1655 and its GitHub issue).

## Plan C implementation riders (2026-08-17)

- §4/§5 result algebra: `AuthResult` gains `Retarget(ServerInput)` (the WorkOS "I already have a
  workspace" completion; pre-boundary, nothing durable) and `Failed` carries `AuthFailureReason`
  `{Other, Unreachable, SigninDenied, NoTenantsFound}` (the setup adapter's funnel discriminator).
- §5 `LoginAsync` carries `adoptServer` (default `false`): `kcap login` never repoints a profile's
  `server_url`; `kcap setup` and the wizard's Paste operation pass `true` (the write that makes a
  fresh profile gate-complete). A `None`-server login on a foreign profile now fails honestly
  instead of writing nothing.
- §5 boundary totality: once entered, publication exceptions never escape as raw exceptions —
  config-commit failure → `Failed` (nothing durable began); post-config failure → `Committed` with
  a credentials warning; per-tenant exchange failures warn and continue.
- §3 step 2/3: pasted input uses `ToServerOrigin`+`ResolveTenantArg` plus the pure loopback default
  (a scheme-less host resolves to `http`) — path-routed self-hosted servers are not expressible in
  the wizard's Paste field (documented residual; `kcap setup <url>` still accepts them).
- §6 step-7 claim application: the conditional put preserves the daemon's existing rules and
  prompt timeout (get-then-put; the v2 identity echo is the guard); an operator-chosen `deny` is
  respected (no put, claim retained inert).
- §4 observation: the two-adapter live/one-shot slot design is superseded — every lane
  classification uses fresh one-shot probes with the instance-bound hello+snapshot correlation
  (carried from the app-lane slice).
- Decision 9 note: the wizard's `SetupFunnel` calls inside shared Core/provisioner flows are inert
  in the app process (`CliTelemetry` never initialized) — kept for CLI parity.
- §3 step 7: a wizard user with an unresolvable daemon binary sees enable/takeover/repair
  withdrawn with reinstall guidance (spawn rows only; a running owned daemon still reports
  enabled).
- Decision 2: past the wizard-close quiesce cap, the graph is built with auto-actions permanently
  closed and one attention line (the §6a rule as implemented).
