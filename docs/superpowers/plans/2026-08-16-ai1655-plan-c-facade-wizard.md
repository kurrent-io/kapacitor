# AI-1655 Plan C — Core Auth Façade, CLI Re-plumb, Onboarding Wizard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the remaining AI-1655 scope: the GUI-neutral Core onboarding façade (§5), the `setup`/`login` re-plumb onto it, the desktop onboarding wizard (§3/§4), the streaming import contract (§7), and wizard-first startup (decision 2) — completing the spec on top of the merged Plan A substrate (#556) and Plan B app lane/claims/gate (#562).

**Architecture:** Bottom-up in four layers. (1) Core: cancellation + async picker threaded through the existing auth flows, a structured progress sink replacing `Console` writes, helper moves, then the `OnboardingFacade` with its ordered commit boundary (before-commit claim hook → config/stamp → tokens, under `CancellationToken.None` once entered). (2) CLI: `kcap login` and `kcap setup` re-plumb onto the façade through thin Spectre adapters, behavior-preserving. (3) App services: streaming process runner, `KcapCli` plugin/import verbs, `WizardAuthService` single-flight driver, claims arming. (4) Wizard UI + wizard-first startup with the `OutcomeChannel.TransferConsumer` handoff.

**Tech Stack:** .NET 10 NativeAOT, Avalonia (headless-testable), TUnit, source-generated JSON. Core façade is BCL-only (no Avalonia/Rx/Spectre types).

**Spec:** `docs/superpowers/specs/2026-08-12-ai1655-onboarding-wizard-design.md` — where this plan and the spec disagree, the spec governs, EXCEPT the deviations pinned below.

## Standing deviations and carry-forwards (binding)

- **One-shot observation only.** Plan B deleted the live-graph observation adapter (spec §4's two-adapter slot machinery): every lane classification uses fresh one-shot probes with the instance-bound sandwich. Plan C does NOT resurrect the live adapter. Flagged in #562.
- **GitHub setup exchange convergence.** Today `kcap setup`'s GitHub discovery defers token exchange to Step 2 (active profile only) while `kcap login --discover` exchanges every tenant. The façade implements the spec's boundary ("config then one token per tenant", decision 7) — after the re-plumb, setup's GitHub path also publishes one token per discovered tenant and Step 2 reports login complete (as the WorkOS branch already does). User-visible setup output is unchanged in shape; tests that pin the deferred `PreAuthToken` dance are updated, not preserved.
- **Advisory accept-time revalidation (Plan B Task 10 R2-1): continued deferral.** A takeover Accept issues `Replace` through the lane, which performs its own fresh floor probe, and the CLI's in-transaction gates re-derive all evidence under the per-label lock — the staleness guard is structural. No consumer-side revalidation is added.
- **`resolveIdentityUnderConfigLock` switches `LoadPure` → `TryLoadPure`** (Plan B adjudication): now that `ConsentFlipClaims.Arm` gains production callers, an unreadable config inside `TryConsume`'s held locks must retain the claim, not throw (Task 10).
- **Provider stamp vocabulary:** the boundary writes the `AuthProvider` constant verbatim (`"GitHubApp"`, `"workos"`, `"None"`); `OnboardingGate` already compares the `None` stamp case-insensitively. Pinned by test (Task 4).
- **`AuthResult` has a fourth arm, `Retarget`.** Spec §5 names `Committed | Cancelled | Failed`; the WorkOS "I already have a workspace" answer (§3's retarget transition, §10's ViewModel row) completes the operation with nothing durable and a server input the caller loops on. Modeling it as `Failed` would be dishonest; it is pre-boundary by construction.
- **Create-workspace is offered only on zero tenants**, mirroring the CLI exactly (`WorkOSDiscovery` consults the provisioner only when discovery finds no tenants). A wizard user choosing "Create a workspace" who turns out to have tenants gets the picker — spec §10 tests only the zero-tenant create row.

## Prior work (do NOT re-implement)

Plan A (#556): daemon boot seed + carriers, `ConsentRulesPutV2`/`consent/3`, `pid`+`instance_id` DTOs, hello↔snapshot correlation, `LocalControlProbe`, gates 28/29/43, embedded digest pipeline, `ServiceEnvironment` allowlist, telemetry spawn marker, decision-10 `ConfigMutator` (+ every CLI writer migrated), **pure `AgentDetection` in Core** (`src/Capacitor.Cli.Core/Setup/AgentDetection.cs` — `Detect(AgentDetectionInputs)`, `FromEnvironment()`), `unit_*` status fields.
Plan B (#562): `DaemonMutationLane` + `OutcomeChannel` (leased FIFO, `TransferConsumer()`), `KcapCli` action-scoped executor + env overlays + `DetachedStartAsync`, `TimeoutKillScope.ProcessOnly`, `BootRefusalMarker` attribution, `ConsentFlipClaims` (+quarantine) / `ConsentFlipCoordinator` (+`AckQuarantineAsync`, surfacing), `OnboardingGate` + `auth_provider` stamp READ, startup carve-out (`autoActionsPermanentlyClosed`, shim auto-offer suppression), single-consumer presentation (`ConsumeMutationOutcomesAsync`/`PresentOutcomeAsync` in `App.axaml.cs`).

## Global Constraints

- Fail-closed doctrine: nothing classifies toward success on missing/inconsistent evidence; unknown reason tokens → attention, never destructive.
- The app emits NO telemetry (decision 9): never call `CliTelemetry.Initialize`; every app-spawned CLI child carries `KCAP_APP_SPAWN_NO_TELEMETRY=1` (already in `KcapCli.Env()`). Core `SetupFunnel` calls no-op in the app.
- Single-presentation rule: `RunAsync` results are waiter-state-only; ONE channel consumer owns actionable presentation; never two prompts from one outcome.
- Core façade: BCL-only, AOT-clean, source-generated serialization. After ANY Core change: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` AND the same for `src/Capacitor.Cli.Daemon` — both empty.
- CLI behavior-preserving: existing command tests keep passing except where a pinned deviation says otherwise. No new CLI verbs or flags; no README/help churn (spec §11).
- Floor literal `0.12.0-beta.1` (`KcapCliCompatibility.Floor`) — do not touch.
- Comments: max ONE short invariant line (≤200 chars); zero review/task provenance; no Linear IDs in code (GitHub issue numbers only).
- Tests: TUnit; `--treenode-filter "/*/*/ClassName/*"`; local runs `TMPDIR=/private/tmp`; `Path.Combine` in path assertions (Windows CI leg); Avalonia tests `[NotInParallel("AvaloniaSession")]`; real-socket tests `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]` + Windows guard; IDE0005 unused usings are build errors.
- Never read agent/daemon-owned files with write-denying opens (`FileShare.ReadWrite`).
- Commit per task; push via `git push https://github.com/kurrent-io/kcap-cli.git <branch>`. ONE PR referencing `Part of #553. AI-1655`.

---

### Task 1: Cancellation + async picker through the existing auth flows

**Files:**
- Modify: `src/Capacitor.Cli.Core/Auth/TenantDiscovery.cs` (`ITenantPicker`), `src/Capacitor.Cli.Core/Auth/AuthProxyClient.cs`, `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`, `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs`, `src/Capacitor.Cli/Commands/SpectreTenantPicker.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/WorkOSDiscoveryTests.cs`, `test/Capacitor.Cli.Tests.Unit/OAuthFlowTests.cs`, new `test/Capacitor.Cli.Tests.Unit/AuthCancellationTests.cs`

**Interfaces (produces):**
```csharp
public interface ITenantPicker {
    DiscoveredTenant? Pick(DiscoveredTenant[] tenants);                                   // unchanged
    Task<DiscoveredTenant?> PickAsync(DiscoveredTenant[] tenants, CancellationToken ct);  // NEW — the counterpart the façade consumes
}
public interface IAuthProxyClient {
    Task<ProxyConfigResponse?> GetConfigAsync(string proxyUrl, CancellationToken ct = default);
    Task<DiscoveryResult> DiscoverTenantsAsync(string proxyUrl, string githubAccessToken, CancellationToken ct = default);
    Task<DiscoveryResult> DiscoverWorkOSTenantsAsync(string proxyUrl, string workosAccessToken, CancellationToken ct = default);
}
```
`OAuthLoginFlow` members gain trailing `CancellationToken ct = default`, threaded into every `HttpClient` call, `Task.Delay`, and `browser.InvokeAsync`: `RunDeviceFlowAsync`, `RunGitHubBrowserFlowAsync`, `ExchangeAndSaveAsync` (all three overloads), `AcquireGitHubTokenAsync`, `AuthenticateWorkOSAsync`, `SwitchWorkOSOrgAsync`, `RefreshWorkOSTokenAsync`, `LoginWithDiscoveryAsync`. The device poll (`RunDeviceFlowAsync` L175-211, today `while (true)`) becomes cancellable: `await Task.Delay(interval, ct)` and `ct.ThrowIfCancellationRequested()` per iteration — cancellation surfaces as `OperationCanceledException`, never a mapped error string. `WorkOSDiscovery.RunWithLiveAuthAsync` and `RunAsync` gain `CancellationToken ct = default`; the two dropped tokens are fixed: L42's `orglessRefresh: async (refreshToken, _)` passes the real token through to `RefreshWorkOSTokenAsync`, and L109 passes `ct` into `provisioner.OfferCreateAsync(tokens, ct)`. `WorkOSDiscovery` consumes `picker.PickAsync(result.Tenants, ct)`; `TenantDiscovery.RunAsync` gains `ct` and consumes `PickAsync` too. `SpectreTenantPicker` implements `PickAsync` as `Task.FromResult<DiscoveredTenant?>(Pick(tenants))` (Spectre prompts are not cancellable; the CLI never cancels). Behavior-preserving: default `ct` everywhere, no call-site changes required outside tests.

- [x] **Step 1: Failing tests** (`AuthCancellationTests` + updates): cancelling during the device poll throws OCE (fake `HttpClient` handler pinning the poll on `authorization_pending`); `WorkOSDiscovery.RunAsync` passes its `ct` into `OfferCreateAsync` (NSubstitute provisioner asserting the received token is the one passed to `RunAsync`, not `default` — use a cancelled-after-start token identity check) and into the `orglessRefresh` delegate; `PickAsync` is what discovery awaits (substitute picker: `PickAsync` returns a tenant, `Pick` configured to throw — flow succeeds); existing `WorkOSDiscoveryTests`/`OAuthFlowTests` updated to stub `PickAsync` instead of `Pick` where discovery is driven.
- [x] **Step 2: Run tests — red.**
- [x] **Step 3: Implement** (mechanical threading; no logic changes).
- [x] **Step 4: Run the full CLI unit suite — green.** AOT publish greps for CLI + daemon — empty.
- [x] **Step 5: Commit** `feat(core): cancellation and async tenant picker through the auth flows`

### Task 2: `IAuthProgress` — structured progress sink replaces Console writes

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/AuthProgress.cs`
- Modify: `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs`, `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs`, `src/Capacitor.Cli.Core/Auth/LoopbackBrowser.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/AuthProgressTests.cs`

**Interfaces (produces):**
```csharp
public interface IAuthProgress {
    void Notice(string message);                       // today's stdout informational lines, verbatim
    void Error(string message);                        // today's stderr lines, verbatim
    void BrowserOpening(string url);                   // browser-open notice + fallback URL
    void DeviceCode(string code, string verificationUri);
    void PollTick();                                   // device-poll "." heartbeat
}
public sealed class ConsoleAuthProgress : IAuthProgress { … }   // prints EXACTLY today's strings
```
Every `Console.Out/Error` write on the login/discovery/exchange paths in `OAuthLoginFlow` and `WorkOSDiscovery` (the complete inventory: OAuthLoginFlow L37/43/106/112, device flow L138/158-173/192/199/207, browser flow L250-318, exchange L325-455, WorkOS L560-618, loopback fallback L495/497; WorkOSDiscovery L59/73/82-87/99/143/160-198) routes through an `IAuthProgress` parameter (trailing, `IAuthProgress? progress = null`, resolved to `ConsoleAuthProgress` at method top — behavior-preserving for untouched callers). The device-code banner maps to `DeviceCode(code, uri)` + `Notice` for the surrounding lines; the poll `Console.Write(".")` maps to `PollTick()`; browser-open sites (`LoopbackBrowser` L25-27 and the device flow's `Process.Start` notice) map to `BrowserOpening(url)` — `LoopbackBrowser` gains a `IAuthProgress? progress = null` ctor parameter. `ConsoleAuthProgress` reproduces the exact current strings (the mapping table lives in this one class; `DeviceCode` prints today's banner, `BrowserOpening` prints today's two lines, `PollTick` prints `"."` without newline). `SetupFunnel` calls are NOT touched (they no-op in the app per decision 9 and must keep firing for the CLI — WorkOS events stay embedded in `WorkOSDiscovery`).

- [x] **Step 1: Failing tests:** a recording `IAuthProgress` captures the device-flow sequence (`DeviceCode` once, `PollTick` per poll, `Notice` on completion) from `RunDeviceFlowAsync` under a scripted handler; `WorkOSDiscovery.RunAsync` zero-tenant path emits the exact `"No Capacitor tenants are linked to your account. Ask your admin to invite you."` through `Error`/`Notice` (pin which stream today's code uses) and writes NOTHING to a captured `Console`; `ConsoleAuthProgress.DeviceCode`/`BrowserOpening` byte-compare against today's literals.
- [x] **Step 2: red.**
- [x] **Step 3: Implement.** Sweep with `grep -n 'Console\.' src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs src/Capacitor.Cli.Core/Auth/LoopbackBrowser.cs` — zero hits on the swept paths when done (the file-level sweep is the acceptance check; `HttpClientExtensions.WriteUnreachableError` gets a string-returning overload the flow feeds to `Error`).
- [x] **Step 4: Full CLI unit suite green; AOT greps empty.**
- [x] **Step 5: Commit** `feat(core): structured auth progress sink with exact-string console adapter`

### Task 3: Helper moves — `ServerInput` to Core, `ValidVisibilities` public

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/ServerInput.cs`
- Modify: `src/Capacitor.Cli.Core/Config/AppConfig.cs`, `src/Capacitor.Cli/Commands/SetupCommand.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/ServerInputTests.cs`

**Interfaces (produces):**
```csharp
public static class ServerInput {
    public static string ToServerOrigin(string input);   // moved verbatim from SetupCommand.ToServerOrigin (L1084)
    public static string ResolveTenantArg(string arg);   // moved verbatim from SetupCommand.ResolveTenantArg (L1101)
}
```
`AppConfig.ValidVisibilities` becomes `public static readonly string[]` (the app reads it — `Capacitor.App` has no `InternalsVisibleTo`). `SetupCommand.ToServerOrigin`/`ResolveTenantArg` become one-line delegating shims (existing tests reference them) or call sites are updated and the shims deleted — prefer updating call sites and deleting; keep whichever the existing test surface makes cheaper, but the Core implementations are the single source.

- [x] **Step 1: Failing tests:** `ServerInputTests` ports the existing `ToServerOrigin`/`ResolveTenantArg` coverage from `SetupCommandTests` against the Core class (same cases, verbatim expectations); one test asserts `AppConfig.ValidVisibilities` is `["private", "project", "org_public", "public"]` (order pinned — Spectre prompt order depends on it).
- [x] **Step 2-4: red → move → green** (full CLI unit suite).
- [x] **Step 5: Commit** `refactor(core): move server-input helpers to Core; publish ValidVisibilities`

### Task 4: `OnboardingFacade` — operations + ordered commit boundary

**Files:**
- Create: `src/Capacitor.Cli.Core/Auth/OnboardingFacade.cs`, `src/Capacitor.Cli.Core/Auth/AuthResult.cs`
- Modify: `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs` (publication split), `src/Capacitor.Cli.Core/Auth/OAuthLoginFlow.cs` (exchange split), `src/Capacitor.Cli.Core/Config/ProfileConfig.cs` only if the stamp helper needs it (stamp record exists from Plan B)
- Test: `test/Capacitor.Cli.Tests.Unit/OnboardingFacadeTests.cs`, `test/Capacitor.Cli.Tests.Unit/CommitBoundaryTests.cs`

**Interfaces (produces):**
```csharp
public sealed record AuthIdentity(string Profile, string CanonicalServer);

public abstract record AuthResult {
    public sealed record Committed(string ActiveProfile, string CanonicalServer, string Provider,
                                   string? Username, IReadOnlyList<AuthIdentity> Published) : AuthResult;
    public sealed record Cancelled : AuthResult;                    // strictly pre-boundary, nothing durable
    public sealed record Failed(string Message) : AuthResult;       // message already rendered via progress
    public sealed record Retarget(string ServerInput) : AuthResult; // WorkOS "I already have a workspace" — pre-boundary
}

public sealed class OnboardingFacade(
        IAuthProgress progress,
        ITenantPicker picker,
        ITenantProvisioner? provisioner,
        Func<IReadOnlyList<AuthIdentity>, CancellationToken, Task>? beforeCommit,  // decision-7 hook; throw = abort, retryable
        Func<HttpClient>? httpFactory = null) {
    public Task<AuthResult> LoginAsync(string serverUrl, bool forceDevice, string? profile, CancellationToken ct);
    public Task<AuthResult> DiscoverAsync(string provider, bool forceDevice, CancellationToken ct);  // provider: AuthProvider.GitHubApp | AuthProvider.WorkOS
}
```
Supporting splits (Core, same task):
```csharp
// WorkOSDiscovery: RunAsync stops before persisting; the façade owns publication.
public abstract record WorkOSDiscoveryFlow {
    public sealed record Ready(DiscoveredTenant[] Tenants, DiscoveredTenant Picked,
                               WorkOSAuthResponse SwitchedAuth, string ClientId) : WorkOSDiscoveryFlow;
    public sealed record Retarget(string ServerInput) : WorkOSDiscoveryFlow;
    public sealed record NoTenants : WorkOSDiscoveryFlow;
    public sealed record Failed : WorkOSDiscoveryFlow;               // message already emitted via progress
}
public static Task<WorkOSDiscoveryFlow> DiscoverAsync(  /* on WorkOSDiscovery; same deps as RunAsync + progress */ …);
// OAuthLoginFlow: exchange split at the TokenStore.SaveAsync seam of ExchangeAndSaveAsync(HttpClient,…) L378.
public static Task<(StoredTokens Tokens, string? Username)?> ExchangeAsync(
    HttpClient http, string serverUrl, string githubAccessToken, string provider, IAuthProgress progress, CancellationToken ct);
```
**Contract.** *`LoginAsync` (login to known server):* GET `/auth/config` (pre-boundary) → dispatch: `None` → boundary with no token (identity = resolved/target profile + canonical server; publications: profile `ServerUrl` write IF the profile doesn't already point there, provider stamp); `GitHubApp` → `AcquireGitHubTokenAsync` (browser/device per today's `ChooseGitHubFlow`) → `ExchangeAsync` (network, pre-boundary) → boundary (token + stamp); `workos` → `AuthenticateWorkOSAsync` for the known server's client id/org → boundary. Unknown provider → `Failed` with today's message. *`DiscoverAsync`:* proxy config → provider branch: GitHub → `AcquireGitHubTokenAsync` → `TenantDiscovery.RunAsync` (picker) → boundary over ALL discovered tenants (`MergeProfiles` then one `ExchangeAsync`+save per tenant — exchange calls are pre-save network but run inside the boundary sequence under `CancellationToken.None`, matching "config then one token per tenant"); WorkOS → `WorkOSDiscovery.DiscoverAsync` → `Ready` → boundary (MergeProfiles + `TokenStore.SaveAsync(picked.ProfileName, …)` from the switched auth — the `StoredTokens` construction moves verbatim from today's `SwitchAndSaveAsync` L184-196), `Retarget` → `AuthResult.Retarget`, `NoTenants`/`Failed` → `Failed`. *The boundary, ordered, both operations:* (1) `beforeCommit(identities, ct)` — the LAST cancellable await; failure or OCE → nothing durable, `Cancelled`/`Failed`; (2) enter: all remaining publications run under `CancellationToken.None` — config (`ConfigMutator.MutateAsync`: MergeProfiles/profile write AND the `auth_provider` stamp for every published identity in the SAME mutation), then token saves in tenant order; a cancel observed after entry still returns `Committed`; (3) crash residue is safe by ordering (claim-without-profile, profile-without-token → gate still fails). *Stamp write:* `profile.AuthProvider = new AuthProviderStamp(provider, canonicalServer)` with the `AuthProvider` constant verbatim; canonical server via `ServerIdentity.Canonicalize`. *Progress:* all rendering through `IAuthProgress`; the façade emits today's success line (`Logged in as …`) via `Notice`.

- [x] **Step 1: Failing tests** (scripted `HttpClient` handlers, NSubstitute picker/provisioner, recording hook, temp config dir):
  - **Boundary ordering:** hook called with the FULL identity set before any file exists; hook throw → no config change, no token file, result `Failed`; hook OCE → `Cancelled`, nothing durable.
  - **Cancellation on both sides of every publication:** cancel during picker await / device poll / exchange network call → `Cancelled` + zero durable writes; cancel signalled after boundary entry (hook completed) → `Committed` with ALL publications present (config + stamp + tokens).
  - **GitHub discovery:** N tenants → N `AuthIdentity` in the hook, N token files, `MergeProfiles` active = picked; per-tenant exchange failure → that tenant's token absent, others present, `Committed` (matches today's login warning-per-failure), warning via `Error`.
  - **WorkOS:** `Ready` → boundary writes config then token (`StoredTokens` fields exactly today's: `Provider = AuthProvider.WorkOS`, `ClientId`, canonical `ServerUrl`); `Retarget` → `AuthResult.Retarget`, nothing durable; zero tenants + provisioner Created → switch + boundary; provisioner Declined/InProgress/Failed → `Failed`, nothing durable.
  - **`None` server:** boundary = profile + stamp, NO token file; gate (`OnboardingGate.EvaluateResolvedAsync`) run against the resulting config returns `Complete` — the stamp-vocabulary pin (`"None"` verbatim honored case-insensitively).
  - **Provider stamp:** written for every published identity inside the boundary; a cancelled pre-boundary login leaves NO stamp.
- [x] **Step 2: red.**
- [x] **Step 3: Implement.** `WorkOSDiscovery.RunAsync`/`SwitchAndSaveAsync` become thin composition over `DiscoverAsync` + the façade-shaped publication (existing signature preserved for the interim; the CLI tasks remove remaining direct callers).
- [x] **Step 4: Full CLI unit suite green; AOT greps empty (both binaries).**
- [x] **Step 5: Commit** `feat(core): onboarding facade with ordered commit boundary and claim hook`

### Task 5: `kcap login` re-plumb onto the façade

**Files:**
- Modify: `src/Capacitor.Cli/Program.cs` (`case "login":` L299-309, `HandleDiscoverLoginAsync` L839-915)
- Test: `test/Capacitor.Cli.Tests.Unit/LoginDiscoverTests.cs` (updated), `test/Capacitor.Cli.Tests.Unit/LoginFacadeParityTests.cs`

**Contract.** Both login paths construct ONE façade: `new OnboardingFacade(new ConsoleAuthProgress(), new SpectreTenantPicker(), provisioner: null, beforeCommit: null)`. Known-server: `facade.LoginAsync(baseUrl!, forceDevice, profile: null, CancellationToken.None)`; `--discover`: `facade.DiscoverAsync(chosenProvider, forceDevice, ct)` — provider chosen exactly as today (`ChooseDiscoveryProvider`); `Retarget` prints today's `Run \`kcap setup {target}\` to configure that workspace.` and exits 1; result mapping `Committed → 0`, else `1` with today's messages (already emitted via the progress sink). Funnel parity: the login path emits NO funnel events of its own — only the WorkOS events embedded in discovery fire (unchanged); the GitHub path must emit NOTHING (assert). Exit codes and stdout/stderr shape match today (`Logged in. Active profile: {profile}.` final line preserved — emitted by the adapter after `Committed`, matching L913).

- [x] **Step 1: Failing tests:** `--discover` GitHub with 2 tenants → 2 token files + merged profiles + active = picked + final active-profile line (parity with the pre-re-plumb behavior pinned in `LoginDiscoverTests`); known-server `None` → exit 0, "no authentication configured" line, stamp written; funnel: a recording telemetry guard asserts zero GitHub funnel events on the login path.
- [x] **Step 2-4: red → re-plumb → green** (full CLI unit suite — `LoginDiscoverTests` updated where they pin `HandleDiscoverLoginAsync` internals).
- [x] **Step 5: Commit** `refactor(cli): login re-plumbed onto the onboarding facade`

### Task 6: `kcap setup` re-plumb onto the façade

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` (Step 1/2, `RunDiscoveryAsync` deleted or reduced to the façade call)
- Test: `test/Capacitor.Cli.Tests.Unit/SetupCommandTests.cs` (updated)

**Contract.** The interactive no-server-arg path calls `facade.DiscoverAsync` with `new SpectreTenantProvisioner(new TenantProvisioningClient(new HttpClient()), ProvisioningEndpoint.Url)` when not headless (as today). Funnel asymmetry preserved AT THE ADAPTER: setup emits `SigninOpened(signinMode, provider)` before the operation, `SigninFailed("github_token_denied")` when the GitHub token acquisition fails (façade `Failed` before any identity was published on the GitHub branch), `SigninCompleted(AuthProvider.GitHubApp)` + `TenantNone(AuthProvider.GitHubApp)` from the operation result/progress — WorkOS events remain embedded in Core (unchanged). `Retarget` loops into `ResolveServerAndProviderAsync(ServerInput.ResolveTenantArg(ServerInput.ToServerOrigin(target)))` exactly as today's L857. Step 2 for a `Committed` discovery reports the loginComplete branch (both providers now — the pinned convergence); `serverUrlArg`/`--no-prompt` paths keep `ResolveServerAndProviderAsync` and use `facade.LoginAsync(serverUrl, forceDevice, activeProfile, …)` where Step 2 needs a login. The `preAuthToken` field and its Step-2 exchange branch are DELETED. Everything from Step 3 on is untouched.

- [x] **Step 1: Failing tests:** GitHub discovery path → tokens for all tenants + Step-2 loginComplete output (updated pins); WorkOS path unchanged output; funnel sequence for setup (`Started` → `SigninOpened` → `SigninCompleted` → … → `Succeeded`) asserted via the existing funnel test harness; `--server-url` + `--no-prompt` behavior byte-identical.
- [x] **Step 2-4: red → re-plumb → green** (full CLI unit suite + `SetupFunnelTests`).
- [x] **Step 5: Commit** `refactor(cli): setup re-plumbed onto the onboarding facade`

### Task 7: Streaming process runner (§7)

**Files:**
- Modify: `src/Capacitor.App/Services/IProcessRunner.cs`, `src/Capacitor.App/Services/DaemonClientService.cs` (nested `ProcessRunner`)
- Test: `test/Capacitor.App.Tests.Unit/StreamingRunnerTests.cs`

**Interfaces (produces):**
```csharp
public enum ProcessStreamKind { Stdout, Stderr }
public sealed record StreamedLine(ProcessStreamKind Kind, string Text);
public sealed record StreamingResult(int ExitCode, bool TimedOut, IReadOnlyList<StreamedLine> Tail);  // NO full captures
public interface IProcessRunner {
    Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct);
    Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
                                            Action<StreamedLine> onLine, CancellationToken ct);
}
```
**Contract.** Each stream line-buffered independently (no cross-stream ordering promise); `onLine` invoked on the pump thread — callback exceptions logged to `Console.Error` and swallowed (never kill the pump); the runner retains only a bounded tail (const `TailLimit = 500` lines total, oldest dropped) mirrored into `StreamingResult.Tail`; full `Stdout`/`Stderr` captures are structurally absent from the result type. Cancellation via `ct` = `KillTree` + await exit (streaming ignores `RunOptions.CancelMode` — §7 pins KillTree); `RunOptions.Timeout` honored with `TimedOut = true` + KillTree.

- [x] **Step 1: Failing tests** (real child processes — `/bin/sh -c` scripts; Windows-guard where needed): interleaved stdout/stderr lines each tagged with the right kind; a >500-line run: callback sees every line, `Tail` holds exactly the LAST 500; a throwing callback: pump survives, exit code still captured; mid-stream cancel: child tree killed (child writes a marker file on exit-signal absence — assert no orphan by PID liveness), method returns; large-output run: process memory of the result bounded (assert `Tail.Count <= 500` and no full-capture property exists — compile-time by shape).
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): streaming process runner with bounded tail and KillTree cancel`

### Task 8: `KcapCli` — `PluginInstallAsync` + streaming import

**Files:**
- Modify: `src/Capacitor.App/Services/KcapCli.cs`
- Test: `test/Capacitor.App.Tests.Unit/KcapCliTests.cs`

**Interfaces (produces, on `IKcapCli` + `KcapCli`):**
```csharp
public enum ImportScopeChoice { Everything, Org, Repo }
public sealed record ImportRequest(ImportScopeChoice Scope, string? OrgOrRepo, IReadOnlyList<string> VendorFlags);
Task<ProcessResult> PluginInstallAsync(string? vendorFlag, CancellationToken ct);        // null = Claude (flagless default)
Task<StreamingResult> ImportAsync(ImportRequest request, Action<StreamedLine> onLine, CancellationToken ct);
```
**Contract.** `PluginInstallAsync`: args `["plugin", "install"]` + vendorFlag when non-null (the §5 exclusive flags: `--codex --cursor --copilot --gemini --kiro --pi --opencode --antigravity`); `Env()` overlay (profile + no-telemetry); bounded `MutationTimeout` (60 s); null `CliPath` → `NoCliResult()`. `ImportAsync`: args `["import"]` + scope (`--all` | `--org <o>` | `--repo <r>`) + `--yes` + each vendor flag, `Env()` overlay, `RunStreamingAsync` with NO internal timeout (imports are long; cancellation is the bound), null `CliPath` → a `StreamingResult(-1, false, [])` with a synthesized `Stderr` tail line `kcap CLI not found`. Neither verb overlays mutation env (non-daemon shelling keeps lenient classification, spec §4).

- [x] **Step 1: Failing tests** (`FakeProcessRunner` in `KcapCliTests`, extended with a streaming recorder): exact argv per scope/vendor combination (Everything+2 vendors; Org; Repo; Claude default flagless); env overlay carries `KCAP_PROFILE` + `KCAP_APP_SPAWN_NO_TELEMETRY` and NOT `KCAP_CONSENT_SEED_DEFAULT`/`KCAP_EXPECT_SERVER_URL`; no-CLI results for both verbs.
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): KcapCli plugin-install and streaming import verbs`

### Task 9: `WizardAuthService` — single-flight driver, claims arming, close handoff

**Files:**
- Create: `src/Capacitor.App/Services/Onboarding/WizardAuthService.cs`
- Test: `test/Capacitor.App.Tests.Unit/WizardAuthServiceTests.cs`

**Interfaces (produces):**
```csharp
public abstract record ConnectIntent {
    public sealed record Paste(string ServerInput) : ConnectIntent;
    public sealed record Discover(string Provider) : ConnectIntent;   // AuthProvider constant
    public sealed record Create : ConnectIntent;                       // WorkOS discovery; provisioner-armed
}
public sealed class AuthAttempt {
    public Task<AuthResult> Result { get; }     // terminal task the app awaits at close (decision 2)
    public void Cancel();                        // pre-boundary: op cancels; post-boundary: Result still ends Committed
}
public sealed class WizardAuthService(
        Func<ConnectIntent, CancellationToken, Task<AuthResult>> runOperation,  // binds the facade + adapters
        ConsentFlipClaims claims) {
    public AuthAttempt? Current { get; }
    public AuthAttempt Begin(ConnectIntent intent);   // single-flight: throws InvalidOperationException while unquiesced
    public Task QuiescedAsync();                       // completes when no attempt is live
}
```
**Contract.** Single-flight: `Begin` while `Current` is non-terminal throws; a new attempt is admitted only after the previous `Result` completed (cancelled-before-boundary or terminal) — Retry re-runs only after quiesce. Cancellation is a distinct outcome (`AuthResult.Cancelled`), never rendered as failure. The composition root builds `runOperation` to construct an `OnboardingFacade` per attempt with: the Sign-in step's `IAuthProgress` (UI-marshaling), the wizard picker/provisioner bridges (Task 12), and the **before-commit hook** = for each identity, `await Task.Run(() => claims.Arm(new ConsentFlipClaim(id.Profile, id.CanonicalServer)))`; a `false` return throws (`InvalidOperationException("claim_arm_failed")`) → the façade aborts pre-boundary → retryable sign-in error (decision 7: hook failure prevents the commit). `Paste` maps to `LoginAsync(ServerInput.ResolveTenantArg(ServerInput.ToServerOrigin(input)), …)`; `Discover(p)` → `DiscoverAsync(p, …)`; `Create` → `DiscoverAsync(AuthProvider.WorkOS, …)` with the provisioner bridge armed. Close handoff (decision 2): the window's close path calls `Cancel()` and the APP awaits `Result` before resolving configuration/building the graph/completing shutdown (wired in Task 16) — pre-boundary resolves `Cancelled` fast; post-boundary resolves `Committed` after publications.

Same task: `App.ResolveConsentFlipIdentity` (App.axaml.cs L397-404) switches `ConfigMutator.LoadPure` → `TryLoadPure`; an unreadable config returns an identity that matches nothing (e.g. `("", "", "")`) so `TryConsume`'s compare retains the claim — fail-closed, never a throw inside the two-lock section.

- [x] **Step 1: Failing tests:** single-flight (`Begin` during live attempt throws; after terminal, admitted); hook arms one claim per identity (recording claims store on a temp path — REAL `ConsentFlipClaims`); `Arm` returning false → `Failed`, nothing published (scripted runOperation asserting the hook threw before its publication step); cancel-pre-boundary → `Cancelled` + `QuiescedAsync` completes; cancel-post-boundary (scripted op that enters boundary then observes cancel) → `Committed`; quarantined store: `Arm` during an attempt still lands in the fresh store (never rejected) and `claims.Quarantine()` is non-null after; `TryLoadPure` switch: corrupt config file + `TryConsume` → claim retained, no exception.
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): wizard auth service with claim-arming hook and close handoff`

### Task 10: Quarantine ack affordance (coordinator path)

**Files:**
- Modify: `src/Capacitor.App/Services/Onboarding/ConsentFlipCoordinator.cs`
- Test: `test/Capacitor.App.Tests.Unit/ConsentFlipCoordinatorTests.cs`

**Contract.** `SurfaceQuarantineOnceAsync` upgrades from a bare `Attention` line to `surface.TryConfirmAsync(new LifecyclePrompt(Kind: "quarantine", …, Disclosure: <preserved path + the recovery guidance from Plan B's exact copy>), ct)` with a single affirmative ("Acknowledge"); `true` → `AckQuarantineAsync()`; `false`/`null` (declined, cancelled, or no dialog capability) → NOT acked, surfaces again next start — acknowledgment must be explicit, never inferred from dismissal. `LifecyclePrompt` gains `public const string KindQuarantine = "quarantine";` and `LifecyclePromptViewModel`/`LifecyclePromptWindow` render it with a single-button layout (confirm-only; no destructive action).

- [x] **Step 1: Failing tests:** quarantine + unacked → one `TryConfirmAsync` with the preserved path in the disclosure; confirm → `ConsentQuarantineAcked` persisted, not re-surfaced on a fresh coordinator; decline → not persisted, re-surfaced; acked state → zero prompts.
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): quarantine acknowledgment prompt on the coordinator path`

### Task 11: Onboarding shell — window, step machine, navigation

**Files:**
- Create: `src/Capacitor.App/Views/Onboarding/OnboardingWindow.axaml`, `src/Capacitor.App/Views/Onboarding/OnboardingWindow.axaml.cs`, `src/Capacitor.App/ViewModels/Onboarding/OnboardingViewModel.cs`, `src/Capacitor.App/ViewModels/Onboarding/WizardStep.cs`
- Test: `test/Capacitor.App.Tests.Unit/OnboardingViewModelTests.cs`

**Interfaces (produces):**
```csharp
public enum WizardStepId { Shim, Connect, SignIn, Defaults, Agents, Import, Daemon, Done }
public interface IWizardStep {
    WizardStepId Id { get; }
    string Title { get; }
    bool Applicable { get; }        // Shim: macOS + resolvable CLI + kcap NOT on terminal PATH
    bool Satisfied { get; }         // re-entry shows satisfied state
    Task OnEnterAsync(CancellationToken ct);
    Task<bool> CanLeaveAsync(WizardNavigation direction, CancellationToken ct);  // SignIn: pre-boundary cancel on Back/Skip
}
public enum WizardNavigation { Back, Next, Skip }
public sealed class OnboardingViewModel : ViewModelBase {
    public IReadOnlyList<IWizardStep> Steps { get; }        // Applicable-filtered, spec §3 order
    public IWizardStep Current { get; }
    public ICommand BackCommand, NextCommand, SkipCommand;
    public event Action? CloseRequested;                    // Done-step finish or window close
}
```
**Contract.** Linear steps, Back/Next, every step individually skippable and retryable (§3); one window, content switched by `DataTemplate` on the step VM type. Skip advances without satisfying; Back from Sign-in post-commit returns to Connect for a different intent while Sign-in shows satisfied on re-entry. Window close (any time) raises `CloseRequested`; the shell never blocks close — the app's startup sequencing (Task 16) owns the quiesce. Styling follows `MainWindow.axaml` conventions.

- [x] **Step 1: Failing tests** (`[NotInParallel("AvaloniaSession")]`, headless; fake steps): applicability filtering (Shim absent when not applicable); Next/Back/Skip transitions across the full order; `CanLeaveAsync(false)` holds the step; skip-then-return shows unsatisfied; Done raises `CloseRequested`.
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): onboarding wizard shell with step state machine`

### Task 12: Sign-in step — progress rendering, picker/provisioner bridges, quarantine notice

**Files:**
- Create: `src/Capacitor.App/ViewModels/Onboarding/SignInStepViewModel.cs`, `src/Capacitor.App/ViewModels/Onboarding/ConnectStepViewModel.cs`, `src/Capacitor.App/Services/Onboarding/WizardAuthBridges.cs`
- Test: `test/Capacitor.App.Tests.Unit/SignInStepViewModelTests.cs`

**Interfaces (produces, in `WizardAuthBridges.cs`):**
```csharp
public sealed class WizardTenantPicker : ITenantPicker {        // PickAsync awaits the VM's selection TCS
    public Task<DiscoveredTenant?> PickAsync(DiscoveredTenant[] tenants, CancellationToken ct);
    public DiscoveredTenant? Pick(DiscoveredTenant[] tenants) => throw new NotSupportedException();  // facade consumes PickAsync only
    public event Action<DiscoveredTenant[]>? SelectionRequested;
    public void Select(DiscoveredTenant? tenant);
}
public sealed class WizardTenantProvisioner(TenantProvisioningClient client, string baseUrl) : ITenantProvisioner {
    public Task<ProvisionOffer> OfferCreateAsync(WorkOSTokenSource tokens, CancellationToken ct);
    // UI hooks the VM implements: org-name/slug prompts, confirm, poll progress
    public Func<CancellationToken, Task<string?>>? PromptOrgName;
    public Func<string, string, CancellationToken, Task<string?>>? PromptSlug;      // (suggestion, availabilityError) → slug|null
    public Func<string, string, CancellationToken, Task<bool>>? ConfirmCreate;      // (slug, origin)
    public Action<int, int>? PollProgress;                                          // (attempt, max) — 4 s × 150 mirror
}
public sealed class UiAuthProgress(Action<Action> post) : IAuthProgress { … }       // marshals every event to the UI scheduler
```
**Contract.** `ConnectStepViewModel` gathers intent only (§3 step 2): paste (validated via `OnboardingGate.ValidServerUrl` after `ServerInput` normalization), discover (provider choice GitHub/WorkOS), create; nothing network, nothing written. `SignInStepViewModel` runs ONE `WizardAuthService.Begin(intent)` rendering: progress notices inline, `BrowserOpening` with clickable fallback URL (`IUrlOpener`), `DeviceCode` display, tenant list on `SelectionRequested` (list UI resolves `Select`), create sub-flow via the provisioner hooks (`SlugValidator.Derive` suggestion, availability re-prompt loop mirroring `SpectreTenantProvisioner.PromptSlugAsync` semantics, provisioning progress `attempt/max`, "still provisioning — finish later by joining `<slug>` from the Connect step" on `InProgress` — the CLI's message GUI-shaped, §9). `Retarget` → back to Connect with the input prefilled. `AuthProvider.None` pasted-URL join auto-satisfies the step on `Committed`. Cancellation ≠ failure in every rendered surface. Quarantine: after any attempt, if `claims.Quarantine()` is non-null and `AppState.ConsentQuarantineAcked` is false → one notice naming the preserved path with an Acknowledge button → `AckQuarantineAsync` (same recovery copy as Task 10). Step transitions per §3: pre-boundary Back/Skip/close → `attempt.Cancel()` + await quiesce via `CanLeaveAsync`; post-boundary → satisfied, Back allowed for a different intent.

- [x] **Step 1: Failing tests** (headless; scripted `runOperation` via the service): the §10 transition table — pasted URL / GitHub discovery / WorkOS discovery / zero-tenant create / retarget / `None` auto-satisfy; Back and Skip on both sides of the boundary (pre → `Cancelled`, nothing durable per the scripted op; post → satisfied); re-entry recognizes satisfied; device-code and browser-fallback render from progress events; picker TCS resolves the façade await; provisioner hook sequence for the create sub-flow incl. `InProgress` copy; quarantine notice once + ack persisted.
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): connect and sign-in wizard steps with facade bridges`

### Task 13: Shim, Defaults, Done steps

**Files:**
- Create: `src/Capacitor.App/ViewModels/Onboarding/ShimStepViewModel.cs`, `src/Capacitor.App/ViewModels/Onboarding/DefaultsStepViewModel.cs`, `src/Capacitor.App/ViewModels/Onboarding/DoneStepViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/WizardSimpleStepsTests.cs`

**Contract.** *Shim (§3 step 1):* `Applicable` = macOS AND resolved absolute CLI path AND `probe.KcapOnPathAsync` positively `false`; Install runs `PathShimInstaller.InstallAsync(target)` (AppleScript sudo, non-forcing, post-install re-probe — reused as-is) and claims `ShimOffered` via `AppStateStore` so the post-wizard `ShimOfferCoordinator` never re-offers; outcomes map: `Installed` → satisfied; `InstalledButNotOnPath`/`Failed` → message + Retry; `Cancelled` → unsatisfied, skippable. *Defaults (§3 step 4, decision 5):* visibility picker over `AppConfig.ValidVisibilities` with the SAME labels as setup's prompt (copy the four converter strings), default `org_public`; daemon-name field default `Environment.UserName.ToLowerInvariant()`; Next persists via `ConfigMutator.MutateAsync(c => …)` writing `DefaultVisibility` + `Daemon.Name` on the active profile — NO claim maintenance (claims key on `{profile, server}`). *Done (§3 step 8):* summary grid from each step's `Satisfied`/skip state incl. why-skipped notes ("kcap CLI not found", "requires sign-in").

- [x] **Step 1: Failing tests:** shim applicability matrix (non-macOS / no CLI / kcap-on-PATH / probe-null → not applicable); install claims `ShimOffered` exactly once; defaults write lands both fields and preserves unrelated config (temp config, REAL `ConfigMutator`); done summary reflects skip reasons.
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): shim, defaults, and done wizard steps`

### Task 14: Agents + Import steps

**Files:**
- Create: `src/Capacitor.App/ViewModels/Onboarding/AgentsStepViewModel.cs`, `src/Capacitor.App/ViewModels/Onboarding/ImportStepViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/AgentsImportStepsTests.cs`

**Contract.** *Agents (§3 step 5, decision 8):* inputs = `AgentDetection.FromEnvironment()` overridden with the login-shell PATH — `inputs with { PathEnv = await probe.TerminalPathAsync(ct) ?? inputs.PathEnv }` (the app feed; a null probe result keeps the process PATH); one checkbox per vendor, pre-checked when `Detected`; Install runs `PluginInstallAsync` per selection sequentially (Claude `null` flag; vendor flags per §5's exclusive list); per-vendor exit codes → ⚠ + Retry on failures, successes stand (§9). *Import (§3 step 6, decision 6):* vendor checkboxes (default: the detected set), scope choice mirroring `ImportScopePrompt`'s vocabulary — `Everything` / `All repos in one org` (+ org text field) / `Specific repository` (+ `owner/name` field); Run streams `ImportAsync` into a live log pane bounded to the LAST 500 lines with an "older lines dropped" header once truncation starts, lines marshaled from the pump to the UI scheduler; Cancel = the ct (KillTree in the runner) + await exit; completion ALWAYS appends "if anything failed, run `kcap import` in a terminal to retry" (unconditional — the CLI exits 0 with per-session errors, §7); failure never blocks Next.

- [x] **Step 1: Failing tests:** detection feed uses the terminal PATH when present, process PATH when probe null (fake probe + crafted `AgentDetectionInputs` asserting a binary only findable via the terminal PATH is detected); per-vendor install argv + failure isolation; import argv per scope; log pane bound at 500 with the drop notice; the unconditional retry line present on success AND failure; cancel kills (scripted runner asserting KillTree-path invoked).
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): coding-agents and import wizard steps`

### Task 15: Daemon step — the §3 step-7 matrix through the lane

**Files:**
- Create: `src/Capacitor.App/ViewModels/Onboarding/DaemonStepViewModel.cs`, `src/Capacitor.App/Services/Onboarding/WizardLifecycleSurface.cs`
- Test: `test/Capacitor.App.Tests.Unit/DaemonStepViewModelTests.cs`

**Interfaces (consumes):** `DaemonMutationLane.RunAsync` (waiter-state-only), `MutationRequestFactory.TryBuild`, `IKcapCli.ServiceStatusAsync`, `ILocalControlOps.PutConsentPolicyV2Async` + `GetConsentPolicyAsync`, `ConsentFlipClaims.Pending()/TryConsume`, `LocalControlCapabilities` via a one-shot `LocalControlProbe`.
**Produces:** `WizardLifecycleSurface : ILifecycleSurface` — routes `Status`/`Attention` into step-local observable text and `ConfirmAsync`/`TryConfirmAsync` into wizard-owned dialogs (windowed over `OnboardingWindow`), so the SAME `ConsumeMutationOutcomesAsync` consumer runs during wizard-first mode with this surface (Task 16 wires it).

**Contract.** Skipped-login users see "requires sign-in" (§9); gate-complete identity resolved fresh at step entry (post-Sign-in config). On enter: fresh `ServiceStatusAsync` snapshot → classify on AI-1654's FULL matrix (§3 step 7 rows, verbatim recoveries): no unit → `Install`; unit stopped/loaded-inactive + null `daemon_pid` → `StartVerified`; positive ownership + identity match → "already enabled" + apply pending claim via the explicit conditional put (NO factory guard — get is skipped; put carries `{resolved name, claim server}`; ack failure → claim pending + repair guidance); ownership without identity match → takeover offer only; manual daemon identity-matched → takeover dialog with AI-1654 disclosures, decline → visibly incomplete + claim STILL applied via conditional put; manual daemon identity-MISMATCH → no mutation, no claim application, takeover the only offered row; orphan/stale marker → repair affordance; `txn_active` → wait; unparseable → honest message, no mutation. Mutations route through `lane.RunAsync(MutationRequestFactory.TryBuild(...))` — results update step state ONLY (single-presentation rule); actionable outcomes arrive through the channel consumer with the wizard surface. Enablement success = the lane's own `Succeeded` predicate (never a local re-derivation). Claim consumption uses `TryConsume` with the Task-9 `TryLoadPure` resolver.

- [x] **Step 1: Failing tests** (scripted `IKcapCli` snapshots + `FakeMutationLane`-style recorder + `ScriptedLocalControlOps` + real claims store): every matrix row above → the exact verb (or explicit no-mutation) + offered affordance; claim application on the owning+identity row and on manual-identity-match decline; NO claim application on wrong-server rows; state-only handling of `RunAsync` results (no dialog from the waiter path — recorded surface sees zero prompts when an outcome also travels the channel); "requires sign-in" when gate incomplete.
- [x] **Step 2-4: red → implement → green.**
- [x] **Step 5: Commit** `feat(app): daemon enablement wizard step on the full state matrix`

### Task 16: Wizard-first startup + consumer handoff

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs`
- Test: `test/Capacitor.App.Tests.Unit/AppStartupCarveOutTests.cs` (extended), `test/Capacitor.App.Tests.Unit/WizardStartupTests.cs`

**Contract (decision 2, replacing Plan B's `Incomplete` arm).** `StartAsync`: lane + channel constructed first (unchanged — app-lifetime). Gate `Complete` → today's startup exactly. Gate `Incomplete` → build NO daemon graph (no `DaemonClientService`, no tray, no lifecycle controller, no shim coordinator): construct the wizard graph — `ConsentFlipClaims.Default()`, `WizardAuthService`, step VMs (Daemon step gets a per-resolved-identity `KcapCli` for status + `LocalControlOps` for the put; `CliResolver` override seam preserved), `OnboardingWindow` as `desktop.MainWindow`, and start `ConsumeMutationOutcomesAsync(channel, wizardSurface, lane.RunAsync, …)` as the consumer. On `CloseRequested` OR window close: (1) `authService.Current?.Cancel()`; (2) await the auth terminal task (post-boundary close runs to `Committed` — decision 2); (3) `AwaitQuiescedAsync(lane.QuiescedAsync, cap)` with the existing §6a post-cap rules (past the cap: proceed, auto-actions closed, attention from re-queried evidence — the lane still owns the live action); (4) `channel.TransferConsumer()`; (5) fresh resolution (re-run the SAME resolution `CreateDefaultAsync` performs — `AppConfig.LoadProfileConfig` → active profile; never the startup-cached `AppConfig.ResolvedProfile`) + `EvaluateResolvedAsync` on it (single-resolution rule); (6) build the normal graph — still-`Incomplete` → the Plan B carve-out arm (auto-actions closed + shim auto-offer suppressed) — and start the root consumer. Shutdown during wizard mode: `OnShutdownRequested` awaits the auth terminal task + lane quiesce under `QuiesceShutdownCap` before disposing (§6a: past the cap, exit with the child detached is safe).

- [x] **Step 1: Failing tests:** gate-incomplete → NO service/tray/lifecycle/shim constructed (assert via the built-object fields), wizard window is MainWindow, consumer live with the wizard surface (enqueue → wizard-surface presentation); close with gate-now-complete → normal graph against the FRESH profile (config changed between open and close is honored); close with gate-still-failing → carve-out graph (auto-actions closed, shim suppressed) — the §10 decision-2 rows: zero service mutation for valid-URL+no-token+abandon, invalid-URL+abandon, and close during an in-flight pre-boundary auth operation (scripted attempt: cancelled, nothing durable, graph waits for terminal); enqueue racing `TransferConsumer` → delivered exactly once (root surface); post-boundary close → graph build AWAITS `Committed`.
- [x] **Step 2-4: red → implement → green** (full app suite; the existing `AppStartupTests`/`AppMutationLaneWiringTests` keep passing).
- [x] **Step 5: Commit** `feat(app): wizard-first startup with consumer handoff and fresh re-resolution`

### Task 17: Wizard E2E-shaped composition tests + spec riders

**Files:**
- Test: `test/Capacitor.App.Tests.Unit/WizardCompositionTests.cs`
- Modify: `docs/superpowers/specs/2026-08-12-ai1655-onboarding-wizard-design.md` (rider), `CLAUDE.md`

**Contract.** Composed flows through the REAL shell + real step VMs (scripted externals only): (a) fresh-machine happy path — paste `None` URL → auto-satisfied Sign-in → defaults written → agents/import skipped (no CLI) → daemon step "requires sign-in" absent (gate complete), Done summary correct; (b) abandon-before-sign-in → close → nothing durable (no config/token/claim writes on the temp dirs); (c) sign-in commit → claims armed → quit → relaunch fixture: `ConsentFlipCoordinator` (normal graph) still holds the claim. Spec rider records the Plan C deviations (this plan's pinned list) next to §5's result algebra and §4's adapter text; CLAUDE.md gains the Plan C paragraph (façade + wizard-first summary, ≤16 lines, replacing nothing).

- [x] **Step 1: Failing composition tests (a)-(c).**
- [x] **Step 2-4: red → implement/wire gaps → green.**
- [x] **Step 5: Commit** `test(app): wizard composition flows; docs: spec riders`

### Task 18: Final verification sweep

- [x] Full app suite (`dotnet run --project test/Capacitor.App.Tests.Unit/...`), full CLI unit + integration suites (`TMPDIR=/private/tmp`), both AOT publish greps empty.
- [x] `grep -rn 'Console\.' src/Capacitor.Cli.Core/Auth/OnboardingFacade.cs` → zero (façade is sink-only); `grep -rn 'AnsiConsole\|Spectre' src/Capacitor.Cli.Core/` → zero (Core stays Spectre-free).
- [x] Comment-discipline self-check on the branch diff: `git diff origin/main | grep -inE 'round|review|task [0-9]|plan [abc]|AI-[0-9]'` → only legitimate hits (issue refs in docs).
- [x] `README.md` untouched (no CLI surface change) — verify no new flag/verb slipped in.
- [x] Commit any stragglers; push; PR: `Part of #553. AI-1655` + the deviation ledger in the description.

## Self-review notes

- Spec §5 coverage: operations (T4), progress sink (T2), cancellation (T1), boundary (T4), async picker (T1), helper moves (T3). §3 steps: 1→T13, 2/3→T12, 4→T13, 5/6→T14, 7→T15, 8→T13. §7→T7/T8/T14. §9 error rows distributed into each step task. §10 rows not already covered by Plans A/B are enumerated inside the owning tasks; E2E stays manual (spec).
- Type-consistency: `AuthResult`/`AuthIdentity`/`ConnectIntent`/`StreamedLine`/`ImportRequest` defined once (T4/T9/T7/T8) and consumed by name in later tasks.
- The wizard never calls `CliTelemetry.Initialize`; all shelling goes through `KcapCli` (marker overlay) — no new telemetry surface.
