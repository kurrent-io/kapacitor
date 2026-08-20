# Test parallel safety: exclusion where state is global, identity where a pid is involved

**Date:** 2026-08-21
**Status:** Implemented (rides its implementation PR)
**Repos:** kcap-cli
**Issue:** AI-2145 (kurrent-io/kcap-cli#634), under AI-1977

## Problem

`CLAUDE.md` currently tells everyone to run one whole suite one test at a time:

> **`Capacitor.Cli.Daemon.Tests.Unit` is not parallel-safe** — it spawns real processes, unix
> sockets and PTYs, and around 39 of its tests collide concurrently. Run it the way CI does, or it
> fails for reasons that have nothing to do with your change:
> `dotnet run --project … -- --maximum-parallel-tests 1`
> A TUnit assembly-level `[ParallelLimiter<T>]` is **not** a substitute: it serialises by wall-clock
> but still leaves `CodexLauncherTests` failing intermittently, where the command-line flag does not.

That guidance costs every local run 2.4–2.9× wall clock, and it is unsound on its own terms: the
flag does not serialise the suite, and the tests it is supposed to protect are still racing under
it. Meanwhile the defect it papers over — a handful of tests mutating process-global state behind a
*keyed* guard that cannot exclude the tests that read it — stays in the tree, invisible.

## Findings

All measurements on this machine (Darwin 25.6.0, 16 logical cores → TUnit default width 64), TUnit
1.65.31, `Capacitor.Cli.Daemon.Tests.Unit` at 2763 tests.

### F1 — bare `[NotInParallel]` is already exclusive against *everything*

`TUnit.Engine` groups tests into four buckets (`GroupTestsByConstraintsCore`): `Parallel`,
`KeyedNotInParallel`, `ParallelGroups`, and the unkeyed `NotInParallel`.
`TestScheduler.ExecuteAllPhasesAsync` runs `(Parallel ∥ Keyed)`, then parallel groups, then
constrained groups, and only then drains the unkeyed bucket through `ExecuteSequentiallyAsync` —
one test at a time. On top of the phase order sits `NotInParallelLock`, a writer-preferring async
reader/writer lock: every test enters it *shared*, an unkeyed-`NotInParallel` test enters it
*exclusive*, and `TestScheduler` enables it as soon as grouping reports any such test. Both paths
funnel through `TestRunner.ExecuteTestInternalAsync`, so keyed and parallel tests alike hold the
read side. The lock exists because `[DependsOn]` can drag a bucket member into another phase.

Three independent confirmations:

- A throwaway TUnit 1.65.31 probe (46 tests: 24 parallel, 6+6 keyed on two keys, a 4-test
  `[ParallelGroup]`, bare at method and class level) recorded a start/end timeline. Bare tests
  overlapped **0** peers; every other bucket overlapped as expected.
- The daemon suite's own report JSON, full parallelism: every one of the 16 bare-marked tests that
  ran on this platform showed **0** peers inside its window (the 17th is Linux-only and did not
  run). `PtySpawnTests` — the class the previous PR marked — is among them.
- Keyed tests in the same run overlapped 14–98 peers each.

One exception, from the same grouping code: a test carrying `[ParallelGroup]` *as well as* bare
`[NotInParallel]` lands in `ConstrainedParallelGroups`, and only the unkeyed bucket gets
`RequiresGlobalNotInParallelLock` set (`MarkGlobalNotInParallelTests`) — so that combination is not
globally exclusive. Nothing in the repo uses `[ParallelGroup]` today, but the guard in §3 checks for
it rather than assuming that stays true.

**Consequence: the requirement "bare `[NotInParallel]` excludes every other test, including keyed
ones and unmarked ones" needs no implementation. It is TUnit's behaviour today.** What it costs is
tail latency: bare tests run last, sequentially. Measured tail for all 36 process-global-state tests
in the suite: **1.55s** of a 67s wall clock.

### F2 — a method-level constraint silently shadows a class-level one

Grouping takes the *first* `NotInParallelConstraint` in `TestContext.ParallelConstraints`, and the
method-level attribute comes first. Probe, both directions:

| Attributes | Bucket it landed in |
|---|---|
| class `[NotInParallel]` + method `[NotInParallel("A")]` | keyed (ran in the keyed phase, before the parallel groups) |
| class `[NotInParallel("A")]` + method `[NotInParallel]` | unkeyed (ran last, alone) |

`Services/LocalPermissionBridgeTests.cs:15` has exactly the first shape: a class-level bare
`[NotInParallel]`, then `[NotInParallel(nameof(LocalPermissionBridgeTests))]` on ~70 test methods.
The class-level attribute is dead.

### F3 — `--maximum-parallel-tests 1` does not serialise a suite

`GetMaxParallelism` feeds only `ExecuteWithGlobalLimitAsync`'s `Parallel.ForEachAsync`, i.e. the
unconstrained bucket. `ConstraintKeyScheduler.ExecuteTestsWithConstraintsAsync` has no global-limit
semaphore at all: it starts every key-disjoint test immediately, and the keyed phase runs
concurrently with the parallel phase.

Measured, same suite, `--maximum-parallel-tests 1`:

- peak simultaneous tests: **4**, not 1;
- `CodexLauncherTests`' `HOME` window still overlapped **17** other tests;
- `CodexHostedAgentRuntimeFactoryTests.Active_review_flow…`' window overlapped **48**.

So the flag narrows the race window; it does not close it. It is a probability reducer that reads
like a guarantee.

### F4 — the suite is green at full parallelism

Four consecutive full-parallelism runs: **2763 tests, 0 failed**, 67s / 73s / 84s / 90s. The same
suite under the documented invocation: **3m 13s**. The SIGABRT that motivated the guidance came
from `PtySpawnTests`, which the same PR fixed with a bare `[NotInParallel]`; the flag was never what
held the suite together. The "around 39 tests collide" figure is best read as the count of tests
*touching* process-global state (a static inventory finds 36), not tests that fail.

### F5 — what actually collides: 36 tests, four hazard kinds

Inventory by scanning test bodies for `Environment.SetEnvironmentVariable`, `WorktreeManager.Snapshot*`
static seams, `ConsoleOutput.Start*`, raw `waitpid`, and raw `kill`, cross-referenced with each
test's real overlap window from the report JSON:

| Hazard | Tests | Guard today | Peak peers in window |
|---|---|---|---|
| Console capture | 3 (`BootRefusalTests`) | bare | 0 |
| Prod static seam (`WorktreeManager.Snapshot*`) | 4 (`StandaloneSnapshotTests`) | bare | 0 |
| Env var (`PATH`, `GIT_CONFIG_*`, `ANTHROPIC_API_KEY`) | 6 | bare | 0 |
| Raw `waitpid` | 4 (`PtySpawnTests`) | bare | 0 (3 of the 4 need no guard — see F6) |
| Env var (`HOME`, `CODEX_HOME`, `GEMINI_CLI_HOME`) | 11 | keyed `"HomeEnvVarMutation"` | 16–98 |
| Env var (`KCAP_DAEMON_SUPERVISED`) | 5 (`DeliberateRefusalExitTests`) | keyed | 14–39 |
| pid liveness poll on a foreign pid | 3 | none | 27–41 |

The keyed guards are unsound because the *readers* are not in the cohort: 18 sites in `src/` read
`HOME` or the user profile, and 15 files in this suite spawn real `git`, which inherits it. A keyed
constraint excludes only tests carrying the same key, so a `HOME` redirect is visible to every
concurrent peer that spawns a child or calls a path helper. `KCAP_DAEMON_SUPERVISED` has the same
shape via `SupervisionDetector`.

The scan also *undercounts*: `CodexConfigWriterTests` mutates `HOME` through a static `ScopedHome`
helper, so a body-level grep does not see it. Any static-analysis approach to this problem inherits
that blind spot; a runtime check does not.

### F6 — the pid hazards split three ways, and only one is dangerous

- **Reaping our own unreaped child is safe.** Three of `PtySpawnTests`' four `waitpid` calls kill a
  live `sleep`, or reap `sleep 0`'s corpse. A terminated child stays a zombie holding its pid number
  until its parent reaps it, so the number cannot be reassigned in between. (This would not hold if
  the test host were PID 1, where .NET reaps every child — it is not, and if it were, these cleanup
  `waitpid`s would already be failing.)
- **One call waits on a pid we no longer own.**
  `PtySpawnTests.Child_side_exec_failure_reports_failed_step_exec_and_reaps_cleanly` asserts
  `waitpid == -1` on a pid the shim has *already* reaped (`Native/pty_shim.c:573 reap_blocking` runs
  on the child-side-error cleanup path), so the number is free. This is the call that can steal a
  recycled `System.Diagnostics.Process` child's status and FailFast the host.
- **Three liveness polls assert on pids we never owned** — a grandchild reparented to init and
  reaped by it: `UnixPtyProcessSpawnTests.Terminate_kills_the_leaders_whole_process_group`, and
  `UnixSpawnerThreadTests`' `Pdeathsig_kills_the_child_when_the_spawner_process_dies` and
  `Agent_survives_unrelated_pool_thread_churn_while_the_thread_lives`. `kill(pid, 0)` is wrong in
  both directions here: a recycled pid reads as "still alive" (false failure in the two
  death-assertions), and in the churn test's *positive* assertion a recycled pid reads as alive when
  the agent has actually died — a false pass.
- **Windows has the same defect.** `Pty/Windows/ConPtyJobObjectTests.cs:112`,
  `IsProcessAlive(int pid) => Process.GetProcessById(pid)`, polled after the job kill to prove the
  child and grandchild are gone. Windows-only, so no local run here touches it, and CI's serial flag
  masks it.

The production code already solves this problem exactly: `ProcessStartToken` (public, Core) yields a
token for a process *incarnation* — `lx:<boot_id>:<starttime-ticks>` from `/proc/pid/stat` field 22
on Linux, `mac:<bootsessionuuid>:<p_uniqueid>` on macOS (a kernel counter never reused within a boot
session), `tk:<absolute StartTime ticks>` on Windows — with a tri-state `Matches` that separates "a
different incarnation" from "cannot compare". `ProcessIdentity` (daemon, internal;
`Capacitor.Cli.Daemon.Tests.Unit` has `InternalsVisibleTo`) wraps it for the reapers.

### F7 — the CI flag is masking a defect in another suite

Found while verifying this change, not by looking for it: at full parallelism
`Capacitor.Cli.Tests.Unit`'s `GitProviderRouterTests.Probe_result_is_memoized_per_host` fails
(`Expected 1, found 0`). The router memoizes into a production static, and the class's
`[Before(Test)]` `ResetMemoForTests()` is itself a process-global mutation, so a concurrent peer's
reset — or its memo entry under the same host — decides what the test observes. CI's
`--maximum-parallel-tests 1` is why nobody has seen it. Fixed here (class-level bare
`[NotInParallel]`, per §1's rule) because leaving a flake I had just reproduced would be worse than
the scope creep; the rest of that suite is still unaudited (see Non-goals).

## Goals

- Make the exclusion guarantee load-bearing where state is genuinely process-global, and enforced
  rather than documented.
- Replace pid-based liveness assertions with identity-based ones, so they are correct instead of
  merely serialised.
- Delete the whole-suite serial invocation from `CLAUDE.md`, and state what the CI flag does and
  does not buy.
- Keep the local daemon-suite loop at its full-parallelism wall clock (~70s).

## Non-goals

- Changing CI. `--max-parallel-test-modules 1` (cross-assembly filesystem sharing) and
  `--maximum-parallel-tests 1` both stay in `ci.yml` for now; this design makes the second one
  unnecessary for the daemon suite but does not audit the other three.
- Auditing `Capacitor.Cli.Core.Tests.Unit`, `Capacitor.Cli.Tests.Unit`,
  `Capacitor.Cli.Tests.Integration` or `Capacitor.App.Tests.Unit`. The 32 classes repo-wide carrying
  `NotInParallel("HomeEnvVarMutation")` keep working unchanged.
- Removing the `HOME` mutation altogether by threading a `home` argument through
  `CodexLauncher.Prepare` (see Follow-ups).
- An assembly-level `[ParallelLimiter<T>]` for the daemon suite. No CPU-width failure appeared in
  four runs; the 23 per-class limiters stay as they are.

## Design

### 1. The rule, as `CLAUDE.md` will state it

Three bullets replace the two added by #627 (the `waitpid` bullet and the "not parallel-safe" block):

- **Bare `[NotInParallel]` is exclusive against the whole assembly** — the parallel bucket, every
  keyed constraint, and every `[ParallelGroup]` — and its tests run last, one at a time. Use it for
  state that is process-global: an environment variable, `Console`, a mutable static in production
  code, the working directory.
- **Keyed `[NotInParallel("k")]` only excludes tests carrying `k`.** It is sound only when every
  *reader* of the shared thing is in the cohort too, not just every writer. An env var fails that
  test the moment any concurrent peer spawns a child (it inherits) or calls a path helper (it
  reads).
- **A method-level `[NotInParallel(…)]` shadows a class-level one** — the first constraint wins, and
  the method's comes first. Never carry both.

And, in place of the serial invocation: the daemon suite runs green at full parallelism
(`dotnet run --project …`); CI's `--maximum-parallel-tests 1` caps only the unconstrained bucket —
keyed tests bypass it entirely — so it is not a parallel-safety guarantee for anything.

The `waitpid` bullet is replaced by the sharper rule from F6: reaping a child you forked and have
not yet reaped is safe, because the zombie holds the pid; what needs care is asserting anything
about a pid you no longer own, and the fix there is identity, not exclusion.

### 2. Marking: env mutation becomes exclusive

Five classes, promoted from keyed to bare. Where a class mutates in a helper (so every test is
affected) the attribute goes on the class; where only some tests mutate it goes on those methods, and
the class-level key is deleted rather than promoted.

| File | Scope of the change | Variable |
|---|---|---|
| `Harness/Codex/CodexLauncherTests.cs` | delete class key; bare on the 6 `Prepare_*` tests | `HOME` |
| `Harness/Codex/CodexHostedAgentRuntimeFactoryTests.cs` | 4 method keys → bare | `HOME`, `CODEX_HOME` |
| `Harness/Codex/CodexConfigWriterTests.cs` | class key → class bare (`ScopedHome` helper) | `HOME` |
| `Harness/Antigravity/AntigravityReviewerHomeTests.cs` | 1 method key → bare | `GEMINI_CLI_HOME` |
| `DeliberateRefusalExitTests.cs` | class key → class bare | `KCAP_DAEMON_SUPERVISED` |

Three attributes are deleted outright, not promoted:

- `Harness/Codex/CodexAppServerLaunchArgsTests.cs` — carries the `HomeEnvVarMutation` key but never
  mutates; it only *reads* `CodexPaths.Home`. Once every writer is exclusive, readers need nothing.
- `Services/AcpHostedAgentRuntimeFactoryTests.cs:1857` — same, a reader comparing a snapshotted
  `HOME` to a live read.
- `Services/LocalPermissionBridgeTests.cs:15` — the dead class-level bare from F2. The ~70
  method-level keys stay: the bridge's loopback listener is a test-owned resource with an
  enumerable cohort (`AgentOrchestrator*` tests share the key), which is what keyed is for.

### 3. Enforcement: `EnvScope` checks the constraint it needs

`test/Capacitor.Tests.Helpers/EnvScope.cs` exists already (save/restore around one variable, used by
8 classes in `Capacitor.Cli.Tests.Unit`) and its remarks *ask* for `[NotInParallel]` without
checking. It grows a check and one factory:

```csharp
public sealed class EnvScope : IDisposable {
    // Requires the current test to carry SOME NotInParallel constraint (keyed or bare).
    public EnvScope(string key, string? value);

    // Requires an UNKEYED constraint: for variables read outside any enumerable cohort
    // (inherited by spawned children, or read by production path helpers).
    public static EnvScope Exclusive(string key, string? value);
}
```

The check reads the same thing the engine reads, and picks the same winner:

```csharp
static NotInParallelConstraint? ResolvedConstraint() =>
    TestContext.Current?.Parallelism.Constraints
        .OfType<NotInParallelConstraint>()
        .FirstOrDefault();   // first-wins, exactly like GroupTestsByConstraintsCore
```

Semantics:

| Situation | `new EnvScope(…)` | `EnvScope.Exclusive(…)` |
|---|---|---|
| no `NotInParallel` at all | throw | throw |
| keyed constraint | pass | throw |
| unkeyed constraint | pass | pass |
| class-level bare shadowed by a method-level key | throw (for `Exclusive`) — the shadow is visible because we resolve first-wins | as stated |
| `TestContext.Current` is null (assembly-level hook, module initializer) | throw | throw |

Throwing on a null context is deliberate: process-wide env pinning that must happen before any test
(`RepoPathStoreGlobalSetup`'s `[ModuleInitializer]`) is not what this type is for and keeps using
raw `Environment.SetEnvironmentVariable`. There is no `Unchecked` escape hatch — an unchecked default
would defeat the point.

Messages name the fix rather than the symptom, e.g. for the keyed case:

> `HOME` is process-global: every child process this suite spawns inherits it and production path
> helpers read it, so a keyed `[NotInParallel("HomeEnvVarMutation")]` cannot exclude the tests that
> observe it. Mark this test bare `[NotInParallel]`.

The 8 existing users in `Capacitor.Cli.Tests.Unit` are all keyed and keep working through the
constructor, unchanged. In the daemon suite all 24 promoted tests adopt `EnvScope.Exclusive` and
lose their hand-rolled `try/finally` — 16 from F5's keyed rows plus `CodexConfigWriterTests`' 8,
which F5's body-level scan missed behind the `ScopedHome` helper — and the 6 already-bare env tests
adopt it too, so the guard covers every env mutation in the suite.

Guard tests (in the daemon suite, next to the convention they enforce): a bare test succeeds and
restores the previous value; a keyed test calling `Exclusive` throws; an unmarked test calling the
constructor throws.

### 3b. Not landed: fail the suite on a raw env mutation in test sources

The runtime guard only covers code that goes *through* `EnvScope`. A source-scan test — walking the
daemon suite's own `.cs` files via `RepoTree.Root()` and failing on any
`Environment.SetEnvironmentVariable` — would close the bypass for new code. Written and green, then
dropped from this change as severable; the bypass stays open, and it is listed under Follow-ups.

### 4. `PidIdentity`: identity instead of a pid number

A small addition to `test/Capacitor.Tests.Helpers/`, over the public token, uniform across
platforms:

```csharp
public static class PidIdentity {
    // The incarnation token for a live pid. Throws if it cannot be read: arming a watch on an
    // unreadable identity would silently degrade to the pid-only check we are removing.
    public static string Capture(int pid);

    // True once the pid no longer carries `identity`: gone, reaped, or reassigned.
    public static bool IsGone(int pid, string identity);

    // Polls IsGone to a deadline; on timeout throws naming pid, identity and elapsed time.
    public static Task WaitUntilGoneAsync(int pid, string identity, TimeSpan timeout);
}
```

`IsGone` is `ProcessStartToken.ForPid(pid)` being null (the pid left the process table) or carrying
a different value under the same scheme (a different incarnation). What that yields per platform:

| Process state | Linux / macOS | Windows |
|---|---|---|
| alive, same incarnation | token matches → not gone | `GetProcessById` succeeds, ticks match → not gone |
| exited, not yet reaped (zombie) | `/proc/pid/stat` still readable, token matches → **not gone** | no zombie concept; `GetProcessById` throws once exited → gone |
| exited and reaped | token unreadable → gone | gone |
| pid reassigned to another process | token differs → gone | ticks differ → gone |

The zombie row is the useful one: it makes "the shim reaped the child" assertable directly, which is
what `Child_side_exec_failure…` is really about, and strictly stronger than `waitpid == -1` — an
`ECHILD` cannot tell a reaped child from a stolen wait, whereas a surviving zombie keeps its token
and fails the assertion.

`Capture` throwing (rather than returning null) is what keeps the null-is-gone rule honest: a
successful capture at arm time proves the token is readable for this pid in this environment, so a
later null means the process left the table, not that we lack rights. That matters most on Windows,
where reading `Process.StartTime` needs query rights and a protected or foreign-user process yields
no token at all. The residual case — a process that changes credentials mid-life and becomes
unreadable — does not arise for our own descendants.

Applied:

| Test | Change |
|---|---|
| `PtySpawnTests.Child_side_exec_failure_reports_failed_step_exec_and_reaps_cleanly` | assert `IsGone(result.Pid, result.StartIdentityString)` instead of `waitpid == -1`. The shim captures identity at `pty_shim.c:688`, before any wait, on this failure path too. |
| `PtySpawnTests` (class) | drop the class-level bare `[NotInParallel]` — the three remaining `waitpid`s are zombie-protected (F6) |
| `UnixPtyProcessSpawnTests.Terminate_kills_the_leaders_whole_process_group` | `Capture` the grandchild's token when the `CHILD:<pid>:DONE` line is read (it is alive then), then `WaitUntilGoneAsync` instead of polling `kill(pid, 0)` |
| `UnixSpawnerThreadTests.Pdeathsig_kills_the_child_when_the_spawner_process_dies` | same shape |
| `UnixSpawnerThreadTests.Agent_survives_unrelated_pool_thread_churn_while_the_thread_lives` | positive assertion becomes "still the same incarnation" (`!IsGone`), which is what the test means; cleanup `kill`+`waitpid` stays (our own child) |
| `Pty/Windows/ConPtyJobObjectTests.Disposing_the_process_kills_child_and_grandchild` | `Capture` both pids while alive, then `WaitUntilGoneAsync`; `IsProcessAlive` is deleted |

Net effect on the exclusion set: no pid test needs `[NotInParallel]`, and `PtySpawnTests` loses the
one it has. The bare set is then exactly the process-global-state set: env, `Console`, prod statics.

**Alternative considered — a Windows-specific handle pin.** Windows does not recycle a pid while any
handle to the process is open, so holding a `Process`/`SafeProcessHandle` from first sighting makes
reuse impossible and needs no token at all. It is strictly stronger than `tk:` comparison, which
rests on (pid, creation-time) and could in principle collide if a pid were recycled *and* the new
process created within the same system-clock tick. Rejected for uniformity: one primitive, one
decision table, no platform branch in the tests. The residual risk is a same-tick creation on a
recycled pid, which is also the granularity Windows itself uses for process identity. If the Windows
leg ever proves that insufficient, pinning is a local change inside `PidIdentity` — the call sites
do not move.

## Cost

- Sequential tail, measured after the change: **1.78s** across 44 bare test instances, against a
  106s wall clock on the slowest of the three runs — inside the 1–2s the design predicted.
  `PtySpawnTests` left the tail, the promoted env classes joined it.
- Local daemon-suite loop: 3m13s → 62s / 66s / 107s across three runs, by deleting the documented
  invocation. (The 107s run is the same suite on a busier machine, not a regression: the flag-free
  loop is variance-prone in a way a serial run is not — still ~2× faster than 3m13s at its worst.)
- CI: unchanged.

## Verification

As run, on this machine (Darwin 25.6.0, arm64):

1. Three full-parallelism runs of `Capacitor.Cli.Daemon.Tests.Unit`: **0 failed** (2767/2767/2768
   tests, 41 skipped), 62s / 66s / 107s.
2. Overlap analysis over the third run's report JSON: the 44 bare test instances that ran overlapped
   **0** peers each. The script that computes it is throwaway analysis, not committed.
3. `Capacitor.Cli.Tests.Unit` at full parallelism surfaced F7 (unrelated to `EnvScope`, which its 8
   keyed users still reach through the constructor unchanged); green after the F7 fix.
4. `dotnet test --solution Capacitor.slnx --max-parallel-test-modules 1 -- --maximum-parallel-tests 1`,
   the way CI runs it.
5. Two changes are not exercisable here and land verified by review plus the CI leg that runs them —
   stated so the gap is not silently inherited: `ConPtyJobObjectTests` (Windows only) and
   `PtySpawnTests.Child_side_exec_failure_reports_failed_step_exec_and_reaps_cleanly`
   (`[RunOn(OS.Linux)]`, skipped on macOS). The latter is the single most load-bearing pid rewrite in
   the change, so its Linux leg is the gate on this PR.

## Risks

- **The guard covers only `EnvScope` users.** A test that keeps calling
  `Environment.SetEnvironmentVariable` directly bypasses the runtime check, and §3b — which would
  have caught that — is not in this change. The convention is enforced only where adopted; today
  that is every env mutation in the daemon suite, with nothing stopping the next one.
- **Promoting to bare hides a class of race rather than removing it.** A test that needs global
  exclusion still cannot observe what a concurrent peer would do to it. The real removal is passing
  `home` explicitly (Follow-ups); exclusion is the correct interim guard, not the destination.
- **`p_uniqueid` is a private macOS ABI.** `ProcessStartToken` already depends on it in production
  and the suite already asserts `mac:` tokens, so this design adds no new exposure — but a macOS
  release that changed the flavor would fail `Capture` loudly (by design) rather than silently
  degrade.
- **Tail growth is unbounded by construction.** Every future bare mark serialises against the whole
  assembly. 1–2s today is cheap; a rule that says "when in doubt, bare" would not stay cheap. The
  `CLAUDE.md` bullets therefore say *when* bare is required, not that it is always safer.

## Follow-ups (not in this change)

- §3b: the source-scan test that fails the daemon suite on a raw `Environment.SetEnvironmentVariable`.

- Thread a `home` argument through `CodexLauncher.Prepare` / `CodexPaths.UserHooksJson` so the six
  `Prepare_*` tests need no env mutation at all, and the same for `CodexConfigWriterTests`.
- Run the same inventory over the other three suites, then decide whether CI still needs
  `--maximum-parallel-tests 1`. F7 is the evidence that at least one of them has a real defect the
  flag is masking, and one fixed test is not an audit. The 32 `HomeEnvVarMutation` classes repo-wide are the obvious first
  question: in `Capacitor.Cli.Tests.Unit` most path resolution is pinned by `KCAP_CONFIG_DIR`
  (`RepoPathStoreGlobalSetup`), which is why the key survives there — worth confirming rather than
  assuming.
- Reconsider `--max-parallel-test-modules 1` once the suites stop sharing filesystem state.
