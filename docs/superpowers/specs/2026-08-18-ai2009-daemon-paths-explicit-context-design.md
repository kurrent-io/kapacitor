# Daemon paths as an explicit context (AI-2009)

Replace the process-global `DaemonLockPaths` static with an explicitly-passed `DaemonStore`
context, so daemon-path tests stop serialising on a shared directory.

## Problem

`DaemonLockPaths` is a static class whose directory is a process-global. Tests isolate it with
`OverrideDirectoryForTesting`, hand-rolled at **242 calls across 36 files**, and serialise on
`[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]` — **137 attribute
usages** across 38 files. (The issue's "388 call sites" counts every textual mention: the 242
calls plus the 137 attribute keys plus doc comments.) The override is the reason the
serialisation exists, not a fix for it.

The same invariant is re-established by hand in eight different shapes — a field plus ctor plus
`Dispose`; a `static TempDir Scratch()` factory; a `CreateScratchDir()`/`Restore()` pair;
`[Before(Test)]`/`[After(Test)]` hooks; fully inline per test; a wrapper helper taking the test
body as a delegate (the most common, all 11 socket suites); a value-returning variant of that;
and classes that declare the key without ever calling the override.

The stakes are not cosmetic. `DaemonLockPaths` deliberately ignores `KCAP_CONFIG_DIR` and pins
its directory under the real home, and the default daemon name is the OS username — so any
window where the override is null resolves the developer's live daemon. A test once read that
directory and `SIGKILL`ed a running daemon and its hosted agents.

This is the first of several path singletons to convert. It is deliberately first because it is
the only one that is cleanly separable: measured file-level overlap between `DaemonLockPaths` and
`PathHelpers.ConfigPath` is **2 files out of 22** (one a doc comment), and with
`PathHelpers.HomeDirectory` it is **0**. There is no dependency edge either — `DaemonLockPaths`
never calls `PathHelpers`. The config-dir family is a separate context for a separate ticket.

## Decision

**Explicit passing, no statics, no ambient context.**

An ambient `AsyncLocal` context was measured against real TUnit hooks and rejected. A value
seeded in `[Before(Assembly)]`, `[Before(Class)]`, `[Before(Test)]`, or a test class constructor
is **not visible in the test body** — setting an `AsyncLocal` mutates the current
`ExecutionContext`, and that mutation never propagates back to the caller. It also cannot be
read in `[After(Test)]`. Seeding inside the test body does work and flows through `await`,
`Task.Run`, `new Thread`, and `Timer` callbacks — but that yields the same one-line ergonomics as
explicit passing while keeping a global with a null fallback.

Explicit passing is also *easier* precisely where ambient is fragile: the off-flow call paths
(`RestartCoordinator`'s `BackgroundService` timer and its control-socket-thread entry,
`LocalControlServer`'s fire-and-forget `_ = HandleConnectionAsync(...)`, `ActivityViewModel`'s Rx
`ticker.Ticks.Subscribe`, `DaemonMutationLane`'s poll loop) all sit on types that already take
injected dependencies.

## The type

A sealed immutable class in `Capacitor.Cli.Core`, replacing `DaemonLockPaths.cs`:

```csharp
public sealed class DaemonStore(string directory) {
    public string Directory { get; } = directory;

    public string LockPath(string daemonName);
    public string PidPath(string daemonName);
    public string StartLockPath(string daemonName);
    public string RestartPendingPath(string daemonName);
    public string VersionPath(string daemonName);
    public string Socket(string daemonName);

    public string StateDirectory(string daemonName);
    public string ConsentDecisionLogPath(string daemonName);

    public void EnsureDirectory();
    public IReadOnlyList<string> EnumerateNames();

    public const string DaemonsDirEnvVar = "KCAP_DAEMONS_DIR";

    public static string Sanitize(string name);      // pure — no context needed
    public static DaemonStore FromEnvironment();     // entry points ONLY
    internal static string ResolveDefaultDir(string? envValue);   // pure — testable fallback
}
```

`LocalSocketPaths` **folds in** as `Socket(name)`. It is one line today
(`Directory/{sanitized}.sock`) but its own static class, and it is the widest transitive surface
in the codebase — 13 call sites across all three processes. Making it a member of the context
those callers already hold removes the fan-out instead of duplicating it.

`Sanitize` **stays static**, because it is pure. 14 of the 60 production references are
`Sanitize` alone; those sites do not change.

Everything else that derives a path from `Directory` takes a `DaemonStore` — one rule, no
per-site judgement:

- **First parameter** for the static helpers: `DaemonRestartMarker`, `DaemonVersionMarker`,
  `ServiceTxnLock`, `ServiceTxnMarker`, `BootRefusalReader`, `BootRefusalMarker`,
  `ConsentDecisionLogReader`, `DaemonPidProbe`, `DaemonKill`, `HelloProbe`.
- **Constructor dependency** for types that already take injected deps: `ServiceVerify`,
  `LocalControlOps`, `LocalControlClient`, `LocalControlServer`, `DaemonMutationLane`,
  `RestartCoordinator`. `AgentOrchestrator` is not among them — it reaches daemon state through
  `DaemonConfig`, which carries the context for it.

## Creation and flow

`FromEnvironment()` is called in exactly three places, and `KCAP_DAEMONS_DIR` is read nowhere
else. After entry the variable is dead to the process.

| process | creation point | how it flows |
|---|---|---|
| daemon | `Capacitor.Cli.Daemon/Program.cs` → `DaemonRunner.RunAsync` | DI singleton, registered before the host is built |
| CLI | `Capacitor.Cli/Program.cs` | parameter into each command's `HandleAsync` (4 dispatch arms) |
| desktop app | `Capacitor.App/App.axaml.cs`, a field initializer on `App` | composition root passes it to `DaemonMutationLane`, `DaemonClientService`, and the `ActivityViewModel` closures |

On the daemon side `DaemonConfig` gains a settable `Paths`, assigned in `RunAsync` before any
consumer, and a `StateDirectory` that derives from it and throws if it was never set — `DaemonConfig`
is built by property assignment, so a required constructor parameter was not available. That deletes
the `config.StateDir ?? DaemonLockPaths.Directory` pattern at all 11 daemon call sites, and no intermediate type changes — `DaemonConfig` is already a DI singleton
built in `RunAsync` before the host, and every downstream consumer (`AgentPidRecordStore`,
`CoverageJournal`, `LaunchConsentStore`, `ReviewerVersionStore`) already takes a plain `string`.

### Cross-process seam

The CLI spawns the daemon as a separate process, so no in-process context can cross that
boundary. When spawning, the CLI writes `KCAP_DAEMONS_DIR` into the child's environment **from
its own context**. That is the only place the variable is written; the child reads it once in
`Main` and forgets it. The env var is a transport, not a source of truth.

## Scale

60 production member invocations across 50 call-site lines in 20 files; 46 lines need the
context, 4 are pure `Sanitize`. Max depth from an entry point to a call site is 5. The cost
driver is not depth but shape: **16 of the 20 enclosing types are static classes**, so the
context becomes a parameter on ~40 static method signatures. `DaemonCommands` alone holds 17 of
the 52 context-needing references. ~15 intermediate types gain a field or parameter; 7 of those
already take injected deps and cost one parameter each.

There is no cached-path hazard: no static constructors anywhere in `src/`, no `static readonly`
initializer touching these helpers, and `Directory` already re-resolves per access. A context
built in `Main` is always set before any call site runs.

## Payoff

Of the 137 daemon-key `[NotInParallel]` attributes:

| bucket | attrs | files |
|---|---|---|
| removable — daemon-dir only | **129** | 30 |
| blocked — also HOME / Console / a static seam | 7 | 7 |
| needs judgement | 2 | 2 |

**94% removable.** The blocked bucket is softer than it looks: 6 of 7 already carry independent
serialisation and merely shed the daemon key from an existing attribute. Exactly one file needs a
replacement — `ServiceTxnMarkerTests`, which swaps `ServiceTxnMarker.FlushDirectory`, an
`internal static Func<string,bool>` that every `ServiceVerify*` test writes markers through.
It takes a **bare** `[NotInParallel]`, not a new key: a key covers only the tests that name it, and
the concurrent writers here are whole other suites that never would.

The 202 bare `[NotInParallel]` attributes do **not** move: 145 are Console captures (per
CLAUDE.md), 57 are `PATH`, umask, loopback ports, real-vendor-CLI spawns, and one GC
allocation-budget test.

## Test side

```csharp
[TempDaemonPaths] public required TempDaemonPaths Daemons { get; init; }   // per test, auto-disposed
...
using var lock = DaemonLock.TryAcquire(Daemons.Paths, "alpha");
```

`TempDaemonPaths` lives in `Capacitor.Tests.Helpers` and owns a **deliberately short**
directory name. `Socket(name)` puts the UDS inside the daemons dir, and the macOS `sockaddr_un`
ceiling is exactly 103 characters — measured: `bind()` succeeds at 103 and fails at 104. With
`$TMPDIR` at 49 chars, a `kcap-test-{hint}-{8 random}` name leaves ~28 characters for the daemon
name, against a longest current name of 22 (`"test-consent-subscribe"`). A per-test directory
that adds a test name, GUID, or nesting blows the limit — **invisibly**, because Linux CI has a
4-char `/tmp` and a 108-byte `sun_path`. A test pins the budget so a regression fails on CI
rather than only on a developer's Mac.

This also retires the 12 privately-invented short prefixes (`lcc`, `lco`, `lcp`, `lch`, `lci`,
`lcov`, `crp`, `csub`, `dsi`, `hp`, `aola`, `aolac`) and the comment duplicated across 11 files.

### Spawning the real binary

13 test files locate the real `kcap` binary, via **11 hand-rolled copies** of
`GetCliBinaryPath()`. Only `ServiceVerifyProcessTests` passes `KCAP_DAEMONS_DIR` to its child;
the other 12 inherit the assembly-wide pin.

A single `KcapProcess` helper in `Capacitor.Tests.Helpers` replaces all 11 resolvers and
**requires** a `DaemonStore`, so a child cannot be spawned without an isolated directory — the
same forcing function as the rest of the design.

### The assembly pin becomes a fail-fast sentinel

In-process the pin stops being load-bearing: the compiler forces every test to pass a context.
Its remaining job is catching a spawn that bypasses `KcapProcess`. So `DaemonPathsGlobalSetup`
pins `KCAP_DAEMONS_DIR` to a path that **cannot** be created, making a miss loud instead of
silently working:

```
{tmp}/kcap-no-daemons-dir/daemons        # "kcap-no-daemons-dir" is a regular FILE
```

`CreateDirectory` on a path whose parent is a file fails with **ENOTDIR**, which is not a
permission check and therefore behaves identically when CI runs as root. Measured alternatives
that do *not* work: an unwritable `0500` parent gives EACCES but is ignored by root; an existing
`0500` directory lets `CreateDirectory` succeed as a no-op and defers the failure to the file
write.

This lands only after the static is gone **and** all 13 spawners route through `KcapProcess` —
until then the 12 that rely on the pin resolving somewhere writable would break.

`DaemonPathsIsolationTests` is rewritten rather than re-attributed: its subject *is* the global,
so "clear the override and assert the default isn't the real home" stops being meaningful. It
becomes a pure-function test of default resolution plus an assertion that the child-process
sentinel is in place.

## Traps

- `ServiceVerifyProcessTests` inspects a flock from the parent while the child holds it, so the
  parent's context and the child's env must agree.
- `DaemonStopSelfPidTests` writes a PID file naming the test runner itself — the source of the
  `UninstallCommandTests` flake, since `daemon stop --yes` enumerates the directory and
  `Process.Kill(entireProcessTree: true)`s what it finds.
- `DaemonKillTests` makes unmocked `Process.GetProcessById` and tree-kill calls.
- `DaemonLockEnumerationTests` asserts `EnumerateNames()` sees *exactly* N, so any leaked marker
  fails it.
- `UninstallCommandTests` takes the daemon key **defensively** and never calls the override; once
  the directory is per-test that concern disappears and the key can go, keeping its HOME /
  `KCAP_CONFIG_DIR` / CWD keys.
- Fixed socket names (`"test"`, `"test-consent-rules"`, `"alpha"`, `"await-test"`) are safe only
  while every test has a distinct directory.

## Work order

One ticket, landed as a sequence so each step builds and passes:

1. Introduce `DaemonStore` (folding in `LocalSocketPaths`); keep the static delegating to it.
2. Convert the daemon cluster via DI and `DaemonConfig.Paths`/`StateDirectory`.
3. Convert the Core markers and `ConsentDecisionLogReader`.
4. Convert the `ServiceVerify` tree.
5. Convert `DaemonCommands`, `StatusCommand`, `AgentCommand`, and the CLI entry point.
6. Convert the App composition root.
7. Delete the static and `LocalSocketPaths`.
8. Add `KcapProcess`; route all 13 spawners through it.
9. Drop the 129 attributes; give `ServiceTxnMarkerTests` a bare `[NotInParallel]`; flip the pin
   to the ENOTDIR sentinel; reduce `DaemonPathsIsolationTests` to the inherited-pin guard.

No architectural guard test is needed for the static — deleting the type is the enforcement. A guard
over `FromEnvironment()`/`KCAP_DAEMONS_DIR` reference counts was considered and **rejected**: a test
that greps source text asserts nothing about behaviour, and the seam already has two structural
defences — `KcapProcess` cannot spawn without a context, and the assembly pin is a path that cannot
be created, so a bypass fails rather than working quietly.

## Out of scope

- The config-dir family (`PathHelpers.ConfigDir` and its 13 derived singletons). Its own ticket.
  Highest-value single fix there: `PathHelpers.cs:13` is `static readonly`, freezing at type
  init, and four downstream singletons freeze on top of it — which is why
  `RepoPathStoreGlobalSetup` needs a `[ModuleInitializer]`.
- The 9 harness `*Paths` classes. They already have no mutable static state and no testing seam;
  their seam is parameter injection plus explicit `*Pure` variants. Folding them into a context
  would be a regression.
- `McpMarker` — zero overlap with anything, already has a constructor seam.
- Non-path process-global state that caps parallelization later: production code that mutates the
  process env (`Program.cs:130`, `DaemonRunner.cs:56`), three commands that call `Console.SetOut`
  and never restore it, one-way sticky latches, and `ProcessUrlPolicy.Current`, which can
  `Environment.Exit(2)` and take the test host down.

## Related

- Follow-up to AI-1982 (#575), which consolidated the temp-directory helper and deliberately left
  these suites alone.
- Parent: AI-1977.
- Independent bug found while surveying: 4 of the 9 harness `*Paths` resolve through
  `PathHelpers.HomeDirectory` while 5 read `SpecialFolder.UserProfile` directly, so a `HOME`
  override reaches 4 of 9 and silently misses the rest.
