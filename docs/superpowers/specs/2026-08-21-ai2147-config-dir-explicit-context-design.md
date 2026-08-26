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
  `CacheRoot` and `AgentsRoot` would be the second.
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
| `CacheRoot` | `{home}/.cache/kcap` | `SqliteNativeResolver` (`native/{lib}`), `OpenCodeImportLedger` |
| `AgentsRoot` | `{home}/.agents` | `AgentsPaths`, `PluginEnvironment.AgentsSkillsDir` |
| service unit dirs | `{home}/Library/LaunchAgents`, `{home}/.config/systemd/user` | `LaunchdUnit`, `SystemdUnit` |
| vendor `*Paths` | **shape unchanged** — keep `string home`, lose the `??=` default | the 9 harness classes |

`PluginEnvironment` is the precedent, not an invention: a record with a `HomeDirectory` field, ~20
derived properties delegating to `KiroPaths.SettingsFile(HomeDirectory)` and friends, and a
`FromProcess()` factory as the single global read. Its one defect — `AgentsSkillsDir` hand-building
`.agents/skills` instead of going through `AgentsPaths` — is the duplication `AgentsRoot` removes.

**Open decision:** whether `CacheRoot` and `AgentsRoot` earn being types at all. Two in-process
consumers each is thin, and the repo prefers copying primitives over false sharing; against that,
both are *currently duplicated literals* across module boundaries, the thing a root type prevents.
Decide before implementing part 2. Not open: either way both take their root as a parameter, since
with no env override that is their only lever.

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

Most `[NotInParallel("HomeEnvVarMutation")]` carriers hold no other key, so they go fully parallel
and Guards pins are down to one. Re-measure on landing.

---

## Work order

Sequenced so each step builds and passes. **Deletions come after the call sites they break** —
production and test alike, which puts the test-side conversion mid-sequence, not last.

Parts 0 and 1 are done (see *Part 1 — landed*); what remains:

**Part 2**

11. Introduce `UserHome`; point `ConfigRoot`'s and `DaemonStore`'s fallbacks at it; decide the
    `CacheRoot` / `AgentsRoot` question.
12. Remove the `??=` defaults from the 9 vendor `*Paths` classes; generalize `PluginEnvironment`.
13. Convert the ~13 path derivations and the ~16 payload writes.
14. Delete `PathHelpers`, `McpMarker`'s override and `McpMarkerGlobalSetup`; reduce `FakeUserHome`
    to a `TempDir` and delete `PathHelpersTests`; put `ProdPathFixture` on `EnvScope`; add the
    central-marker test.
15. Drop the 42 HOME attributes.

**Part 3**

16. Add `Microsoft.CodeAnalysis.BannedApiAnalyzers` for `GetFolderPath`, once part 2 leaves it a
    single call site.

No architectural guard test is needed for either static — deletion is the enforcement, and the seam
keeps two structural defences: a child cannot be spawned without an explicit variable, and the
assembly pin cannot be created, so a bypass fails loudly. A guard that greps source text asserts
nothing about behaviour.

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
| `GetFolderPath` | part 3, after part 2 | 1 — inside `UserHome.FromEnvironment()` |
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
- Reshaping the harness `*Paths` classes beyond removing their fallback. Their parameter-injection
  seam is correct; folding them into a context would be a regression, as AI-2009 concluded.
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
