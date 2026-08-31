# Config directory and home as explicit contexts (AI-2147)

Replace the frozen `PathHelpers.ConfigDir` static — and the four `static readonly` fields that
re-freeze on top of it — with an explicitly-passed `ConfigRoot`. Then, as part 2, replace
`PathHelpers.HomeDirectory` with a `UserHome` context and the derived roots that hang off it.

Follow-up to AI-2009 (#611), which did this for `DaemonLockPaths` → `DaemonStore` and deliberately
left `PathHelpers` alone. Parent: AI-1977. Blocks AI-1956, the bug side of this work, which carries
the failure evidence.

**Status.** Part 1 is built: `ConfigRoot` is threaded everywhere and `PathHelpers` is down to
`HomeDirectory`, which part 2 takes. Part 1 below describes what exists; parts 2 and 3 are still
proposals, and the work order carries only their remaining steps.

## Problem

A path resolved into a `static readonly` freezes at type init, and the freeze is never one capture:
downstream fields capture their own path at *their* type init, so un-freezing the top one alone
accomplishes nothing. Because the value is frozen, the only lever a test has is the environment
variable, which forces a `[ModuleInitializer]` to beat a racing read — and every path-based test in
the process then shares one directory, arbitrated by cleanup helpers and `[NotInParallel]` keys.

AI-1956 has what that costs: a leftover token bound to another server reads as an auth lapse two
layers down, and the test fails on a request-count assertion that never mentions auth.

`PathHelpers.ConfigDir` was five such freezes. `PathHelpers.HomeDirectory` is the one left, and it
is what part 2 removes.

## Part 1 — landed

`ConfigRoot` is constructor-injected or passed through every reader of the config directory,
including the composition roots and every spawned child; `AppConfig`'s resolved state is a returned
`ProfileContext` rather than a static; `PathHelpers.ConfigDir`, `ConfigPath` and `Config` are gone,
leaving `HomeDirectory` alone. Tests take a per-test `[TempConfigRoot]`, and the assembly-wide
`KCAP_CONFIG_DIR` pin became an uncreatable-path sentinel, so anything still resolving a root for
itself dies with `ENOTDIR` rather than quietly reading the developer's own `~/.config/kcap`.

Two things fell out that the plan did not have: the nine hook commands became instance classes (each
threaded the root through three or four private helpers), and the `Stopwatch` tick count they passed
around became a `HookBudget` on an injected `TimeProvider`. Read `ConfigRoot`, `ProfileContext` and
`TempConfigRoot` for the shapes; this document no longer restates them.

### Rules that carry forward

These were settled by part 1 and bind part 2 — `UserHome` mirrors them rather than re-deciding.

- **Explicit passing, no statics, no ambient context.** An `AsyncLocal` seeded in a TUnit hook or a
  test constructor is invisible in the test body: the `ExecutionContext` mutation never propagates
  back to the caller. Measured in AI-2009; not re-litigated.
- **A root with its own env override takes no home parameter. A root without one takes a root
  explicitly**, because that is its only lever. `ConfigRoot` and `DaemonStore` are the first shape;
  `AgentsPaths` and the cache tenants are the second.
- **Named members belong on the owner of each file**, never on the root — a root that enumerated
  its tenants' filenames would change every time one of them gained a file. The rule cuts both ways:
  `HookSpool` and `TranscriptSpool` own their own directory names, and their plain-directory
  constructors stay, because many test sites pass a directory that genuinely is not a root.
- **`Path.Join`, not `Path.Combine`.** `Combine(root, "/etc", "passwd")` returns `/etc/passwd` — a
  rooted segment silently escapes the root.
- **Default resolution stays private inside `FromEnvironment`**, so nothing ships that exists only
  for tests. Assert the fallback through `FromEnvironment` under `EnvScope.Exclusive(var, null)` and
  bare `[NotInParallel]`, and assert its *shape* rather than a literal path.
- **A hand-built root is legitimate; a second resolver is not.** A service unit's baked value or a
  sandbox genuinely is not this process's root.
- **The four command classes that already take a context take the home in the same constructor** —
  `DaemonCommands`, `AgentCommand`, `ReposCommand`, `UninstallCommand` — rather than gaining a
  fourth parameter.

---

# Part 2 — the home directory

Lands second, for the reasons above. Recorded here so the effort stays one design.

## The type, and the derived roots

```csharp
public sealed class UserHome(string path) {
    public string Path { get; } = path;
    public static UserHome FromEnvironment();   // entry points ONLY: $HOME if rooted, else UserProfile
}
```

Derived roots each take **an explicit root and have no fallback**; `UserHome` is composed into them
at the entry point, never read inside them. A module does not depend on being at home — it defaults
to being there because its composer said so. The exception is the two roots with their own env
override, per part 1's rule: `ConfigRoot.FromEnvironment()` and `DaemonStore.FromEnvironment()` stay
parameterless and *call* `UserHome.FromEnvironment()`. That call is the point of part 2 for them —
`DaemonStore` reads `UserProfile` directly today, and folding it in leaves exactly one home
resolution in the codebase.

| root | value | tenants |
|---|---|---|
| the cache literal | `{home}/.cache/kcap` | `SqliteNativeResolver` (`native/{lib}`) and `OpenCodeImportLedger`, each taking an explicit path |
| `AgentsPaths` | `{home}/.agents` | itself, and `PluginEnvironment.AgentsSkillsDir` delegating to it |
| service unit dirs | `{home}/Library/LaunchAgents`, `{home}/.config/systemd/user` | `LaunchdUnit`, `SystemdUnit` |
| vendor `*Paths` | **shape unchanged** — keep `string home`, lose the `??=` default | the 9 harness classes |

`PluginEnvironment` is the precedent, not an invention: a record with a `HomeDirectory` field, ~20
derived properties delegating to `KiroPaths.SettingsFile(HomeDirectory)` and friends, and a
`FromProcess()` factory as the single global read. Its one defect — `AgentsSkillsDir` hand-building
`.agents/skills` instead of going through `AgentsPaths` — is the duplication step 12 removes.

**Decided: neither `CacheRoot` nor `AgentsRoot` becomes a type.** `AgentsPaths` is already the root
for `.agents`; giving it `home` as a parameter and having `PluginEnvironment.AgentsSkillsDir`
delegate to it removes the same duplication a new type would, with nothing in between. The cache
literal is triplicated rather than doubled, and the third copy — the OpenCode plugin's JS computing
`join(homedir(), ".cache", "kcap", "opencode")` inside OpenCode's own process — provably cannot be
unified, so a type spanning two of the three would claim an invariant that does not hold. Both
tenants already accept an explicit path; they take one. Two consumers apiece is also below the bar
where this repo prefers a shared type to a copied primitive.

## The three populations

48 reads across 33 production files — 31 via `PathHelpers.HomeDirectory` (19 files), 17 reading
`Environment.SpecialFolder.UserProfile` directly (14 files). Not one kind of work:

| shape | count | change |
|---|---|---|
| `home ??= …` optional param, already seamed | 11 | delete the default; `home` becomes required |
| `["home_dir"] = PathHelpers.HomeDirectory` — a hook **payload field**, not a path | ~16 | one line each, zero risk |
| genuine unconditional path derivations | ~13 | take the appropriate root |

The payload writes are included deliberately, not for tidiness: converting them takes
`PathHelpers.HomeDirectory` to zero references so it can be **deleted**, and deletion is the
enforcement, as in AI-2009. Leave 16 sites reading a static and a future call site reintroduces the
global with nothing to stop it.

**The split-resolution bug is Windows-only.** `PathHelpers.HomeDirectory` honours `$HOME` first; the
17 direct `UserProfile` reads do not — among them `GeminiPaths.cs:19`, `CopilotPaths.cs:14`,
`PiPaths.cs:15/36`, `OpenCodePaths.cs:22/30`. **On Unix the two agree**, because
`GetFolderPath(UserProfile)` reads `HOME` under the hood; the repo records this twice already
(`PathHelpersTests.cs:22`, `FakeUserHome.cs:12`). So a `HOME` override splits only on Windows — the
leg CI reddens on and the one no developer runs locally.

Unification is safe: on Windows `$HOME` is either unset (falls back to the profile) or set to the
profile — identical either way — and if a user set it elsewhere, honouring it is more correct than
ignoring it. After this the profile is read in exactly one place.

## What part 2 deletes

- `PathHelpers.HomeDirectory` and, with `ConfigDir` gone in part 1, `PathHelpers` itself
- `McpMarker._centralRootOverride` and `OverrideCentralRootForTesting`
- `McpMarkerGlobalSetup` — a Guards pin
- `FakeUserHome`'s real-profile rooting and its 20-line comment: it becomes a plain `TempDir`.
  `PathHelpersTests` (4 tests, all about the deleted type) goes with it, and `ProdPathFixture.cs:24`
  stops mutating `HOME` outside `EnvScope`.
- 16 direct `UserProfile` reads collapse to one

`McpMarker` needs no real profile once home is a passed value. `MarkerPath` reads the profile once
(`:150`) for two uses — user-scope classification and the central marker root — so a per-test home
puts **both inside the test's own directory**, precisely what the override was hand-arranging.

**Consequence:** that branch is exercised by accident today, when a developer's real profile happens
to be a git repo and `IsInsideRepo` flips the classification. A temp home removes the accident, so
it needs a **deliberate** test placing a config outside the test home.

## Part 2 traps

- **Three sites mutate the real profile with no seam today**, and part 2 forces isolation on them:
  `SqliteNativeResolver.DefaultCacheRoot()` (downloads a native library into `~/.cache/kcap/native`,
  latent only because `SqliteNativeResolverTests` passes an explicit cache path),
  `OpenCodeImportLedger.DefaultPath()` (safe only because one test passes `ledgerPathOverride`), and
  `DaemonConfig.WorktreeRoot` (overridden by hand in ~10 daemon tests, forced by nothing).
- **`.agents` and `.capacitor` each have a non-home anchor.** `AntigravityPaths.cs:152` is
  `{workspaceRoot}/.agents/plugins`; `WorktreeManager.cs:221` and `:1424` are
  `{repoPath}/.capacitor/worktrees`. The derived roots cover home-anchored builders only — do not
  unify by literal.
- **The OpenCode plugin's cache dir is out of reach and stays that way.**
  `OpenCodeExtensionInstaller.ExtensionContent` is a ~450-line `const` raw string (`const` for
  NativeAOT) whose JS computes `join(homedir(), ".cache", "kcap", "opencode")` in OpenCode's *own*
  Node process. `homedir()` honours `$HOME`, so a spawned child with a redirected `$HOME` is
  isolated, but no in-process context can reach it and interpolating a root means escaping 450 lines
  of braces.

## Server side: `home_dir` is display-only

Verified in kcap-server (ignoring `src/cli`, a kcap-cli submodule). `HomeDir` is persisted to a
sticky `home_dir text` column (`CapacitorDb.cs:1666`) and consumed only by display shortening via
`EventFormatting.RelativePath`. **No `Path.Combine`, `File.*` or `Directory.*` anywhere from
`HomeDir`** — no filesystem touches it, so a test-controlled value cannot cause traversal, and it is
already Full-tier-only (`SessionResponseShaper.cs:58,141`, asserted by `SessionLevelFloorTests`). So
converting the payload writes changes only where the string comes from; test runs get a temp path,
matching what `HookRoundTripTests` already hardcodes, and no test asserts it. `InternalsVisibleTo`
reaches 12 kcap-server test assemblies, but no server code outside `src/cli` touches the affected
types, so the signature changes are cross-repo-safe. Re-check on landing.

Measured, `[NotInParallel("HomeEnvVarMutation")]` does not come off with the home: of its 22
carriers only one holds nothing else that needs serialising. The rest read a vendor override —
`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `COPILOT_HOME`, `GEMINI_CLI_HOME`, `KIRO_HOME`,
`XDG_CONFIG_HOME` — which this part keeps env-driven, so the key outlives the home it is named
after. Guards pins do drop to one.

---

## Test homes: ephemeral, with one exception

**Every test home is ephemeral.** A directory acting like a home is always enough, because nothing
under test asks the OS who the user is — it joins paths under a root it was handed. The single
exception is a **live cert needing the operator's own credentials**: seven of the nine memory-index
cert suites reach the real home through `MemoryIndexLiveCertHarness`, to read the real
`~/.config/kcap` and authenticate a real vendor turn. A temp home there is not a directory acting
like a home — it is an empty config, and the cert fails `401` in a costume that reads as "not logged
in". All are gated on `KCAP_*_LIVE`, so a normal run resolves no home at all.

Three other things legitimately read the real home, and none of them is a home a test acts
*through*:

- **A guard asserting the real location is NOT reached.** `ConfigDirIsolationTests`,
  `DaemonStoreIsolationTests` and the reviewer's `HOME` assertion in
  `AcpHostedAgentRuntimeFactoryTests` compute the developer's real path in order to assert the
  resolved one differs. Hand them an ephemeral home and both sides are throwaway paths that always
  differ — the assertion passes for the wrong reason and guards nothing.
- **A real machine resource.** `BorrowedReviewSandboxTests` proves the sandbox cannot read the
  user's actual keychain, ssh key and cloud credentials, skipping each case when the file is absent;
  a temp home would assert that a nonexistent file is unreadable. `SqliteNativeResolverTests` reads
  the real NuGet cache to catch native-package drift.
- **An ambient default confined to a `FromEnvironment` factory.** `CursorPaths` and
  `AgentDetection` take the home as a parameter and read only the *other* process inputs — Windows
  `ApplicationData` among them — inside that one named factory, which is the shape the carry-forward
  rules already require. Tests construct the record directly.

A test OF the resolution is not an exception either — it reaches its ephemeral home through the
environment instead of through a value, under `EnvScope.Exclusive` and bare `[NotInParallel]`, which
is the only way to exercise the env-reading path at all. This is not only `UserHomeTests`: any test
asserting what a `FromEnvironment` picks is in the category, including the ones whose subject is a
*derived* path. `ConfigRootTests` asserts the home fallback of `ConfigRoot.FromEnvironment()`, so an
injected home changes nothing it reads and the assertion compares a throwaway path against the
developer's own. Substituting a value there converts a passing test into a failing one, which is the
benign half; the danger is the assertion that keeps passing while measuring nothing.

`Homes.Current` — `UserHome.FromEnvironment()` behind a helper — goes away entirely. It was the
behaviour-preserving choice while converting call sites and it leaves the ambient dependency
standing: a test holding it is isolated by whatever redirected `HOME`, not by the value it passes.
The exception does not need it: the cert harness calls `FromEnvironment` directly, next to the
comment explaining why, and `UserHomeTests` calls it because it is the thing under test. Deleting the
helper is the enforcement, as it was for `PathHelpers`.

### The shape

`TempFixtureAttribute<T>` already carries `Shared` and `Key`, so this is one fixture plus one
attribute, mirroring `TempConfigRoot`:

```csharp
[TempHome] public required TempHome Home { get; init; }
```

`TempHome` owns a `TempDir`, exposes the `UserHome` over it, and forwards the seeding members a test
needs to plant a vendor's dotfiles. One thing it does that `TempConfigRoot` does not: it takes its
path from `TempDir.GetResolvedPath()` rather than `Path`. `CodexConfigToml`'s guard refuses a
symlinked component and a Mac's temp root is `/var` → `/private`, so handed the unresolved form a
Codex registration returns `Failed` and writes nothing — on macOS only, which no CI leg covers.
Resolving is opt-in on `TempDir` because it costs 8 characters of the `sockaddr_un` budget; a home
spends them. `FakeUserHome` exists for this and `TempHome` subsumes it.

Being ephemeral, it is also not under the real home — nesting there would put `McpMarker` on its
sidecar branch by accident of ancestry rather than by the value passed, which is how a marker test
agrees with the code for the wrong reason.

### Sharing one home per class

`Shared` widens the lifetime but hands one directory to tests running concurrently, so `PerClass`
fits a class that never mutates its home. Make that a property the type keeps rather than a promise
the reader makes: a shared `TempHome` captures a manifest of its tree once seeded and compares it on
disposal, failing and naming the class when a test wrote into it. Without the check the failure mode
is a test that passes alone and interferes in parallel — the exact bug this seam exists to remove —
so the sharing is worth having only with it.

Per-test stays the default. `PerClass` is for a read-only fixture expensive enough to seed that
sharing pays for the check.

## Work order

Sequenced so each step builds and passes. **Deletions come after the call sites they break** —
production and test alike, which puts the test-side conversion mid-sequence, not last.

Parts 0 and 1 are done (see *Part 1 — landed*), and so are steps 11-15; what remains is 16 on.

**Part 2**

11. *Landed.* Introduce `UserHome`; point `ConfigRoot`'s and `DaemonStore`'s fallbacks at it.
12. *Landed.* Remove the `??=` defaults from the 9 vendor
    `*Paths` classes; generalize `PluginEnvironment`.
    **One commit per vendor**, after a shared commit that puts a `UserHome` on the entry points.
    The 11 in the table are the `home ??=` declarations, not the ripple: their callers do not pass a
    home today, so deleting a default moves the work to roughly 350 call sites across the nine —
    ~195 in production, ~155 in tests. Claude alone, measured, is 16 production errors in 9 files.
    A parameterless member that resolved its own home (`ClaudePaths.Projects` and friends) becomes a
    method taking one, which is where most of those sites come from.
    Claude came last and cost more than a deleted `??=`: it was the one vendor whose paths were a
    static class, so the conversion is the instance shape the other eight already have — a ctor
    taking the override, a `FromEnvironment` that names the one ambient read, and four launcher
    helpers taking the paths object rather than a home string. `PluginEnvironment` exposes it, which
    forces the type public like its siblings.

    `UserConfigJson` does not derive from the root: with the config dir set it lives inside it, and
    without one it is a SIBLING of `~/.claude`. The ctor resolves both bases so no caller can
    collapse them.
13. *Landed.* Convert the ~13 path derivations and the ~16 payload writes. One site keeps its own
    read: `SqliteNativeResolver.DefaultCacheRoot` feeds a `DllImport` resolver installed from a
    static constructor, so it has no caller to take a home from. It reads through
    `UserHome.FromEnvironment` so the definition of home stays in one place.
14. *Landed.* Delete `PathHelpers`, `McpMarker`'s override and `McpMarkerGlobalSetup`; reduce
    `FakeUserHome` to a `TempDir` and delete `PathHelpersTests`; add the central-marker test.
    `ProdPathFixture` needed no `EnvScope`: with the home injected it stops redirecting `HOME`
    altogether, proven by running its suites under a bogus ambient `HOME`.
15. *Landed.* Convert the tests off the ambient home (*Test homes*, above): `TempHome`,
    `[TempHome]`, and every `Homes.Current` site onto one of them. `Homes.Current` is deleted, so the
    only `FromEnvironment` callers left in tests are the cert harness and `UserHomeTests`.

    The estimate of "exactly 2 sites injection cannot reach" was wrong by an order of magnitude: 25
    of 297 needed more than a property read — 15 private static helpers (which simply stop being
    static), 3 nested command fixtures and 5 fake service managers (which take a home of their own),
    plus one static harness that cannot host an injected property at all. Regex cannot tell an
    expression-bodied instance method from a field initializer reliably enough to enumerate them;
    the transform-then-compile pass does, and CS0120 is the exact list.

    Three suites seeded a temp home, redirected `HOME` to it, and then handed the command a
    *different* injected home — passing only because nothing checked the two agreed. A test whose
    home arrives as a value must be seeded through that same value, or the redirect has to go.
16. *Landed.* Retire the HOME half of the parallelism attributes. The last raw
    `Environment.SetEnvironmentVariable("HOME", …)` was `SetupCommandTests`' E2E fixture, and it was
    vestigial: the command already took the injected home, so the four tests pass under a bogus
    ambient `HOME` with the redirect gone. Nothing mutates `HOME` now outside `EnvScope.Exclusive`,
    which enforces bare exclusion itself.

    The key is `VendorEnvOverrides`, and the cohort is 16 suites rather than the handful expected —
    only `LaunchdStartStopTests` left it. What holds the rest in is `PluginEnvironment.Paths`:
    resolving the bundle eagerly when the record is built is an ambient read of every override
    variable, so a suite that constructs one is a reader however its home arrives. Those suites clear
    the variables *because* of that read, which is the order to unpick — hand the bundle in, and both
    the clears and the cohort go.

    PATH is in the cohort too: `kcap` and the vendor CLIs resolve through it, so a suite that stages
    a fake binary is mutating what the override-reading suites detect through.

**Part 3**

17. *Landed.* `Microsoft.CodeAnalysis.BannedApiAnalyzers`, `RS0030` as an error, one root
    `BannedSymbols.txt` reaching every project through `Directory.Build.props`. Both `GetFolderPath`
    overloads are entries, and each legitimate site — 2 in src, 5 in tests — carries its own
    `#pragma warning disable` naming why it is exempt.

    Nothing in that file can be read for confidence: an unrecognised line and a mistyped doc ID are
    both ignored in silence, so the only evidence a ban works is a call that fails to compile. One
    throwaway call per assembly is how coverage was established — src, Helpers and all five test
    projects.

No architectural guard test is needed for either static — deletion is the enforcement, and the seam
keeps two structural defences: a child cannot be spawned without an explicit variable, and the
assembly pin cannot be created, so a bypass fails loudly. A guard that greps source text asserts
nothing about behaviour.

# Part 4 — one paths bundle instead of an override bag

Part 2 gives every vendor a constructor plus one named `FromEnvironment`. What it does not reconcile
is the second reader: `AgentDetectionInputs` carries `GeminiCliHome`, `CopilotHome`, `KiroHome`,
`PiAgentDir`, `OpenCodeConfigDir`, `XdgConfigHome`, `XdgDataHome`, `Platform` and `AppData` as raw
strings so `AgentDetection.Detect` and `HarnessCatalog` can rebuild the same nine `*Paths` objects.
`GEMINI_CLI_HOME` therefore has three readers — `GeminiPaths`, `AntigravityPaths` (whose whole layout
hangs off Gemini's root) and the inputs record — and nothing ties the derivations together.
`HarnessCatalog` is already inconsistent with itself: six rows resolve from the injected snapshot,
claude/codex/cursor re-read ambient env, and the type documents the divergence as accepted.

A `HarnessPaths` bundle holds one instance per vendor plus `AgentsPaths`, built by a single
`FromEnvironment(UserHome)` that *calls* each vendor's own factory — never an inlined
`GetEnvironmentVariable`, and never a constructor taking nine override strings, either of which
rebuilds the bag. Antigravity is composed from the Gemini instance, so the shared root agrees by
construction. `KnownHarness.IsWired` takes the bundle, so all nine rows are one shape, reached
through an extension — `paths.IsWired(vendorId)` — rather than a member, so a layout need not know
about install policy.

PATH is the other half, and it is `BinaryProbe(PathEnv, PathExt, IsWindows)`: not a detection input
but the one place a command name resolves to something the OS will launch, so `CliExecutable`'s
second copy of the PATH/PATHEXT walk folds into it. A second copy is how the Windows `.cmd` gap
survived — `CreateProcess` appends only `.exe`, and npm drops an extensionless `#!/bin/sh` shim
beside `codex.cmd`. Every platform fact being a field means those launch semantics are now pinned
from a Unix host rather than skipped there. Detection and resolution take the two values
separately; a pairing type would only earn its keep if callers threaded both, and the commands that
already hold a bundle would then hold a second one.

Members are eager: every vendor constructor is string composition with no I/O, and a lazily
recomputed property is how `PluginEnvironment` came to read `CLAUDE_CONFIG_DIR` once per property
access, splitting one vendor's layout into two reads mid-command.

A launch is not this process. `KiroReviewerHome`, `OpenCodeReviewerConfigDir` and
`AntigravityReviewerHome` deliberately point a child at an isolated vendor home — the last of them to
stop an ambient `GEMINI_CLI_HOME` escaping it — so launch code keeps building paths from the
directory it just created, and `CodexLauncher` keeps reading what its child will inherit. Those are
the allowlist; everywhere else `*Paths.FromEnvironment` ends with one caller, pinnable like
`GetFolderPath`.

The direction that changes: a site needing one vendor takes that vendor's paths, a site fanning out
over vendors takes the bundle, and `UserHome` retreats to the input of `HarnessPaths.FromEnvironment`,
`ConfigRoot.FromEnvironment` and `DaemonStore.FromEnvironment`.

18. *Landed.* Add `HarnessPaths`; give `BinaryProbe` the walk and delete `CliExecutable`; convert
    `Detect` and delete `AgentDetectionInputs`; move `KnownHarness.IsWired` onto the bundle; give
    `PluginEnvironment` a bundle in place of its recomputing properties; then
    `SkillsCommand.Targets`, `SetupCommand.BuildImportSources`, `StatusCommand` and
    `UninstallCommand`.

    `BuildImportSources` had a second copy in `Program.cs` — the same nine constructions, filtered by
    a different expression — so the import verb and the wizard could disagree about what a vendor's
    source reads. One builder, one filter. What blocked it was two sites that took a home rather than
    a layout: `ClaudeImportSource` derived the projects dir itself, and the OpenCode import ledger
    lived on the ledger type. The ledger path is now `OpenCodePaths.ImportLedgerJson` — it stays
    under kcap's own cache, so an OpenCode override cannot move it.

Left for its own change, in `2026-08-27-harness-modules-design.md`: the bundle and
`AgentDetectionResult` are still types that name every vendor as a member, and retiring that shape
means one module per vendor behind capability interfaces, which reaches every consumer of the
positional result.

## Considered: enforcing it with an analyzer

Deleting a type enforces nothing about the API underneath: `GetFolderPath` and
`GetEnvironmentVariable` cannot be deleted, so a future call site reintroduces the global freely.
[`BannedApiAnalyzers`](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md)
closes that: a `BannedSymbols.txt` `AdditionalFiles` item, `RS0030` per member, the text after the
`;` as the message. Verified on .NET 10, including that a mistyped doc ID matches nothing and fails
silently — this analyzer's usual way of being useless:

```
M:System.Environment.GetFolderPath(System.Environment.SpecialFolder);Take a UserHome.
M:System.Environment.GetEnvironmentVariable(System.String);Read env only at an entry point.
M:System.Environment.SetEnvironmentVariable(System.String,System.String);Use Helpers' EnvScope.
```

`dotnet_diagnostic.RS0030.severity = error` promotes it from warning and `#pragma warning disable
RS0030` suppresses exactly the wrapped site: an error everywhere, one visible exemption, build-time
only. Scheduling turns on one thing — **the ban is per-symbol, not per-argument.**
`GetEnvironmentVariable("KCAP_CONFIG_DIR")` cannot be forbidden while `("KCAP_URL")` is allowed, so
an entry is worth adding only once its legitimate sites are few enough to exempt one by one:

| symbol | when | remaining sites |
|---|---|---|
| `GetFolderPath` | part 3, after part 2 | 2 in src, 5 in tests — all legitimate, all exempted |
| `GetEnvironmentVariable` | not soon | many legitimate; would need broad exemptions |
| `SetEnvironmentVariable` (test projects) | its own ticket | many raw calls outside Helpers |

The third would turn an existing CLAUDE.md rule — mutate env through `EnvScope`, never directly —
into a build error, but it is not a tail item here: 243 sites is a migration of its own, in files
this seam never opens.

## Out of scope

- Making `CliTelemetry` an instance. Its `_client`, `_deviceId` and `_suppressedSticky` are
  process-lifetime by design; giving it a root needs no unpicking, and unpicking it here would hide
  a behaviour change in a path change.
- The `AppConfig.LoadProfileConfig` `FileShare.Read` fix — a product bug that can ship on its own.
  AI-1956.
- Migrating the raw `SetEnvironmentVariable` test sites to `EnvScope`, and the ban that follows.
- Reshaping the harness `*Paths` classes beyond removing their fallback and composing them into part
  4's bundle. Their parameter-injection seam is correct; folding them into one context type would be
  a regression, as AI-2009 concluded — the bundle holds the instances, it does not replace them.
- The two biggest remaining keys, because neither global is ours:
  `[NotInParallel("AvaloniaSession")]` guards Avalonia's headless session and
  `RxSchedulers.MainThreadScheduler`, and `nameof(LocalPermissionBridgeTests)` guards the OS
  loopback port space, where `static HashSet<int> ClaimedPorts`
  is the arbiter.
- `ProcessUrlPolicy.Current` (3 sites, can `Environment.Exit(2)` out of the test host) and the three
  commands calling `Console.SetOut`/`SetError` without restoring. Both real, both separate.

## Related

- `docs/superpowers/specs/2026-08-22-capacitor-http-client-factory-design.md` — the HTTP
  layer this ticket deliberately leaves static-but-rooted, and why it lands after part 1.
- AI-2009 (#611) — the pattern this follows; `DaemonStore`, `[TempDaemonPaths]`, `KcapProcess`.
- AI-1956 — the bug side: reproduction, the seven latent Integration tests, the `FileShare` hazard.
- AI-630 (#67) — why the daemons directory ignores `KCAP_CONFIG_DIR`.
- AI-1932 — the TUnit upgrade whose reshuffled schedule turned these latent order-dependencies into
  CI failures. Parent: AI-1977.
