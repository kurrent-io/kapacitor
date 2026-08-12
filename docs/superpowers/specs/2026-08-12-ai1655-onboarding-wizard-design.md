# AI-1655 — First-run onboarding wizard (desktop supervisor slice 3)

**Date:** 2026-08-12
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
| 1 | **Trigger: the wizard opens only when setup is incomplete** — the resolved profile has no valid absolute `http(s)` server URL, OR no usable token (no token file, or expired with no refresh token). Checks are in-proc (`AppConfig.ResolveActiveProfile` + a `TokenStore` read), no server round-trip, no refresh side effects. Configured machines never see the wizard. Rejected: always-on-first-run (annoys existing npm-CLI users for one consent-flip offer); a persisted "wizard done" claim (derived state re-opens the wizard until setup is actually complete, and stops the moment it is). |
| 2 | **Wizard-first startup: when the gate fires, `App.StartAsync` builds NO daemon graph** — no `DaemonClientService`, no tray, no `DaemonLifecycleController`, no `ShimOfferCoordinator`. The wizard is the only window. Forced, not just simpler: everything in the graph pins identity at construction (`KcapCli` pins the profile, `DaemonClientService` pins the daemon name) and on a gate-failing machine the wizard is what *decides* those identities. On wizard close — finished or abandoned — startup re-resolves the profile fresh and builds the normal graph exactly as today. Also kills by construction the AI-1654 shim-offer dialog racing the wizard. |
| 3 | **Hybrid drive: auth/discovery/provisioning run IN-PROC via the same Core code the CLI uses; steps with tested non-interactive CLI surfaces are shelled.** This amends umbrella §7's "every step shells the bundled CLI" the way AI-1650 amended the MVVM choice — the rationale (reuse existing tested flows, never reimplement) is preserved: `OAuthLoginFlow`/`WorkOSDiscovery`/`WorkOSTokenSource`/`TokenStore` ARE the tested flow, in Core, behind injectable `ITenantPicker`/`ITenantProvisioner` seams; the Spectre prompts were always a thin CLI driver. Shelling login is impossible for exactly the cases that matter: multi-tenant discovery and create-workspace are Spectre prompts that hard-fail on redirected stdin, and the tenant pick happens mid-auth-session (a shelled two-phase `login --list`/`login --tenant` forces re-auth between list and pick). Rejected: growing a non-interactive CLI surface (`login --tenant`, `tenant create`, `detect --json` — large public surface in an app slice, and the mid-session pick problem stands); PTY-driving the interactive commands (screen-scraping Spectre redraws has no contract). |
| 4 | **Shelled steps:** `kcap plugin install --<vendor>` per selected harness (prompt-free, idempotent), `kcap import <scope> --yes` with vendor flags (streamed), and the AI-1654 `service install --verify` transaction verbatim for daemon enablement. The consent flip goes over the app's existing `ConsentIpc` (get → mutate default → put), not a CLI child — the CLI's `consent set-default` does the identical read-modify-write and the app already speaks the protocol natively. |
| 5 | **The wizard includes visibility and daemon name** (setup's step 3/6 and 5/6, which umbrella §7's list omitted): a Defaults step with the visibility picker (default `org_public`) and daemon-name field (default: lowercased username). |
| 6 | **Import step: vendor checkboxes + scope choice** (Everything / one org / specific repo — mirroring `ImportScopePrompt`'s vocabulary), running `kcap import --all|--org <o>|--repo <r> --yes` plus selected vendor flags. Setup's own embedded import is current-repo-scoped, which is meaningless for a GUI launch. |
| 7 | **`consent_flip_pending`: completing sign-in persists a claim; step 7 applies the flip and clears it; an abandoned wizard applies it on a later attach — but only while the daemon's policy is still factory state (default `allow`, zero rules).** Without this, sign-in-then-quit relaunches into a gate-passing app whose lifecycle controller silently auto-installs an `allow` daemon — the exact complaint this slice answers. The factory-state guard means an operator-configured daemon is never clobbered. Abandoning before sign-in sets nothing. |
| 8 | **Harness detection moves to `Capacitor.Cli.Core` as a parameterized probe** (`AgentDetection`: PATH string + home dir as arguments, BCL-only). Today it is split across `AgentDetector` (PATH walk) and `SetupCommand` (home-dir checks) in the CLI project — unreachable from the app, and a GUI process's `PATH` is not the user's terminal PATH anyway. `SetupCommand` consumes the moved code unchanged; the wizard feeds it `LoginShellProbe.TerminalPathAsync`. No new CLI verb. |
| 9 | **Telemetry: no new event names.** Core-emitted `SetupFunnel` events (`SigninCompleted`, `SigninFailed`, `TenantNone`) fire automatically in-proc; the app's provisioner adapter calls the same `Workspace*` methods `SpectreTenantProvisioner` does; `CliTelemetry` initializes with the same opt-out gates as the CLI. The funnel spec's double-count rule (no second producer of `cli_setup_*` completion names) is honored by NOT emitting setup-command-shaped events from the app. |

## 3. Wizard flow

Linear steps, Back/Next, every step individually skippable and retryable (umbrella §7). One
window (`OnboardingWindow`), step content switched by template.

1. **Command-line tool** — the PATH shim. Shown only when applicable: macOS + `CliResolver`
   resolved an absolute path + the login-shell probe positively found no `kcap` on the terminal
   PATH. Reuses `PathShimInstaller` (AppleScript sudo, non-forcing symlink, post-install re-probe);
   claims `ShimOffered` in app-state so the post-wizard `ShimOfferCoordinator` never re-offers.
2. **Connect** — join an existing workspace (paste slug/URL — `kcap setup`'s `ToServerOrigin`/
   slug-expansion rules — or "find my workspaces" via discovery) or **Create a workspace** (the
   formerly-reserved branch, now live; §5).
3. **Sign in** — in-proc browser login (WorkOS AuthKit PKCE via loopback; the CLI's 5-minute
   timeout). Inline tenant picker when discovery finds several; GitHub-provider servers use the
   device flow with the code + URL rendered in the step. Tokens land via the shared `TokenStore`
   (`~/.config/kcap/tokens/{profile}.json`), so CLI, daemon, and app agree. Completing this step
   persists `consent_flip_pending` (decision 7).
4. **Defaults** — visibility picker + daemon-name field (decision 5), written in-proc to the
   active profile.
5. **Coding agents** — `AgentDetection` results as pre-checked checkboxes →
   `kcap plugin install --<vendor>` per selection (Claude Code is the flagless default install).
6. **Import history** — vendor checkboxes + scope choice (decision 6); output lines stream into a
   live log pane; Cancel kills the child tree. A failed import never blocks finishing onboarding.
7. **Enable daemon** — `service install --verify` through `KcapCli.ServiceInstallVerifiedAsync`
   (pinned profile + `KCAP_PROFILE` + terminal-PATH overlay, the AI-1654 contract; the per-label
   flock protects against concurrent terminal CLIs). Then the consent flip over `ConsentIpc`,
   which doubles as the readiness proof: a `ConsentAck` is a live-daemon round trip.
8. **Done** — summary of what was set up and what was skipped (and why).

Abandonment: config writes are incremental, so a re-entered wizard finds steps pre-satisfied.
Accepted residual, documented: a user who signs in and quits never re-enters (the gate now
passes), so hooks/import stay undone until `kcap setup`/`kcap import` or the AI-1656 settings
surface — consent is covered by the pending-flip claim.

## 4. Components

```
src/Capacitor.App/Views/Onboarding/OnboardingWindow.axaml(.cs)
src/Capacitor.App/ViewModels/Onboarding/OnboardingViewModel.cs      — step state machine
src/Capacitor.App/ViewModels/Onboarding/<Step>StepViewModel.cs      — one per step
src/Capacitor.App/Services/Onboarding/OnboardingGate.cs             — decision 1 trigger
src/Capacitor.App/Services/Onboarding/WizardAuthService.cs          — in-proc auth façade
src/Capacitor.App/Services/Onboarding/AvaloniaTenantPicker.cs       — ITenantPicker
src/Capacitor.App/Services/Onboarding/AvaloniaTenantProvisioner.cs  — ITenantProvisioner
src/Capacitor.Cli.Core/Setup/AgentDetection.cs                      — moved probe (decision 8)
```

- **`OnboardingGate`** answers the decision-1 predicate once at startup; `App.StartAsync`
  branches on it before `DaemonClientService.CreateDefaultAsync` (decision 2).
- **`WizardAuthService`** wraps `WorkOSDiscovery.RunWithLiveAuthAsync(picker, provisioner)` and
  `OAuthLoginFlow.LoginWithDiscoveryAsync`. The picker/provisioner adapters bridge to step
  ViewModels via one `TaskCompletionSource` per question — the same seam shape as the app's
  existing dialogs. `WorkOSTokenSource` keeps the provisioning poll's org-less access token alive
  (AI-1171), and its `CurrentRefreshToken` is used for the final org switch (refresh tokens rotate
  single-use) — both handled by the same Core code that handles them for the CLI.
- **`KcapCli`** gains `PluginInstallAsync(vendor)` (bounded timeout) and a streaming import call.
  The wizard constructs its own instance after Connect/Sign-in, pinned to the wizard-chosen
  profile — in wizard-first mode no startup instance exists to conflict with.
- **`IProcessRunner`** gains a line-streaming variant (`RunStreamingAsync`, per-line callback,
  `KillTree` on cancel) for import.
- **Config writes** (profile for pasted-URL joins, `default_visibility`, `daemon.name`,
  `active_profile`) go through the same Core config machinery the CLI uses; discovery joins get
  profiles via `TenantDiscovery.MergeProfiles` for free.
- **App state** (`app-state.json`) gains one field: `consent_flip_pending` (decision 7). The shim
  claim reuses the existing `ShimOffered`/`ShimDenied` fields.
- **Pending-flip application:** a small `ConsentFlipCoordinator` (app service, sibling of
  `ShimOfferCoordinator`) — in normal post-wizard startup, when `consent_flip_pending` is set and
  attach reaches `Connected`, it runs the same get→guard→put sequence; on success or on finding
  non-factory policy it clears the claim.

## 5. Create a workspace

The provisioner adapter renders: organization-name field → slug field pre-filled with
`SlugValidator.Derive`, validated inline, with a live availability check (the same call
`PromptSlugAsync` makes) → confirm → provisioning progress mirroring the CLI's contract (4 s
poll, 150 max ≈ 10 minutes) with poll count shown. Timeout → "still provisioning — finish later
by joining `<slug>` from the Connect step" (the CLI's own message, GUI-shaped). `WorkspaceFailed`
reasons surface verbatim. Funnel events per decision 9.

## 6. Error handling

Uniform rule: every failure is a message + Retry/Skip on its step; nothing wedges the wizard.

- **No CLI resolved** (pre-AI-1653, no npm install): steps 1/5/6/7 show "kcap CLI not found" and
  stay skippable; Connect/Sign-in/Defaults still work fully (in-proc). Done lists what was skipped.
- **Sign-in:** browser timeout → Retry. WorkOS has no device fallback (loopback-bind failure →
  retry); GitHub device flow renders in-step.
- **Create workspace:** §5. Availability-check and provisioning errors surface with retry.
- **Coding agents:** per-vendor exit codes; a failed vendor gets ⚠ + retry, successes stand.
- **Import:** the CLI exits 0 even with per-session errors, so the step shows the final Done-grid
  lines verbatim plus a "run `kcap import` in a terminal to retry" pointer when the log contains
  errors — no fragile output parsing. Cancel = `KillTree`.
- **Enable daemon:** skipped-login users see "requires sign-in". Already-running daemon → skip
  install, flip only. Install failures surface the AI-1654 verify exit codes (20–27) with the same
  wording the lifecycle controller uses. Flip failure with the daemon up → retry; the claim
  persists either way until applied.
- **Factory-state guard** (decision 7): the pending flip applies only when the live policy is
  default `allow` with zero rules; anything else clears the claim without writing.

## 7. Testing

TUnit throughout, existing disciplines (`[NotInParallel("AvaloniaSession")]`, real-socket harness
rules, `Path.Combine` in path assertions for the Windows CI leg).

- **`OnboardingGate` matrix:** no profile / invalid URL / no token file / expired-with-refresh /
  expired-without / valid.
- **Step ViewModels** via `Avalonia.Headless` against fakes: scripted `IProcessRunner`, faked
  `WizardAuthService`, picker/provisioner adapters driven directly through their
  `TaskCompletionSource` seams (multi-tenant pick, create-workspace happy/timeout/failed paths).
- **`AgentDetection`** Core tests: PATH-as-parameter, home-dir probes, Windows path discipline.
- **Streaming runner** against a real child process, including mid-stream `KillTree` cancel.
- **Consent flip:** get→guard→put against the real-socket harness; factory-guard matrix (factory /
  non-default / rules present); pending-claim set-on-signin, apply-on-attach, clear-on-non-factory.
- **App startup:** gate-fires → wizard-first (asserting no service/tray/lifecycle/shim coordinator
  built); wizard-close → normal graph builds with freshly resolved profile (extends
  `AppStartupTests` patterns).
- **Funnel:** adapter unit tests assert the decision-9 emission points.
- **E2E stays manual** (umbrella §10): fresh machine full pass; abandon-after-sign-in → flip lands
  on next attach; multi-tenant pick; real create-workspace against staging (`KCAP_SIGNUP_URL`);
  import cancel mid-run; shim step on a `.zshrc`-only-PATH machine.

## 8. Scope boundaries

- **AI-1653 keeps:** bundling, signing/notarization, auto-update, the bundle-relative
  `CliResolver` arm. The wizard runs from source via `KCAP_APP_CLI_PATH`.
- **AI-1656 keeps:** the settings surfaces that edit everything the wizard set.
- **Zero server-side work** (signup rides the existing provisioning backend) and **zero new CLI
  flags or verbs** — the only CLI-project change is the decision-8 `AgentDetection` move, which is
  user-invisible, so no README/help churn.
- One PR (references AI-1655 and its GitHub issue).
