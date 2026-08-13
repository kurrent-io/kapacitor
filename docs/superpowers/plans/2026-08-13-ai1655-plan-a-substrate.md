# AI-1655 Plan A — CLI/Daemon/Core Substrate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land every CLI, daemon, and Core change the onboarding wizard depends on — headless-complete and independently shippable (PR 1 of 3 for AI-1655).

**Architecture:** Three layers of change: (1) Core groundwork — a locked config-mutation API replacing `AppConfig.SaveProfileConfig`, pure-input agent detection, and a telemetry-suppression marker; (2) consent substrate — daemon boot-time policy seeding/expectation enforcement behind unit-baked directives, a boot-refusal marker, the `ConsentRulesPutV2` identity-conditional IPC frame, and `pid`/`instance_id` identity on the hello/status DTOs with client-side correlation; (3) service-verb hardening — embedded daemon digest, gated `service start --verify` (exit 28/29), install/replace digest rechecks, detached-start digest gate (exit 43), and new `unit_*` status fields. Spec: `docs/superpowers/specs/2026-08-12-ai1655-onboarding-wizard-design.md` (§2 decisions 4, 7–10; §3 step 7; §4 marker/carrier contracts; §6; §8; §11).

**Tech Stack:** .NET 10 NativeAOT, TUnit on MTP, System.Text.Json source-gen, launchd (macOS), MSBuild/MinVer.

## Global Constraints

- Zero new CLI flags or verbs; user-visible CLI behavior unchanged → no README/help churn.
- All new env controls are exact-literal: `KCAP_CONSENT_SEED_DEFAULT` accepts ONLY `prompt`; any other value fails closed.
- New exit codes/tokens (pinned): `VerifyExit.StartGate = 28` / `"verify_start_gate"`, `VerifyExit.StartGateDrift = 29` / `"verify_start_gate_drift"`, detached digest refusal exit `43` with stderr line `daemon_start_reason=package_inconsistent`.
- Reason lines are prefix-parsed; exactly one MATCHING line required; zero/duplicate/conflicting matching lines fail closed; unrelated stderr never affects routing.
- `start_gate_reason=` enum is TOTAL: `directive_missing|directive_invalid|identity_mismatch|foreign_binary|package_inconsistent|evidence_unreadable`.
- Boot-refusal marker tokens (TOTAL): `server_expectation_mismatch`, `consent_seed_unwritable`, `consent_seed_invalid`.
- New IPC: `FrameType.ConsentRulesPutV2 = 19` (next free client→daemon byte), capability `"consent/3"`; additive DTO members only, NO protocol bump (`HelloProtocol.CurrentVersion` stays 1).
- Daemon refusals exit **0** (KeepAlive no-respin rule, AI-1654 decision 6).
- JsonArray: use `new JsonArray(a, b)` constructor, never collection expressions (AOT).
- Every task's tests: TUnit `[Test]`; real-socket tests carry `[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]` and a Windows early-return guard; path assertions use `Path.Combine` (Windows CI leg).
- After all tasks: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release` and the daemon equivalent must show zero IL2xxx/IL3xxx warnings.
- Run unit tests with `TMPDIR=/private/tmp` on macOS (`dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`); filter one class with `--treenode-filter "/*/*/ClassName/*"`.
- Commit after every task; messages reference AI-1655.

---

### Task 1: Telemetry spawn marker (`KCAP_APP_SPAWN_NO_TELEMETRY`)

**Files:**
- Modify: `src/Capacitor.Cli.Core/Telemetry/CliTelemetry.cs` (add suppression seam)
- Modify: `src/Capacitor.Cli/Program.cs:79-115` (consume-and-remove before `CliTelemetry.Initialize`)
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/SpawnMarkerTests.cs` (new)

**Interfaces:**
- Consumes: `CliTelemetry.Initialize(string command, string? serverUrl, bool loggedIn)` (existing).
- Produces: `public const string SpawnNoTelemetryVar = "KCAP_APP_SPAWN_NO_TELEMETRY"` on `CliTelemetry`, and `public static bool ConsumeSpawnMarker(Func<string, string?> get, Action<string> clear)` returning true when the marker was present (and removing it). Plan B's app overlays this variable on every spawned CLI child.

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/Telemetry/SpawnMarkerTests.cs
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class SpawnMarkerTests {
    [Test]
    public async Task Consume_removes_marker_and_reports_presence() {
        var env = new Dictionary<string, string?> { [CliTelemetry.SpawnNoTelemetryVar] = "1" };
        var suppressed = CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(suppressed).IsTrue();
        await Assert.That(env.ContainsKey(CliTelemetry.SpawnNoTelemetryVar)).IsFalse();
    }

    [Test]
    public async Task Consume_without_marker_is_inert() {
        var env = new Dictionary<string, string?>();
        var suppressed = CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(suppressed).IsFalse();
    }

    [Test]
    public async Task Marker_does_not_touch_users_own_KCAP_TELEMETRY() {
        var env = new Dictionary<string, string?> {
            [CliTelemetry.SpawnNoTelemetryVar] = "1",
            ["KCAP_TELEMETRY"] = "1",
        };
        CliTelemetry.ConsumeSpawnMarker(k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(env["KCAP_TELEMETRY"]).IsEqualTo("1");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `TMPDIR=/private/tmp dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/SpawnMarkerTests/*"`
Expected: compile FAIL — `SpawnNoTelemetryVar` / `ConsumeSpawnMarker` not defined.

- [ ] **Step 3: Implement the seam in `CliTelemetry`**

Add to `src/Capacitor.Cli.Core/Telemetry/CliTelemetry.cs` (next to `Initialize`):

```csharp
/// The app-spawned-child marker (spec decision 9): consumed for telemetry suppression and
/// REMOVED from the process environment before command dispatch, so nothing this process
/// spawns (a detached daemon, hosted children) can observe it. Never touches the user's own
/// KCAP_TELEMETRY choice.
public const string SpawnNoTelemetryVar = "KCAP_APP_SPAWN_NO_TELEMETRY";

public static bool ConsumeSpawnMarker(Func<string, string?> get, Action<string> clear) {
    if (string.IsNullOrEmpty(get(SpawnNoTelemetryVar))) return false;
    clear(SpawnNoTelemetryVar);
    return true;
}
```

Then make `Initialize` honor a suppressed run. `Initialize(command, serverUrl, loggedIn)` currently proceeds to `NoticeAndFirstRun` + enabling. Add an optional parameter (additive, existing callers unaffected):

```csharp
public static void Initialize(string command, string? serverUrl, bool loggedIn, bool suppressed = false) {
    if (suppressed) return; // app-spawned child: no notice, no device id, no events, _client stays null
    // ... existing body unchanged ...
}
```

- [ ] **Step 4: Wire `Program.cs`**

In `src/Capacitor.Cli/Program.cs`, immediately BEFORE line 115's `CliTelemetry.Initialize(command, baseUrl, loggedIn);` insert:

```csharp
// spec decision 9: an app-spawned CLI child must not emit CLI-labeled telemetry nor consume
// the one-time privacy notice on an invisible stderr. Consume-and-REMOVE before dispatch so
// no grandchild (detached daemon, hosted agents) can observe the marker.
var telemetrySuppressed = CliTelemetry.ConsumeSpawnMarker(
    Environment.GetEnvironmentVariable,
    k => Environment.SetEnvironmentVariable(k, null));
```

and change the init call to `CliTelemetry.Initialize(command, baseUrl, loggedIn, telemetrySuppressed);`.

- [ ] **Step 5: Run tests, run the full Telemetry test class group, verify pass**

Run: `TMPDIR=/private/tmp dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/SpawnMarkerTests/*"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/CliTelemetry.cs src/Capacitor.Cli/Program.cs test/Capacitor.Cli.Tests.Unit/Telemetry/SpawnMarkerTests.cs
git commit -m "AI-1655: process-local telemetry suppression marker for app-spawned CLI children"
```

---

### Task 2: Config mutation API — pure load primitive + `MutateAsync`

**Files:**
- Create: `src/Capacitor.Cli.Core/Config/ConfigMutator.cs`
- Modify: `src/Capacitor.Cli.Core/Config/AppConfig.cs:282-377` (`LoadProfileConfig` becomes pure; `SaveProfileConfig` DELETED)
- Test: `test/Capacitor.Cli.Tests.Unit/Config/ConfigMutatorTests.cs` (new)

**Interfaces:**
- Consumes: `ConfigMigration.MigrateIfNeeded(string json)` → `MigrationResult { Config, ShouldPersist }`; `ConfigFileLock.Acquire(string path, TimeSpan? timeout)`; `ProfileConfigJsonContextIndented.Default.ProfileConfig`; `AppConfig.GetConfigPath()`.
- Produces:
  - `public static class ConfigMutator` with `public static Task<ProfileConfig> MutateAsync(Func<ProfileConfig, ProfileConfig> mutate, CancellationToken ct = default)` — acquires `ConfigFileLock` on `config.json`, re-reads via the pure primitive, applies migration in memory, applies `mutate`, publishes via **unique** temp + rename, all inside a synchronous critical section on one thread (async callers get `Task.Run` internally). Returns the published config.
  - `AppConfig.LoadProfileConfig` keeps its signature but becomes **pure** (never writes); its migration-persist branch routes through `ConfigMutator.MutateAsync(c => c, ct)`.
  - `AppConfig.SaveProfileConfig` is **deleted** (compile errors drive Task 3's caller migration).

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/Config/ConfigMutatorTests.cs
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Config;

[NotInParallel("KCAP_CONFIG_DIR")] // mutates the config root via env
public class ConfigMutatorTests {
    static IDisposable TempConfigDir(out string dir) {
        var d = Directory.CreateTempSubdirectory("kcap-cfg-").FullName;
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", d);
        dir = d;
        return new Cleanup(d);
    }

    sealed class Cleanup(string dir) : IDisposable {
        public void Dispose() {
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Test]
    public async Task Mutate_preserves_unrelated_fields_written_by_a_concurrent_style_writer() {
        using var _ = TempConfigDir(out var dir);
        // seed: profile "a" with a server URL
        await ConfigMutator.MutateAsync(c => c with {
            Profiles = new(c.Profiles) { ["a"] = new Profile { ServerUrl = "https://a.example" } },
        });
        // writer 1 sets machine_id; writer 2 (stale-snapshot style: mutation function only
        // touches its own field) sets active_profile — both must survive.
        await ConfigMutator.MutateAsync(c => c with { MachineId = "m-123" });
        await ConfigMutator.MutateAsync(c => c with { ActiveProfile = "a" });

        var final = await AppConfig.LoadProfileConfig();
        await Assert.That(final.MachineId).IsEqualTo("m-123");
        await Assert.That(final.ActiveProfile).IsEqualTo("a");
        await Assert.That(final.Profiles["a"].ServerUrl).IsEqualTo("https://a.example");
    }

    [Test]
    public async Task Mutate_uses_unique_temp_names() {
        using var _ = TempConfigDir(out var dir);
        // two concurrent mutations must not collide on a shared fixed .tmp name
        var t1 = ConfigMutator.MutateAsync(c => c with { MachineId = "one" });
        var t2 = ConfigMutator.MutateAsync(c => c with { ActiveProfile = "p2" });
        await Task.WhenAll(t1, t2);

        var final = await AppConfig.LoadProfileConfig();
        await Assert.That(final.MachineId).IsEqualTo("one");
        await Assert.That(final.ActiveProfile).IsEqualTo("p2");
        // no orphaned fixed-name temp file
        await Assert.That(File.Exists(Path.Combine(dir, "config.json.tmp"))).IsFalse();
    }

    [Test]
    public async Task Legacy_v1_config_is_migrated_in_memory_and_persisted_through_the_mutation() {
        using var _ = TempConfigDir(out var dir);
        // minimal v1 flat config (no "version"/"profiles" — ConfigMigration's v1 shape)
        await File.WriteAllTextAsync(Path.Combine(dir, "config.json"),
            """{"server_url":"https://legacy.example"}""");

        var result = await ConfigMutator.MutateAsync(c => c with { MachineId = "post-migration" });

        await Assert.That(result.Version).IsEqualTo(2);
        await Assert.That(result.MachineId).IsEqualTo("post-migration");
        // migration survived the same publication as the mutation
        var reread = await AppConfig.LoadProfileConfig();
        await Assert.That(reread.Version).IsEqualTo(2);
        await Assert.That(reread.MachineId).IsEqualTo("post-migration");
    }

    [Test]
    public async Task LoadProfileConfig_is_pure_and_never_writes() {
        using var _ = TempConfigDir(out var dir);
        var cfgPath = Path.Combine(dir, "config.json");
        await File.WriteAllTextAsync(cfgPath, """{"server_url":"https://legacy.example"}""");
        var before = File.GetLastWriteTimeUtc(cfgPath);

        var cfg = await AppConfig.LoadProfileConfig();

        await Assert.That(cfg.Version).IsEqualTo(2);           // migrated in memory
        // NOTE: LoadProfileConfig routes persistence through MutateAsync — one write is
        // allowed here (the legacy-persist behavior), but the FILE must now be v2 and valid.
        var reread = JsonDocument.Parse(await File.ReadAllTextAsync(cfgPath));
        await Assert.That(reread.RootElement.TryGetProperty("version", out var v) && v.GetInt32() == 2).IsTrue();
        _ = before;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `TMPDIR=/private/tmp dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ConfigMutatorTests/*"`
Expected: compile FAIL — `ConfigMutator` not defined.

- [ ] **Step 3: Implement `ConfigMutator` and refactor `AppConfig`**

Create `src/Capacitor.Cli.Core/Config/ConfigMutator.cs`:

```csharp
using System.Text.Json;

namespace Capacitor.Cli.Core.Config;

/// The ONE writer of config.json (spec decision 10). Field-scoped mutation under
/// ConfigFileLock: lock → re-read fresh → migrate in memory → apply the caller's mutation →
/// publish via UNIQUE temp + rename. The critical section is synchronous on one thread —
/// ConfigFileLock is a thread-affine named Mutex (WaitOne/ReleaseMutex), so no await may
/// occur while it is held; async callers are wrapped in Task.Run here.
public static class ConfigMutator {
    public static Task<ProfileConfig> MutateAsync(
            Func<ProfileConfig, ProfileConfig> mutate, CancellationToken ct = default) =>
        Task.Run(() => Mutate(mutate), ct);

    public static ProfileConfig Mutate(Func<ProfileConfig, ProfileConfig> mutate) {
        var path = AppConfig.GetConfigPath();
        var dir  = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        using (ConfigFileLock.Acquire(path)) {
            var current = LoadPure(path);           // fresh re-read + in-memory migration
            var next    = mutate(current);
            Publish(path, next);
            return next;
        }
    }

    /// Pure load: parse + migrate in memory, NEVER writes (decision 10 — the legacy
    /// LoadProfileConfig persisted the v1→v2 migration during load, which under this API
    /// would recursively acquire the same thread-affine mutex).
    public static ProfileConfig LoadPure(string path) {
        if (!File.Exists(path))
            return new() { Profiles = new() { ["default"] = new() } };

        string json;
        try {
            json = File.ReadAllText(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return new() { Profiles = new() { ["default"] = new() } };
        }

        try {
            return ConfigMigration.MigrateIfNeeded(json).Config;
        } catch (JsonException) {
            return new() { Profiles = new() { ["default"] = new() } };
        }
    }

    static void Publish(string path, ProfileConfig config) {
        var tmp = $"{path}.tmp-{Guid.NewGuid():N}"[..(path.Length + 13)];
        File.WriteAllBytes(tmp,
            JsonSerializer.SerializeToUtf8Bytes(config, ProfileConfigJsonContextIndented.Default.ProfileConfig));
        File.Move(tmp, path, overwrite: true);
    }
}
```

(If the `[..(path.Length + 13)]` slice reads awkwardly during implementation, use `var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];` — the `LaunchConsentStore.TryReplace` precedent.)

In `src/Capacitor.Cli.Core/Config/AppConfig.cs`:
1. **Delete** `SaveProfileConfig` (lines 365-377) entirely.
2. Rewrite `LoadProfileConfig`'s persist branch (lines 310-333): replace `await SaveProfileConfig(result.Config, ct);` with `await ConfigMutator.MutateAsync(c => c, ct);` (identity mutation — `MutateAsync` re-reads and re-migrates under the lock, publishing the migrated form). Keep the one-time stderr notice and the warning catch exactly as they are.
3. Keep `NormalizeProfileVisibilities(result.Config)` as the return.

- [ ] **Step 4: Build Core only, confirm the expected caller breakage list**

Run: `dotnet build src/Capacitor.Cli.Core/Capacitor.Cli.Core.csproj`
Expected: Core compiles (its internal callers `WorkOSDiscovery`, `MachineIdProvider` will FAIL — that is Task 3's driver; if Core itself fails on those two, fix them NOW as part of this task using the same pattern as Task 3 Step 1 and note it in the commit).

- [ ] **Step 5: Migrate the two Core callers (`WorkOSDiscovery`, `MachineIdProvider`)**

Pattern — a read-modify-write like:

```csharp
var config = await AppConfig.LoadProfileConfig(ct);
var updated = /* ... build new config from `config` ... */;
await AppConfig.SaveProfileConfig(updated, ct);
```

becomes a field-scoped mutation that re-derives its change from the FRESH snapshot:

```csharp
await ConfigMutator.MutateAsync(c => /* same transformation applied to c */, ct);
```

Concretely: in `WorkOSDiscovery` the profile-merge write becomes `await ConfigMutator.MutateAsync(c => TenantDiscovery.MergeProfiles(c, tenants, ...), ct)` (whatever expression previously produced the saved config, applied to `c`). In `MachineIdProvider`, the machine-id stamp becomes `await ConfigMutator.MutateAsync(c => c.MachineId is null ? c with { MachineId = newId } : c, ct)`.

- [ ] **Step 6: Run the new tests + full Core-touching unit groups**

Run: `TMPDIR=/private/tmp dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/ConfigMutatorTests/*"`
Expected: PASS (4 tests). (The CLI project still fails to build — Task 3.)

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.Cli.Core/Config/ConfigMutator.cs src/Capacitor.Cli.Core/Config/AppConfig.cs src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs src/Capacitor.Cli.Core/MachineIdProvider.cs test/Capacitor.Cli.Tests.Unit/Config/ConfigMutatorTests.cs
git commit -m "AI-1655: locked field-scoped config mutation API; SaveProfileConfig deleted"
```

---

### Task 3: Config mutation API — migrate all CLI callers

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ConfigCommand.cs`, `ProfileCommand.cs`, `UseCommand.cs`, `UpdateCommand.cs`, `IgnoreCommand.cs`, `RemapCommand.cs`, `ImportCommand.cs`, `SetupCommand.cs`, `src/Capacitor.Cli/Program.cs` (each site that called `AppConfig.SaveProfileConfig` or wrote `config.json` itself)
- Test: `test/Capacitor.Cli.Tests.Unit/Config/ConfigWriterMigrationTests.cs` (new)

**Interfaces:**
- Consumes: `ConfigMutator.MutateAsync(Func<ProfileConfig, ProfileConfig>, CancellationToken)` from Task 2.
- Produces: zero remaining `SaveProfileConfig` references repo-wide; `ProfileCommand`/`UseCommand`'s private atomic-save helpers deleted.

- [ ] **Step 1: Enumerate all break sites**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj 2>&1 | grep -E "SaveProfileConfig|error CS"`
Also: `grep -rn "config.json.tmp\|SaveProfileConfig" src/ --include='*.cs'`
Expected: the compiler lists every caller; `ProfileCommand.cs:182-192` and `UseCommand.cs` have their OWN fixed-temp writers to delete too.

- [ ] **Step 2: Migrate every site to a field-scoped mutation**

The mechanical rule for each site: the code currently loads a config, computes a modified copy, and saves it. Move the *computation* inside the lambda so it applies to the fresh under-lock snapshot. Examples of the exact transformations:

`ConfigCommand` `set` (whole-doc load-modify-save around lines 66-74/95-103):

```csharp
// BEFORE (shape):
var config = await AppConfig.LoadProfileConfig();
var profile = config.Profiles[name] with { ServerUrl = value };
await AppConfig.SaveProfileConfig(config with { Profiles = new(config.Profiles) { [name] = profile } });

// AFTER:
await ConfigMutator.MutateAsync(c => {
    var p = c.Profiles.GetValueOrDefault(name) ?? new Profile();
    return c with { Profiles = new(c.Profiles) { [name] = p with { ServerUrl = value } } };
});
```

`UseCommand` (binding write): `await ConfigMutator.MutateAsync(c => c with { ProfileBindings = new(c.ProfileBindings) { [repoRoot] = profileName } });` (and `ActiveProfile` for `--global`). Delete its private save helper.

`ProfileCommand` `add`/`remove`: same pattern on `Profiles`; delete the private atomic-save helper at 182-192.

`UpdateCommand`/`IgnoreCommand`/`RemapCommand`/`ImportCommand` (remembered org)/`SetupCommand`/`Program.cs`: identical mechanical treatment — each existing computed change becomes a lambda over `c`.

- [ ] **Step 3: Write the cross-writer race test**

```csharp
// test/Capacitor.Cli.Tests.Unit/Config/ConfigWriterMigrationTests.cs
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Config;

[NotInParallel("KCAP_CONFIG_DIR")]
public class ConfigWriterMigrationTests {
    [Test]
    public async Task No_writer_bypasses_the_mutator() {
        // Compile-time guarantee is the main assertion (SaveProfileConfig no longer exists);
        // this test pins the runtime behavior: two interleaved field-scoped mutations from
        // different "commands" both survive.
        var dir = Directory.CreateTempSubdirectory("kcap-cfg-race-").FullName;
        Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", dir);
        try {
            var writers = Enumerable.Range(0, 16).Select(i => ConfigMutator.MutateAsync(c => c with {
                Profiles = new(c.Profiles) { [$"p{i}"] = new Profile { ServerUrl = $"https://p{i}.example" } },
            }));
            await Task.WhenAll(writers);

            var final = await AppConfig.LoadProfileConfig();
            for (var i = 0; i < 16; i++)
                await Assert.That(final.Profiles.ContainsKey($"p{i}")).IsTrue();
        } finally {
            Environment.SetEnvironmentVariable("KCAP_CONFIG_DIR", null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
```

- [ ] **Step 4: Full build + full unit suite**

Run: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` → zero errors, then
`TMPDIR=/private/tmp dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS (existing command tests keep passing — behavior-preserving migration).

- [ ] **Step 5: Commit**

```bash
git add -A src/ test/Capacitor.Cli.Tests.Unit/Config/ConfigWriterMigrationTests.cs
git commit -m "AI-1655: migrate every config.json writer to ConfigMutator"
```

---

### Task 4: Pure-input overloads on env-reading path helpers

**Files:**
- Modify: `src/Capacitor.Cli.Core/Kiro/KiroPaths.cs:20` (`ConfigRoot` gains `kiroHome` parameter)
- Modify: `src/Capacitor.Cli.Core/OpenCode/OpenCodePaths.cs:26` (`DataDir` gains `xdgDataHome` parameter), `:62` (`IsInstalled` gains pass-through params)
- Test: `test/Capacitor.Cli.Tests.Unit/PurePathOverloadTests.cs` (new)

**Interfaces:**
- Consumes: existing helper shapes (report §10): `KiroPaths.ConfigRoot(string? home = null)`, `OpenCodePaths.ConfigDir(string? home, string? configDir)`, `OpenCodePaths.DataDir(string? home)`, `PiPaths.AgentDir(string? home, string? agentDir)` (already pure-capable), `GeminiPaths.IsInstalled(string? home, string? geminiCliHome)` (already pure-capable).
- Produces: `KiroPaths.ConfigRoot(string? home = null, string? kiroHome = null)` and `KiroPaths.IsInstalled(string? home = null, string? kiroHome = null)`; `OpenCodePaths.DataDir(string? home = null, string? xdgDataHome = null)`, `OpenCodePaths.ConfigDir(string? home = null, string? configDir = null, string? xdgConfigHome = null)`, `OpenCodePaths.IsInstalled(string? home = null, string? configDir = null, string? xdgConfigHome = null, string? xdgDataHome = null)`. Every added param defaults to the env read (existing global-reading entry points delegate to the pure form — zero behavior change for current callers).

- [ ] **Step 1: Write the failing test**

```csharp
// test/Capacitor.Cli.Tests.Unit/PurePathOverloadTests.cs
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.OpenCode;

namespace Capacitor.Cli.Tests.Unit;

public class PurePathOverloadTests {
    [Test]
    public async Task KiroPaths_honors_injected_kiro_home_without_env() {
        var root = KiroPaths.ConfigRoot(home: "/h", kiroHome: "/custom/kiro");
        await Assert.That(root).IsEqualTo("/custom/kiro");
        await Assert.That(KiroPaths.ConfigRoot(home: "/h", kiroHome: null))
            .IsEqualTo(Path.Combine("/h", ".kiro"));
    }

    [Test]
    public async Task OpenCodePaths_honors_injected_xdg_values_without_env() {
        await Assert.That(OpenCodePaths.ConfigDir(home: "/h", configDir: null, xdgConfigHome: "/xdgc"))
            .IsEqualTo(Path.Combine("/xdgc", "opencode"));
        await Assert.That(OpenCodePaths.DataDir(home: "/h", xdgDataHome: "/xdgd"))
            .IsEqualTo(Path.Combine("/xdgd", "opencode"));
        await Assert.That(OpenCodePaths.DataDir(home: "/h", xdgDataHome: null))
            .IsEqualTo(Path.Combine("/h", ".local", "share", "opencode"));
    }

    [Test]
    public async Task Injected_values_run_in_parallel_without_process_env_mutation() {
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            KiroPaths.IsInstalled(home: $"/nonexistent-{i}", kiroHome: null))));
        await Assert.That(results.All(r => r == false)).IsTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails** (compile error: no such overloads).

- [ ] **Step 3: Implement the overloads**

`KiroPaths.cs`:

```csharp
public static string ConfigRoot(string? home = null, string? kiroHome = null) {
    kiroHome ??= Environment.GetEnvironmentVariable("KIRO_HOME");
    if (!string.IsNullOrEmpty(kiroHome)) return kiroHome;

    home ??= PathHelpers.HomeDirectory;
    return Path.Combine(home, ".kiro");
}

public static bool IsInstalled(string? home = null, string? kiroHome = null) =>
    Directory.Exists(ConfigRoot(home, kiroHome));
```

(The existing single-param call sites keep compiling — the new parameter is optional and defaults to the env read.) Apply the same shape to `OpenCodePaths.ConfigDir` (add `xdgConfigHome` param defaulting to `Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")`), `DataDir` (add `xdgDataHome`), and `IsInstalled` (pass-through). Thread the params through `PluginsDir`/`McpConfigJson`/`AgentsMd` only where they already forward `home`/`configDir` (do not widen signatures nobody needs).

- [ ] **Step 4: Run tests + full unit suite** (existing path tests must keep passing). Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Kiro/KiroPaths.cs src/Capacitor.Cli.Core/OpenCode/OpenCodePaths.cs test/Capacitor.Cli.Tests.Unit/PurePathOverloadTests.cs
git commit -m "AI-1655: pure-input overloads for env-reading path helpers"
```

---

### Task 5: `AgentDetection` in Core + `SetupCommand` consumption

**Files:**
- Create: `src/Capacitor.Cli.Core/Setup/AgentDetection.cs` (move of `AgentDetector` + composition)
- Modify: `src/Capacitor.Cli/Commands/AgentDetector.cs` (becomes a thin delegating shim, or delete + fix usings)
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs:210-238` (consume the Core composition)
- Test: `test/Capacitor.Cli.Tests.Unit/Setup/AgentDetectionTests.cs` (new)

**Interfaces:**
- Consumes: Task 4's pure overloads; `CursorPaths.IsInstalled(string? home, OsPlatform? platform)`; `GeminiPaths.IsInstalled(home, geminiCliHome)`; `AntigravityPaths.IsInstalled(home, geminiCliHome)`; `CopilotPaths.IsInstalled()`.
- Produces (in `Capacitor.Cli.Core.Setup`):

```csharp
public sealed record AgentDetectionInputs(
    string? PathEnv, string? PathExt, bool IsWindows, string? Home,
    Func<string, string?> Env);          // product overrides: KIRO_HOME, PI_CODING_AGENT_DIR, OPENCODE_CONFIG_DIR, XDG_*, GEMINI_CLI_HOME

public sealed record DetectedAgent(bool BinaryFound, bool InstallSignalFound) {
    public bool Detected => BinaryFound || InstallSignalFound;
}

public sealed record AgentDetectionResult(
    DetectedAgent Claude, DetectedAgent Codex, DetectedAgent Cursor, DetectedAgent Copilot,
    DetectedAgent Gemini, DetectedAgent Kiro, DetectedAgent Pi, DetectedAgent OpenCode,
    DetectedAgent Antigravity);

public static class AgentDetection {
    public static AgentDetectionResult Detect(AgentDetectionInputs inputs);
    public static AgentDetectionInputs FromEnvironment();                  // current-process defaults
    public static bool BinaryOnPath(string binaryName, AgentDetectionInputs inputs); // the moved PATH walk
}
```

- [ ] **Step 1: Write the failing parity tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/Setup/AgentDetectionTests.cs
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit.Setup;

public class AgentDetectionTests {
    static AgentDetectionInputs Inputs(string? pathEnv = null, string? home = null,
            Dictionary<string, string?>? env = null) =>
        new(pathEnv, PathExt: null, IsWindows: false, Home: home,
            Env: k => env?.GetValueOrDefault(k));

    [Test]
    public async Task Binary_probe_walks_injected_path_with_execute_bit() {
        var dir = Directory.CreateTempSubdirectory("kcap-detect-").FullName;
        var claude = Path.Combine(dir, "claude");
        await File.WriteAllTextAsync(claude, "#!/bin/sh\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var r = AgentDetection.Detect(Inputs(pathEnv: dir, home: "/nonexistent"));
        await Assert.That(r.Claude.BinaryFound).IsTrue();
        await Assert.That(r.Codex.BinaryFound).IsFalse();
    }

    [Test]
    public async Task Gemini_marker_rules_bare_dot_gemini_is_NOT_installed() {
        var home = Directory.CreateTempSubdirectory("kcap-detect-home-").FullName;
        Directory.CreateDirectory(Path.Combine(home, ".gemini")); // bare dir, no markers
        var r = AgentDetection.Detect(Inputs(pathEnv: "", home: home));
        await Assert.That(r.Gemini.InstallSignalFound).IsFalse();

        await File.WriteAllTextAsync(Path.Combine(home, ".gemini", "settings.json"), "{}");
        var r2 = AgentDetection.Detect(Inputs(pathEnv: "", home: home));
        await Assert.That(r2.Gemini.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task Kiro_binary_probe_includes_kiro_cli_and_home_signal_honors_injected_override() {
        var kiroHome = Directory.CreateTempSubdirectory("kcap-kiro-").FullName;
        var r = AgentDetection.Detect(Inputs(pathEnv: "", home: "/nonexistent",
            env: new() { ["KIRO_HOME"] = kiroHome }));
        await Assert.That(r.Kiro.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task Antigravity_probes_both_agy_and_antigravity_binaries() {
        var dir = Directory.CreateTempSubdirectory("kcap-agy-").FullName;
        var agy = Path.Combine(dir, "agy");
        await File.WriteAllTextAsync(agy, "#!/bin/sh\n");
        File.SetUnixFileMode(agy, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var r = AgentDetection.Detect(Inputs(pathEnv: dir, home: "/nonexistent"));
        await Assert.That(r.Antigravity.BinaryFound).IsTrue();
    }

    [Test]
    public async Task Unreadable_path_entries_do_not_throw() {
        var r = AgentDetection.Detect(Inputs(pathEnv: "/nonexistent-a:/nonexistent-b", home: "/nonexistent"));
        await Assert.That(r.Claude.Detected).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify compile failure.**

- [ ] **Step 3: Implement `AgentDetection`**

Move the exact `AgentDetector` internals into `Capacitor.Cli.Core.Setup.AgentDetection` (same PATH walk, PATHEXT handling, execute-bit check — verbatim from the report §9, but every env read replaced by the `inputs` values). Composition (mirrors `SetupCommand.cs:211-238` verbatim semantics):

```csharp
public static AgentDetectionResult Detect(AgentDetectionInputs i) {
    bool Bin(string name) => BinaryOnPath(name, i);
    var home = i.Home;
    var geminiCliHome = i.Env("GEMINI_CLI_HOME");

    return new(
        Claude:  new(Bin("claude"), false),
        Codex:   new(Bin("codex"), false),
        Cursor:  new(false, CursorPaths.IsInstalled(home)),
        Copilot: new(Bin("copilot"), CopilotPaths.IsInstalled()),
        Gemini:  new(Bin("gemini"), GeminiPaths.IsInstalled(home, geminiCliHome)),
        Kiro:    new(Bin("kiro") || Bin("kiro-cli"), KiroPaths.IsInstalled(home, i.Env("KIRO_HOME"))),
        Pi:      new(Bin("pi"), PiPaths.IsInstalled(home)),   // PiPaths.AgentDir already accepts agentDir; add pass-through if needed
        OpenCode: new(Bin("opencode"),
            OpenCodePaths.IsInstalled(home, i.Env("OPENCODE_CONFIG_DIR"), i.Env("XDG_CONFIG_HOME"), i.Env("XDG_DATA_HOME"))),
        Antigravity: new(Bin("antigravity") || Bin("agy"), AntigravityPaths.IsInstalled(home, geminiCliHome)));
}
```

`BinaryOnPath` = the moved `AgentDetector.IsInstalled(binaryName, paths, extensions, isExecutable)` with `paths` from `i.PathEnv?.Split(Path.PathSeparator) ?? []` and extensions from `i.PathExt`/`i.IsWindows`. `FromEnvironment()` supplies `Environment.GetEnvironmentVariable("PATH")`, `"PATHEXT"`, `OperatingSystem.IsWindows()`, `PathHelpers.HomeDirectory`, `Environment.GetEnvironmentVariable`.

Note: `CursorPaths.IsInstalled(home)` may need its existing `platform` param defaulted — keep its current signature.

- [ ] **Step 4: Point `SetupCommand` at the composition**

Replace `SetupCommand.cs:211-238` with:

```csharp
var r = Capacitor.Cli.Core.Setup.AgentDetection.Detect(Capacitor.Cli.Core.Setup.AgentDetection.FromEnvironment());
var detected = new CodingAgentsStep.DetectedAgents(
    Claude: r.Claude.Detected, Codex: r.Codex.Detected, Cursor: r.Cursor.Detected,
    Copilot: r.Copilot.Detected, Gemini: r.Gemini.Detected, Kiro: r.Kiro.Detected,
    Pi: r.Pi.Detected, OpenCode: r.OpenCode.Detected, Antigravity: r.Antigravity.Detected);
```

Keep `Capacitor.Cli/Commands/AgentDetector.cs` as a one-line delegating shim if other CLI call sites exist (`grep -rn "AgentDetector.IsInstalled" src/`); otherwise delete it and fix usings.

- [ ] **Step 5: Run tests + existing SetupDecisions/Setup tests. Expected: PASS.**

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Core/Setup/AgentDetection.cs src/Capacitor.Cli/Commands/AgentDetector.cs src/Capacitor.Cli/Commands/SetupCommand.cs test/Capacitor.Cli.Tests.Unit/Setup/AgentDetectionTests.cs
git commit -m "AI-1655: AgentDetection moves to Core with pure inputs"
```

---

### Task 6: `pid` + `instance_id` on hello/status DTOs

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/HelloIpc.cs` (`HelloReplyDto`)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs` (`DaemonInfoDto`)
- Modify: `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs:103-106` (fill hello), `src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs` (fill status — find the `DaemonInfoDto` construction site)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/LocalControlHelloTests.cs` and `DaemonStatusIpcTests.cs`

**Interfaces:**
- Consumes: `DaemonConfig.InstanceId` (set from `daemonLock.InstanceId` at `DaemonRunner.cs:250`; in the test harness set it explicitly on the `DaemonConfig`).
- Produces (additive members, snake_case via existing contexts — old clients skip them):

```csharp
public sealed record HelloReplyDto(
    int ProtocolVersion, string DaemonVersion, string DaemonName, List<string>? Capabilities,
    int? Pid = null, string? InstanceId = null);

public sealed record DaemonInfoDto(
    string Name, string Version, string ServerUrl, string Connection, int MaxAgents, int ActiveAgents,
    int? Pid = null, string? InstanceId = null);
```

- [ ] **Step 1: Write the failing test** (extend `LocalControlHelloTests` — same harness):

```csharp
[Test]
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public async Task Hello_reply_carries_pid_and_instance_id() {
    if (OperatingSystem.IsWindows()) return;
    await RunAsync("hello-id", async (h, ct) => {
        h.Config.InstanceId = "inst-test-1";
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
        var frame = await FrameCodec.ReadAsync(s, ct);
        var dto = JsonSerializer.Deserialize(frame!.Value.Payload, HelloIpcJsonContext.Default.HelloReplyDto);

        await Assert.That(dto!.Pid).IsEqualTo(Environment.ProcessId);
        await Assert.That(dto.InstanceId).IsEqualTo("inst-test-1");
    });
}
```

And in `DaemonStatusIpcTests` (same shape): subscribe, read the first `DaemonStatus` frame, assert `dto.Daemon.Pid == Environment.ProcessId` and `dto.Daemon.InstanceId == h.Config.InstanceId`.

- [ ] **Step 2: Run to verify FAIL** (positional-record compile or null assertions).

- [ ] **Step 3: Implement** — add the optional members to both records; in `LocalControlServer.HandleHelloAsync` construct `new HelloReplyDto(HelloProtocol.CurrentVersion, DaemonRunner.ResolveDaemonVersion(), config.Name, [.. LocalControlCapabilities.Current], Environment.ProcessId, config.InstanceId)`; in `DaemonStatusIpc` add `Environment.ProcessId, config.InstanceId` to the `DaemonInfoDto` construction. Exact-JSON tests that assert full payload strings will need the two new snake_case members added (`"pid"`, `"instance_id"`) — update those fixtures, do NOT loosen them.

- [ ] **Step 4: Run the Daemon test group. Expected: PASS (incl. updated exact-JSON fixtures).**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/HelloIpc.cs src/Capacitor.Cli.Core/LocalIpc/StatusIpc.cs src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs src/Capacitor.Cli.Daemon/Services/DaemonStatusIpc.cs test/
git commit -m "AI-1655: additive pid/instance_id identity on hello and status DTOs"
```

---

### Task 7: `LocalControlClient` hello↔snapshot correlation

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalControlClient.cs` (`CycleOutcome`, `RunCycleAsync`, `Connected` event)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Daemon/` client tests (find the existing `LocalControlClient` suite via `grep -rln "LocalControlClient" test/`)

**Interfaces:**
- Consumes: Task 6's DTO members.
- Produces: `LocalControlEvent.Connected` gains additive identity — new record shape:

```csharp
public sealed record ConnectedIdentity(int? Pid, string? InstanceId, string DaemonName, string DaemonVersion);

public sealed record Connected(
    IReadOnlyList<string>? Capabilities, DaemonStatusDto FirstSnapshot,
    ConnectedIdentity? Identity = null) : LocalControlEvent;
```

Invariant: when BOTH hello and the first snapshot carry `pid`/`instance_id` and they disagree, the cycle is classified `daemon_incompatible` (retried) — `Connected` is NEVER emitted on a mismatch. When either side lacks the fields (pre-slice daemon), `Identity` is populated from hello alone and no mismatch is inferred (old daemons remain attachable; the consumer decides what identity-less means).

- [ ] **Step 1: Write the failing test** — real-socket harness with a scripted server (follow the existing client-test pattern in the suite): serve a hello reply with `pid: 111, instance_id: "A"`, then a first snapshot whose `daemon` carries `pid: 222, instance_id: "B"`. Assert the client does NOT emit `Connected` and the surfaced event is `Unreachable("daemon_incompatible", ...)`. Second test: matching ids → `Connected` with `Identity.Pid == 111 && Identity.InstanceId == "A"`. Third: hello without the fields → `Connected` with `Identity.Pid == null`, no mismatch.

```csharp
[Test]
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public async Task Mismatched_hello_and_snapshot_identity_never_emits_Connected() {
    if (OperatingSystem.IsWindows()) return;
    // Use the suite's scripted-socket helper: hello reply from process A, snapshot from process B.
    // (Concretely: serve HelloReply {pid:111,instance_id:"A"} then DaemonStatus whose DaemonInfoDto
    // has {pid:222,instance_id:"B"}; iterate client.RunAsync and collect events until Unreachable.)
    var events = await RunScriptedCycleAsync(
        helloJson:  """{"protocol_version":1,"daemon_version":"1.0.0","daemon_name":"x","capabilities":["consent/1","status/1"],"pid":111,"instance_id":"A"}""",
        statusJson: """{"daemon":{"name":"x","version":"1.0.0","server_url":"http://s","connection":"connected","max_agents":5,"active_agents":0,"pid":222,"instance_id":"B"},"agents":[]}""");

    await Assert.That(events.OfType<LocalControlEvent.Connected>()).IsEmpty();
    await Assert.That(events.OfType<LocalControlEvent.Unreachable>().Any(u => u.Reason == "daemon_incompatible")).IsTrue();
}
```

(`RunScriptedCycleAsync` is a test helper you add beside the suite's existing scripted-server plumbing — a Unix socket accepting one hello connection answering `helloJson` then EOF, and one subscribe connection answering `statusJson` then holding open.)

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement** — in `RunCycleAsync` (lines 162-201): retain the parsed `HelloReplyDto` (today discarded after capability extraction); after `ReadSnapshotAsync` returns the first snapshot, compare `hello.Pid`/`hello.InstanceId` against `snapshot.Daemon.Pid`/`snapshot.Daemon.InstanceId` — both-present-and-unequal → `return CycleOutcome.Failed(Incompat, hello.DaemonVersion);`. Extend `CycleOutcome` with `ConnectedIdentity? Identity` and populate `Connected` at the `RunAsync:130` yield with `new ConnectedIdentity(hello.Pid, hello.InstanceId, hello.DaemonName, hello.DaemonVersion)`.

- [ ] **Step 4: Run the client suite + app-shell tests (`DaemonClientServiceTests` consumes Connected positionally — additive default keeps it compiling). Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/LocalControlClient.cs test/
git commit -m "AI-1655: client-enforced hello/snapshot instance correlation before Connected"
```

---

### Task 8: `LocalControlProbe` — bounded one-shot hello + snapshot

**Files:**
- Create: `src/Capacitor.Cli.Core/LocalIpc/LocalControlProbe.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/LocalControlProbeTests.cs` (new; reuse the `LocalControlHelloTests` harness pattern)

**Interfaces:**
- Consumes: `LocalSocketPaths.Socket(name)`, `FrameCodec`, `HelloIpcJsonContext`, `StatusIpcJsonContext`, Task 6 DTO members.
- Produces:

```csharp
public sealed record ProbeResult(
    bool Reachable, HelloReplyDto? Hello, DaemonStatusDto? Snapshot, bool IdentityConsistent);

public static class LocalControlProbe {
    /// ONE hello connection + ONE subscribe connection, first snapshot only, both bounded by
    /// `timeout`. IdentityConsistent is false when both sides carry pid/instance_id and they
    /// disagree (daemon swapped between the two dials). Never retries; never throws on an
    /// unreachable/undecodable peer — Reachable=false.
    public static Task<ProbeResult> ProbeAsync(string daemonName, TimeSpan timeout, CancellationToken ct = default);
}
```

- [ ] **Step 1: Write the failing tests** (against the real harness):

```csharp
[Test]
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public async Task Probe_returns_hello_and_first_snapshot_with_consistent_identity() {
    if (OperatingSystem.IsWindows()) return;
    await RunAsync("probe-a", async (h, ct) => {
        h.Config.InstanceId = "inst-p1";
        var r = await LocalControlProbe.ProbeAsync("probe-a", TimeSpan.FromSeconds(5), ct);

        await Assert.That(r.Reachable).IsTrue();
        await Assert.That(r.Hello!.DaemonName).IsEqualTo("probe-a");
        await Assert.That(r.Snapshot!.Daemon.InstanceId).IsEqualTo("inst-p1");
        await Assert.That(r.IdentityConsistent).IsTrue();
    });
}

[Test]
public async Task Probe_on_missing_socket_reports_unreachable_without_throwing() {
    var r = await LocalControlProbe.ProbeAsync("no-such-daemon-xyz", TimeSpan.FromMilliseconds(500));
    await Assert.That(r.Reachable).IsFalse();
    await Assert.That(r.Hello).IsNull();
}
```

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement** — dial the socket, write `new LocalFrame(FrameType.Hello)`, read + deserialize `HelloReplyDto` (bounded via `CancellationTokenSource(timeout)` linked to `ct`); dispose; dial again, write `new LocalFrame(FrameType.StatusSubscribe)`, read the first `DaemonStatus` frame, deserialize, dispose. `IdentityConsistent = hello.Pid is null || snap.Daemon.Pid is null || (hello.Pid == snap.Daemon.Pid && hello.InstanceId == snap.Daemon.InstanceId)`. Catch `SocketException`/`IOException`/`InvalidDataException`/`EndOfStreamException`/`OperationCanceledException`(timeout) → `Reachable=false` (partial: hello-only → `Reachable=true, Snapshot=null, IdentityConsistent=false`).

- [ ] **Step 4: Run tests. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/LocalControlProbe.cs test/Capacitor.Cli.Tests.Unit/Daemon/LocalControlProbeTests.cs
git commit -m "AI-1655: LocalControlProbe one-shot hello+snapshot seam"
```

---

### Task 9: `ConsentRulesPutV2` + `consent/3`

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs` (add `ConsentRulesPutV2 = 19`)
- Modify: `src/Capacitor.Cli.Core/LocalIpc/ConsentIpc.cs` (add DTO)
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentIpc.cs` (new handler), `LocalControlServer.cs:43-64` (route), `LocalControlCapabilities.cs` (advertise)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/ConsentRulesPutV2Tests.cs` (new; reuse harness)

**Interfaces:**
- Consumes: `LaunchConsentStore.TryReplace`, `ConsentAckDto(bool Ok, string? Error, bool? RuleSaved)`, `DaemonConfig.Name`/`ServerUrl`.
- Produces:

```csharp
// ConsentIpc.cs — MANDATORY expected identity (fail-closed by frame shape):
public sealed record ConsentPolicyPutV2Dto(
    string ExpectedName, string ExpectedServerUrl, ConsentPolicyDto Policy);
```

`FrameType.ConsentRulesPutV2 = 19` (client→daemon; ack is the existing `ConsentAck = 74`). Capability list becomes `["consent/1", "consent/2", "consent/3", "status/1"]`. Mismatch ack: `new ConsentAckDto(false, "identity_mismatch", null)` — nothing mutated. Missing/empty expected fields: `new ConsentAckDto(false, "malformed policy payload", null)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/ConsentRulesPutV2Tests.cs — reuse LocalControlHelloTests harness helpers
[Test]
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public async Task V2_put_with_matching_identity_mutates_and_acks_ok() {
    if (OperatingSystem.IsWindows()) return;
    await RunAsync("putv2-a", async (h, ct) => {
        var dto = new ConsentPolicyPutV2Dto("putv2-a", h.Config.ServerUrl,
            new ConsentPolicyDto("prompt", 45, []));
        var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
        var ack = await ReadAck(s, ct);

        await Assert.That(ack.Ok).IsTrue();
        // policy actually changed:
        await Assert.That(File.ReadAllText(Path.Combine(h.Config.StateDir!, "consent.json"))).Contains("prompt");
    });
}

[Test]
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public async Task V2_put_with_wrong_server_acks_identity_mismatch_and_mutates_nothing() {
    if (OperatingSystem.IsWindows()) return;
    await RunAsync("putv2-b", async (h, ct) => {
        var before = File.Exists(Path.Combine(h.Config.StateDir!, "consent.json"))
            ? File.ReadAllText(Path.Combine(h.Config.StateDir!, "consent.json")) : null;
        var dto = new ConsentPolicyPutV2Dto("putv2-b", "https://other-server.example",
            new ConsentPolicyDto("prompt", 45, []));
        var json = JsonSerializer.Serialize(dto, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto);
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, LocalFrame.ConsentJson(FrameType.ConsentRulesPutV2, json), ct);
        var ack = await ReadAck(s, ct);

        await Assert.That(ack.Ok).IsFalse();
        await Assert.That(ack.Error).IsEqualTo("identity_mismatch");
        var after = File.Exists(Path.Combine(h.Config.StateDir!, "consent.json"))
            ? File.ReadAllText(Path.Combine(h.Config.StateDir!, "consent.json")) : null;
        await Assert.That(after).IsEqualTo(before);
    });
}

[Test]
[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public async Task Capabilities_advertise_consent3() {
    if (OperatingSystem.IsWindows()) return;
    await RunAsync("putv2-c", async (h, ct) => {
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
        var frame = await FrameCodec.ReadAsync(s, ct);
        var dto = JsonSerializer.Deserialize(frame!.Value.Payload, HelloIpcJsonContext.Default.HelloReplyDto);
        await Assert.That(dto!.Capabilities!).Contains("consent/3");
    });
}
```

(`ReadAck` helper: read one frame, assert `FrameType.ConsentAck`, deserialize `ConsentAckDto`.)

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement**

`ConsentIpc.cs`: add the record + `[JsonSerializable(typeof(ConsentPolicyPutV2Dto))]` to `ConsentIpcJsonContext`.
`LaunchConsentIpc.cs`: add

```csharp
public async Task HandleRulesPutV2Async(string payload, Stream stream, CancellationToken ct) {
    ConsentPolicyPutV2Dto? dto = null;
    try { dto = JsonSerializer.Deserialize(payload, ConsentIpcJsonContext.Default.ConsentPolicyPutV2Dto); }
    catch (JsonException) { }
    if (dto is null || string.IsNullOrEmpty(dto.ExpectedName) || string.IsNullOrEmpty(dto.ExpectedServerUrl)) {
        await WriteAck(stream, new ConsentAckDto(false, "malformed policy payload", null), ct);
        return;
    }
    if (dto.ExpectedName != _config.Name
            || !string.Equals(NormalizeUrl(dto.ExpectedServerUrl), NormalizeUrl(_config.ServerUrl), StringComparison.OrdinalIgnoreCase)) {
        await WriteAck(stream, new ConsentAckDto(false, "identity_mismatch", null), ct);
        return;
    }
    // identity verified on THE SAME CONNECTION carrying the write — delegate to the v1 body:
    var v1Json = JsonSerializer.Serialize(dto.Policy, ConsentIpcJsonContext.Default.ConsentPolicyDto);
    await HandleRulesPutAsync(v1Json, stream, ct);
}

static string NormalizeUrl(string u) => u.TrimEnd('/');
```

(`LaunchConsentIpc` needs `DaemonConfig` — extend its primary constructor: `LaunchConsentIpc(LaunchConsentBroker broker, LaunchConsentStore store, DaemonConfig config, ILogger<LaunchConsentIpc> logger)`; fix the DI registration and the test harness construction.)
`LocalControlServer.cs`: add `FrameType.ConsentRulesPutV2 => consentIpc.HandleRulesPutV2Async(payload, stream, ct)` to the dispatch switch.
`LocalControlCapabilities.cs`: `Current = ["consent/1", "consent/2", "consent/3", "status/1"]`.
Structural rejection needs no code: a pre-slice daemon's switch hits `default: LocalFrame.Error(...)` for byte 19 — add one test asserting TODAY's dispatch (drive a harness with the v2 route removed is not possible post-change; instead assert the client-side contract in Plan B; here, assert `consent/3` gating knowledge stays in the capability list).

- [ ] **Step 4: Run the Daemon group + `LaunchConsentIpc` existing tests. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/LocalIpc/ src/Capacitor.Cli.Daemon/Services/ test/
git commit -m "AI-1655: ConsentRulesPutV2 identity-conditional frame + consent/3"
```

---

### Task 10: Daemon boot carriers — capture, ambient removal, scrub, respawn re-injection

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/DaemonConfig.cs` (add `ConsentSeedDirective`, `ExpectedServerUrl`, `BootAttemptId`)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs:126-172` (capture + remove, in the env-reads block)
- Modify: `src/Capacitor.Cli.Daemon/Services/DetachedRespawnStrategy.cs:24-32` (re-inject into successor)
- Modify: the PTY env scrub list (`grep -rln "PtyEnvScrub" src/Capacitor.Cli.Daemon/`) — add all three vars
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/BootCarrierTests.cs` (new)

**Interfaces:**
- Consumes: nothing new.
- Produces:

```csharp
// DaemonConfig additions:
public string? ConsentSeedDirective { get; set; }   // raw value of KCAP_CONSENT_SEED_DEFAULT (validated in Task 11)
public string? ExpectedServerUrl   { get; set; }    // raw value of KCAP_EXPECT_SERVER_URL
public string? BootAttemptId       { get; set; }    // raw value of KCAP_BOOT_ATTEMPT

// DaemonRunner: static, testable capture helper
internal static void CaptureBootCarriers(DaemonConfig config, Func<string, string?> get, Action<string> clear) {
    config.ConsentSeedDirective = get("KCAP_CONSENT_SEED_DEFAULT");
    config.ExpectedServerUrl    = get("KCAP_EXPECT_SERVER_URL");
    config.BootAttemptId        = get("KCAP_BOOT_ATTEMPT");
    clear("KCAP_CONSENT_SEED_DEFAULT");
    clear("KCAP_EXPECT_SERVER_URL");
    clear("KCAP_BOOT_ATTEMPT");
}

// DaemonRunner: the carrier names, shared with DetachedRespawnStrategy and the scrub lists
internal static class BootCarriers {
    public const string Seed     = "KCAP_CONSENT_SEED_DEFAULT";
    public const string Expect   = "KCAP_EXPECT_SERVER_URL";
    public const string Attempt  = "KCAP_BOOT_ATTEMPT";
    public static readonly string[] All = [Seed, Expect, Attempt];
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/BootCarrierTests.cs
public class BootCarrierTests {
    [Test]
    public async Task Capture_reads_all_three_and_removes_them_from_ambient() {
        var env = new Dictionary<string, string?> {
            [BootCarriers.Seed] = "prompt", [BootCarriers.Expect] = "https://s.example",
            [BootCarriers.Attempt] = "att-1", ["OTHER"] = "kept",
        };
        var config = new DaemonConfig();
        DaemonRunner.CaptureBootCarriers(config, k => env.GetValueOrDefault(k), k => env.Remove(k));

        await Assert.That(config.ConsentSeedDirective).IsEqualTo("prompt");
        await Assert.That(config.ExpectedServerUrl).IsEqualTo("https://s.example");
        await Assert.That(config.BootAttemptId).IsEqualTo("att-1");
        await Assert.That(env.ContainsKey(BootCarriers.Seed)).IsFalse();
        await Assert.That(env.ContainsKey(BootCarriers.Expect)).IsFalse();
        await Assert.That(env.ContainsKey(BootCarriers.Attempt)).IsFalse();
        await Assert.That(env["OTHER"]).IsEqualTo("kept");
    }

    [Test]
    public async Task Respawn_successor_env_reinjects_seed_and_expectation_but_not_attempt() {
        var config = new DaemonConfig {
            ConsentSeedDirective = "prompt", ExpectedServerUrl = "https://s.example", BootAttemptId = "att-1",
        };
        var env = DetachedRespawnStrategy.SuccessorEnvOverlay(config);

        await Assert.That(env[BootCarriers.Seed]).IsEqualTo("prompt");
        await Assert.That(env[BootCarriers.Expect]).IsEqualTo("https://s.example");
        // an attempt id is per-ACTION; a self-respawn is not the app's action:
        await Assert.That(env.ContainsKey(BootCarriers.Attempt)).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement**

- `DaemonConfig`: add the three nullable string props.
- `DaemonRunner`: add `BootCarriers` + `CaptureBootCarriers`; call it in `RunAsync` right after `config.OriginalArgs = args;` (line 42) with `Environment.GetEnvironmentVariable` / `k => Environment.SetEnvironmentVariable(k, null)` — BEFORE the host builder exists, so no descendant can observe the vars.
- `DetachedRespawnStrategy`: add

```csharp
internal static Dictionary<string, string> SuccessorEnvOverlay(DaemonConfig config) {
    var env = new Dictionary<string, string>();
    if (!string.IsNullOrEmpty(config.ConsentSeedDirective)) env[DaemonRunner.BootCarriers.Seed] = config.ConsentSeedDirective;
    if (!string.IsNullOrEmpty(config.ExpectedServerUrl))    env[DaemonRunner.BootCarriers.Expect] = config.ExpectedServerUrl;
    return env;
}
```

and in `Restart()` after the `ArgumentList` loop: `foreach (var (k, v) in SuccessorEnvOverlay(config)) psi.Environment[k] = v;`.
- PTY/ACP scrub: locate the existing scrub list (`PtyEnvScrub`) and append `DaemonRunner.BootCarriers.All`; if ACP/Pi process factories have their own env construction, add the same removal there (`grep -rn "Environment\[" src/Capacitor.Cli.Daemon/Services/ | grep -i factory` to find them). Defense in depth only — capture-and-remove already cleared the ambient env.

- [ ] **Step 4: Run tests + full Daemon group. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/ test/Capacitor.Cli.Tests.Unit/Daemon/BootCarrierTests.cs
git commit -m "AI-1655: boot-local carrier lifecycle for seed/expectation/attempt vars"
```

---

### Task 11: Boot seed classification + `default_source` + coded refusals

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/Services/LaunchConsentStore.cs` (add `default_source` to `PolicyDoc`; add `SeedResult BootSeed(string directive)`)
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (invoke between DaemonLock acquire and DI build; refusal → exit 0)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/BootSeedTests.cs` (new)

**Interfaces:**
- Consumes: Task 10's `config.ConsentSeedDirective`; the store's `_path`/`Load` internals.
- Produces:

```csharp
// PolicyDoc gains DefaultSource (snake_case "default_source"; absent in old files):
internal sealed record PolicyDoc(string? Default, int? PromptTimeoutSeconds, List<RuleDoc>? Rules, string? DefaultSource = null);

public enum SeedOutcome { Seeded, Respected, Rewritten, Quarantined, RefusedInvalidDirective, RefusedUnwritable }
public sealed record SeedResult(SeedOutcome Outcome, string? RefusalToken);

// On LaunchConsentStore:
public SeedResult BootSeed(string directive);
```

Classification (spec §6, exact): directive ≠ literal `"prompt"` → `RefusedInvalidDirective` (token `consent_seed_invalid`); file absent → write `{default:"prompt", default_source:"seed"}` → `Seeded`; unreadable/malformed/null-doc/unrecognized-default → rename aside to `consent.json.quarantined-<n>` + seed → `Quarantined`; valid `prompt`/`deny` → `Respected`; valid `allow` with ≥1 rule → `Respected`; valid `allow`, zero rules, `default_source == "operator"` → `Respected`; valid `allow`, zero rules, source `seed`/absent → rewrite default to `prompt` (preserve timeout), source `seed` → `Rewritten`. Any write failure → `RefusedUnwritable` (token `consent_seed_unwritable`). Additionally: `TryReplace` (the IPC put path) now stamps `default_source: "operator"` on every persist.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/BootSeedTests.cs
public class BootSeedTests {
    static LaunchConsentStore Store(string dir) => new(dir, NullLogger.Instance);
    static string PolicyPath(string dir) => Path.Combine(dir, "consent.json");

    [Test]
    public async Task Absent_file_seeds_prompt_with_seed_source() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Seeded);
        var json = await File.ReadAllTextAsync(PolicyPath(dir));
        await Assert.That(json).Contains("\"default\": \"prompt\"");
        await Assert.That(json).Contains("\"default_source\": \"seed\"");
    }

    [Test]
    public async Task Operator_allow_survives_reseed() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[],"default_source":"operator"}""");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(dir))).Contains("\"allow\"");
    }

    [Test]
    public async Task Unstamped_factory_looking_allow_is_rewritten_to_prompt() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[]}""");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Rewritten);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(dir))).Contains("\"prompt\"");
    }

    [Test]
    public async Task Allow_with_rules_is_respected() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir),
            """{"default":"allow","prompt_timeout_seconds":45,"rules":[{"action":"deny","requester":"x"}]}""");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
    }

    [Test]
    public async Task Malformed_file_is_quarantined_and_seeded() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir), "{not json");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
        await Assert.That(Directory.GetFiles(dir, "consent.json.quarantined-*")).IsNotEmpty();
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(dir))).Contains("\"prompt\"");
    }

    [Test]
    public async Task Unrecognized_default_value_is_a_silent_allow_arm_and_gets_quarantined() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        await File.WriteAllTextAsync(PolicyPath(dir), """{"default":"totally-bogus","rules":[]}""");
        var r = Store(dir).BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Quarantined);
    }

    [Test]
    [Arguments("")] [Arguments("allow")] [Arguments("deny")] [Arguments("Prompt")] [Arguments("bogus")]
    public async Task Non_literal_prompt_directives_refuse(string directive) {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        var r = Store(dir).BootSeed(directive);
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.RefusedInvalidDirective);
        await Assert.That(r.RefusalToken).IsEqualTo("consent_seed_invalid");
        await Assert.That(File.Exists(PolicyPath(dir))).IsFalse();
    }

    [Test]
    public async Task Operator_put_stamps_operator_source() {
        var dir = Directory.CreateTempSubdirectory("seed-").FullName;
        var store = Store(dir);
        store.TryReplace(new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45, []), out _);
        await Assert.That(await File.ReadAllTextAsync(PolicyPath(dir))).Contains("\"default_source\": \"operator\"");
        // and a later reseed respects it:
        var r = store.BootSeed("prompt");
        await Assert.That(r.Outcome).IsEqualTo(SeedOutcome.Respected);
    }
}
```

- [ ] **Step 2: Run to verify FAIL** (visibility note: if `LaunchConsentStore`/`SeedOutcome` are `internal`, the unit test project already has `InternalsVisibleTo` for daemon internals — check `src/Capacitor.Cli.Daemon/*.csproj` for the existing `InternalsVisibleTo` item; the harness tests in `LocalControlHelloTests` construct `LaunchConsentStore` directly, so it is already test-visible).

- [ ] **Step 3: Implement `BootSeed`** — classification per the table above. Use a raw parse that DISTINGUISHES the arms `Load()` collapses (`Load()` maps unknown default → Allow silently; `BootSeed` must re-parse `PolicyDoc` itself): absent → seed; parse exception / null doc / `doc.Default` not in `{"allow","deny","prompt"}` → quarantine (rename to `consent.json.quarantined-{DateTime.UtcNow.Ticks}`) + seed; else classify per default/rules/source. Seed/rewrite writes reuse `TryReplace`'s temp+rename+0600 shape but stamp `default_source: "seed"`; introduce a private `bool Persist(PolicyDoc doc, out string? error)` shared by `TryReplace` (stamping `"operator"`) and `BootSeed` (stamping `"seed"`). All under `lock (_gate)`, updating `_current` on success.

- [ ] **Step 4: Wire `DaemonRunner`** — after `config.InstanceId = daemonLock.InstanceId;` (line 250) and after `coverageStateDir` is computed (move that computation up beside it if needed):

```csharp
if (!string.IsNullOrEmpty(config.ConsentSeedDirective)) {
    var seedStore = new LaunchConsentStore(coverageStateDir, loggerForBoot);
    var seed = seedStore.BootSeed(config.ConsentSeedDirective);
    if (seed.Outcome is SeedOutcome.RefusedInvalidDirective or SeedOutcome.RefusedUnwritable) {
        BootRefusal.TryWrite(coverageStateDir, config, seed.RefusalToken!);   // Task 12
        await Console.Error.WriteLineAsync($"kcap-daemon: refusing to start: {seed.RefusalToken}");
        return 0;   // comes to rest under KeepAlive — AI-1654 decision 6
    }
}
```

(`loggerForBoot`: a `NullLogger`/console logger is acceptable pre-host; reuse the store instance for DI by registering the already-constructed `seedStore` instead of constructing a second one at line 302.) Until Task 12 exists, stub `BootRefusal.TryWrite` as a no-op with a `// Task 12` comment or reorder to do Task 12 first — the plan orders it next; implement the call now and the class in Task 12, keeping this task's commit at the end of Task 12 if the daemon project must compile. Preferred: implement Tasks 11+12 as two test cycles but allow this wiring snippet to land in Task 12's commit.

- [ ] **Step 5: Run the BootSeed tests. Expected: PASS (store-level; runner wiring compiles in Task 12).**

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/Services/LaunchConsentStore.cs test/Capacitor.Cli.Tests.Unit/Daemon/BootSeedTests.cs
git commit -m "AI-1655: boot-time consent seed classification with default_source provenance"
```

---

### Task 12: Expectation check + boot-refusal marker

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/BootRefusal.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (expectation check + Task 11 wiring + success cleanup)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/BootRefusalTests.cs` (new)

**Interfaces:**
- Consumes: `config.ExpectedServerUrl`, `config.ServerUrl` (resolved at line 50), `config.BootAttemptId`, `config.Name`, `config.InstanceId`.
- Produces:

```csharp
public sealed record BootRefusalRecord(
    int Schema, string DaemonName, string Token, string? Expectation, string? Resolved,
    int Pid, string? InstanceId, string? AttemptId, DateTimeOffset Timestamp);

public static class BootRefusal {
    public static string MarkerPath(string stateDir) => Path.Combine(stateDir, "boot-refusal.json");
    /// Contained best-effort: NEVER throws (the state dir may be exactly as unwritable as the
    /// condition being reported); atomic temp+rename when it can.
    public static void TryWrite(string stateDir, DaemonConfig config, string token);
    public static BootRefusalRecord? TryRead(string stateDir);   // corrupt → rename aside + null
    public static void TryDelete(string stateDir);               // hygiene; failure logged by caller
}
```

Expectation semantics: with `config.ExpectedServerUrl` non-empty, compare `AppConfig.NormalizeUrl(config.ExpectedServerUrl)` vs `AppConfig.NormalizeUrl(config.ServerUrl)` case-insensitively BEFORE the host is built (i.e., before any `ServerConnection`/token use). Mismatch → `TryWrite(..., "server_expectation_mismatch")` + stderr line + `return 0`. A passing boot (both checks green, or no directives) calls `TryDelete` before proceeding.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Capacitor.Cli.Tests.Unit/Daemon/BootRefusalTests.cs
public class BootRefusalTests {
    [Test]
    public async Task Write_then_read_round_trips_identity() {
        var dir = Directory.CreateTempSubdirectory("refusal-").FullName;
        var config = new DaemonConfig {
            Name = "d1", ExpectedServerUrl = "https://a", ServerUrl = "https://b",
            InstanceId = "i-1", BootAttemptId = "att-9",
        };
        BootRefusal.TryWrite(dir, config, "server_expectation_mismatch");

        var r = BootRefusal.TryRead(dir);
        await Assert.That(r!.Token).IsEqualTo("server_expectation_mismatch");
        await Assert.That(r.DaemonName).IsEqualTo("d1");
        await Assert.That(r.Expectation).IsEqualTo("https://a");
        await Assert.That(r.Resolved).IsEqualTo("https://b");
        await Assert.That(r.Pid).IsEqualTo(Environment.ProcessId);
        await Assert.That(r.AttemptId).IsEqualTo("att-9");
    }

    [Test]
    public async Task Write_into_unwritable_dir_is_swallowed() {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("refusal-").FullName, "missing", "deep");
        // no Directory.CreateDirectory — TryWrite must not throw
        BootRefusal.TryWrite(dir, new DaemonConfig { Name = "d" }, "consent_seed_unwritable");
        await Assert.That(BootRefusal.TryRead(dir)).IsNull();
    }

    [Test]
    public async Task Corrupt_marker_is_renamed_aside_and_reads_null() {
        var dir = Directory.CreateTempSubdirectory("refusal-").FullName;
        await File.WriteAllTextAsync(BootRefusal.MarkerPath(dir), "{corrupt");
        await Assert.That(BootRefusal.TryRead(dir)).IsNull();
        await Assert.That(File.Exists(BootRefusal.MarkerPath(dir))).IsFalse();
        await Assert.That(Directory.GetFiles(dir, "boot-refusal.json.quarantined-*")).IsNotEmpty();
    }

    [Test]
    public async Task Expectation_comparison_normalizes_trailing_slash_and_case() {
        await Assert.That(DaemonRunner.ExpectationSatisfied("https://S.example/", "https://s.example")).IsTrue();
        await Assert.That(DaemonRunner.ExpectationSatisfied("https://a.example", "https://b.example")).IsFalse();
        await Assert.That(DaemonRunner.ExpectationSatisfied(null, "https://b.example")).IsTrue(); // no expectation
    }
}
```

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement** — `BootRefusal` with a source-gen JSON context (snake_case, `[JsonSerializable(typeof(BootRefusalRecord))]`); `TryWrite` catches ALL exceptions; `TryRead` catches parse errors, renames to `boot-refusal.json.quarantined-{ticks}`, returns null. `DaemonRunner`: add

```csharp
internal static bool ExpectationSatisfied(string? expected, string resolved) =>
    string.IsNullOrEmpty(expected)
    || string.Equals(AppConfig.NormalizeUrl(expected), AppConfig.NormalizeUrl(resolved), StringComparison.OrdinalIgnoreCase);
```

and wire the full pre-host boot-check block (after line 250, replacing Task 11's provisional snippet):

```csharp
var stateDirForBoot = Path.Combine(config.StateDir ?? DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(config.Name));
if (!ExpectationSatisfied(config.ExpectedServerUrl, config.ServerUrl)) {
    BootRefusal.TryWrite(stateDirForBoot, config, "server_expectation_mismatch");
    await Console.Error.WriteLineAsync("kcap-daemon: refusing to start: server_expectation_mismatch");
    return 0;
}
if (!string.IsNullOrEmpty(config.ConsentSeedDirective)) {
    var seedStore = new LaunchConsentStore(stateDirForBoot, NullLogger.Instance);
    var seed = seedStore.BootSeed(config.ConsentSeedDirective);
    if (seed.Outcome is SeedOutcome.RefusedInvalidDirective or SeedOutcome.RefusedUnwritable) {
        BootRefusal.TryWrite(stateDirForBoot, config, seed.RefusalToken!);
        await Console.Error.WriteLineAsync($"kcap-daemon: refusing to start: {seed.RefusalToken}");
        return 0;
    }
}
BootRefusal.TryDelete(stateDirForBoot);   // passing boot clears leftovers (hygiene)
```

(Note `coverageStateDir` at line 261 computes the same path — deduplicate to one variable.)

- [ ] **Step 4: Add the adversarial end-to-end test** (real harness, extends `BootSeedTests` or a new integration-style unit test): start a full `DaemonRunner.RunAsync` is heavyweight — instead assert at the seam: construct the store, run `BootSeed("prompt")` on an absent file, then `new LaunchConsentGate(store, ...)` and assert a launch request with no UI resolves to deny (the existing gate tests show the API — `LaunchConsentGate` + `LaunchConsentPolicy` with `Prompt` default and no prompter → deny). This pins "launch meets prompt, fail-closed deny" without a live server.

```csharp
[Test]
public async Task Seeded_policy_denies_an_immediate_launch_with_no_ui() {
    var dir = Directory.CreateTempSubdirectory("seed-e2e-").FullName;
    var store = new LaunchConsentStore(dir, NullLogger.Instance);
    store.BootSeed("prompt");
    await Assert.That(store.Current.Default).IsEqualTo(LaunchConsentDefault.Prompt);
    // Gate behavior for Prompt + no prompter is pinned by the existing AI-1623 gate tests
    // (prompt_no_ui → deny); this assertion documents the seed→gate linkage.
}
```

- [ ] **Step 5: Run the Daemon test group. Expected: PASS.**

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli.Daemon/ test/Capacitor.Cli.Tests.Unit/Daemon/
git commit -m "AI-1655: server expectation check + boot-refusal marker; boot checks run before host build"
```

---

### Task 13: `ServiceEnvironment` allowlist + plist env parsing + `unit_*` status fields

**Files:**
- Modify: `src/Capacitor.Cli/Services/ServiceEnvironment.cs:14` (allowlist)
- Modify: `src/Capacitor.Cli/Services/LaunchdUnit.cs` (add `EnvFromPlist`)
- Modify: `src/Capacitor.Cli/Commands/ServiceStatusJson.cs` + `DaemonCommands.cs:1080-1096` (new fields)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Services/` (LaunchdUnit tests) + the ServiceStatusJson render tests

**Interfaces:**
- Consumes: `LaunchdUnit.Plist(ServiceSpec)` emits `<key>EnvironmentVariables</key><dict>` (write side exists; READ side does not — this task adds it).
- Produces:

```csharp
// LaunchdUnit:
public static IReadOnlyDictionary<string, string> EnvFromPlist(string plistXml);

// ServiceStatusJson record (additive members):
public sealed record ServiceStatusJson(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath,
    string? InstallBinaryPath, int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive,
    string? UnitProfile = null, string? UnitServerUrl = null,
    string? UnitExpectedServer = null, string? UnitConsentSeed = null);
```

Derivations (UX evidence only — spec §3): `UnitProfile` = baked `KCAP_PROFILE`; `UnitExpectedServer` = baked `KCAP_EXPECT_SERVER_URL`; `UnitConsentSeed` = baked `KCAP_CONSENT_SEED_DEFAULT` (the VALUE); `UnitServerUrl` = baked `KCAP_URL` if present, else the baked profile's `server_url` read from the baked `KCAP_CONFIG_DIR`'s (or default) config root via `ConfigMutator.LoadPure` — null on any ambiguity. `ServiceEnvironment.Keys` gains `"KCAP_CONSENT_SEED_DEFAULT"` and `"KCAP_EXPECT_SERVER_URL"`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test]
public async Task EnvFromPlist_round_trips_what_Plist_writes() {
    // ServiceSpec's exact construction: copy the existing LaunchdUnit Plist test's spec
    // (grep -rn "LaunchdUnit.Plist" test/ — reuse its builder verbatim), overriding only Env:
    var spec = ExistingPlistTestSpec() with {
        Env = new Dictionary<string, string> {
            ["KCAP_PROFILE"] = "acme",
            ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
            ["KCAP_EXPECT_SERVER_URL"] = "https://s",
        },
    };
    var xml = LaunchdUnit.Plist(spec);
    var env = LaunchdUnit.EnvFromPlist(xml);

    await Assert.That(env["KCAP_PROFILE"]).IsEqualTo("acme");
    await Assert.That(env["KCAP_CONSENT_SEED_DEFAULT"]).IsEqualTo("prompt");
    await Assert.That(env["KCAP_EXPECT_SERVER_URL"]).IsEqualTo("https://s");
}

[Test]
public async Task EnvFromPlist_on_plist_without_env_dict_returns_empty() {
    var xml = LaunchdUnit.Plist(ExistingPlistTestSpec() with { Env = new Dictionary<string, string>() });
    await Assert.That(LaunchdUnit.EnvFromPlist(xml)).IsEmpty();
}

[Test]
public async Task Status_json_carries_unit_fields_snake_cased() {
    var json = JsonSerializer.Serialize(
        new ServiceStatusJson("svc", true, "installed", "/b", "/b", null, null, false, false,
            UnitProfile: "acme", UnitServerUrl: "https://s", UnitExpectedServer: "https://s",
            UnitConsentSeed: "prompt"),
        ServiceJsonContext.Default.ServiceStatusJson);

    await Assert.That(json).Contains("\"unit_profile\":\"acme\"");
    await Assert.That(json).Contains("\"unit_consent_seed\":\"prompt\"");
    await Assert.That(json).Contains("\"unit_expected_server\":\"https://s\"");
}
```

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement**

`EnvFromPlist` (mirror of `BinaryFromPlist`'s XDocument style):

```csharp
public static IReadOnlyDictionary<string, string> EnvFromPlist(string plistXml) {
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    var topDict = XDocument.Parse(plistXml).Root?.Element("dict");
    if (topDict is null) return result;
    XElement? cursor = null;
    foreach (var el in topDict.Elements()) {
        if (cursor is not null) {           // cursor was <key>EnvironmentVariables</key>
            if (el.Name == "dict") {
                string? key = null;
                foreach (var kv in el.Elements()) {
                    if (kv.Name == "key") key = kv.Value;
                    else if (kv.Name == "string" && key is not null) { result[key] = kv.Value; key = null; }
                }
            }
            break;
        }
        if (el.Name == "key" && el.Value == "EnvironmentVariables") cursor = el;
    }
    return result;
}
```

`ServiceEnvironment.Keys`: append the two new names. `ServiceStatusJson`/`ServiceStatusRender.Render`: read the plist (already read for `BinaryPath` in `QueryCore` — thread the raw env dict through `ServiceQuery` or re-read the plist file inside `ServiceStatusJson` assembly in `DaemonCommands.cs:1080` where `LaunchdUnit.PlistPath(id)` is accessible; prefer the re-read at the command layer to avoid widening `ServiceQuery`). Compute `UnitServerUrl` per the derivation above; wrap the profile-config read in try/catch → null.

- [ ] **Step 4: Run the Services + Commands test groups. Expected: PASS (existing exact-JSON status fixtures updated with the new snake_case members, nulls written).**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Services/ src/Capacitor.Cli/Commands/ test/
git commit -m "AI-1655: bake seed/expectation env into units; unit_* evidence on service status --json"
```

---

### Task 14: Embedded daemon digest — build pipeline

**Files:**
- Create: `src/Capacitor.Cli/DaemonDigest.cs` (runtime accessor) + `src/Capacitor.Cli/Capacitor.Cli.csproj` (generation target)
- Modify: `.github/workflows/release.yml:242-256` (reorder: daemon first, digest, CLI) + add post-package assertion
- Test: `test/Capacitor.Cli.Tests.Unit/Services/DaemonDigestTests.cs` (new)

**Interfaces:**
- Produces:

```csharp
// src/Capacitor.Cli/DaemonDigest.cs
namespace Capacitor.Cli;

public static partial class DaemonDigest {
    /// 64 lowercase hex chars, or the fail-closed placeholder (64 zeros) when no daemon
    /// artifact was available at build time. Generated constant lives in obj/ (GeneratedDigest).
    public static string Expected => GeneratedDigest.Value;

    public const string Placeholder = "0000000000000000000000000000000000000000000000000000000000000000";

    public static bool IsUsable => Expected != Placeholder && Expected.Length == 64;

    public static bool Matches(string filePath) {
        if (!IsUsable) return false;                       // fail closed
        try {
            using var s = File.OpenRead(filePath);
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s)) == Expected;
        } catch {
            return false;                                   // unreadable evidence → fail closed
        }
    }
}
```

- MSBuild: a `GenerateDaemonDigest` target in `Capacitor.Cli.csproj`, `BeforeTargets="CoreCompile"`, producing `$(IntermediateOutputPath)DaemonDigest.g.cs` with `internal static class GeneratedDigest { public const string Value = "<hex-or-placeholder>"; }`, sourcing from: `$(KcapDaemonDigest)` property when provided (release), else hashing `$(KcapDaemonArtifact)` when the property points at a file, else the placeholder. Property-driven — dev/CI builds without the daemon artifact compile with the placeholder and the gates fail closed.

- [ ] **Step 1: Write the failing test**

```csharp
public class DaemonDigestTests {
    [Test]
    public async Task Placeholder_is_not_usable_and_never_matches() {
        // Local dev/test builds carry the placeholder unless -p:KcapDaemonDigest was passed:
        if (!DaemonDigest.IsUsable) {
            var f = Path.GetTempFileName();
            await File.WriteAllTextAsync(f, "anything");
            await Assert.That(DaemonDigest.Matches(f)).IsFalse();
        }
    }

    [Test]
    public async Task Matches_hashes_file_content() {
        // exercised via the internal seam: compute what Matches computes
        var f = Path.GetTempFileName();
        await File.WriteAllBytesAsync(f, [1, 2, 3]);
        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 }));
        await Assert.That(DaemonDigest.HashOf(f)).IsEqualTo(expected);   // add internal static string HashOf(string)
    }
}
```

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement the runtime class + MSBuild target**

Add to `Capacitor.Cli.csproj`:

```xml
<Target Name="GenerateDaemonDigest" BeforeTargets="CoreCompile">
  <PropertyGroup>
    <KcapDaemonDigestValue>$(KcapDaemonDigest)</KcapDaemonDigestValue>
    <KcapDaemonDigestValue Condition="'$(KcapDaemonDigestValue)' == '' And '$(KcapDaemonArtifact)' != '' And Exists('$(KcapDaemonArtifact)')">@(KcapDaemonArtifactHash)</KcapDaemonDigestValue>
    <KcapDaemonDigestValue Condition="'$(KcapDaemonDigestValue)' == ''">0000000000000000000000000000000000000000000000000000000000000000</KcapDaemonDigestValue>
    <KcapDigestFile>$(IntermediateOutputPath)DaemonDigest.g.cs</KcapDigestFile>
  </PropertyGroup>
  <ItemGroup Condition="'$(KcapDaemonArtifact)' != '' And Exists('$(KcapDaemonArtifact)')">
    <KcapDaemonArtifactFile Include="$(KcapDaemonArtifact)" />
  </ItemGroup>
  <GetFileHash Files="@(KcapDaemonArtifactFile)" Algorithm="SHA256" HashEncoding="hex" Condition="'@(KcapDaemonArtifactFile)' != ''">
    <Output TaskParameter="Items" ItemName="KcapDaemonArtifactHashed" />
  </GetFileHash>
  <PropertyGroup Condition="'@(KcapDaemonArtifactHashed)' != ''">
    <KcapDaemonDigestValue>$([System.String]::Copy('%(KcapDaemonArtifactHashed.FileHash)').ToLowerInvariant())</KcapDaemonDigestValue>
  </PropertyGroup>
  <WriteLinesToFile File="$(KcapDigestFile)" Overwrite="true"
      Lines="namespace Capacitor.Cli%3B internal static class GeneratedDigest { public const string Value = &quot;$(KcapDaemonDigestValue)&quot;%3B }" />
  <ItemGroup>
    <Compile Include="$(KcapDigestFile)" />
  </ItemGroup>
</Target>
```

(Note: `GetFileHash` runs before property evaluation of the earlier group — validate ordering while implementing; the simple invariant to keep is: `-p:KcapDaemonDigest=<hex>` wins, else `-p:KcapDaemonArtifact=<path>` is hashed, else placeholder.)

Release workflow (`release.yml`) — swap the two publish steps (daemon FIRST), then insert between them and after:

```yaml
      - name: Compute daemon digest
        id: daemon-digest
        shell: bash
        run: |
          BIN=publish/daemon/${{ matrix.daemon-binary }}
          echo "DIGEST=$(shasum -a 256 "$BIN" | cut -d' ' -f1)" >> "$GITHUB_OUTPUT"

      - name: Publish CLI AOT binary
        run: >
          dotnet publish ${{ env.CLI_PROJECT }}
          -c Release
          -r ${{ matrix.rid }}
          -p:MinVerVersionOverride=${{ steps.version.outputs.VERSION }}
          -p:KcapDaemonDigest=${{ steps.daemon-digest.outputs.DIGEST }}
          -o publish/cli/
```

and after the npm copy step:

```yaml
      - name: Assert packaged daemon matches embedded digest
        shell: bash
        run: |
          PACKED=npm/${{ matrix.npm-package }}/bin/${{ matrix.daemon-binary }}
          ACTUAL=$(shasum -a 256 "$PACKED" | cut -d' ' -f1)
          if [ "$ACTUAL" != "${{ steps.daemon-digest.outputs.DIGEST }}" ]; then
            echo "::error::packaged daemon digest mismatch"; exit 1
          fi
```

(Linux runners: `shasum -a 256` → use `sha256sum | cut -d' ' -f1` guarded by availability, or `python3 -c` — keep one portable line: `openssl dgst -sha256 -r "$BIN" | cut -d' ' -f1` works on all matrix images. Windows leg: use `pwsh` `Get-FileHash`. Implement per-OS with `if [[ "$RUNNER_OS" == ... ]]` or the `shell: pwsh` variant on win-x64.)

AI-1653 signing constraint: leave a YAML comment above the digest step — `# AI-1653: when signing lands, sign the daemon BEFORE this step (signing changes the bytes).`

- [ ] **Step 4: Verify locally** — `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` (placeholder path) then `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj -p:KcapDaemonDigest=$(openssl dgst -sha256 -r $(which ls) | cut -d' ' -f1)` and run the digest tests. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/DaemonDigest.cs src/Capacitor.Cli/Capacitor.Cli.csproj .github/workflows/release.yml test/Capacitor.Cli.Tests.Unit/Services/DaemonDigestTests.cs
git commit -m "AI-1655: build-time embedded daemon digest with fail-closed placeholder"
```

---

### Task 15: Detached-start digest gate (exit 43)

**Files:**
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs:144-255` (`StartDetached`)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/DetachedDigestGateTests.cs` (new)

**Interfaces:**
- Consumes: `DaemonDigest.IsUsable`/`Matches`; `ResolveDaemonBinary()`.
- Produces: when `KCAP_CONSENT_SEED_DEFAULT` is present in the process env (i.e., an app-managed start), `StartDetached` validates the sibling BEFORE spawning: `DaemonDigest.Matches(daemonPath)` false → stderr `daemon_start_reason=package_inconsistent` + **exit 43**, nothing spawned. No directive → unchanged behavior. Extract the gate as a testable seam:

```csharp
internal static int? DetachedDigestGate(string daemonPath, Func<string, string?> env) {
    if (string.IsNullOrEmpty(env(DaemonRunner.BootCarriers.Seed))) return null;   // manual start: no gate
    if (DaemonDigest.Matches(daemonPath)) return null;
    Console.Error.WriteLine("daemon_start_reason=package_inconsistent");
    return 43;
}
```

(Note: `DaemonRunner.BootCarriers` is a daemon-project type; the CLI project does not reference it — duplicate the string constant locally: `const string SeedVar = "KCAP_CONSENT_SEED_DEFAULT";` with a comment naming the daemon twin, same pattern as the app's duplicated verify codes.)

- [ ] **Step 1: Write the failing tests**

```csharp
public class DetachedDigestGateTests {
    [Test]
    public async Task No_directive_means_no_gate() {
        var exit = DaemonCommands.DetachedDigestGate("/nonexistent", _ => null);
        await Assert.That(exit).IsNull();
    }

    [Test]
    public async Task Directive_with_placeholder_digest_fails_closed_exit_43() {
        // dev/test builds carry the placeholder → Matches() is false → gate refuses
        var exit = DaemonCommands.DetachedDigestGate("/nonexistent",
            k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
        await Assert.That(exit).IsEqualTo(43);
    }
}
```

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement** — insert the gate call in `StartDetached` right after `ResolveDaemonBinary()` succeeds (line ~169): `if (DetachedDigestGate(daemonPath, Environment.GetEnvironmentVariable) is int gateExit) return gateExit;`. The env flows into the daemon child automatically (psi inherits env), which is what arms the daemon's own boot checks.

- [ ] **Step 4: Run tests. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/DaemonCommands.cs test/Capacitor.Cli.Tests.Unit/Commands/DetachedDigestGateTests.cs
git commit -m "AI-1655: detached-start digest gate — exit 43 daemon_start_reason=package_inconsistent"
```

---

### Task 16: `service start --verify` gates — Phase A (exit 28) and Phase B (exit 29)

**Files:**
- Modify: `src/Capacitor.Cli/Services/ServiceVerify.cs` (`VerifyExit` constants; `StartVerifiedAsync` gains the gated path)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Services/ServiceVerifyStartTests.cs`

**Interfaces:**
- Consumes: `LaunchdUnit.EnvFromPlist` (Task 13), `DaemonDigest` (Task 14), `ConfigMutator.LoadPure` (Task 2), the existing `readPlist`/`plistExists` ctor seams, `ServiceTxnMarker`.
- Produces:

```csharp
// VerifyExit additions:
public const int StartGate = 28;          public const string StartGateToken      = "verify_start_gate";
public const int StartGateDrift = 29;     public const string StartGateDriftToken = "verify_start_gate_drift";

// Gate evidence + reasons:
public enum StartGateReason { DirectiveMissing, DirectiveInvalid, IdentityMismatch, ForeignBinary, PackageInconsistent, EvidenceUnreadable }
// stderr line: start_gate_reason=<directive_missing|directive_invalid|identity_mismatch|foreign_binary|package_inconsistent|evidence_unreadable>

// New ctor seam (keeps existing tests compiling via default): Func<string, string?> gateEnv = null
// — when gateEnv("KCAP_CONSENT_SEED_DEFAULT") is non-empty the gated path is active.
internal static StartGateReason? EvaluateStartGate(
    IReadOnlyDictionary<string, string> unitEnv, string? unitBinaryPath,
    string? installBinaryPath, Func<string, string?> env);   // pure, fully unit-testable
```

Gate rules (pure function): directive in invoking env but `unitEnv` lacks `KCAP_CONSENT_SEED_DEFAULT` → `DirectiveMissing`; unit value ≠ `"prompt"` → `DirectiveInvalid`; `DaemonDigest.Matches(unitBinaryPath)` false → path-equality split: unit binary path canonically equals `installBinaryPath` → `PackageInconsistent`, else `ForeignBinary`; unit effective identity (baked `KCAP_URL` precedence, else baked-profile lookup under baked `KCAP_CONFIG_DIR` via `ConfigMutator.LoadPure`) vs invoking expectation (`env("KCAP_PROFILE")` + `env("KCAP_EXPECT_SERVER_URL")`) mismatch, OR the unit's own baked `KCAP_EXPECT_SERVER_URL` differing from either → `IdentityMismatch`; unreadable/duplicate/ambiguous evidence → `EvidenceUnreadable`. Null → pass.
Phase placement in `StartVerifiedAsync`: Phase A runs after `ServiceTxnLock` acquisition and the fresh query, BEFORE the marker/mutation — failure prints exactly one `start_gate_reason=` line + returns 28 (nothing touched). Phase B (gated path only): instead of kickstart-when-loaded, boot out any loaded label, re-evaluate BOTH fingerprints (plist re-read + digest re-check) immediately before bootstrap — change detected → marker-backed rollback to `unloaded-plist-retained` + exit 29.

- [ ] **Step 1: Write the failing pure-gate tests**

```csharp
[Test]
public async Task Gate_inactive_without_invoking_directive() {
    var r = ServiceVerify.EvaluateStartGate(new Dictionary<string, string>(), "/b", "/b", _ => null);
    await Assert.That(r).IsNull();
}

[Test]
public async Task Missing_unit_directive_is_directive_missing() {
    var r = ServiceVerify.EvaluateStartGate(
        new Dictionary<string, string>(),  // unit bakes nothing
        "/b", "/b", k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
    await Assert.That(r).IsEqualTo(StartGateReason.DirectiveMissing);
}

[Test]
public async Task Unit_directive_with_wrong_value_is_directive_invalid() {
    var unit = new Dictionary<string, string> { ["KCAP_CONSENT_SEED_DEFAULT"] = "allow" };
    var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b",
        k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null);
    await Assert.That(r).IsEqualTo(StartGateReason.DirectiveInvalid);
}

[Test]
public async Task Digest_mismatch_at_canonical_sibling_is_package_inconsistent_elsewhere_foreign() {
    // placeholder digest in test builds → Matches() false for any file:
    var unit = new Dictionary<string, string> { ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt" };
    string? Env(string k) => k switch {
        "KCAP_CONSENT_SEED_DEFAULT" => "prompt", _ => null };

    var same = ServiceVerify.EvaluateStartGate(unit, "/opt/kcap/kcap-daemon", "/opt/kcap/kcap-daemon", Env);
    await Assert.That(same).IsEqualTo(StartGateReason.PackageInconsistent);

    var other = ServiceVerify.EvaluateStartGate(unit, "/somewhere/else/kcap-daemon", "/opt/kcap/kcap-daemon", Env);
    await Assert.That(other).IsEqualTo(StartGateReason.ForeignBinary);
}

[Test]
public async Task Stale_unit_expectation_is_identity_mismatch() {
    var unit = new Dictionary<string, string> {
        ["KCAP_CONSENT_SEED_DEFAULT"] = "prompt",
        ["KCAP_PROFILE"] = "a",
        ["KCAP_URL"] = "https://s.example",              // unit resolves S
        ["KCAP_EXPECT_SERVER_URL"] = "https://s.example",
    };
    string? Env(string k) => k switch {
        "KCAP_CONSENT_SEED_DEFAULT" => "prompt",
        "KCAP_PROFILE" => "a",
        "KCAP_EXPECT_SERVER_URL" => "https://t.example",  // fresh invocation expects T
        _ => null };
    // digest can't pass in test builds — inject a digest-pass seam for identity-only tests:
    var r = ServiceVerify.EvaluateStartGate(unit, "/b", "/b", Env, digestMatches: _ => true);
    await Assert.That(r).IsEqualTo(StartGateReason.IdentityMismatch);
}
```

(Add the `Func<string, bool>? digestMatches = null` test seam to `EvaluateStartGate`, defaulting to `DaemonDigest.Matches`.)

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement `EvaluateStartGate`** exactly per the rules; order of checks: directive-missing → directive-invalid → digest (foreign/package) → identity → pass; wrap ALL evidence reads in try/catch → `EvidenceUnreadable`. Reason-line writer: `Say($"start_gate_reason={reason switch { StartGateReason.DirectiveMissing => "directive_missing", StartGateReason.DirectiveInvalid => "directive_invalid", StartGateReason.IdentityMismatch => "identity_mismatch", StartGateReason.ForeignBinary => "foreign_binary", StartGateReason.PackageInconsistent => "package_inconsistent", _ => "evidence_unreadable" }}");`

- [ ] **Step 4: Wire Phase A + Phase B into `StartVerifiedAsync`** (gated only when `gateEnv(SeedVar)` non-empty): Phase A after the initial under-lock query using `readPlist(plistPath)` + `EnvFromPlist`; Phase B replaces the kickstart arm — `manager.Stop(id)` (bootout) when loaded, re-read plist + re-check digest, drift → write marker phase `"gate-drift"` → `Rollback(serviceId)` → print `verify_start_gate_drift` + reason line context → return 29. Add `FakeServiceManager`-driven tests: loaded-inactive unit whose `readPlist` seam returns different content on the second read → exit 29, final state unloaded-plist-retained (assert `manager.Calls` has `stop` and no `start` after drift); pre-mutation gate failure → exit 28 and `manager.Calls` contains only `query` entries.

- [ ] **Step 5: Run the ServiceVerify suites. Expected: PASS (existing ungated tests untouched — gate default-inactive).**

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/Services/ServiceVerify.cs test/Capacitor.Cli.Tests.Unit/Services/
git commit -m "AI-1655: gated service start — exit 28 phase-A reasons, exit 29 phase-B drift"
```

---

### Task 17: Install/replace digest viability arm + rechecks

**Files:**
- Modify: `src/Capacitor.Cli/Services/ServiceVerify.cs:242-360` (`InstallVerifiedAsync`)
- Test: extend `test/Capacitor.Cli.Tests.Unit/Services/ServiceVerifyInstallTests.cs`

**Interfaces:**
- Consumes: `DaemonDigest`, the existing viability arm (missing sibling / unusable profile → `VerifyExit.Viability`).
- Produces: when the invoking env carries the seed directive (same `gateEnv` seam), install/replace: (1) viability gains a digest check — `DaemonDigest.Matches(installBinaryPath)` false → `VerifyExit.Viability` (21) + line `viability_reason=package_inconsistent`; (2) immediately before bootstrap AND at the final post-readiness recheck, re-check the digest — drift → `InstallRollback(...)` with the existing rollback machinery, exit `VerifyExit.StartGateDrift` (29) + reason line. Ungated installs: unchanged.

- [ ] **Step 1: Write the failing tests** (FakeServiceManager pattern; `digestMatches` seam scripted to flip between calls):

```csharp
[Test]
public async Task Gated_install_with_bad_sibling_digest_aborts_viability_with_reason_line() {
    var dir = Directory.CreateTempSubdirectory().FullName;
    DaemonLockPaths.OverrideDirectoryForTesting(dir);
    try {
        var manager = new FakeServiceManager();
        Task<HelloProbeResult> Hello(string id, TimeSpan _) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
        var stderr = CaptureStderr(); // suite helper capturing Console.Error, or assert via Say-seam
        var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
            gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null,
            digestMatches: _ => false);   // viability digest check fails

        var exit = await sut.InstallVerifiedAsync(TestSpec(), replace: false, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.Viability);
        await Assert.That(stderr.ToString()).Contains("viability_reason=package_inconsistent");
        await Assert.That(manager.Calls.Where(c => c is "start" or "stop")).IsEmpty();
    } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
}

[Test]
public async Task Gated_install_digest_drift_before_bootstrap_rolls_back_with_29() {
    var dir = Directory.CreateTempSubdirectory().FullName;
    DaemonLockPaths.OverrideDirectoryForTesting(dir);
    try {
        var manager = new FakeServiceManager();
        Task<HelloProbeResult> Hello(string id, TimeSpan _) =>
            Task.FromResult(new HelloProbeResult(true, 1, "1.2.3", "kcap-daemon"));
        var digestCalls = 0;
        var sut = new ServiceVerify(manager, _ => 4242, Hello, TimeProvider.System,
            gateEnv: k => k == "KCAP_CONSENT_SEED_DEFAULT" ? "prompt" : null,
            digestMatches: _ => Interlocked.Increment(ref digestCalls) == 1); // pass viability, fail pre-bootstrap

        var exit = await sut.InstallVerifiedAsync(TestSpec(), replace: false, expectedVersion: null);

        await Assert.That(exit).IsEqualTo(VerifyExit.StartGateDrift);
        // install's verified-safe failure state: no unit on disk
        await Assert.That(File.Exists(LaunchdUnit.PlistPath(TestSpec().ServiceId))).IsFalse();
    } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
}
```

(`TestSpec()` / `CaptureStderr()` — reuse the install suite's existing spec builder and output-capture helpers; `gateEnv`/`digestMatches` are the new optional ctor seams from Task 16.)

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement** — thread `gateEnv`/`digestMatches` through `InstallVerifiedAsync`; the pre-bootstrap re-check sits right before the bootstrap call inside the transaction; the post-readiness re-check joins the existing final on-disk recheck.

- [ ] **Step 4: Run the install/replace suites. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Services/ServiceVerify.cs test/Capacitor.Cli.Tests.Unit/Services/
git commit -m "AI-1655: install/replace embedded-digest viability + pre-bootstrap and post-readiness rechecks"
```

---

### Task 18: Boot-refusal attribution in verify readiness failures

**Files:**
- Modify: `src/Capacitor.Cli/Services/ServiceVerify.cs` (pre-clear + readiness-failure marker consultation)
- Create: `src/Capacitor.Cli/Services/BootRefusalReader.cs` (CLI-side read of the daemon's marker)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/BootRefusalAttributionTests.cs` (new)

**Interfaces:**
- Consumes: the marker file shape from Task 12 (`boot-refusal.json` in `{DaemonLockPaths.Directory or StateDir}/{Sanitize(name)}/`); `IsReadyAsync`'s observed `JobPid`.
- Produces:

```csharp
// CLI-side reader (duplicated record — the CLI does not reference the daemon project;
// same pattern as the app's duplicated verify codes):
public sealed record BootRefusalEvidence(
    string DaemonName, string Token, string? Expectation, string? Resolved,
    int Pid, string? InstanceId, string? AttemptId);

public static class BootRefusalReader {
    public static string MarkerPath(string daemonName) =>
        Path.Combine(DaemonLockPaths.Directory, DaemonLockPaths.Sanitize(daemonName), "boot-refusal.json");
    public static BootRefusalEvidence? TryRead(string daemonName);      // corrupt → null (leave file)
    public static bool TryClear(string daemonName);                     // VERIFIED delete: returns false if still exists
    public static void Consume(string daemonName);                      // best-effort delete after attribution
}

// ServiceVerify (gated path): attribution rules —
//   pre-clear: TryClear before bootstrap; false → coded attribution DISABLED for this action (log line), proceed.
//   on readiness timeout: TryRead; attribute ONLY when evidence.DaemonName == serviceId's daemon name
//   AND evidence.Expectation == the unit's baked KCAP_EXPECT_SERVER_URL
//   AND evidence.Pid equals a JobPid positively observed via IsReadyAsync during THIS readiness window;
//   attributed → Say($"refusal_reason={evidence.Token}") + Consume; exit stays VerifyExit.ReadinessTimeout.
```

Total refusal tokens (Global Constraints): `server_expectation_mismatch`, `consent_seed_unwritable`, `consent_seed_invalid`. The `refusal_reason=` line obeys prefix rules (exactly one line emitted).

- [ ] **Step 1: Write the failing tests**

```csharp
public class BootRefusalAttributionTests {
    // pure-rule tests on a static helper:
    // internal static bool Attributable(BootRefusalEvidence e, string daemonName, string? unitExpectation, IReadOnlySet<int> observedJobPids)

    [Test]
    public async Task Marker_with_matching_name_expectation_and_observed_pid_attributes() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://s", "https://t", 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://s", new HashSet<int> { 4242 })).IsTrue();
    }

    [Test]
    public async Task Foreign_pid_never_attributes_even_with_same_name_and_expectation() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://s", "https://t", 9999, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://s", new HashSet<int> { 4242 })).IsFalse();
    }

    [Test]
    public async Task Different_daemon_name_never_attributes() {
        var e = new BootRefusalEvidence("other", "consent_seed_unwritable", null, null, 4242, "i", null);
        await Assert.That(ServiceVerify.Attributable(e, "d1", null, new HashSet<int> { 4242 })).IsFalse();
    }

    [Test]
    public async Task Attempt_id_bearing_marker_is_detached_evidence_not_service_evidence() {
        var e = new BootRefusalEvidence("d1", "server_expectation_mismatch", "https://s", "https://t", 4242, "i", "att-1");
        await Assert.That(ServiceVerify.Attributable(e, "d1", "https://s", new HashSet<int> { 4242 })).IsFalse();
    }
}
```

Plus a `FakeServiceManager`-driven test: readiness timeout with a marker planted (matching name/expectation/pid=the fake's `RunningPid`) → stderr contains exactly one `refusal_reason=server_expectation_mismatch` line and the marker file is gone; pre-clear failure (marker file locked/undeletable — simulate by planting a DIRECTORY at the marker path) → no `refusal_reason=` line, a logged notice, mutation proceeds.

- [ ] **Step 2: Run to verify FAIL.**

- [ ] **Step 3: Implement** — collect observed job PIDs during the readiness polls into a set; on the timeout path run the attribution; `TryClear` before bootstrap on the gated path. Success path (`Ok`) also calls `Consume` (hygiene).

- [ ] **Step 4: Run suites. Expected: PASS.**

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Services/ test/Capacitor.Cli.Tests.Unit/Services/BootRefusalAttributionTests.cs
git commit -m "AI-1655: boot-refusal marker attribution (verified pre-clear + job-PID correlation)"
```

---

### Task 19: Substrate wrap-up — AOT, suites, spec/plan riders

**Files:**
- Verify only (no new source); plan + spec ride the PR.

- [ ] **Step 1: Full unit + integration suites**

Run:
```bash
TMPDIR=/private/tmp dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj
dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj
```
Expected: PASS (know the baseline: ~15 pre-existing local failures unrelated to this work may exist per repo memory — compare against a pre-branch baseline run, no NEW failures).

- [ ] **Step 2: AOT publish checks**

Run:
```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' ; echo "cli-exit=$?"
dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}' ; echo "daemon-exit=$?"
```
Expected: no IL-warning lines (grep exit 1).

- [ ] **Step 3: Push and open PR 1**

```bash
git push https://github.com/kurrent-io/kcap-cli.git alexeyzimarev/ai-1655-first-run-onboarding-wizard-desktop-supervisor-slice-3
```
PR body references `AI-1655` and its GitHub issue (`Closes` is NOT used — two more PRs follow; use `Part of #<issue>`); title: "Onboarding substrate: consent seeding, service gates, config mutation API (AI-1655 1/3)". Note in the body: only `license/cla` is a required check — poll `gh pr checks` manually before merging (repo memory: auto-merge fires early).

- [ ] **Step 4: Commit any wrap-up fixes**

```bash
git add -A && git commit -m "AI-1655: substrate wrap-up (test/AOT fixes)"
```

---

## Self-Review Notes

- **Spec coverage (Plan A scope):** decision 4 (Tasks 10-12, 15), decision 7's daemon-side dependency `consent/3` (Task 9), decision 8 (Tasks 4-5), decision 9's child-suppression (Task 1), decision 10 (Tasks 2-3), §3 step-7 CLI gates (Tasks 16-17), §4 marker/carriers (Tasks 10-12, 18), §6 conditional put + seeding (Tasks 9, 11), §8 (Task 5), §11 digest pipeline (Task 14), status fields (Task 13). NOT in Plan A (deliberate): everything app-side (`DaemonMutationLane`, claims, gate, floor, resolver seam — Plan B), the Core auth façade + wizard (Plan C), and the §10 rows that require the app (adversarial launch with a real server, lane concurrency, channel semantics).
- **Known judgment calls for the implementer:** exact insertion lines shift as files change — anchor on the quoted code, not the line numbers; `LaunchConsentIpc` constructor change ripples through the test harness (one-line fixes); if `GetFileHash` MSBuild ordering fights back in Task 14, compute the hash in a tiny inline task or restrict digest sourcing to the `-p:KcapDaemonDigest` property (release) + placeholder (dev) — the dev co-built arm is a convenience, not a safety property (placeholder fails closed).
