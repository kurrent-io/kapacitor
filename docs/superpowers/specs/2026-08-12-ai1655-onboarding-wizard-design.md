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
| 2 | **Wizard-first startup: when the gate fires, `App.StartAsync` builds NO daemon graph** — no `DaemonClientService`, no tray, no `DaemonLifecycleController`, no `ShimOfferCoordinator`. The wizard is the only window. Forced, not just simpler: everything in the graph pins identity at construction (`KcapCli` pins the profile, `DaemonClientService` pins the daemon name) and on a gate-failing machine the wizard is what *decides* those identities. On wizard close — finished or abandoned — startup re-resolves the profile fresh and builds the normal graph, **with one carve-out: if the gate still fails at close, the lifecycle controller starts with its startup auto-action arm permanently closed and the post-close auto shim offer suppressed** (user-clicked actions and the tray shim item keep working). Without the carve-out, a URL-valid/token-less machine whose user closes the wizard before sign-in would flow straight into AI-1654's silent auto-install — §4.1 preconditions require a profile URL but no token. Gate and graph must also agree on what "valid URL" means: both use the same `ServerIdentity` canonicalization (today `App.ValidProfileName` accepts any absolute URI, e.g. `file://`, that the gate would reject). Graph construction after wizard close additionally **defers until both wizard lanes have quiesced** — the service-operation lane (§6a) and the auth lane's commit boundary (§5): a close after the boundary begins detaches the UI, but the app awaits the operation's terminal `Committed`/`Failed` before resolving configuration, building the graph, or completing shutdown. |
| 3 | **Hybrid drive: auth/discovery/provisioning run IN-PROC via a GUI-neutral Core façade; steps with tested non-interactive CLI surfaces are shelled.** This amends umbrella §7's "every step shells the bundled CLI" the way AI-1650 amended the MVVM choice — the rationale (reuse existing tested flows, never reimplement) is preserved: `OAuthLoginFlow`/`WorkOSDiscovery`/`WorkOSTokenSource`/`TokenStore` ARE the tested flow, in Core. Shelling login is impossible for exactly the cases that matter: multi-tenant discovery and create-workspace are Spectre prompts that hard-fail on redirected stdin, and the tenant pick happens mid-auth-session (a shelled two-phase `login --list`/`login --tenant` forces re-auth between list and pick). The Core seams as they exist today are NOT app-consumable — sync `ITenantPicker.Pick`, no cancellation anywhere, `Console` writes inside `WorkOSDiscovery`/`OAuthLoginFlow`, GitHub discovery orchestration living in `SetupCommand` with internal helpers — so this slice reshapes them into an explicit onboarding façade (§5) that the CLI's `setup`/`login` re-plumb onto, behavior-preserving. Rejected: growing a non-interactive CLI surface (large public surface in an app slice; the mid-session pick problem stands); PTY-driving the interactive commands (screen-scraping Spectre redraws has no contract). |
| 4 | **App-managed daemons are born `prompt`: the DAEMON seeds its own policy at boot, behind a unit-baked directive.** The app sets `KCAP_CONSENT_SEED_DEFAULT=prompt` in the env overlay of its `service install [--replace] --verify` calls; `ServiceEnvironment`'s baked-env allowlist gains the key, so it becomes **deliberate unit content** — a property of the app-managed unit consumed at every daemon boot, not transaction control (and, being plist content, it is already covered by AI-1654's `TxnMarker` fingerprint and rollback: a failed install rolls the directive back with the unit; no separate seed artifact, phases, or flush ordering exist). At boot, with the directive present, the daemon — under its own `DaemonLock`, as the sole writer of `consent.json`, strictly BEFORE `ServerConnection` and before the launch gate serves anything — classifies its policy file with its own parser rules (§6). Nothing outside the daemon ever writes the file, which is what closes the offline-writer race: a manual `kcap daemon start` does not hold the service flock, so a CLI-side seed write could interleave with a live daemon's in-memory policy; a boot-time self-seed cannot. This is the causal barrier: the policy is committed before the daemon can register, so *no* post-attach flip has to beat a server-issued launch. If the directive is present and the seed/quarantine write fails, the daemon logs a coded reason and **exits 0** — comes to rest under `KeepAlive` per AI-1654 decision 6, never runs with an uncommittable policy. Headless daemons are untouched (no directive → today's behavior exactly; the upgrade-safe `allow` default stands). This generalizes umbrella §5's "the app's onboarding flips the daemon it manages to `prompt`" to *every* unit the app writes — deliberate: an app-managed desktop daemon has a UI for prompts (AI-1652), and a prompt-with-no-UI resolves as the designed fail-closed deny. Detached `daemon start -d` (the tray fallback; no unit) does not seed — covered by the decision-7 claim path, documented. |
| 5 | **The wizard includes visibility and daemon name** (setup's step 3/6 and 5/6, which umbrella §7's list omitted): a Defaults step with the visibility picker (default `org_public`) and daemon-name field (default: lowercased username). |
| 6 | **Import step: vendor checkboxes + scope choice** (Everything / one org / specific repo — mirroring `ImportScopePrompt`'s vocabulary), running `kcap import --all|--org <o>|--repo <r> --yes` plus selected vendor flags. Setup's own embedded import is current-repo-scoped, which is meaningless for a GUI launch. |
| 7 | **The consent-flip claim covers what seeding cannot: PRE-EXISTING daemons.** With decision 4, a daemon the app installs is born `prompt`; the claim's only remaining job is a daemon that already exists at onboarding time (running, or installed-stopped with an existing policy file). Claims live in their own store — `{config}/consent-flip-claims.json`, a collection keyed by canonical `{profile, server URL, daemon name}` with merge semantics (arm upserts; apply/clear removes only its key) — mutated under `ConfigFileLock` and **flushed (file and directory) before the sign-in commit proceeds**, AI-1654-marker-grade. Deliberately NOT in `app-state.json` (UX-grade by contract). The façade's async **before-commit hook** carries the full identity SET the boundary is about to make gate-complete — GitHub discovery publishes a token per discovered tenant, so one claim per published identity (each with that profile's resolved daemon name) — and **hook failure prevents the commit** (retryable sign-in error). "Sign-in completion" means the façade's commit boundary (§5), NOT `SetupFunnel.SigninCompleted`. The Defaults step rebinding the daemon name re-keys the claim under the same lock. Step 7 applies/consumes the claim; `ConsentFlipCoordinator` applies it to pre-existing daemons on a later attach (§6). Abandoning before sign-in arms nothing (decision 2's carve-out covers that population). **Store corruption fails safe, not open** (§4): the corrupt file is quarantined and surfaced, the coordinator goes inert — and no `allow` daemon can be minted meanwhile, because every app install seeds `prompt` regardless of claim-store health. |
| 8 | **Harness detection moves to `Capacitor.Cli.Core` as `AgentDetection`, composing the existing per-vendor rules through PURE inputs** (§8). `AgentDetector` (today in the CLI project) moves to Core; the env-reading vendor helpers (`KiroPaths` `KIRO_HOME`, `PiPaths` `PI_CODING_AGENT_DIR`, `OpenCodePaths` `OPENCODE_CONFIG_DIR`/XDG) gain pure overloads/input records so PATH, PATHEXT, home, and every relevant override are passed as values — an injected accessor around an aggregate that still reads globals inside would be composition theater, and parity tests must run in parallel without mutating process env. The app feeds the terminal PATH from `LoginShellProbe`. No new CLI verb. |
| 9 | **The app emits NO telemetry this slice; desktop-onboarding funnel coverage is explicitly deferred** to a follow-up issue. The app never calls `CliTelemetry.Initialize`, so Core's embedded `SetupFunnel` emissions no-op (`Capture`/`CaptureNow` are guarded on `Enabled`/`_client`). Reversed from the pre-review draft, which was unsafe on three verified counts: `Initialize` hardcodes `source: "cli"` and prints the one-time privacy disclosure to `Console.Error` — invisible in a WinExe, silently consuming `notice_shown`; `CaptureNow` is deliberately sync-over-async, safe only in a console app without a SynchronizationContext, and can deadlock on Avalonia's UI context; and app-emitted fragments would corrupt the funnel while mislabeled as CLI traffic. The follow-up owns: an app source label, a visible disclosure surface, async delivery, and the desktop funnel sequence. |
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
   the active profile; a daemon-name change re-keys the decision-7 claim under its lock.
5. **Coding agents** — `AgentDetection` results (§8) as pre-checked checkboxes →
   `kcap plugin install --<vendor>` per selection (Claude Code is the flagless default install).
6. **Import history** — vendor checkboxes + scope choice (decision 6); output streams into a live
   log pane per the §7 contract; Cancel kills the child tree. A failed import never blocks
   finishing onboarding.
7. **Enable daemon** — through the §6a service lane, on **AI-1654's full state matrix** (not a
   reduced one), from a fresh `service status --json`:
   - *No unit, no live owner* → `service install --verify` (bakes the seed directive, decision 4).
   - *Unit present, loaded-inactive or stopped, `daemon_pid` null* → `service start --verify`,
     but ONLY when the unit passes two pre-start gates; failing either routes to the dialoged
     takeover (`--replace` bakes the directive and the current binary) instead of a bare start:
     **(a) directive + consent-capable binary** — the unit bakes `KCAP_CONSENT_SEED_DEFAULT`
     AND the unit's `binary_path` answers a bounded `--version` at or above the
     directive-introducing version. A pre-consent legacy binary ignores both the seed and the
     policy file entirely, and `start --verify` deliberately accepts capability-incompatible
     hellos (AI-1654 §3.4), so post-start discovery is too late — the gate must precede
     bootstrap. **(b) identity** — the unit's baked profile matches the expected one.
     `service status --json` gains additive `unit_profile` and `unit_consent_seed` fields
     (parsed from the unit's baked env, next to the existing `binary_path` parse) so both gates
     read from evidence, not assumption. The same gates apply to the lifecycle controller's
     silent auto-start row: gate failure → no silent start, affordance surfaced.
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
```

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
  (persisted, app-state) resolves the quarantine. Safe by decision 4 regardless: no automatic
  path can mint an `allow` daemon while claims are unreadable, because app-written units seed
  `prompt` independently of this store.
- **`KcapCli`** gains `PluginInstallAsync(vendor)` (bounded timeout), the §7 streaming import
  call, and the decision-4 `KCAP_CONSENT_SEED_DEFAULT=prompt` env overlay on its
  install/replace calls (the verbs that WRITE the unit — a start consumes whatever the unit
  already bakes, which is exactly what step 7's pre-start gates check). The wizard constructs
  its own instance after Sign-in, pinned to the wizard-chosen profile — in wizard-first mode no
  startup instance exists to conflict with.
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
  when a claim matching the *current* resolved profile/daemon is pending and attach reaches
  `Connected`, it runs capability check → get → factory guard → conditional put; on success, or
  on finding non-factory policy, it clears the claim (its key only). A claim whose identity no
  longer matches stays pending and inert. **Scope note:** the coordinator exists for
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
- **Enable daemon:** skipped-login users see "requires sign-in". §3 step-7 matrix + §6a lane
  semantics for navigation; install failures surface the AI-1654 verify exit codes (20–27) with
  the same wording the lifecycle controller uses. A daemon that cannot commit its seeded policy
  (decision 4) logs a coded reason and exits 0 — the unit comes to rest and the step (or the
  normal attach UX) shows a down daemon, never a live one on an uncommitted policy; no new
  `VerifyExit` code exists because the CLI transaction writes no policy file.
  `identity_mismatch` ack or missing `consent/3` → no write, claim pending, repair guidance.
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
  quarantined + seeded; unreadable → quarantined + seeded; seed-write failure → coded log +
  exit 0, unit at rest, no respin (KeepAlive) and no live daemon. `default_source` stamping:
  IPC put → `operator`, boot seed → `seed`, operator `allow` surviving restarts. No directive
  (headless unit / manual start) → every arm behaves exactly as today. Directive baked into the
  unit is covered by the existing TxnMarker fingerprint tests (rolls back with the unit).
- **Claim store & protocol:** arm flushed before boundary (fault injection at write/flush/rename,
  then proceed through a successful token commit + quit + relaunch → coordinator still holds the
  claim); hook failure blocks the commit; **multi-tenant GitHub discovery arms one claim per
  published identity**; keyed merge under two concurrent writers (two identities → two keys, no
  clobber); `AuthProvider.None` profile-only commit still arms; Defaults rebinding re-keys;
  terminal `kcap use` to another (claimed) identity → that identity's claim applies on its own
  attach; **quarantine lifecycle**: corruption before Sign-in (arming lands in the fresh store,
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
- **Step-7 matrix:** every §3 row — install / start-existing passing both pre-start gates /
  **stopped unit with a pre-consent binary** (no directive or version below the floor: no start,
  takeover offered — fixture: a stopped legacy daemon whose server sends work immediately; it
  must never be started into an `allow`-capable registration) / **stopped unit with mismatched
  `unit_profile`** (takeover, never a bare start) / owning+identity (skip + claim apply) /
  owning wrong-server (takeover offered, NOT "enabled") / manual owner no unit, identity match /
  manual owner + non-owning unit / **manual owner wrong-server** (no mutation, no claim
  application, takeover the only offer) / orphan label / stale marker (repair) / `txn_active`
  (wait) / `unknown` (no mutation) — against real `service status --json` fixtures, incl. the
  new `unit_profile`/`unit_consent_seed` fields.
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
  (§6) — new frame type, structurally rejected by old daemons, no protocol bump; **the decision-4
  boot seed** — directive-gated policy classification before `ServerConnection`, the
  `default_source` provenance stamp in `consent.json` (daemon-internal, never on the wire), and
  the coded exit-0 refusal when the seed cannot be committed. Enumerated CLI changes:
  `ServiceEnvironment`'s baked-env allowlist gains `KCAP_CONSENT_SEED_DEFAULT` (deliberate unit
  content — decision 4); `service status --json` gains additive `unit_profile` and
  `unit_consent_seed` fields parsed from the unit's baked env (§3 step 7's pre-start gates);
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
