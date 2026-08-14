# AI-1655 Plan B — App mutation lane, consent-flip claims, onboarding gate

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the app-side safety substrate of the onboarding wizard — the `DaemonMutationLane`
singleton (probe → mutation → reconciliation), the `ConsentFlipClaims` store +
`ConsentFlipCoordinator`, the `OnboardingGate`, and the decision-2 startup carve-out — wired into
the existing lifecycle controller and main-window Start, WITHOUT the wizard UI (Plan C).

**Architecture:** Everything lands in `src/Capacitor.App/` except three Core additions (the
`auth_provider` profile stamp, `ILocalControlOps.PutConsentPolicyV2Async`, and an additive
`Identity` on the app-visible attach status). The lane is one deep module composed from small,
separately-tested pieces built first: version-floor predicate, validated path resolver, process-only
kill mode, outcome algebra + reason-line parsing, boot-refusal marker reader, leased outcome
channel, observation adapters. Plan A's CLI/daemon substrate (gates 28/29, exit 43, boot-refusal
markers, `ConsentRulesPutV2`/`consent/3`, `pid`/`instance_id` DTOs, `LocalControlProbe`,
`ConfigMutator`) is consumed, never modified.

**Tech stack:** .NET 10, Avalonia (app), Rx/DynamicData (existing app patterns), TUnit on MTP,
`Microsoft.Extensions.TimeProvider.Testing`.

**Governing spec:** `docs/superpowers/specs/2026-08-12-ai1655-onboarding-wizard-design.md` —
sections §4 (components), §6 (consent), §6a (service lane), decisions 1/2/4/7/10, §10 (testing).
Where this plan and the spec disagree, the spec governs; stop and flag it.

## Global Constraints

- The compatibility floor is the literal `0.12.0-beta.1` — never `0.12.0`.
- Fail-closed doctrine: missing/malformed/ambiguous evidence → attention/repair outcomes, never
  `Succeeded`, never a destructive recovery. Unknown/future reason tokens → fail-closed attention.
- Reason-line rule: machine-readable stderr lines are parsed by prefix; exactly ONE matching line
  is required — zero, duplicate, or conflicting matching lines fail closed. Unrelated stderr never
  affects routing.
- `ConfigFileLock` is a thread-affine named Mutex: no `await` while held; async callers wrap the
  whole critical section in `Task.Run`. Global lock order is config → claims, both held across any
  compare-and-delete.
- Attribution never uses wall-clock comparison; detached-start attribution is by `attempt_id`
  equality, service-verb attribution is verified-pre-clear + observed-job-PID correlation (the CLI
  transaction does this; the app consumes only the `refusal_reason=` line).
- App-managed daemon spawns carry `KCAP_CONSENT_SEED_DEFAULT=prompt`, `KCAP_EXPECT_SERVER_URL`,
  `KCAP_PROFILE`, and `KCAP_APP_SPAWN_NO_TELEMETRY=1`; no bare-`"kcap"` fallback on any mutation
  path.
- Repo rules: no Linear IDs in code comments; concise comments (one short invariant line max);
  `JsonElementExtensions` for JSON kind checks; `new JsonArray(...)` constructor; `Path.Combine`
  in path assertions (Windows CI leg); TUnit filter syntax `--treenode-filter "/*/*/ClassName/*"`;
  run local suites with `TMPDIR=/private/tmp`; real-socket tests take
  `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]` + a Windows guard.
- After any Core/CLI/daemon-touching task: `dotnet publish -c Release` for BOTH
  `src/Capacitor.Cli/Capacitor.Cli.csproj` and `src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`
  must show zero `IL[23]0xx` warnings. The app is not AOT-published; it must still build clean.
- No README changes in this plan (zero user-visible CLI surface changes).

## Existing seams this plan consumes (verified against `origin/main` @ 2732f9fbd)

- `KcapCli(IProcessRunner runner, string? cliPath, string daemonName, string profileName, Func<CancellationToken,Task<string?>>? terminalPathAsync)`
  with `VersionAsync`, `ServiceStatusAsync`, `ServiceStartVerifiedAsync`,
  `ServiceInstallVerifiedAsync(bool replace, ct)`, `DetachedStartAsync`; null `CliPath` degrades to
  synthetic `ProcessResult(127, "", "kcap CLI not found", false)`.
- `RunOptions(IReadOnlyDictionary<string,string>? EnvOverlay, TimeSpan? Timeout, CancelMode CancelMode)`;
  `ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)`; runner impl is
  `DaemonClientService.ProcessRunner` (internal nested); internal timeout kill is currently ALWAYS
  `Process.Kill(entireProcessTree: true)`.
- `DaemonClientService` publishes `IObservable<AttachStatus> Status` (BehaviorSubject),
  `Snapshots`, `Agents`; `AttachStatus(AttachState State, string? Reason, IReadOnlyList<string>? Capabilities, string? DaemonVersion = null)`;
  `LocalControlEvent.Connected(Capabilities, FirstSnapshot, ConnectedIdentity? Identity)` — the
  service currently DISCARDS `Identity`.
- `LocalControlProbe.ProbeAsync(string daemonName, TimeSpan timeout, ct)` →
  `ProbeResult(bool Reachable, HelloReplyDto? Hello, DaemonStatusDto? Snapshot, bool IdentityConsistent)`.
- `FrameType.ConsentRulesPutV2 = 19`; `ConsentPolicyPutV2Dto(string ExpectedName, string ExpectedServerUrl, ConsentPolicyDto Policy)`;
  `ConsentAckDto(bool Ok, string? Error, bool? RuleSaved)`; daemon answers `identity_mismatch` as a
  coded `Error` string; capability `consent/3`. `ILocalControlOps` has NO v2-put method yet.
- `ConfigFileLock.Acquire(string configPath, TimeSpan? timeout = null)` (10s default, SHA-256-of-path
  mutex name); `ConfigMutator.MutateAsync/Mutate/LoadPure/TryLoadPure`;
  `AppConfig.ResolveActiveProfile(string[])`, `AppConfig.ResolvedProfile`,
  `AppConfig.GetConfigPath()`; `DaemonNameResolver.Resolve(args, profileDaemonName)`;
  `ServerIdentity.Canonicalize/SameServer/Matches`; `PathHelpers.ConfigPath(name)`.
- `TokenStore.LoadForProfileAsync(profile, ct)` (raw, refresh-free) → `StoredTokens?` with
  `AccessToken`, `RefreshToken`, `ExpiresAt`, `Provider` (`AuthProvider.GitHubApp|WorkOS|None`
  consts), `ClientId`, `ServerUrl`, `IsExpired`.
- `PrereleaseSemver.Compare(string? a, string? b)` (full SemVer2 precedence; unparseable sorts
  lowest). NOTE: it ACCEPTS invalid SemVer (leading-zero cores, illegal identifiers) — the strict
  parse in Task 1 must reject those BEFORE comparing.
- `LoginShellProbe : ILoginShellProbe` with `TerminalPathAsync(ct)` and
  `KcapOnPathAsync(ct, forceRefresh)`; per-instance task-caching; "cacheable" = both shell attempts
  ran to completion.
- CLI-side `BootRefusalReader` (`src/Capacitor.Cli/Services/BootRefusalReader.cs`) — the
  duplication precedent for the app-side reader in Task 6. Marker path:
  `Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(daemonName), "boot-refusal.json")`.
  Record members on disk: `schema, daemon name, token, expectation, resolved, pid, instance_id,
  attempt_id, timestamp`.
- `DaemonLifecycleController` (39K) — decision matrix + `_gate` SemaphoreSlim serialization +
  `PhaseClosed` + `_armClaimed` once-per-run arm; `ShimOfferCoordinator` gates on `PhaseClosed`.
- `AppStateStore` / `AppState(bool ShimOffered, bool ShimDenied, IReadOnlyList<string>? DeclinedTakeoverPairs)`;
  in-process `SemaphoreSlim` only (UX-grade by contract).
- App tests live flat in `test/Capacitor.App.Tests.Unit/`; `FakeProcessRunner` per-file pattern
  (capture fields + injectable behavior); `ScriptedLocalControlOps` shared fake for
  `ILocalControlOps`; `AppStateStoreTests` is the file-store test template.
- Verify exit codes / tokens (CLI): 28 `verify_start_gate` + `start_gate_reason=` enum
  `directive_missing|directive_invalid|identity_mismatch|foreign_binary|package_inconsistent|evidence_unreadable`;
  29 `verify_start_gate_drift`; readiness timeout token `verify_readiness_timeout` with optional
  `refusal_reason=` line; detached `daemon start` exit 43 + `daemon_start_reason=package_inconsistent`;
  boot-refusal reason tokens `server_expectation_mismatch|consent_seed_unwritable|consent_seed_invalid`.

## File structure (created/modified by this plan)

```
src/Capacitor.App/Services/KcapCliCompatibility.cs                     (new, Task 1)
src/Capacitor.App/Services/LoginShellProbe.cs                          (modify, Task 2)
src/Capacitor.App/Services/IProcessRunner.cs                           (modify, Task 3)
src/Capacitor.App/Services/DaemonClientService.cs                      (modify, Tasks 3, 8, 10)
src/Capacitor.App/Services/KcapCli.cs                                  (modify, Task 4)
src/Capacitor.App/Services/Mutation/MutationModel.cs                   (new, Task 5)
src/Capacitor.App/Services/Mutation/ReasonLine.cs                      (new, Task 5)
src/Capacitor.App/Services/Mutation/BootRefusalMarker.cs               (new, Task 6)
src/Capacitor.App/Services/Mutation/OutcomeChannel.cs                  (new, Task 7)
src/Capacitor.App/Services/Mutation/DaemonObservation.cs               (new, Task 8)
src/Capacitor.App/Services/Mutation/DaemonMutationLane.cs              (new, Tasks 9a/9b)
src/Capacitor.App/Services/DaemonLifecycleController.cs                (modify, Task 10)
src/Capacitor.App/Services/AttachStatus.cs                             (modify, Task 8)
src/Capacitor.App/Services/Onboarding/ConsentFlipClaims.cs             (new, Task 11)
src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs                     (modify, Task 12)
src/Capacitor.App/Services/Onboarding/ConsentFlipCoordinator.cs        (new, Task 13)
src/Capacitor.Cli.Core/Config/ProfileConfig.cs                         (modify, Task 14)
src/Capacitor.App/Services/Onboarding/OnboardingGate.cs                (new, Task 14)
src/Capacitor.App/App.axaml.cs                                         (modify, Tasks 10, 15)
src/Capacitor.App/Services/AppStateStore.cs                            (modify, Task 13)
test/Capacitor.App.Tests.Unit/…                                        (one test file per new type)
test/Capacitor.Cli.Tests.Unit/…                                        (Tasks 12, 14 Core tests)
```

---

### Task 1: `KcapCliCompatibility` — strict floor predicate

**Files:**
- Create: `src/Capacitor.App/Services/KcapCliCompatibility.cs`
- Test: `test/Capacitor.App.Tests.Unit/KcapCliCompatibilityTests.cs`

**Interfaces:**
- Produces: `public static class KcapCliCompatibility { public const string Floor = "0.12.0-beta.1"; public static bool Satisfies(string? version); public static bool StrictParse(string version); }`
- Consumes: `Capacitor.Cli.Core.PrereleaseSemver.Compare`.

**Contract:** `Satisfies` = non-null AND `StrictParse(version)` AND
`PrereleaseSemver.Compare(version, Floor) >= 0`. `StrictParse` implements the SemVer 2.0.0 grammar
strictly: `MAJOR.MINOR.PATCH` numeric identifiers with NO leading zeros (`0` itself is fine),
optional `-prerelease` whose dot-separated identifiers are each either strictly-numeric with no
leading zeros or alphanumeric-with-hyphen (nonempty), optional `+build` (any nonempty
dot-separated `[0-9A-Za-z-]+` identifiers, ignored for comparison but still validated). No
regex-free requirement — a `[GeneratedRegex]` is fine (the app is not AOT-published, but prefer
manual parsing anyway for symmetry with Core style).

- [ ] **Step 1: Failing tests.** Matrix (each row `[Arguments]`): `"0.12.0-beta.1"` → true;
  `"0.12.0-beta.2"` → true; `"0.12.0"` → true; `"0.12.1-beta.1"` → true; `"0.13.0"` → true;
  `"0.11.9"` → false; `"0.12.0-beta.0"` → false; `"0.12.0-alpha.9"` → false (alpha < beta);
  `"01.2.3"` → false (strict parse); `"0.12.0-beta.01"` → false (leading-zero prerelease
  numeric); `"0.12.0-"` → false; `"0.12"` → false; `""`/null/`"unknown"`/`"v0.12.0"` → false;
  `"0.12.0+build.5"` → true (build ignored); `"0.12.0-beta.1+x"` → true.
- [ ] **Step 2: Run, verify failure** (type absent).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, verify pass.** `TMPDIR=/private/tmp dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/KcapCliCompatibilityTests/*"`
- [ ] **Step 5: Commit** `feat(app): strict CLI compatibility floor 0.12.0-beta.1`

### Task 2: `ILoginShellProbe.KcapPathAsync` — validated path-returning resolver

**Files:**
- Modify: `src/Capacitor.App/Services/LoginShellProbe.cs`
- Test: `test/Capacitor.App.Tests.Unit/LoginShellProbeTests.cs` (extend)

**Interfaces:**
- Produces: `Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false)` on
  `ILoginShellProbe` + impl.
- Consumes: the existing probe script machinery (`-lic`/`-lc` attempts, sentinel parsing, caching).

**Contract:** Same probe transport as `KcapOnPathAsync` but the script runs `command -v kcap` and
returns raw output. Validation before returning: trim to first line; must be an absolute rooted
path (`Path.IsPathRooted` AND `Path.IsPathFullyQualified`) to an EXISTING regular file
(`File.Exists`; a directory fails). Alias output (`alias kcap=…`), function definitions
(multi-line), relative paths, and bare words → null. Null = no pin (callers fail closed).
Caching mirrors `KcapOnPathAsync` exactly: its own independent cache field, same "cacheable only if
both shell attempts completed" rule, `forceRefresh` bypass-and-repopulate. Symlinks are NOT
resolved here — invocation-time resolution is deliberate.

- [ ] **Step 1: Failing tests** using the existing fake-runner pattern in `LoginShellProbeTests`:
  absolute existing file → returned verbatim; absolute path with spaces → returned;
  relative `bin/kcap` → null; bare `kcap` → null; `alias kcap='/usr/local/bin/kcap'` → null;
  multi-line function definition → null; absolute path to a MISSING file → null; absolute path to
  a directory → null; cache: second call runs no new process; `forceRefresh: true` re-runs;
  process-start failure not cached.
- [ ] **Step 2: Run, verify failure.**
- [ ] **Step 3: Implement** (share the script-run/parse plumbing with `KcapOnPathAsync` via a
  private helper; do not duplicate the shell-attempt loop).
- [ ] **Step 4: Run, verify pass.**
- [ ] **Step 5: Commit** `feat(app): validated path-returning kcap resolver on the login-shell probe`

### Task 3: Process-only timeout kill mode on the runner

**Files:**
- Modify: `src/Capacitor.App/Services/IProcessRunner.cs`, `src/Capacitor.App/Services/DaemonClientService.cs` (nested `ProcessRunner`)
- Test: `test/Capacitor.App.Tests.Unit/ProcessRunnerTests.cs` (extend)

**Interfaces:**
- Produces: `public enum TimeoutKillScope { Tree, ProcessOnly }` and an additive `RunOptions`
  member `TimeoutKillScope TimeoutKill = TimeoutKillScope.Tree` (record default preserves every
  existing call site).
- Consumes: nothing new.

**Contract:** Only the INTERNAL-timeout kill path changes: on deadline expiry,
`TimeoutKillScope.Tree` → today's `Kill(entireProcessTree: true)`; `ProcessOnly` →
`Kill(entireProcessTree: false)`. `CancelMode` (caller-token) semantics are untouched —
`KillTree` cancellation still kills the tree regardless of `TimeoutKill` (cancel is the caller
saying "tear it down"; the timeout is the lane sparing the detached daemon).

- [ ] **Step 1: Failing test (real processes, POSIX-guarded
  `Skip.When(OperatingSystem.IsWindows(), …)`):** run `/bin/sh -c 'echo child-started; sleep 30 & echo $!; wait'`
  variant that prints the grandchild PID, with `Timeout = 500ms, TimeoutKill = ProcessOnly`;
  assert `TimedOut == true` and the printed grandchild PID is STILL ALIVE (`kill -0` via
  `Process.GetProcessById` not throwing), then clean it up (`kill`). Sibling test with
  `TimeoutKill = Tree` asserts the grandchild is dead. A third test pins that `CancelMode.KillTree`
  cancellation kills the tree even with `TimeoutKill = ProcessOnly`.
- [ ] **Step 2: Run, verify failure** (member absent).
- [ ] **Step 3: Implement** (one-line scope selection at the existing `KillAndAwaitAsync` call).
- [ ] **Step 4: Run, verify pass** (`ProcessRunnerTests` suite).
- [ ] **Step 5: Commit** `feat(app): process-only timeout kill scope on the runner`

### Task 4: `KcapCli` becomes the action-scoped executor

**Files:**
- Modify: `src/Capacitor.App/Services/KcapCli.cs` (+ `IKcapCli`)
- Test: `test/Capacitor.App.Tests.Unit/KcapCliTests.cs` (extend)

**Interfaces:**
- Produces: constructor gains `string? canonicalServer = null` (nullable — long-lived non-mutating
  instances pass null); `DetachedStartAsync(CancellationToken ct)` gains an overload
  `DetachedStartAsync(string bootAttemptId, CancellationToken ct)`; a new
  `public const string`s block for the overlay variable names.
- Consumes: Task 3's `TimeoutKillScope`.

**Contract (env overlays, per spawn kind):**
- EVERY spawn (version/status/mutations/detached): `KCAP_PROFILE` (existing) +
  `KCAP_APP_SPAWN_NO_TELEMETRY=1` (new — Plan A's `Program.cs` consumes-and-removes it).
- Daemon-mutating spawns (`ServiceInstallVerifiedAsync`, `ServiceStartVerifiedAsync`,
  `DetachedStartAsync`): additionally `KCAP_CONSENT_SEED_DEFAULT=prompt` and — when
  `canonicalServer` is non-null — `KCAP_EXPECT_SERVER_URL=<canonicalServer>`. A null
  `canonicalServer` on a mutation call throws `InvalidOperationException` (mutations are
  action-scoped; the lane always binds a server — constructing a mutating call without one is a
  programming error, not a runtime state).
- `DetachedStartAsync(bootAttemptId, ct)`: additionally `KCAP_BOOT_ATTEMPT=<bootAttemptId>`, a
  bounded `Timeout` (`DetachedStartTimeout = TimeSpan.FromSeconds(75)` — above the CLI's own
  60s mutation budget), and `TimeoutKill = TimeoutKillScope.ProcessOnly`. The parameterless
  overload delegates with a fresh `Guid.NewGuid().ToString("N")` (kept for the graph's
  non-lane callers until Task 10 removes them).
- `VersionAsync` args gain `--no-update-check` if not already present.

- [ ] **Step 1: Failing tests** (FakeProcessRunner captures `SeenOptions`): every existing call
  asserts `KCAP_APP_SPAWN_NO_TELEMETRY=1`; mutation calls assert seed + expectation overlays;
  status/version calls assert seed/expectation ABSENT; detached start asserts `KCAP_BOOT_ATTEMPT`
  present, `Timeout == 75s`, `TimeoutKill == ProcessOnly`, `CancelMode == AbandonWait`; mutation
  with null server throws; user env (`KCAP_TELEMETRY`) never touched (overlay contains only the
  documented keys).
- [ ] **Step 2: Run, verify failure.**
- [ ] **Step 3: Implement.** Update existing constructions (`App.axaml.cs`
  `BuildLifecycleController`, `DaemonClientService.CreateDefaultAsync` if it constructs one) to
  pass `canonicalServer: AppConfig.ResolvedProfile?.ServerUrl` canonicalized via
  `ServerIdentity.Canonicalize` — they may currently be non-mutating-only; pass the value anyway
  so Task 10's rewiring doesn't have to revisit signatures.
- [ ] **Step 4: Run full `KcapCliTests` + build the app project.**
- [ ] **Step 5: Commit** `feat(app): action-scoped env overlays and bounded detached start on KcapCli`

### Task 5: Outcome algebra + reason-line parsing

**Files:**
- Create: `src/Capacitor.App/Services/Mutation/MutationModel.cs`, `src/Capacitor.App/Services/Mutation/ReasonLine.cs`
- Test: `test/Capacitor.App.Tests.Unit/MutationModelTests.cs`, `test/Capacitor.App.Tests.Unit/ReasonLineTests.cs`

**Interfaces (produced, exact):**
```csharp
public enum MutationVerb { Install, Replace, StartVerified, DetachedStart }

public sealed record MutationRequest(
    MutationVerb Verb, string Profile, string CanonicalServer, string DaemonName);

public enum RecoverySurface { Takeover, Reinstall, Attention, Storage, None }

public abstract record MutationOutcome {
    public sealed record Succeeded : MutationOutcome;
    public sealed record SucceededAfterTimeout : MutationOutcome;
    public sealed record AttentionSkew(string Detail) : MutationOutcome;
    public sealed record AttentionRepair(string Detail) : MutationOutcome;
    public sealed record UnconfirmedNoAttach : MutationOutcome;
    public sealed record Refused(string Reason, RecoverySurface Surface) : MutationOutcome;
    public sealed record Failed(int ExitCode, string? Reason, RecoverySurface Surface) : MutationOutcome;
}

public sealed record OutcomeEnvelope(MutationRequest Request, MutationOutcome Outcome);

public static class ReasonRouting {
    public static RecoverySurface ForStartGate(string token);     // 28/29 start_gate_reason=
    public static RecoverySurface ForDaemonStart(string token);   // 43 daemon_start_reason=
    public static RecoverySurface ForBootRefusal(string token);   // refusal_reason= / marker token
}

public static class ReasonLine {
    // Exactly one line beginning with `prefix` (after trimming \r): its token.
    // Zero matching lines, two+ matching lines, or two lines with different tokens → null.
    public static string? TrySingle(string stderr, string prefix);
}
```

**Routing tables (pinned by spec §3/§4):**
- `ForStartGate`: `directive_missing|directive_invalid|identity_mismatch|foreign_binary` →
  `Takeover`; `package_inconsistent` → `Reinstall`; `evidence_unreadable` and ANY unknown →
  `Attention`.
- `ForDaemonStart`: `package_inconsistent` → `Reinstall`; unknown → `Attention`.
- `ForBootRefusal`: `server_expectation_mismatch` → `Takeover`; `consent_seed_unwritable` →
  `Storage`; `consent_seed_invalid` → `Takeover`; unknown → `Attention`.

- [ ] **Step 1: Failing tests.** `ReasonLine`: single match → token; zero → null; duplicate same
  token → null; two different tokens → null; matching line surrounded by unrelated stderr → token;
  prefix appearing mid-line (not line start) → not a match; `\r\n` input handled. `ReasonRouting`:
  every pinned token row + one unknown per table → `Attention`.
- [ ] **Step 2: Run, verify failure.**
- [ ] **Step 3: Implement** (pure; no I/O).
- [ ] **Step 4: Run, verify pass.**
- [ ] **Step 5: Commit** `feat(app): mutation outcome algebra and machine-readable reason routing`

### Task 6: App-side boot-refusal marker reader

**Files:**
- Create: `src/Capacitor.App/Services/Mutation/BootRefusalMarker.cs`
- Test: `test/Capacitor.App.Tests.Unit/BootRefusalMarkerTests.cs`

**Interfaces:**
- Produces:
```csharp
public sealed record BootRefusalEvidence(
    string DaemonName, string Token, string? Expectation, string? Resolved,
    int Pid, string? InstanceId, string? AttemptId);
public static class BootRefusalMarker {
    public static string MarkerPath(string daemonName);   // DaemonLockPaths.Directory/Sanitize(name)/boot-refusal.json
    public static BootRefusalEvidence? TryRead(string daemonName);   // absent/corrupt → null, left in place
    public static BootRefusalEvidence? TryAttribute(string daemonName, string attemptId); // read + AttemptId equality + identity fields non-empty; on match: consume (best-effort delete) and return; else null, marker untouched
}
```
- Consumes: `DaemonLockPaths.Directory`/`Sanitize` (Core). Mirror the CLI's
  `BootRefusalReader` duplication precedent (same on-disk member names; source-generated JSON ctx;
  the app never quarantines or clears a foreign marker — `TryAttribute` deletes ONLY on an
  attributed match, because the lane is that marker's single consumer for detached starts).

- [ ] **Step 1: Failing tests** (temp dir via `DaemonLockPaths.OverrideDirectoryForTesting`, the
  `[NotInParallel]` key from Global Constraints): valid marker + matching attempt → returned AND
  file deleted; valid marker + different attempt → null AND file retained; marker with null
  `attempt_id` → never attributed; corrupt JSON → null, file retained; absent → null; daemon-name
  mismatch inside the record (foreign evidence) → not attributed.
- [ ] **Step 2–4: red → implement → green.**
- [ ] **Step 5: Commit** `feat(app): boot-refusal marker reader with attempt-scoped attribution`

### Task 7: Leased single-consumer outcome channel

**Files:**
- Create: `src/Capacitor.App/Services/Mutation/OutcomeChannel.cs`
- Test: `test/Capacitor.App.Tests.Unit/OutcomeChannelTests.cs`

**Interfaces (produced, exact):**
```csharp
public sealed class OutcomeLease {
    public OutcomeEnvelope Envelope { get; }
    public void Ack();          // presentation reached the user — envelope permanently consumed
    public void CancelLease();  // consumer tore down before presentation — requeue exactly once
}
public sealed class OutcomeChannel {
    public void Enqueue(OutcomeEnvelope envelope);                       // producer side (lane)
    public IAsyncEnumerable<OutcomeLease> ConsumeAsync(CancellationToken ct); // ONE active consumer
    public void TransferConsumer();  // atomically completes the current consumer's enumeration;
                                     // the next ConsumeAsync call owns everything undelivered
}
```

**Contract:** FIFO. An envelope is LEASED at dequeue, not removed: `Ack` consumes;
`CancelLease` requeues at the FRONT exactly once (a second cancel of the same envelope after its
requeue-redelivery does NOT requeue again — track a per-envelope requeued flag); an envelope whose
lease is neither acked nor cancelled when the consumer's enumeration ends (ct fired or
`TransferConsumer`) is treated as lease-cancelled (requeue-once rule applies). Exactly one active
consumer: a second concurrent `ConsumeAsync` throws `InvalidOperationException`. Enqueue racing
`TransferConsumer` delivers to exactly one consumer (lock the handoff). No envelope is ever
dropped or duplicated: N enqueues → exactly N acks across all consumers, regardless of transfers.

- [ ] **Step 1: Failing tests:** two enqueued outcomes, waiterless → both surface in FIFO order
  exactly once; late enqueue after a consumer drained empty → wakes the live consumer; enqueue
  racing `TransferConsumer` (barrier via TaskCompletionSource ordering, not sleeps) → delivered to
  exactly one; dequeue-then-`CancelLease` → redelivered to the NEXT consumer exactly once;
  cancel-after-redelivery-ack → consumed, not duplicated; consumer ct cancellation with an
  unacked lease → requeued; second concurrent consumer throws; ack-after-transfer of an
  already-presented envelope → consumed, never requeued (the visible-dialog disposition).
- [ ] **Step 2–4: red → implement → green.** Implementation hint: a `Channel`-free custom queue
  (`LinkedList<Entry>` + `SemaphoreSlim`/TCS wakeup + one lock) is simpler to make
  transfer-atomic than `System.Threading.Channels`.
- [ ] **Step 5: Commit** `feat(app): leased FIFO outcome channel with atomic consumer transfer`

### Task 8: Observation adapters + identity on `AttachStatus`

**Files:**
- Create: `src/Capacitor.App/Services/Mutation/DaemonObservation.cs`
- Modify: `src/Capacitor.App/Services/AttachStatus.cs`, `src/Capacitor.App/Services/DaemonClientService.cs`
- Test: `test/Capacitor.App.Tests.Unit/DaemonObservationTests.cs`, extend `DaemonClientServiceTests`

**Interfaces (produced, exact):**
```csharp
// AttachStatus gains an additive member (default null — every existing construction compiles):
public sealed record AttachStatus(AttachState State, string? Reason,
    IReadOnlyList<string>? Capabilities, string? DaemonVersion = null,
    ConnectedIdentity? Identity = null);

public sealed record ObservedEvidence(
    bool Reachable, IReadOnlyList<string>? Capabilities, string? DaemonVersion,
    string? ServerUrl, string? DaemonName, int? Pid, string? InstanceId,
    bool IdentityConsistent);   // hello↔snapshot pid+instance agreement, BOTH sides present

public interface IDaemonObservation {
    Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct);
}
public sealed class OneShotObservation(TimeSpan timeout) : IDaemonObservation;      // LocalControlProbe
public sealed class LiveGraphObservation(IDaemonClientService client) : IDaemonObservation;
```

**Contract:** `OneShotObservation` calls `LocalControlProbe.ProbeAsync(request.DaemonName, …)` and
maps: unreachable → `ObservedEvidence(false, …)`; reachable → capabilities/version from hello,
server + name from `Snapshot.Daemon`, pid/instance from hello, `IdentityConsistent` from the
probe's own correlation. `LiveGraphObservation` first checks FULL identity: the client's pinned
`DaemonName == request.DaemonName` AND the current snapshot's server matches
`request.CanonicalServer` (`ServerIdentity.Matches`) — on ANY mismatch it returns null
(caller falls back to a one-shot; the graph's client cannot observe an arbitrary target). On match
it reads the CURRENT `AttachStatus` (must be `Connected` — else evidence is
`Reachable=false`) + latest snapshot, and `IdentityConsistent` requires `Identity` non-null with
pid+instance matching the snapshot's `Daemon.Pid`/`InstanceId`. In `DaemonClientService.Apply`,
thread `Connected.Identity` into the published `AttachStatus`.

- [ ] **Step 1: Failing tests.** DaemonClientService: a `Connected` event with identity → `Status`
  emission carries it. OneShot (scripted probe results via an injectable
  `Func<string, TimeSpan, CancellationToken, Task<ProbeResult>>` seam on the adapter): reachable
  consistent / reachable inconsistent / unreachable / pre-slice daemon (null pid+instance →
  `IdentityConsistent == false`). LiveGraph (fake `IDaemonClientService` — extract the needed
  members; the fake publishes controlled `AttachStatus`/snapshots): name mismatch → null; server
  mismatch → null; connected with matching identity → consistent evidence; connected with
  hello↔snapshot pid mismatch → `IdentityConsistent == false`; non-Connected → unreachable.
- [ ] **Step 2–4: red → implement → green.**
- [ ] **Step 5: Commit** `feat(app): daemon observation adapters with instance-bound evidence`

### Task 9a: `DaemonMutationLane` — skeleton (serialization, coalescing, probe, executor binding)

**Files:**
- Create: `src/Capacitor.App/Services/Mutation/DaemonMutationLane.cs`
- Test: `test/Capacitor.App.Tests.Unit/DaemonMutationLaneTests.cs`

**Interfaces (produced, exact):**
```csharp
public sealed class DaemonMutationLane : IAsyncDisposable {
    public DaemonMutationLane(
        IProcessRunner runner, ILoginShellProbe shellProbe, OutcomeChannel channel,
        Func<string?> cliOverride,                       // () => CliResolver override result (absolute or null)
        Func<MutationRequest, string?, IKcapCli> executorFactory,  // (request, pinnedPath) => action-scoped KcapCli
        Func<MutationRequest, IDaemonObservation> oneShotFactory,
        TimeProvider time);
    public void SetLiveAdapter(IDaemonObservation? live);   // atomic slot swap at graph build/teardown
    public Task<MutationOutcome> RunAsync(MutationRequest request, CancellationToken waiterCt);
    public Task QuiescedAsync(CancellationToken ct);
    public ValueTask DisposeAsync();
}
```

**Contract (this task delivers the mechanics; classification is Task 9b):**
- ONE owned action at a time. An arriving request equal to the IN-FLIGHT request (record equality)
  coalesces: it awaits the same owned task. A DIFFERENT request queues (FIFO) and, when admitted,
  performs its OWN fresh probe. Waiter cancellation detaches THAT waiter only (`WaitAsync(ct)`
  pattern) — the owned action always runs to its terminal state under the lane's lifetime token.
- Owned action sequence: (1) **pin**: `cliOverride()` if non-null else
  `await shellProbe.KcapPathAsync(ct, forceRefresh: false)`; null pin →
  `Refused("cli_not_found", Attention)`, no spawn. (2) **floor probe**: build the action executor
  ONCE via `executorFactory(request, pinnedPath)`; `VersionAsync` through it;
  `!KcapCliCompatibility.Satisfies(version)` → `Refused("cli_below_floor", Attention)`, no
  mutation. (3) **observation pin**: choose the observation strategy ONCE at action start — the
  live adapter slot if set AND `ObserveAsync` would target the matching identity (delegate the
  decision: try live, null → one-shot), pinned for the action's lifetime. (4) **mutation** through
  the SAME executor (verb-dispatched: `ServiceInstallVerifiedAsync(replace:…)`,
  `ServiceStartVerifiedAsync`, `DetachedStartAsync(attemptId, ct)` with a fresh
  `Guid` attempt id). (5) hand `ProcessResult` + context to the Task 9b classifier. (6) enqueue
  actionable outcomes on `channel` (all except `Succeeded`/`SucceededAfterTimeout`, which are
  waiter-state-only; waiterless success is logged); resolve all waiters with the outcome;
  quiesce; admit next.
- `QuiescedAsync` completes when no action is owned and the queue is empty.

- [ ] **Step 1: Failing tests** (fake runner scripted per verb; fake shell probe; recording
  executor factory): identical concurrent requests → ONE probe, ONE mutation, both waiters get
  the outcome; different queued request → second fresh probe after admission (probe count == 2,
  strictly after the first action's terminal state); waiter A cancels → B still completes, action
  uncancelled; ALL waiters cancel → action still reaches terminal state and its actionable
  outcome lands in the channel; null pin → `Refused(cli_not_found)`, zero runner invocations of
  the mutation verb; below-floor version → `Refused(cli_below_floor)`, no mutation call; probe
  in-flight blocks the mutation until resolved (TCS-gated fake); executor factory called exactly
  once per owned action with the pinned path (identical filename for version + mutation —
  asserted via the factory's recording); `QuiescedAsync` completes only after terminal state;
  CLI replaced between two actions (cliOverride returns pathA then pathB) → second action pins
  pathB (fresh pin per action).
- [ ] **Step 2–4: red → implement → green.**
- [ ] **Step 5: Commit** `feat(app): daemon mutation lane skeleton — serialization, coalescing, per-action pinning`

### Task 9b: `DaemonMutationLane` — classification & reconciliation

**Files:**
- Modify: `src/Capacitor.App/Services/Mutation/DaemonMutationLane.cs`
- Test: extend `DaemonMutationLaneTests.cs`

**Contract (the classifier, per verb):**
- **Service verbs (Install/Replace/StartVerified):** exit 0 → post-result observation;
  `Succeeded` requires successful process result AND observed identity match
  (`ServerIdentity.Matches(evidence.ServerUrl, request.CanonicalServer)` and
  `evidence.DaemonName == request.DaemonName`) AND `IdentityConsistent` AND ownership — the
  executor's `ServiceStatusAsync` reports `job_pid == daemon_pid`, both non-null (add
  `JobPid`/`DaemonPid`/`TxnActive`/`TxnMarkerStale` to the app's `ServiceSnapshot` if absent —
  additive parse of the existing `service status --json` fields). ANY missing/inconsistent leg →
  `AttentionSkew`, never `Succeeded`. Exit 28 → `ReasonLine.TrySingle(stderr, "start_gate_reason=")`;
  token → `Failed(28, token, ReasonRouting.ForStartGate(token))`; null (zero/dup/conflict) →
  `Failed(28, null, Attention)`. Exit 29 → `Failed(29, token?, Attention)` (never auto-retry).
  Readiness-timeout exits carrying a `refusal_reason=` line →
  `Refused(token, ReasonRouting.ForBootRefusal(token))`; without the line → `UnconfirmedNoAttach`.
  Other nonzero → `Failed(code, null, Attention)`.
- **DetachedStart:** exit 0 → bounded post-spawn observation window (poll the pinned observation,
  `DetachedConfirmWindow = 10s`, injectable via `TimeProvider`): full evidence (reachable,
  identity match, `IdentityConsistent`) → `Succeeded`; a boot-refusal marker attributed via
  `BootRefusalMarker.TryAttribute(request.DaemonName, attemptId)` →
  `Refused(token, ForBootRefusal(token))`; window expiry with neither → `UnconfirmedNoAttach`.
  Exit 43 → `ReasonLine.TrySingle(stderr, "daemon_start_reason=")` →
  `Failed(43, token, ForDaemonStart(token))` / null → `Failed(43, null, Attention)`.
  `ProcessResult.TimedOut == true` (the lane's own bounded wrapper timeout, process-only kill) →
  the SAME full-evidence observation: complete → `SucceededAfterTimeout`; marker attributed →
  `Refused`; else → `UnconfirmedNoAttach`.
- **Legacy/skew evidence:** observed `Capabilities` missing `"consent/3"`, or a pre-slice
  status/hello without pid+instance, or version below floor at observation time → `AttentionSkew`.
  Stale `txn_marker` / orphan-unit signals from `ServiceStatusAsync` → `AttentionRepair`.
- All evidence contributing to ONE classification must carry the SAME `InstanceId` (evidence is
  captured as one `ObservedEvidence` — the adapter already enforces hello↔snapshot; the ownership
  read's `daemon_pid` must equal `evidence.Pid` or the classification degrades to `AttentionSkew`).

- [ ] **Step 1: Failing tests** (scripted runner + scripted observation): the §10 lane matrix
  subset — mutation failure beside an already-Connected daemon → NOT `Succeeded`; wrong-server
  Connected → `AttentionSkew`; manual/non-owning Connected (job≠daemon pid) → not `Succeeded`;
  unreachable, no owner → `UnconfirmedNoAttach`; missing `consent/3` → `AttentionSkew`;
  pre-slice daemon (null pid/instance) → `AttentionSkew`, never `Succeeded`; pid-vs-daemon_pid
  cross-check failure → `AttentionSkew`; exit 28 with each routed token → `Failed(28,…)` with
  the pinned surface; 28 with zero AND with duplicate conflicting lines → `Failed(28, null,
  Attention)`; exit 29 → attention, and the test asserts no second mutation is attempted;
  exit 43 routed/unknown; readiness timeout with `refusal_reason=server_expectation_mismatch` →
  `Refused` with `Takeover`; detached exit-0 + attributed marker → `Refused` (marker consumed —
  file gone); detached exit-0 + FOREIGN marker (different attempt id) → `UnconfirmedNoAttach`,
  marker retained; detached wrapper `TimedOut` + full evidence → `SucceededAfterTimeout` (runner
  result shape asserted: the existing non-nullable `ProcessResult` with `TimedOut == true`);
  `TimedOut` + incomplete evidence → `UnconfirmedNoAttach`; success is waiter-state-only (channel
  stays empty); every actionable outcome enqueued exactly once with its `MutationRequest`.
- [ ] **Step 2–4: red → implement → green.**
- [ ] **Step 5: Commit** `feat(app): mutation lane outcome classification and detached reconciliation`

### Task 10: Route the three mutation surfaces through the lane

**Files:**
- Modify: `src/Capacitor.App/Services/DaemonLifecycleController.cs`,
  `src/Capacitor.App/Services/DaemonClientService.cs`, `src/Capacitor.App/App.axaml.cs`,
  `src/Capacitor.App/ViewModels/MainWindowViewModel.cs` (whatever calls `StartDaemonAsync`)
- Test: extend `DaemonLifecycleControllerTests` + `DaemonClientServiceTests`

**Contract:**
- `App.StartAsync` constructs ONE `DaemonMutationLane` FIRST (before any graph object) and
  disposes it last; the composition root drains `channel.ConsumeAsync` into the existing
  attention/status surfaces (`ILifecycleSurface`) routed by `RecoverySurface` (Takeover → the
  existing takeover-dialog path with its disclosures; Reinstall → reinstall guidance message;
  Attention/Storage → attention state; a `Refused`/`Failed` detail message always names the coded
  token). This root consumer starts immediately — in Plan B there is no wizard consumer yet, so
  no `TransferConsumer` call sites land here (the API is exercised in tests; Plan C adds the
  wizard handoff).
- `DaemonLifecycleController`: every mutating branch (auto-install, auto-start, `StartActionAsync`,
  takeover/replace) builds a `MutationRequest` from its pinned identity and awaits
  `lane.RunAsync` instead of calling `IKcapCli` mutation methods directly. Its OWN `_gate`
  continues to serialize decision-making; the lane serializes execution. Its result handling maps
  outcome cases onto the existing surface semantics — but per the channel contract, `RunAsync`
  results update controller STATE only (retry availability, `PhaseClosed`); actionable
  presentation flows through the channel consumer, so delete/bypass the controller's direct
  attention-raising for lane-executed mutations (single-presentation rule). `QuiescedAsync`
  awaits the lane too.
- `DaemonClientService.StartDaemonAsync` (main-window Start/Retry): delegate to
  `lane.RunAsync(new MutationRequest(DetachedStart, …))` via an injected
  `Func<CancellationToken, Task<MutationOutcome>>` — the service no longer spawns
  `daemon start -d` itself and loses the bare-`"kcap"` fallback; with no resolvable CLI the
  outcome is `Refused("cli_not_found")` surfaced as the honest message. Keep `RestartLoopAsync`
  kick on success outcomes (incl. `SucceededAfterTimeout`).
- The lane's live adapter: `SetLiveAdapter(new LiveGraphObservation(service))` right after the
  graph's `DaemonClientService` is constructed; `SetLiveAdapter(null)` at teardown.

- [ ] **Step 1: Failing tests:** controller auto-start routes through a fake lane (recording
  `MutationRequest`s — verb `StartVerified`, identity = pinned profile/server/name); main-window
  Start produces verb `DetachedStart` through the lane and NO direct runner spawn from
  `DaemonClientService` (fake runner records zero `daemon start` invocations); a lane
  `AttentionSkew` outcome does NOT raise the controller's direct attention surface (channel-only);
  `Refused(cli_not_found)` surfaces the honest not-found message; success still kicks
  `RestartLoopAsync`.
- [ ] **Step 2–4: red → implement → green.** Run the FULL app unit suite — this task touches the
  controller's existing matrix tests; adapt their fakes to the lane seam without weakening any
  existing assertion (each preserved assertion moves to the lane-request boundary).
- [ ] **Step 5: Commit** `feat(app): route lifecycle and main-window mutations through the mutation lane`

### Task 11: `ConsentFlipClaims` store

**Files:**
- Create: `src/Capacitor.App/Services/Onboarding/ConsentFlipClaims.cs`
- Test: `test/Capacitor.App.Tests.Unit/ConsentFlipClaimsTests.cs`

**Interfaces (produced, exact):**
```csharp
public sealed record ConsentFlipClaim(string Profile, string CanonicalServer);  // key = both, canonicalized

public sealed class ConsentFlipClaims(string path) {   // {config}/consent-flip-claims.json
    public static ConsentFlipClaims Default();          // PathHelpers.ConfigPath("consent-flip-claims.json")
    public IReadOnlyList<ConsentFlipClaim> Pending();               // read-only snapshot (lock, read, release)
    public bool Arm(ConsentFlipClaim claim);                        // upsert + durable flush; false = write failed
    public bool TryConsume(ConsentFlipClaim claim, Func<(string Profile, string Server, string DaemonName)> reResolveUnderConfigLock, string expectedDaemonName);
    public QuarantineState? Quarantine();                           // non-null once a corrupt file was set aside
    public sealed record QuarantineState(string PreservedPath);
}
```

**Contract:**
- File shape: `{"version":1,"claims":[{"profile":"…","server":"…"}]}` — source-generated ctx.
- EVERY mutation runs under `ConfigFileLock.Acquire(path)` (the claims file's own lock) as a
  synchronous critical section (callers `Task.Run` if async). `Arm`: read-fresh → upsert by key →
  unique temp write → flush file (`FileStream.Flush(flushToDisk: true)`) → rename → best-effort
  directory flush. Returns false on any failure (caller blocks the commit — decision 7).
- `TryConsume` is the two-lock conditional clear (spec §6): acquire the CONFIG lock
  (`ConfigFileLock.Acquire(AppConfig.GetConfigPath())`) FIRST, call `reResolveUnderConfigLock()`
  (re-reads config under that lock), and only if the re-resolved `{Profile, Server}` equals the
  claim's key AND the resolved daemon name equals `expectedDaemonName` — still holding config —
  acquire the claims lock and remove the key (same durable publication as `Arm`). Any mismatch or
  pre-rename failure → claim retained, return false. Post-rename durability failures → still
  consumed (return true) — a crash-resurrected key is safe (idempotent re-apply). Fixed lock
  order config → claims; no await anywhere inside.
- Corruption on ANY read: rename the file aside to `consent-flip-claims.quarantined-<n>.json`
  (first free `n`), start a FRESH empty store, record `QuarantineState`. Arming never rejects due
  to old corruption.

- [ ] **Step 1: Failing tests** (temp config dir per test; real `ConfigFileLock`): arm → durable
  file with the key; arm twice same key → one entry; two distinct identities → two keys, no
  clobber (concurrent arms via `Task.Run` race, both present after); consume with matching
  re-resolve → key removed; re-resolve returns a DIFFERENT daemon name → retained + false;
  re-resolve different server → retained; corrupt file → quarantined aside (original preserved
  with content intact), fresh store arms fine, `Quarantine()` non-null; write-failure injection
  (make the directory read-only, POSIX-guarded) → `Arm` false; a rename injected between capture
  and consume (simulated by mutating what `reResolveUnderConfigLock` returns) → retained.
- [ ] **Step 2–4: red → implement → green.**
- [ ] **Step 5: Commit** `feat(app): durable consent-flip claim store with two-lock conditional clear`

### Task 12: `ILocalControlOps.PutConsentPolicyV2Async` (Core)

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalControlOps.cs` (+ the `ILocalControlOps` interface)
- Test: `test/Capacitor.Cli.Tests.Unit/LocalIpc/LocalControlOpsV2PutTests.cs` (real-socket, Unix-guarded)
- Modify: `test/Capacitor.App.Tests.Unit/ScriptedLocalControlOps.cs` (add the scripted member)

**Interfaces:**
- Produces: `Task<ConsentAckDto> PutConsentPolicyV2Async(ConsentPolicyPutV2Dto put, CancellationToken ct)`
  — sends `FrameType.ConsentRulesPutV2` on a fresh connection (same connect/one-frame/read-ack
  pattern as the existing `PutConsentPolicyAsync`), returns the ack verbatim. Transport failures
  throw (callers map to "retry later"); a decoded ack with `Ok == false` returns normally.

- [ ] **Step 1: Failing tests** against the real daemon-side handler (the existing real-socket
  harness pattern used by Plan A's `HandleRulesPutV2Async` tests — reuse its server fixture):
  identity match → applied (follow-up `GetConsentPolicyAsync` sees it) + `Ok == true`; name
  mismatch → `Ok == false, Error == "identity_mismatch"`, policy unchanged; server mismatch →
  same. Scripted fake: `QueuePutV2(...)`/capture list mirroring the existing per-verb pattern.
- [ ] **Step 2–4: red → implement → green.** AOT publish both binaries — zero IL warnings.
- [ ] **Step 5: Commit** `feat(core): identity-conditional consent rules put (v2) on local control ops`

### Task 13: `ConsentFlipCoordinator` + quarantine ack in app-state

**Files:**
- Create: `src/Capacitor.App/Services/Onboarding/ConsentFlipCoordinator.cs`
- Modify: `src/Capacitor.App/Services/AppStateStore.cs` (`AppState` gains
  `bool ConsentQuarantineAcked = false`)
- Test: `test/Capacitor.App.Tests.Unit/ConsentFlipCoordinatorTests.cs`

**Interfaces:**
```csharp
public sealed class ConsentFlipCoordinator(
    IDaemonClientService client, ILocalControlOps ops, ConsentFlipClaims claims,
    Func<(string Profile, string Server, string DaemonName)> resolveIdentityUnderConfigLock,
    ILifecycleSurface surface, IAppStateStore appState, CancellationToken lifetime) {
    public void Start();   // subscribes client.Status
}
```

**Contract (spec §6, coordinator path):** On each transition to `Connected` with
`Capabilities` containing `"consent/3"`: snapshot `claims.Pending()`; for a claim whose
`{Profile, CanonicalServer}` matches the CURRENT resolved identity (resolved via the injected
resolver — one plain read, NOT yet under lock): (1) resolve target daemon name from current
config; (2) `GetConsentPolicyAsync` → FACTORY GUARD: proceed only if default is `allow` with zero
rules (any other policy → claim left pending and inert — NOT consumed); (3)
`PutConsentPolicyV2Async(new(name, claim.CanonicalServer, promptPolicy))`; ack `Ok == false` (any
error, incl. `identity_mismatch`) → claim retained, stop; (4) on success,
`claims.TryConsume(claim, resolveIdentityUnderConfigLock, name)` — the two-lock conditional
clear; a false return leaves it pending for the next graph. Missing `consent/3` → no put, claim
pending. All of this single-flight (one pass per Connected transition; a `SemaphoreSlim(1,1)`
like `ConsentService`). Quarantine surfacing: on `Start()`, if `claims.Quarantine()` is non-null
and `AppState.ConsentQuarantineAcked` is false → surface ONE attention state naming the preserved
path + the recovery guidance ("pre-existing daemons may need `kcap daemon consent set-default
prompt`, or re-run onboarding"); acknowledging persists the flag.

- [ ] **Step 1: Failing tests** (fake client Status subject + `ScriptedLocalControlOps` +
  scripted resolver): Connected+capability+matching claim+factory-allow-zero-rules → get, v2 put
  with `{resolved name, claim server}`, consume called; policy with rules → NO put, claim
  pending; default `prompt` → no put; missing capability → nothing; `identity_mismatch` ack →
  claim retained; put success + resolver now returns a renamed daemon at consume time → claim
  retained (the §10 rename-injection rows: after resolve / after get / after put); non-matching
  `{profile, server}` claim → inert; quarantine surfaced once, ack persisted → not re-surfaced
  on restart (fresh coordinator + acked state).
- [ ] **Step 2–4: red → implement → green.**
- [ ] **Step 5: Commit** `feat(app): consent-flip coordinator with factory guard and conditional clear`

### Task 14: `auth_provider` stamp (Core) + `OnboardingGate`

**Files:**
- Modify: `src/Capacitor.Cli.Core/Config/ProfileConfig.cs` — `Profile` gains
  `[JsonPropertyName("auth_provider")] public AuthProviderStamp? AuthProvider { get; init; }`
  with `public sealed record AuthProviderStamp([property: JsonPropertyName("provider")] string Provider, [property: JsonPropertyName("server_url")] string ServerUrl)`
  (additive, serialization ctx updated; nothing WRITES it in Plan B — the wizard's commit boundary
  writes it in Plan C).
- Create: `src/Capacitor.App/Services/Onboarding/OnboardingGate.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` — `ValidProfileName`'s URL validity check delegates to
  the same validator the gate uses.
- Test: `test/Capacitor.App.Tests.Unit/OnboardingGateTests.cs`, `test/Capacitor.Cli.Tests.Unit/Config/ProfileAuthProviderStampTests.cs`

**Interfaces:**
```csharp
public abstract record GateResult {
    public sealed record Complete : GateResult;
    public sealed record Incomplete(GateReason Reason) : GateResult;
}
public enum GateReason { NoProfile, InvalidServerUrl, NoToken, TokenUnusableBinding, TokenUnusableExpired }
public static class OnboardingGate {
    public static bool ValidServerUrl(string? url);   // the ONE shared validator: canonical http(s) via ServerIdentity
    public static Task<GateResult> EvaluateAsync(CancellationToken ct);
}
```

**Contract (decision 1 — local, side-effect-free, no refresh):** resolve profile
(`AppConfig.ResolveActiveProfile([])`); no resolvable profile → `NoProfile`. Profile's
`server_url` must canonicalize to an absolute `http`/`https` origin (`ValidServerUrl` — rejects
`file://`, bare words; built on `ServerIdentity.TryCanonicalizeForStamping` restricted to
http/https) → else `InvalidServerUrl`. Provider stamp: if `profile.AuthProvider` is
`{Provider: "none"}` AND `ServerIdentity.SameServer(stamp.ServerUrl, profile.ServerUrl)` →
`Complete` (no token needed); a stale stamp (server differs) is ignored (token required). Token:
`TokenStore.LoadForProfileAsync(profileName, ct)` (raw, refresh-free); null/unreadable/corrupt →
`NoToken`; token present but its `ServerUrl` binding fails `SameServer` against the profile →
`TokenUnusableBinding`; unexpired → `Complete`; expired → refresh-capable ⇒ `Complete` where
GitHubApp is ALWAYS refresh-capable and WorkOS requires BOTH `RefreshToken` and `ClientId`, else
`TokenUnusableExpired`. Legacy unbound token (null `ServerUrl` stamp): treat as usable binding
(pre-upgrade tokens carry no stamp — matches `TokenStore`'s own leniency) — pin this with a test
either way after reading `TokenStore.SameServer` usage; if `TokenStore` treats unbound as
usable-for-any, the gate must agree (decision 1: "matching TokenStore's real refresh rules").

- [ ] **Step 1: Failing tests** — the full §10 gate matrix: no profile / invalid URL incl.
  `file://` (via the SHARED validator — one test asserts `App.ValidProfileName` and the gate
  reject the same `file://` input) / no token file / WorkOS expired with both refresh fields
  (Complete) / WorkOS expired missing `ClientId` (TokenUnusableExpired) / GitHubApp expired
  (Complete) / wrong-server token (TokenUnusableBinding) / legacy unbound token (pinned per
  `TokenStore` semantics) / corrupt token file (NoToken) / stamp `none` matching server
  (Complete, no token file at all) / stale `none` stamp after `server_url` change (NoToken) /
  legacy profile without stamp (token required). Core test: `auth_provider` round-trips through
  serialization and is absent-by-default (old configs load with null).
- [ ] **Step 2–4: red → implement → green.** AOT publish both CLI binaries (Core changed).
- [ ] **Step 5: Commit** `feat: onboarding gate with provider-aware token usability and shared URL validator`

### Task 15: Startup carve-out wiring (gate-aware graph, no wizard yet)

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs`, `src/Capacitor.App/Services/DaemonLifecycleController.cs`
  (constructor gains `bool autoActionsPermanentlyClosed = false`),
  `src/Capacitor.App/Services/ShimOfferCoordinator.cs` (suppression flag or simply not started)
- Test: `test/Capacitor.App.Tests.Unit/AppStartupCarveOutTests.cs` (headless where needed)

**Contract (decision 2's carve-out, WITHOUT wizard-first windowing — that lands in Plan C):**
`App.StartAsync` evaluates `OnboardingGate.EvaluateAsync()` FIRST. `Complete` → today's startup
exactly. `Incomplete` → the graph is still built (Plan C replaces this arm with wizard-first),
but: the lifecycle controller is constructed with `autoActionsPermanentlyClosed: true` — its
startup auto-action branches (auto-install/auto-start/silent repair) never fire, `PhaseClosed`
still completes on the first terminal attach, user-clicked `StartActionAsync` still works — and
the `ShimOfferCoordinator` auto-offer is not started (tray manual shim item keeps working). The
`DaemonMutationLane` is constructed before the gate evaluation either way (app-lifetime
singleton). Mark the `Incomplete` arm with a comment: `// Plan C replaces this arm with wizard-first startup (spec decision 2).`

- [ ] **Step 1: Failing tests:** gate-incomplete → controller constructed with auto-actions
  closed (no mutation request reaches the lane on a fresh-machine fixture: valid URL + no token,
  attach unreachable) AND shim auto-offer never starts; gate-complete → auto-actions armed as
  today (existing startup-matrix tests keep passing); user-clicked Start still routes a
  `DetachedStart` request through the lane in the carve-out mode. Assert ZERO service mutations
  for: valid URL + no token; invalid/non-HTTP URL (the two §10 carve-out rows applicable without
  a wizard).
- [ ] **Step 2–4: red → implement → green.** Run the FULL app suite + both AOT publishes + the
  full CLI/daemon unit + integration suites (`TMPDIR=/private/tmp`).
- [ ] **Step 5: Commit** `feat(app): gate-aware startup carve-out — auto-actions closed on incomplete setup`

### Task 16: Project docs + final verification sweep

**Files:**
- Modify: `CLAUDE.md` (the "What this project does" section gains a short Plan-B paragraph:
  app-side `DaemonMutationLane` singleton, `ConsentFlipClaims`/`ConsentFlipCoordinator`,
  `OnboardingGate` + carve-out, pointing at the spec)
- No README change (no user-visible CLI surface changed).

- [ ] **Step 1:** Write the CLAUDE.md paragraph (match the existing sections' density; ≤ 12 lines).
- [ ] **Step 2:** Full verification: app unit suite, CLI unit suite, integration suite, both AOT
  publishes (zero IL warnings), `dotnet build Capacitor.slnx` clean (IDE0005 is error-enforced).
- [ ] **Step 3: Commit** `docs: record plan-B app substrate in CLAUDE.md`

---

## Explicitly deferred to Plan C (do NOT build here)

Wizard UI (`OnboardingWindow`, step ViewModels), `WizardAuthService`, the Core auth façade (§5)
and `setup`/`login` re-plumb, `KcapCli.PluginInstallAsync` + streaming import (§7), the
`auth_provider` stamp WRITE path, wizard-first startup windowing + wizard-close graph handoff
(`TransferConsumer` call sites), claim ARMING from the commit boundary (Plan B arms only in
tests), `AgentDetection` app feed. The §10 rows covering those flows land with Plan C.

## Self-review notes

- Task ordering is dependency-clean: 1–8 are leaves; 9a needs 1,2,4,5,7,8; 9b needs 6; 10 needs
  9b; 13 needs 11,12; 15 needs 10,14; 16 last.
- Interfaces named in later tasks are defined in earlier ones (checked: `MutationRequest`/
  `MutationOutcome`/`OutcomeChannel`/`ObservedEvidence`/`KcapCliCompatibility.Satisfies`/
  `KcapPathAsync`/`TimeoutKillScope`/`BootRefusalMarker.TryAttribute`).
- Every task carries its own red → green cycle and commit; no placeholders remain.
