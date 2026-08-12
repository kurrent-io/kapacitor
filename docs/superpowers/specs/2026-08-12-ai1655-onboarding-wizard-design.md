# AI-1655 — First-run onboarding wizard (desktop supervisor slice 3)

**Date:** 2026-08-12 (revised after spec-review round 1, Codex reviewer)
**Status:** Approved design.
**Issue:** AI-1655. Umbrella spec: [2026-07-31-desktop-supervisor-app-design.md](2026-07-31-desktop-supervisor-app-design.md) §6–7.
**Prior slices:** control IPC + consent (AI-1623/AI-1648), app shell (AI-1650), tray/agents/stop (AI-1651), consent prompts + Activity (AI-1652), daemon lifecycle + PATH shim (AI-1654). AI-1653 (bundling/signing) has NOT landed — the wizard keeps the `KCAP_APP_CLI_PATH` dev seam and works from source.

## 1. Problem

A fresh desktop machine has no profile, no token, no hooks, no daemon — and the app today shows a
main window that can only say "daemon unreachable". AI-1654's lifecycle controller deliberately
does nothing on fresh machines (its §4.2: "The wizard (AI-1655) owns fresh machines"). Umbrella §7
assigns first-run onboarding to a wizard: PATH shim, connect, login, harness hook setup, historical
import, daemon enablement — and the consent-default flip from the upgrade-safe `allow` to `prompt`,
which is what actually answers the silent-launch complaint on desktop machines. The 2026-08-10
issue amendment folds **create a workspace** into scope: the branch umbrella decision 6 reserved,
riding the existing WorkOS self-service provisioning backend (AI-914/915/916) through the CLI's
existing create-tenant flow (AI-1110) — no new server-side work.

## 2. Decisions

| # | Decision |
|---|----------|
| 1 | **Trigger: the wizard opens only when setup is incomplete.** The gate is a local, side-effect-free eligibility check (`OnboardingGate`, §4) returning an explicit reason: no resolvable profile, no canonical `http(s)` server URL, or no usable token. "Usable" is **provider-aware, matching `TokenStore`'s real refresh rules**: the token file must exist for the resolved profile, be stamped for the profile's canonical server, and be unexpired OR refresh-capable — where GitHubApp is *always* refresh-capable (server `/auth/refresh`; its `RefreshToken` is normally null) and WorkOS requires both `RefreshToken` and `ClientId`. A profile whose persisted provider is `none` (§4 gate) needs no token at all. No server round-trip, no refresh side effects. Configured machines never see the wizard. Rejected: always-on-first-run (annoys existing npm-CLI users for one consent-flip offer); a persisted "wizard done" claim (derived state re-opens the wizard until setup is actually complete, and stops the moment it is). |
| 2 | **Wizard-first startup: when the gate fires, `App.StartAsync` builds NO daemon graph** — no `DaemonClientService`, no tray, no `DaemonLifecycleController`, no `ShimOfferCoordinator`. The wizard is the only window. Forced, not just simpler: everything in the graph pins identity at construction (`KcapCli` pins the profile, `DaemonClientService` pins the daemon name) and on a gate-failing machine the wizard is what *decides* those identities. On wizard close — finished or abandoned — startup re-resolves the profile fresh and builds the normal graph, **with one carve-out: if the gate still fails at close, the lifecycle controller starts with its startup auto-action arm permanently closed and the post-close auto shim offer suppressed** (user-clicked actions and the tray shim item keep working). Without the carve-out, a URL-valid/token-less machine whose user closes the wizard before sign-in would flow straight into AI-1654's silent auto-install — §4.1 preconditions require a profile URL but no token — minting an `allow` daemon the moment a later terminal login makes it operational, with no pending flip recorded. Gate and graph must also agree on what "valid URL" means: both use the same `ServerIdentity` canonicalization (today `App.ValidProfileName` accepts any absolute URI, e.g. `file://`, that the gate would reject). |
| 3 | **Hybrid drive: auth/discovery/provisioning run IN-PROC via a GUI-neutral Core façade; steps with tested non-interactive CLI surfaces are shelled.** This amends umbrella §7's "every step shells the bundled CLI" the way AI-1650 amended the MVVM choice — the rationale (reuse existing tested flows, never reimplement) is preserved: `OAuthLoginFlow`/`WorkOSDiscovery`/`WorkOSTokenSource`/`TokenStore` ARE the tested flow, in Core. Shelling login is impossible for exactly the cases that matter: multi-tenant discovery and create-workspace are Spectre prompts that hard-fail on redirected stdin, and the tenant pick happens mid-auth-session (a shelled two-phase `login --list`/`login --tenant` forces re-auth between list and pick). The Core seams as they exist today are NOT app-consumable — sync `ITenantPicker.Pick`, no cancellation anywhere, `Console` writes inside `WorkOSDiscovery`/`OAuthLoginFlow`, GitHub discovery orchestration living in `SetupCommand` with internal helpers — so this slice reshapes them into an explicit onboarding façade (§5) that the CLI's `setup`/`login` re-plumb onto, behavior-preserving. Rejected: growing a non-interactive CLI surface (`login --tenant`, `tenant create`, `detect --json` — large public surface in an app slice, and the mid-session pick problem stands); PTY-driving the interactive commands (screen-scraping Spectre redraws has no contract). |
| 4 | **Shelled steps:** `kcap plugin install --<vendor>` per selected harness (prompt-free, idempotent), `kcap import <scope> --yes` with vendor flags (streamed, §7), and the AI-1654 `service install --verify` transaction verbatim for daemon enablement. The consent flip goes over the app's existing `ConsentIpc` (get → mutate default → put), not a CLI child — the CLI's `consent set-default` does the identical read-modify-write and the app already speaks the protocol natively. |
| 5 | **The wizard includes visibility and daemon name** (setup's step 3/6 and 5/6, which umbrella §7's list omitted): a Defaults step with the visibility picker (default `org_public`) and daemon-name field (default: lowercased username). |
| 6 | **Import step: vendor checkboxes + scope choice** (Everything / one org / specific repo — mirroring `ImportScopePrompt`'s vocabulary), running `kcap import --all|--org <o>|--repo <r> --yes` plus selected vendor flags. Setup's own embedded import is current-repo-scoped, which is meaningless for a GUI launch. |
| 7 | **`consent_flip_pending` is a scoped claim armed BEFORE the sign-in commit.** The claim is a record — `{profile, canonical server URL, daemon name}` — not a boolean, persisted to app-state *before* the façade's durable commit (profile + org-scoped token via `TokenStore.SaveAsync`). "Sign-in completion" means that commit, NOT `SetupFunnel.SigninCompleted` (which fires after org-less WorkOS auth, before tenant pick/org switch/token save). Arm-before-commit is the crash-consistency protocol: a crash between claim and commit leaves a claim without a token — the gate still fails, the wizard returns, no harm; the dangerous inverse (token without claim) cannot happen. This keeps `app-state.json` UX-grade (no fsync): a lost/corrupt claim degrades to today's behavior, never to a wrong flip, because application is guarded (§6). The Defaults step rebinding the daemon name updates the claim's daemon name. Claim-write failure → in-memory claim for the run (step 7 still applies it), logged; documented residual. Step 7 applies the flip and clears the claim; an abandoned wizard applies it via `ConsentFlipCoordinator` on a later attach (§6). Abandoning before sign-in arms nothing (covered instead by decision 2's carve-out). |
| 8 | **Harness detection moves to `Capacitor.Cli.Core` as a parameterized probe** (`AgentDetection`, §8: PATH string, PATHEXT, platform rules, home dir as inputs; BCL-only, AOT-clean). Today it is split across `AgentDetector` (PATH walk) and `SetupCommand` (home-dir checks) in the CLI project — unreachable from the app, and a GUI process's `PATH` is not the user's terminal PATH anyway. `SetupCommand` consumes the moved code (behavior parity asserted by tests); the wizard feeds it `LoginShellProbe.TerminalPathAsync`. No new CLI verb. |
| 9 | **The app emits NO telemetry this slice; desktop-onboarding funnel coverage is explicitly deferred** to a follow-up issue. The app never calls `CliTelemetry.Initialize`, so Core's embedded `SetupFunnel` emissions no-op (`Capture`/`CaptureNow` are guarded on `Enabled`/`_client`). Reversed from the pre-review draft ("reuse `SetupFunnel` from the adapters"), which was unsafe on three verified counts: `Initialize` hardcodes `source: "cli"` and prints the one-time privacy disclosure to `Console.Error` — invisible in a WinExe, so an app-first user would silently consume `notice_shown` and never see the disclosure the telemetry spec requires; `CaptureNow` is deliberately sync-over-async, safe only "because this is a console app with no SynchronizationContext" (its own comment) and can deadlock on Avalonia's UI context; and app-emitted fragments (`Workspace*` without `cli_setup_started`/`succeeded`) would corrupt the funnel while mislabeled as CLI traffic. The follow-up owns: an app source label, a visible disclosure surface, async delivery, and the desktop funnel sequence. |
| 10 | **Wizard config mutations use a cross-process read-modify-write discipline**: acquire `ConfigFileLock`, re-read under the lock, mutate only the intended fields, write via unique temp + rename (the `TelemetryState` pattern — `AppConfig.SaveProfileConfig`'s fixed `config.json.tmp` and lock-free load-modify-save is not safe against a concurrent terminal `kcap config set`/`profile add`/`use`). Accepted residuals, documented: the app has no single-instance guard (pre-existing app-wide gap, not created or widened by this slice — the wizard only makes the same writes the CLI makes); concurrent `plugin install` children rely on each installer's own file discipline. |

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
   can be listed, and WorkOS creation runs inside discovery after org-less sign-in, so a step
   split along "pick tenant, then sign in" would misstate the real flow.
3. **Sign in** — executes ONE façade operation (§5) for the chosen intent, rendering its
   structured progress inline: browser-open notice with the fallback URL, GitHub device code +
   verification URL when that flow applies, the tenant picker list when discovery finds several,
   and the create-workspace sub-flow (org name → slug with live availability → confirm →
   provisioning progress mirroring the CLI's 4 s × 150 poll contract, `WorkOSTokenSource` keeping
   the org-less token alive per AI-1171, its `CurrentRefreshToken` used for the final org switch).
   The durable commit (profile + token) lands via the same Core writes the CLI uses, with the
   decision-7 claim armed immediately before. Pasted-URL joins to an `AuthProvider.None` server
   auto-satisfy this step (no token needed; the gate's provider rule keeps it satisfied).
   **Step transitions:** while the operation is in flight, Back/Skip/close cancel it through the
   §5 cancellation lane (a cancelled attempt performs no durable write); after the commit, Back
   returns to Connect for a *different* intent, and re-entry shows the step satisfied (profile +
   usable token present). Retry re-runs the operation only after the previous lane has quiesced.
4. **Defaults** — visibility picker + daemon-name field (decision 5), written per decision 10 to
   the active profile; a daemon-name change updates the decision-7 claim.
5. **Coding agents** — `AgentDetection` results (§8) as pre-checked checkboxes →
   `kcap plugin install --<vendor>` per selection (Claude Code is the flagless default install).
6. **Import history** — vendor checkboxes + scope choice (decision 6); output streams into a live
   log pane per the §7 contract; Cancel kills the child tree. A failed import never blocks
   finishing onboarding.
7. **Enable daemon** — `service install --verify` through `KcapCli.ServiceInstallVerifiedAsync`
   (pinned profile + `KCAP_PROFILE` + terminal-PATH overlay, the AI-1654 contract; the per-label
   flock protects against concurrent terminal CLIs). Then the consent flip, gated by the §6
   identity probe. When a daemon is already running under the target name, the probe decides:
   identity match → skip install, flip only; mismatch → no flip, claim stays pending, repair
   guidance surfaced (same-name manual daemon on a different server must not receive the flip —
   `ConsentAck` alone only proves *some* daemon answered).
8. **Done** — summary of what was set up and what was skipped (and why).

Abandonment: config writes are incremental, so a re-entered wizard finds steps pre-satisfied.
Accepted residual, documented: a user who signs in and quits never re-enters (the gate now
passes), so hooks/import stay undone until `kcap setup`/`kcap import` or the AI-1656 settings
surface — consent is covered by the pending claim, and pre-sign-in abandonment by the decision-2
carve-out.

## 4. Components (app)

```
src/Capacitor.App/Views/Onboarding/OnboardingWindow.axaml(.cs)
src/Capacitor.App/ViewModels/Onboarding/OnboardingViewModel.cs      — step state machine
src/Capacitor.App/ViewModels/Onboarding/<Step>StepViewModel.cs      — one per step
src/Capacitor.App/Services/Onboarding/OnboardingGate.cs             — decision 1 trigger
src/Capacitor.App/Services/Onboarding/WizardAuthService.cs          — façade driver + cancellation lane
src/Capacitor.App/Services/Onboarding/ConsentFlipCoordinator.cs     — pending-claim application (§6)
```

- **`OnboardingGate`** answers the decision-1 predicate once at startup, returning
  `(Complete | Incomplete(reason))` with reasons: `no_profile`, `invalid_server_url`,
  `no_token`, `token_unusable(binding|expired_unrefreshable)`. Provider-aware refresh capability
  per decision 1. **Provider persistence:** the profile gains an optional `auth_provider` field
  (additive config schema), stamped by setup/login/the wizard whenever `/auth/config` is read;
  `none` exempts the profile from the token requirement. Legacy profiles without the field are
  treated as token-required — documented residual: a pre-existing `AuthProvider.None` server
  profile sees the wizard until the field is stamped (one `kcap config set` or one wizard pass).
  Unreadable/corrupt token files → `token_unusable` (the wizard is the recovery path). The gate
  and `App.ValidProfileName` share one `ServerIdentity`-based validator (decision 2).
- **`WizardAuthService`** drives the §5 façade: single-flight — one auth operation at a time, a
  new attempt only after the previous lane has quiesced (cancelled or completed); cancellation is
  a distinct outcome, never rendered as failure.
- **`KcapCli`** gains `PluginInstallAsync(vendor)` (bounded timeout) and the §7 streaming import
  call. The wizard constructs its own instance after Sign-in, pinned to the wizard-chosen
  profile — in wizard-first mode no startup instance exists to conflict with.
- **App state** (`app-state.json`) gains the decision-7 claim record. The shim claim reuses the
  existing `ShimOffered`/`ShimDenied` fields. The store stays UX-grade by the arm-before-commit
  protocol (decision 7).

## 5. Core onboarding façade

The load-bearing Core work of this slice. A GUI-neutral module in `Capacitor.Cli.Core/Auth/`
(BCL-only, AOT-clean, source-generated serialization; no Avalonia/Rx/Spectre types) exposing the
existing flows through app-consumable seams. The CLI's `setup`/`login` re-plumb onto the same
façade through thin Spectre adapters — one tested implementation, two front-ends,
behavior-preserving for the CLI (asserted by its existing command tests).

- **Operations** (each cancellable end to end): *login to known server* (wraps
  `OAuthLoginFlow.LoginWithDiscoveryAsync`), *discover-and-join* (WorkOS and GitHub — the GitHub
  discovery orchestration moves out of `SetupCommand.RunDiscoveryAsync` into the façade, making
  `AcquireGitHubTokenAsync` reachable), *create workspace* (the `ITenantProvisioner` path).
- **Structured progress, not console output**: an injected progress/notice sink replaces the
  `Console.Out/Error` writes on these paths (browser-open notice + fallback URL, device code +
  verification URL, poll ticks, error text). The CLI adapter prints them exactly as today; the
  app renders them in the Sign-in step.
- **Cancellation propagated everywhere**: `WorkOSDiscovery.RunWithLiveAuthAsync`/`RunAsync`,
  `IAuthProxyClient`, the browser wait, the GitHub device poll (today an unbounded loop),
  provisioning polls/refreshes, and the final org switch all gain `CancellationToken`
  parameters; `RunAsync` passes the token it already receives into
  `ITenantProvisioner.OfferCreateAsync` (today dropped). A cancelled operation performs no
  durable write (commit points check the token) and surfaces as `Cancelled`, distinct from
  failure. Single-use refresh-token rotation is why the single-flight lane (§4) is mandatory:
  two overlapping WorkOS attempts can invalidate each other's sessions.
- **Async picker**: `ITenantPicker.Pick` (sync) gains an async, cancellable counterpart the
  façade consumes; the Spectre picker adapts trivially, the app's picker awaits the ViewModel.
- **Helper moves**: `SetupCommand.ToServerOrigin` and `ResolveTenantArg` become public Core
  helpers; `AppConfig.ValidVisibilities` becomes public. §10's scope statement enumerates the
  CLI-project churn this causes.
- **Commit protocol**: profile creation/activation (`TenantDiscovery.MergeProfiles` for
  discovery; explicit profile write for pasted URLs) and `TokenStore.SaveAsync` remain the
  single durable commit, reached only on a non-cancelled operation, with the app's decision-7
  claim armed immediately before the façade is asked to commit.

## 6. Consent flip

- **Step-7 flip** (explicit user action on the Enable-daemon step): identity probe, then
  get → mutate default to `prompt` → put over `ConsentIpc`. No factory guard — the user is
  looking at the step that says what it does.
- **Identity probe** (step 7 and coordinator alike): a bounded one-shot hello + status snapshot
  against the target socket must match the claim's daemon name AND canonical server URL
  (`DaemonStatusDto.ServerUrl`) before any put. Mismatch → no write, claim pending, surfaced
  with repair guidance.
- **`ConsentFlipCoordinator`** (normal post-wizard startup, sibling of `ShimOfferCoordinator`):
  when a claim matching the *current* resolved profile/daemon is pending and attach reaches
  `Connected`, it runs probe → factory guard → put; on success, or on finding non-factory
  policy, it clears the claim. A claim whose identity no longer matches the resolved
  profile/daemon stays pending and inert (never applied to a different daemon than it was armed
  for).
- **Factory guard** (coordinator path only): apply only when the live policy is default `allow`
  with zero rules. Two deliberate weakenings, accepted and documented rather than mechanized:
  (a) *provenance*: an explicit operator-chosen `allow` + zero rules is indistinguishable from
  factory state over the wire — the flip errs toward the safer `prompt` and the operator can
  revert (`kcap daemon consent set-default allow`); distinguishing would need a daemon-side
  provenance field and conditional-put semantics, out of proportion for this window. (b) *race*:
  get→put has no CAS, identical to the CLI's own `consent set-default`; a concurrent consent
  edit landing in the window can be overwritten. The window is one IPC round-trip on a
  just-onboarded machine; accepted.

## 7. Import streaming contract

`IProcessRunner` gains a streaming variant: channel-tagged lines (`stdout` | `stderr`, each
stream line-buffered independently — no cross-stream ordering promise), callbacks invoked on the
pump (never the UI) thread and marshaled by the ViewModel to the UI scheduler, callback
exceptions logged and swallowed (never kill the pump), a bounded in-memory tail (last 500 lines;
older lines drop, the pane says so), and a final `ProcessResult` returned in addition to the
stream. Cancel/close = `KillTree` + await exit — no orphaned children. The CLI exits 0 even with
per-session errors and reports them only in human-formatted output, so the wizard does **not**
parse for them: the step *unconditionally* shows "if anything failed, run `kcap import` in a
terminal to retry" alongside the CLI's own final summary lines (revised from the pre-review
draft's "when the log contains errors", which contradicted "no fragile parsing").

## 8. AgentDetection contract (Core move)

Inputs: PATH string, PATHEXT string (Windows extension policy — today's `AgentDetector` reads
both), an executable-rule mode (Unix execute-bit vs Windows PATHEXT match, selected by the
caller), and the home directory. Output: per-vendor `{binary_found, home_signal_found}`.
Vendor signals enumerated exactly as `SetupCommand` has them today: binaries `claude`, `codex`,
`cursor-agent`, `copilot`, `gemini`, `kiro`, `pi`, `opencode`, `agy` (Antigravity's CLI is `agy`,
not `antigravity`); home signals `~/.claude`, `~/.codex`, `~/.cursor`, `~/.copilot`, `~/.gemini`,
`~/.kiro`, `~/.pi`, `~/.config/opencode`, `~/.gemini/antigravity`. Edge behavior: unreadable
PATH entries/home dirs → not detected (no throw); symlinks followed; duplicate PATH entries
deduped. Parity tests assert the moved probe reproduces today's detection matrix;
`SetupCommand` consumes it unchanged. NativeAOT publish must stay warning-free with no new Core
dependency.

## 9. Error handling

Uniform rule: every failure is a message + Retry/Skip on its step; nothing wedges the wizard.

- **No CLI resolved** (pre-AI-1653, no npm install): steps 1/5/6/7 show "kcap CLI not found" and
  stay skippable; Connect/Sign-in/Defaults still work fully (in-proc). Done lists what was skipped.
- **Sign-in:** browser timeout (the CLI's 5 minutes) → Retry through the quiesced lane. WorkOS
  has no device fallback (loopback-bind failure → retry); the GitHub device flow renders in-step
  and is now bounded by cancellation (§5). Cancellation ≠ failure in every surface.
- **Create workspace:** availability-check and provisioning errors surface with retry;
  provisioning timeout → "still provisioning — finish later by joining `<slug>` from the Connect
  step" (the CLI's own message, GUI-shaped); `WorkspaceFailed` reasons verbatim.
- **Coding agents:** per-vendor exit codes; a failed vendor gets ⚠ + retry, successes stand.
- **Import:** §7. Cancel = `KillTree`; failure never blocks Next.
- **Enable daemon:** skipped-login users see "requires sign-in". Identity-probe mismatch → §6
  repair surface. Install failures surface the AI-1654 verify exit codes (20–27) with the same
  wording the lifecycle controller uses. Flip failure with the daemon up → retry; the claim
  persists until applied or cleared per §6.
- **Gate edge cases:** unreadable token file → wizard (it is the recovery path); config
  unreadable → wizard with `no_profile`.

## 10. Testing

TUnit throughout, existing disciplines (`[NotInParallel("AvaloniaSession")]`, real-socket harness
rules, `Path.Combine` in path assertions for the Windows CI leg).

- **`OnboardingGate` matrix:** no profile / invalid URL (incl. `file://` — shared-validator
  assertion) / no token file / WorkOS expired with+without `RefreshToken`+`ClientId` / GitHubApp
  expired (usable — server-refreshable) / wrong-server token / legacy unbound token / corrupt
  token file / provider `none` (no token required) / legacy profile without `auth_provider`.
- **Decision-2 carve-out:** gate-still-failing close → graph builds with auto-actions closed and
  shim auto-offer suppressed; assertions of **zero service mutation** for: valid URL + no token +
  abandon; invalid/non-HTTP URL + abandon; close during an in-flight auth operation.
- **Claim protocol:** arm-before-commit ordering (crash injected between claim write and token
  commit → wizard returns, no flip target armed without it); claim-write failure → in-memory
  path; Defaults rebinding updates the claim; claim identity mismatch (profile switched via
  terminal `kcap use` between arming and application) → coordinator leaves it pending.
- **Consent flip:** identity probe (same-name/same-server → applies; same-name/different-server
  → pending + surfaced) against the real-socket harness; factory-guard matrix (factory / explicit
  non-default / rules present); the two documented §6 weakenings asserted as behavior (explicit
  allow+zero flips; concurrent mutation between get and put is overwritten) so the residuals stay
  deliberate.
- **Façade:** cancellation at every await point — picker, availability check, each provisioning
  poll/refresh, final org switch, browser wait, device poll — each asserting *no durable write*;
  Retry-after-cancel only after quiesce; single-flight (second attempt while one is in flight is
  rejected); CLI behavior parity for `setup`/`login` through the Spectre adapters (existing
  command tests keep passing unmodified where they exist).
- **Step ViewModels** via `Avalonia.Headless`: the §3 transition table — pasted URL / GitHub
  discovery / WorkOS discovery / zero-tenant create / "I already have a workspace" retarget /
  `AuthProvider.None` auto-satisfy — plus Back/Skip before and after the commit, and re-entry
  recognizing satisfied states.
- **`AgentDetection`:** §8 parity matrix, PATHEXT/Windows rules, unreadable-dir behavior.
- **Streaming runner** against a real child process: interleaved stdout/stderr tagging, bounded
  tail, callback exception swallowed, mid-stream `KillTree` cancel with no orphaned child, final
  result delivered.
- **Config writes:** lock + re-read-under-lock preserves unrelated fields against a concurrent
  writer; unique temp names (two writers, no shared-tmp collision).
- **App startup:** gate-fires → wizard-first (asserting no service/tray/lifecycle/shim
  coordinator built); wizard-close → normal graph with freshly resolved profile (extends
  `AppStartupTests` patterns).
- **AOT:** `dotnet publish` warning-free (Core façade + AgentDetection are BCL-only).
- **E2E stays manual** (umbrella §10): fresh machine full pass; abandon-after-sign-in → flip
  lands on next attach; abandon-before-sign-in → no auto-install; multi-tenant pick; real
  create-workspace against staging (`KCAP_SIGNUP_URL`); import cancel mid-run; shim step on a
  `.zshrc`-only-PATH machine.

## 11. Scope boundaries

- **AI-1653 keeps:** bundling, signing/notarization, auto-update, the bundle-relative
  `CliResolver` arm. The wizard runs from source via `KCAP_APP_CLI_PATH`.
- **AI-1656 keeps:** the settings surfaces that edit everything the wizard set.
- **Follow-up issue (new):** desktop app telemetry — source label, disclosure surface, async
  delivery, desktop onboarding funnel (decision 9).
- **Zero server-side work** (signup rides the existing provisioning backend) and **zero new CLI
  verbs or flags**. CLI-project changes, enumerated: `AgentDetection` move (decision 8);
  `setup`/`login` re-plumbed onto the §5 façade with Spectre adapters (behavior-preserving);
  `ToServerOrigin`/`ResolveTenantArg` moved to Core. Core gains the façade seams (§5), the
  `auth_provider` profile field (§4), and public `ValidVisibilities`. User-visible CLI behavior
  is unchanged, so no README/help churn.
- One PR (references AI-1655 and its GitHub issue).
