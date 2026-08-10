# AI-1654 Daemon Lifecycle Management + PATH Shim Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The desktop app installs/starts the kcap daemon as a launchd LaunchAgent through crash-safe CLI transactions, offers takeover on version skew, and installs a `/usr/local/bin/kcap` PATH shim.

**Architecture:** Approach A per the approved spec (`docs/superpowers/specs/2026-08-10-ai1654-daemon-lifecycle-path-shim-design.md` — read it first; every task cites its section). The app shells the CLI for queries and **single-call** mutations; every destructive sequence lives inside ONE CLI process (`service install [--replace] --verify`, `service start --verify`) that owns mutation + ownership/readiness verification + rollback. The app adds a lifecycle state machine, dialogs, and the shim.

**Tech Stack:** .NET 10 NativeAOT, TUnit (Microsoft Testing Platform), Avalonia + ReactiveUI (app), source-generated System.Text.Json only.

## Global Constraints

- AOT: no reflection-based JSON — every new DTO goes in a `JsonSerializerContext`; `dotnet publish -c Release` must show no IL3050/IL2026 (run after CLI changes; `dotnet build` does NOT surface them).
- No `JsonArray` collection expressions (`new JsonArray(...)` constructor only). Use `JsonElementExtensions` instead of checking JSON value kinds.
- Windows CI leg exists: build path assertions with `Path.Combine`, never hardcoded `/`.
- TUnit test filter syntax: `--treenode-filter "/*/*/ClassName/*"` (glob; bare `*ClassName*` silently matches nothing).
- Read agent-owned files only via `FileShare.ReadWrite` opens (not relevant to files this plan creates — config-owned files are fine with normal reads).
- Comments: sparse, only for constraints code can't show. No Linear issue numbers in comments (GitHub numbers allowed).
- README + `src/Capacitor.Cli.Core/Resources/help-usage.txt` must be updated in this same PR for the new CLI surface (Task 25).
- Commit messages: end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Run CLI unit tests: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/<ClassName>/*"`. App tests: same with `test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj`.

---

## Part A — CLI foundations (Tasks 1–8)

### Task 1: Tri-state launchd classification + rich `ServiceQuery`

Spec §3.4 "Tri-state launchd classification". `launchctl` results become `Loaded`/`Absent`/`Unknown` — `Absent` ONLY on the positive could-not-find signature. The manager gains a `Query` method that also runs the launchd probe when the plist is absent (orphan labels) and parses `job_pid`.

**Files:**
- Modify: `src/Capacitor.Cli/Services/IServiceManager.cs` (add records + interface method)
- Modify: `src/Capacitor.Cli/Services/LaunchdUnit.cs` (classifier + pid parse)
- Modify: `src/Capacitor.Cli/Services/LaunchdServiceManager.cs` (implement `Query`)
- Modify: `src/Capacitor.Cli/Services/SystemdUnit.cs` + `src/Capacitor.Cli/Services/WindowsTaskUnit.cs` managers (minimal `Query` mapping — find the two `IServiceManager` implementations in those files/siblings and delegate to existing `Status`)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdClassifyTests.cs`

**Interfaces (Produces):**
```csharp
enum LabelProbe { Loaded, Absent, Unknown }
// UnitPresent = plist file exists; State/BinaryPath keep existing semantics;
// JobPid = launchctl's "pid = N" when running, else null; Probe = the tri-state.
record ServiceQuery(LabelProbe Probe, bool UnitPresent, ServiceState State, string? BinaryPath, int? JobPid);
interface IServiceManager { /* existing members … */ ServiceQuery Query(string serviceId); }
// LaunchdUnit gains:
public static LabelProbe ClassifyPrint(int exitCode, string stdout, string stderr);
public static int? PidFromPrint(string stdout);
```

- [ ] **Step 1: Write failing tests**

```csharp
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class LaunchdClassifyTests {
    [Test]
    public async Task Zero_exit_running_is_loaded() {
        await Assert.That(LaunchdUnit.ClassifyPrint(0, "state = running\npid = 924\n", "")).IsEqualTo(LabelProbe.Loaded);
    }

    [Test]
    public async Task Could_not_find_is_absent() {
        await Assert.That(LaunchdUnit.ClassifyPrint(113, "", "Could not find service \"io.kurrent.kcap.daemon.x\" in domain for user gui: 501"))
            .IsEqualTo(LabelProbe.Absent);
    }

    [Test]
    public async Task Nonzero_without_not_found_signature_is_unknown() {
        await Assert.That(LaunchdUnit.ClassifyPrint(1, "", "Operation not permitted")).IsEqualTo(LabelProbe.Unknown);
    }

    [Test]
    public async Task Pid_parsed_from_print() {
        await Assert.That(LaunchdUnit.PidFromPrint("\tstate = running\n\tpid = 924\n")).IsEqualTo(924);
    }

    [Test]
    public async Task Pid_null_when_absent() {
        await Assert.That(LaunchdUnit.PidFromPrint("\tstate = waiting\n")).IsNull();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/LaunchdClassifyTests/*"`
Expected: compile error (`LabelProbe` not defined).

- [ ] **Step 3: Implement**

In `IServiceManager.cs` add `LabelProbe`, `ServiceQuery`, and `ServiceQuery Query(string serviceId);` to the interface. In `LaunchdUnit.cs`:

```csharp
public static LabelProbe ClassifyPrint(int exitCode, string stdout, string stderr) {
    if (exitCode == 0) return LabelProbe.Loaded;
    // The one positive not-found signature launchctl emits; anything else is
    // indistinguishable from permission/tool failure and must stay Unknown.
    return stderr.Contains("Could not find service", StringComparison.OrdinalIgnoreCase)
        ? LabelProbe.Absent
        : LabelProbe.Unknown;
}

public static int? PidFromPrint(string stdout) {
    foreach (var line in stdout.Split('\n')) {
        var t = line.Trim();
        if (t.StartsWith("pid = ", StringComparison.Ordinal) && int.TryParse(t["pid = ".Length..].Trim(), out var pid))
            return pid;
    }
    return null;
}
```

In `LaunchdServiceManager.cs`:

```csharp
public ServiceQuery Query(string serviceId) {
    var path        = LaunchdUnit.PlistPath(serviceId);
    var unitPresent = File.Exists(path);
    var bin         = unitPresent ? LaunchdUnit.BinaryFromPlist(File.ReadAllText(path)) : null;
    // Probe launchd even without a plist: an orphaned loaded label must be visible.
    var (code, stdout, stderr) = ServiceProcess.Run("launchctl", LaunchdUnit.PrintArgs(Uid(), serviceId));
    var probe = LaunchdUnit.ClassifyPrint(code, stdout, stderr);
    var state = probe == LabelProbe.Loaded ? LaunchdUnit.StatusFromPrint(code, stdout) : ServiceState.NotInstalled;
    return new ServiceQuery(probe, unitPresent, state, bin, probe == LabelProbe.Loaded ? LaunchdUnit.PidFromPrint(stdout) : null);
}
```

For the systemd and Windows managers, implement `Query` as a translation of their existing `Status`: `new ServiceQuery(LabelProbe.Unknown /*no classifier yet*/, s.State != ServiceState.NotInstalled, s.State, s.BinaryPath, null)` — but return `LabelProbe.Loaded` when `State != NotInstalled` and `LabelProbe.Absent` when `NotInstalled`, so `--json` (Task 4) doesn't fail spuriously on those platforms. macOS is the only platform with the transaction in this slice.

- [ ] **Step 4: Run tests + full CLI unit suite; expected PASS.**
- [ ] **Step 5: Commit** — `feat: tri-state launchd classification + ServiceQuery`

### Task 2: Validated daemon-pid probe (`DaemonPidProbe`)

Spec §3.4 `daemon_pid`: PID-file owner, live identity-validated via the existing `IsOurDaemon` start-token check. Extract the existing private logic in `DaemonCommands` into a reusable internal helper (do NOT duplicate it — move and delegate).

**Files:**
- Create: `src/Capacitor.Cli/Services/DaemonPidProbe.cs`
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (move `ReadPidFile`/`IsOurDaemon`/`ProcessStartToken` call sites to use the probe; keep behavior identical)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/DaemonPidProbeTests.cs`

**Interfaces (Produces):**
```csharp
static class DaemonPidProbe {
    /// Validated live owner of the name, or null (absent/unusable file, dead PID,
    /// token mismatch). Same semantics daemon stop uses before killing.
    public static int? ValidatedPid(string daemonName);
}
```

- [ ] **Step 1: Write failing tests** (use `DaemonLockPaths.OverrideDirectoryForTesting` — internal, already used by existing tests via `InternalsVisibleTo`):

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class DaemonPidProbeTests {
    [Test]
    public async Task Null_when_no_pid_file() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try { await Assert.That(DaemonPidProbe.ValidatedPid("nosuch")).IsNull(); }
        finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Null_for_dead_pid() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            // PID 1 exists but is not kcap-daemon; an impossible token forces the token path to mismatch.
            File.WriteAllText(DaemonLockPaths.PidPath("x"), "999999999 tok:deadbeef");
            await Assert.That(DaemonPidProbe.ValidatedPid("x")).IsNull();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }
}
```

(Write the PID-file content in whatever exact format `ReadPidFile` parses — read `DaemonCommands.ReadPidFile` first and mirror the real format; the dead-PID test uses a PID far above `pid_max`.)

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** — move the body of the existing PID validation into `DaemonPidProbe.ValidatedPid` (returns the pid only when `ReadPidFile` parses AND the process exists AND `IsOurDaemon(pid, token)`), then change `DaemonCommands`' stop path to call the probe where it previously used the private helpers. `ReadPidFile`/`IsOurDaemon` move into `DaemonPidProbe` as private statics; `DaemonCommands` keeps only calls.
- [ ] **Step 4: Run the FULL CLI unit suite** (stop-path tests must stay green — this is a pure refactor plus one new public seam).
- [ ] **Step 5: Commit** — `refactor: extract DaemonPidProbe (validated pid-file owner)`

### Task 3: Per-label service transaction lock (`ServiceTxnLock`)

Spec §3.4 "Per-label cross-process lock": distinct file, fixed `DaemonLockPaths.Directory` namespace, command-layer ownership, non-blocking probe for status, never unlinked, bounded contention.

**Files:**
- Create: `src/Capacitor.Cli/Services/ServiceTxnLock.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceTxnLockTests.cs`

**Interfaces (Produces):**
```csharp
sealed class ServiceTxnLock : IDisposable {
    public static string LockPath(string serviceId); // {DaemonLockPaths.Directory}/{serviceId}.service-lock
    /// Blocks up to `wait`; null on contention timeout. Lock file is created but NEVER deleted.
    public static ServiceTxnLock? TryAcquire(string serviceId, TimeSpan wait);
    /// Non-blocking probe: true iff some process currently holds the lock.
    public static bool IsHeld(string serviceId);
    public void Dispose(); // releases the FileStream lock
}
```

- [ ] **Step 1: Write failing tests**

```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTxnLockTests {
    [Test]
    public async Task Acquire_release_and_probe() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            await Assert.That(ServiceTxnLock.IsHeld("a")).IsFalse();
            using (var l = ServiceTxnLock.TryAcquire("a", TimeSpan.Zero)) {
                await Assert.That(l).IsNotNull();
                await Assert.That(ServiceTxnLock.IsHeld("a")).IsTrue();
                await Assert.That(ServiceTxnLock.TryAcquire("a", TimeSpan.FromMilliseconds(50))).IsNull(); // bounded contention
            }
            await Assert.That(ServiceTxnLock.IsHeld("a")).IsFalse();      // released
            await Assert.That(File.Exists(ServiceTxnLock.LockPath("a"))).IsTrue(); // never unlinked
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Distinct_from_daemon_lock_path() {
        await Assert.That(ServiceTxnLock.LockPath("a")).IsNotEqualTo(DaemonLockPaths.LockPath("a"));
    }
}
```

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** with `FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)` retried on `IOException` until `wait` elapses (poll every 100 ms; `TimeSpan.Zero` = single attempt). `IsHeld` = try open with `FileShare.None`, dispose immediately, return false on success / true on `IOException` when the file exists. Note: same-process re-open with `FileShare.None` conflicts too, which is exactly what the tests exercise.
- [ ] **Step 4: Run tests; PASS.**
- [ ] **Step 5: Commit** — `feat: per-label service transaction flock`

### Task 4: `service status --json`

Spec §3.4 JSON shape. Fails non-zero when the launchd probe is `Unknown`. `install_binary_path` comes from the existing `ResolveDaemonBinary()` (make it `internal static` in `DaemonCommands` if private). `txn_marker`/`txn_active` are wired now with the marker existence (Task 5) and lock probe (Task 3); until Task 5 lands `txn_marker` reads a plain `File.Exists` on the marker path constant defined here and moved into `ServiceTxnMarker` in Task 5.

**Files:**
- Create: `src/Capacitor.Cli/Commands/ServiceStatusJson.cs` (DTO + context + renderer)
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (`ServiceAsync`: `case "status"` honors `--json`)
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/ServiceStatusJsonTests.cs`

**Interfaces (Produces):**
```csharp
// snake_case on the wire via JsonSourceGenerationOptions(PropertyNamingPolicy = SnakeCaseLower)
public sealed record ServiceStatusJson(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath,
    string? InstallBinaryPath, int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive);
[JsonSerializable(typeof(ServiceStatusJson))] public partial class ServiceJsonContext : JsonSerializerContext;
// Renderer (pure, testable):
internal static class ServiceStatusRender {
    /// Returns (json, exitCode). Probe==Unknown => (null, 1) — unknown never masquerades as not_installed.
    public static (string? Json, int ExitCode) Render(ServiceQuery q, string serviceId,
        string? installBinaryPath, int? daemonPid, bool txnMarker, bool txnActive);
}
```

- [ ] **Step 1: Write failing tests**

```csharp
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ServiceStatusJsonTests {
    [Test]
    public async Task Renders_snake_case_full_payload() {
        var q = new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/u/kcap-daemon", 42);
        var (json, exit) = ServiceStatusRender.Render(q, "default", "/i/kcap-daemon", 42, false, false);
        await Assert.That(exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(json!);
        var r = doc.RootElement;
        await Assert.That(r.GetProperty("service_id").GetString()).IsEqualTo("default");
        await Assert.That(r.GetProperty("unit_present").GetBoolean()).IsTrue();
        await Assert.That(r.GetProperty("state").GetString()).IsEqualTo("running");
        await Assert.That(r.GetProperty("binary_path").GetString()).IsEqualTo("/u/kcap-daemon");
        await Assert.That(r.GetProperty("install_binary_path").GetString()).IsEqualTo("/i/kcap-daemon");
        await Assert.That(r.GetProperty("job_pid").GetInt32()).IsEqualTo(42);
        await Assert.That(r.GetProperty("daemon_pid").GetInt32()).IsEqualTo(42);
        await Assert.That(r.GetProperty("txn_active").GetBoolean()).IsFalse();
    }

    [Test]
    public async Task Unknown_probe_fails_nonzero() {
        var q = new ServiceQuery(LabelProbe.Unknown, true, ServiceState.NotInstalled, "/u/kcap-daemon", null);
        var (json, exit) = ServiceStatusRender.Render(q, "default", null, null, false, false);
        await Assert.That(json).IsNull();
        await Assert.That(exit).IsEqualTo(1);
    }

    [Test]
    public async Task Present_but_unloaded_reports_not_installed_with_unit_present() {
        var q = new ServiceQuery(LabelProbe.Absent, true, ServiceState.NotInstalled, "/u/kcap-daemon", null);
        var (json, _) = ServiceStatusRender.Render(q, "d", null, null, false, false);
        using var doc = JsonDocument.Parse(json!);
        await Assert.That(doc.RootElement.GetProperty("state").GetString()).IsEqualTo("not_installed");
        await Assert.That(doc.RootElement.GetProperty("unit_present").GetBoolean()).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.** `state` string = `q.State switch { NotInstalled => "not_installed", Installed => "installed", Running => "running" }`. Wire into `ServiceAsync`: when `rest.Contains("--json")`, call `manager.Query(id)`, gather `installBinaryPath = ResolveDaemonBinary()`, `daemonPid = DaemonPidProbe.ValidatedPid(id)`, `txnActive = ServiceTxnLock.IsHeld(id)`, `txnMarker = File.Exists(Path.Combine(DaemonLockPaths.Directory, id + ".service-txn"))`; print json or the unknown error to stderr. Human path unchanged.
- [ ] **Step 4: Run tests + `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` (expect no output).**
- [ ] **Step 5: Commit** — `feat: kcap daemon service status --json`

### Task 5: Durable phase-recording transaction marker (`ServiceTxnMarker`)

Spec §3.4 "Marker first, durable, and phase-recording".

**Files:**
- Create: `src/Capacitor.Cli/Services/ServiceTxnMarker.cs`
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (status `--json` `txn_marker` now uses `ServiceTxnMarker.Exists(id)`)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceTxnMarkerTests.cs`

**Interfaces (Produces):**
```csharp
// snake_case JSON, own context in the same file (MarkerJsonContext).
public sealed record TxnMarker(
    int Version,            // 1
    string Operation,       // "install" | "replace" | "start"
    string Phase,           // "captured"|"label-cleared"|"owner-stopped"|"written"|"bootstrapped"|"committed"
    string PreState,        // serialized ServiceQuery summary, e.g. "loaded|unit|/path|pid=42"
    string SafeState,       // "no-unit" | "unloaded-plist-retained"
    string? PlistFingerprint); // SHA-256 hex of the exact plist text this txn wrote, once written
static class ServiceTxnMarker {
    public static string MarkerPath(string serviceId); // fixed namespace: DaemonLockPaths.Directory
    public static bool Exists(string serviceId);
    public static TxnMarker? Read(string serviceId);        // null on missing/corrupt (defaults, no crash)
    public static void Write(string serviceId, TxnMarker m); // temp+rename, fsync file AND directory
    public static void Delete(string serviceId);
    public static string Fingerprint(string plistText);      // SHA-256 hex
}
```

- [ ] **Step 1: Write failing tests** — round-trip write/read/delete under `OverrideDirectoryForTesting`; corrupt file → `Read` returns null; `Fingerprint` is stable hex; `Write` then `Read` after phase update returns the new phase; marker path is under `DaemonLockPaths.Directory` (assert with `Path.Combine`).
```csharp
using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceTxnMarkerTests {
    static TxnMarker M(string phase = "captured") => new(1, "install", phase, "absent|nounit||pid=", "no-unit", null);

    [Test]
    public async Task Roundtrip_and_phase_update() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            ServiceTxnMarker.Write("a", M());
            await Assert.That(ServiceTxnMarker.Read("a")!.Phase).IsEqualTo("captured");
            ServiceTxnMarker.Write("a", M("written") with { PlistFingerprint = ServiceTxnMarker.Fingerprint("<plist/>") });
            await Assert.That(ServiceTxnMarker.Read("a")!.Phase).IsEqualTo("written");
            ServiceTxnMarker.Delete("a");
            await Assert.That(ServiceTxnMarker.Exists("a")).IsFalse();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }

    [Test]
    public async Task Corrupt_marker_reads_null() {
        var dir = Directory.CreateTempSubdirectory().FullName;
        DaemonLockPaths.OverrideDirectoryForTesting(dir);
        try {
            File.WriteAllText(ServiceTxnMarker.MarkerPath("a"), "{not json");
            await Assert.That(ServiceTxnMarker.Read("a")).IsNull();
        } finally { DaemonLockPaths.OverrideDirectoryForTesting(null); }
    }
}
```
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.** `Write`: serialize with `MarkerJsonContext`, write to `MarkerPath + ".tmp"` via a `FileStream`, `Flush(flushToDisk: true)`, `File.Move(tmp, path, overwrite: true)`, then open the directory… .NET cannot fsync a directory portably — on macOS/Linux use `File.OpenHandle` on the file post-rename + `RandomAccess.FlushToDisk(handle)`; directory durability: open the dir with `Directory` fd via `File.OpenHandle` is not supported — acceptable implementation: flush the file handle before AND after rename; document the directory-entry residual in a code comment as best-effort on .NET. (Spec durability intent honored to the extent the BCL allows; the marker content itself is torn-proof.)
- [ ] **Step 4: Run tests; PASS.**
- [ ] **Step 5: Commit** — `feat: durable phase-recording service transaction marker`

### Task 6: `service uninstall` hardening (benign absence)

Spec §3.4 uninstall bullet. On non-zero bootout, re-query: `Absent` → success + delete; `Loaded`/`Unknown` → retain + non-zero. Success asserts label absence AND file removal. Mutating verbs acquire the Task-3 lock at the command layer.

**Files:**
- Modify: `src/Capacitor.Cli/Services/LaunchdServiceManager.cs` (`Uninstall` returns a result instead of void — change `IServiceManager.Uninstall` to `bool Uninstall(string serviceId, out string? error)`; update all three managers + callers)
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (`case "uninstall"` under `ServiceTxnLock`, exit non-zero on false)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdUninstallTests.cs`

Make `LaunchdServiceManager`'s launchctl runner injectable for this test: add an internal constructor parameter `Func<string, string[], (int, string, string)>? runProcess = null` defaulting to `ServiceProcess.Run` (same pattern as the existing `UnitFileWriter` seam).

- [ ] **Step 1: Write failing tests** — three cases with a scripted fake runner + temp `AgentsDir` (the plist path derives from `PathHelpers.HomeDirectory`; use the existing test seam for home if one exists in `PathHelpers`, else set `HOME` via the pattern neighboring service tests use — check `ServiceFilesTests` for the established approach and copy it):
  - bootout exit 0 → plist deleted, returns true.
  - bootout non-zero + re-query print says "Could not find service" → true, plist deleted (benign absence).
  - bootout non-zero + re-query print exit 0 (still loaded) → false, plist retained.
  - bootout non-zero + re-query permission error (unknown) → false, plist retained.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** exactly that sequence in `Uninstall`; wire the command case: acquire `ServiceTxnLock.TryAcquire(id, TimeSpan.FromSeconds(10))` (null → coded contention message, exit 1), call, report.
- [ ] **Step 4: Run full CLI suite (uninstall callers updated: `UninstallCommand.HandleAsync` compiles).**
- [ ] **Step 5: Commit** — `feat: uninstall distinguishes benign label absence from failed bootout`

### Task 7: `service stop`/`start` hardening (bootout / bootstrap-or-kickstart)

Spec §3.4 stop/start bullet. Stop = bootout retaining the plist (a SIGTERM cannot stop a lock-losing KeepAlive job); start = bootstrap when unloaded, kickstart when loaded. Both under the lock.

**Files:**
- Modify: `src/Capacitor.Cli/Services/LaunchdServiceManager.cs` (`Stop` → bootout; `Start` → probe then bootstrap/kickstart)
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (lock around `case "start"`/`case "stop"`)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdStartStopTests.cs`

- [ ] **Step 1: Write failing tests** with the scripted runner: `Stop` issues `bootout` argv (not `kill`) and leaves the plist file; `Start` with probe=Absent issues `bootstrap` with the plist path; probe=Loaded issues `kickstart`; probe=Unknown throws/errors without issuing either.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.** Note `StopByName`'s guard message in `DaemonCommands` ("a raw stop would be auto-restarted") stays true — after this task it is *more* true; no change there.
- [ ] **Step 4: Run full CLI suite.**
- [ ] **Step 5: Commit** — `feat: service stop unloads the label; start bootstraps when unloaded`

### Task 8: Bounded `ServiceProcess` + one-shot `HelloProbe`

Spec §3.4 two-phase deadline ("every child launchctl invocation gets the remaining phase time") and the per-verb hello probe.

**Files:**
- Modify: `src/Capacitor.Cli/Services/ServiceProcess.cs` (add `RunBounded`)
- Create: `src/Capacitor.Cli/Services/HelloProbe.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceProcessBoundedTests.cs`, `test/Capacitor.Cli.Tests.Unit/Services/HelloProbeTests.cs`

**Interfaces (Produces):**
```csharp
// ServiceProcess gains:
public static (int ExitCode, string StdOut, string StdErr, bool TimedOut) RunBounded(
    string file, string[] args, TimeSpan timeout);   // tree-kills + awaits on timeout
// HelloProbe:
public sealed record HelloProbeResult(bool WellFormed, int? ProtocolVersion, string? DaemonVersion, string? DaemonName);
static class HelloProbe {
    /// One dial + Hello frame + reply, bounded. WellFormed=false on connect failure,
    /// timeout, non-HelloReply frame, or undeserializable payload.
    public static Task<HelloProbeResult> RunAsync(string daemonName, TimeSpan timeout);
}
```

- [ ] **Step 1: Write failing tests.** `RunBounded`: `/bin/sleep 30` with 200 ms timeout → `TimedOut=true`, returns promptly (< 5 s). `HelloProbe`: start an in-test `UnixDomainSocketEndPoint` listener on `LocalSocketPaths.Socket(name)` (under `OverrideDirectoryForTesting` if socket paths honor it — check `LocalSocketPaths` first; the existing `LocalControlClient` tests show how to host a fake daemon socket — copy that harness) that replies with a valid `HelloReplyDto` (via `FrameCodec.WriteAsync`, `HelloIpcJsonContext`) → `WellFormed=true`, version/name populated. No listener → `WellFormed=false`. Listener replying an `Error` frame → `WellFormed=false`.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.** `RunBounded` = existing `Run` with `WaitForExit(timeout)`; on expiry `p.Kill(entireProcessTree: true)` then `WaitForExit()`. `HelloProbe` mirrors the hello leg of `LocalControlClient.RunCycleAsync` (dial, write `FrameType.Hello`, read one frame, deserialize `HelloReplyDto`) without the `status/1` capability gate — that gate is exactly what start-verify must NOT apply (spec §3.4 per-verb hello contract).
- [ ] **Step 4: Run tests; PASS.**
- [ ] **Step 5: Commit** — `feat: bounded ServiceProcess + one-shot hello probe`

---

## Part B — the CLI transaction (Tasks 9–12) and daemon exits (Task 13)

### Task 9: Transaction engine skeleton + `service start --verify`

Spec §3.4 `--verify`. One process: marker → mutate → ownership+readiness → final recheck → commit, or rollback to the verified-safe failure state. Engine is a class with injectable seams so every §7 case is drivable without launchctl.

**Files:**
- Create: `src/Capacitor.Cli/Services/ServiceVerify.cs`
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (`case "start"` honors `--verify`)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceVerifyStartTests.cs`

**Interfaces (Produces):**
```csharp
public static class VerifyExit {          // coded, stable; stderr carries the token
    public const int Ok = 0;
    public const int Contended = 20;      // service lock or name contended without --replace
    public const int Viability = 21;
    public const int BootoutUnknown = 22;
    public const int StopUnconfirmed = 23;
    public const int ReadinessTimeout = 24;
    public const int HelloValidation = 25;
    public const int RollbackBudget = 26;
    public const int RestoreVerification = 27;
}
/// Injectable clock + seams; defaults are production.
sealed class ServiceVerify(
    IServiceManager manager,
    Func<string, int?> validatedDaemonPid,                 // DaemonPidProbe.ValidatedPid
    Func<string, TimeSpan, Task<HelloProbeResult>> hello,  // HelloProbe.RunAsync
    TimeProvider time,
    TimeSpan? forwardBudget = null,   // default 20s
    TimeSpan? rollbackReserve = null) // default 10s
{
    /// start --verify: no viability check (spec: start writes nothing).
    /// Accepts ANY well-formed hello (capability-incompatible old daemons included).
    public Task<int> StartVerifiedAsync(string serviceId);
    /// install [--replace] --verify (Task 10/11).
    public Task<int> InstallVerifiedAsync(ServiceSpec spec, bool replace, string? expectedVersion);
}
```

Console writes inside the engine go through a private `Say(string)` that swallows `IOException` (spec: closed-stdio tolerance — the npm grandchild shares the GUI's pipes).

- [ ] **Step 1: Write failing tests** (fake `IServiceManager` recording argv-order; scripted hello; scripted pid):
  - **Happy bootstrap:** query Absent+plist → start → hello WellFormed + `Query().JobPid == validatedDaemonPid` → exit `Ok`; marker written before `manager.Start`, deleted after; phases observed `captured → bootstrapped → committed` (spy the marker via `ServiceTxnMarker.Read` snapshots the fake manager takes inside its `Start`).
  - **Readiness never satisfied** (hello always `WellFormed=false`, e.g. daemon died pre-socket): forward cutoff (use a `FakeTimeProvider`-style manual clock — TUnit tests elsewhere inject `TimeProvider`; copy that pattern) → rollback = `manager.Stop` (bootout, plist retained) → exit `ReadinessTimeout`; marker phase ends deleted after verified restore (fake query returns Absent post-stop).
  - **Ownership mismatch** (hello ok but `JobPid != daemon_pid`): rollback + `ReadinessTimeout`… no — distinct: ownership failure also lands in `ReadinessTimeout` after the deadline (the predicate simply never holds). Assert no `Uninstall` call ever happens on the start path (plist must be retained — verified-safe state).
  - **Start accepts capability-incompatible hello:** hello returns `WellFormed=true, DaemonVersion="0.9.0"` (no capability data at all) → still `Ok`.
  - **Rollback restore verification fails** (post-stop query still Loaded): exit `RestoreVerification`, marker retained.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** `StartVerifiedAsync`:

```
acquire ServiceTxnLock (bounded) else Contended
pre = manager.Query(id)
write marker(operation="start", phase="captured", pre, safe="unloaded-plist-retained")
manager.Start(id)                      // bootstrap-or-kickstart (Task 7)
update marker phase="bootstrapped"
poll until forward cutoff:
    hello(id, remaining) is WellFormed AND manager.Query(id).JobPid == validatedDaemonPid(id) (both non-null)
    → final recheck: same conjunction again → delete marker; return Ok
forward cutoff hit → rollback within reserve:
    manager.Stop(id)                   // bootout, plist retained
    re-query: Probe==Absent? → delete marker; return ReadinessTimeout
    else → keep marker; return RestoreVerification
```

Wire `case "start"`: `--verify` present → `new ServiceVerify(manager, DaemonPidProbe.ValidatedPid, HelloProbe.RunAsync, TimeProvider.System).StartVerifiedAsync(id)` exit code; stderr gets the coded token (`verify_readiness_timeout` etc. — one constant string per `VerifyExit` member, defined next to it).
- [ ] **Step 4: Run tests; PASS. Publish AOT check.**
- [ ] **Step 5: Commit** — `feat: service start --verify transaction`

### Task 10: `service install --verify` (fresh-install path)

Spec §3.4: viability inside, classifier-gated initial bootout, write only on positive `Absent`, expected-version hello validation, final on-disk fingerprint recheck, verified-safe failure state = no label + no file.

**Files:**
- Modify: `src/Capacitor.Cli/Services/ServiceVerify.cs` (`InstallVerifiedAsync`, `replace:false` path)
- Modify: `src/Capacitor.Cli/Services/LaunchdServiceManager.cs` (split `Install` so the engine can drive bootout/write/bootstrap as separate steps: add `void WriteAndBootstrap(ServiceSpec spec)` = `WriteUnitFiles` + bootstrap only, no leading bootout — the engine owns the bootout via classifier)
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (`ServiceInstall` honors `--verify`; computes `expectedVersion` from its own `AssemblyInformationalVersion` — same attribute `--version` prints)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceVerifyInstallTests.cs`

- [ ] **Step 1: Write failing tests:**
  - **Viability aborts before anything:** `installBinaryPath` seam returns null → exit `Viability`; fake manager records zero calls; no marker file.
  - **Initial probe Loaded/Unknown → abort `BootoutUnknown`**, nothing written (fresh install without `--replace` must not clear labels: exit `Contended` when Loaded — assert the distinction: Loaded → `Contended`, Unknown → `BootoutUnknown`).
  - **Happy path:** Absent → marker `captured` → write+bootstrap (fingerprint recorded, phase `written` then `bootstrapped`) → hello WellFormed with `DaemonVersion == expectedVersion` + ownership + final recheck incl. on-disk plist fingerprint match → `Ok`, marker gone.
  - **Version mismatch hello** (`DaemonVersion != expected`): → rollback (uninstall its own unit: label absent + file removed asserted) → `HelloValidation`.
  - **Old writer replaced the plist between bootstrap and final recheck** (fake returns different plist text on the final read): → rollback → `RestoreVerification`/rollback path — assert rollback deletes ONLY on fingerprint match: here the fingerprint mismatches, so the foreign plist is retained, marker retained, exit `RestoreVerification`.
  - **Lock-loser** (`ownership never holds`, hello well-formed but pids differ): forward cutoff → rollback (bootout + delete own unit, verified) → `ReadinessTimeout`; validated manual pid seam asserted never killed (no stop-owner call exists on the fresh path).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** `InstallVerifiedAsync(spec, replace:false, expectedVersion)`:

```
acquire lock else Contended
viability: spec.DaemonBinaryPath exists else Viability (caller resolved it; re-stat here)
pre = Query; probe Loaded → Contended; Unknown → BootoutUnknown
write marker(op="install", phase="captured", safe="no-unit")
plist = manager.GenerateFiles(spec).Single().Content; fingerprint → marker phase="written"
manager.WriteAndBootstrap(spec) → marker phase="bootstrapped"
poll: hello WellFormed && hello.DaemonVersion == expectedVersion && ownership
    → final recheck: ownership && File.ReadAllText(plistPath) fingerprint matches marker
        → marker phase="committed" → delete marker → Ok
    fingerprint mismatch → foreign writer: keep marker, keep foreign file, exit RestoreVerification
hello well-formed but wrong version → rollback → HelloValidation
cutoff → rollback (Uninstall own unit — only if current file fingerprint matches; verified Absent+gone) → ReadinessTimeout
rollback verification fails or reserve exhausted → keep marker → RestoreVerification / RollbackBudget
```

- [ ] **Step 4: Run tests; PASS.**
- [ ] **Step 5: Commit** — `feat: service install --verify (fresh path)`

### Task 11: `--replace` ownership matrix + raw-kill helper + marker recovery

Spec §3.4 `--replace` + marker recovery authority. Also the leftover-marker self-heal at transaction entry.

**Files:**
- Create: `src/Capacitor.Cli/Services/DaemonKill.cs` (raw-kill helper below `StopByName`)
- Modify: `src/Capacitor.Cli/Services/ServiceVerify.cs` (`replace:true` matrix + entry-time marker recovery)
- Modify: `src/Capacitor.Cli/Commands/DaemonCommands.cs` (`--replace` flag)
- Test: `test/Capacitor.Cli.Tests.Unit/Services/ServiceVerifyReplaceTests.cs`

**Interfaces (Produces):**
```csharp
static class DaemonKill {
    /// Kill the VALIDATED owner of the name. No console I/O, no service-installed
    /// check (the public StopByName guard would no-op exactly in the takeover case),
    /// no lock acquisition (the transaction already holds the service flock).
    /// True when the process is gone afterwards.
    public static bool KillValidatedOwner(string daemonName, int validatedPid, TimeSpan wait);
}
```

- [ ] **Step 1: Write failing tests:**
  - **Owning label:** pre Query = Loaded, JobPid==daemon_pid → sequence asserted: bootout (via `manager.Stop`) FIRST → re-query Absent + pid gone → write+bootstrap → verify → Ok. No `DaemonKill` call.
  - **Non-owning label + manual owner:** Loaded, JobPid != validatedPid → bootout label → then `DaemonKill.KillValidatedOwner` → termination confirmed (pid seam flips to null AND hello seam returns not-well-formed for the dead daemon) → install → Ok. Exact call order asserted.
  - **No live owner** (validatedPid null, probe Absent, plist present — reinstall-over-stopped-unit): NO kill call, straight to install → Ok.
  - **Stop unconfirmed** (pid seam keeps returning the pid): → `StopUnconfirmed`, nothing written.
  - **Entry-time marker recovery:** a leftover marker phase="written" with fingerprint F; on-disk plist matches F → cleaned (deleted) under the lock, then transaction proceeds. Marker "committed" → only the marker is cleared, existing unit untouched. Marker fingerprint ≠ on-disk plist → exit `RestoreVerification` (surface, never heal).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.** `DaemonKill.KillValidatedOwner`: `Process.GetProcessById(pid)` → `Kill(entireProcessTree: true)` → `WaitForExit(wait)` → return `!IsOurDaemon`-style existence check via the probe (re-call `validatedDaemonPid` — gone = null). The matrix slots into `InstallVerifiedAsync` between viability and the fresh-path bootout gate; stop-confirmation = validatedPid null AND a fresh hello dial fails, polled within the forward budget.
- [ ] **Step 4: Run tests; PASS.**
- [ ] **Step 5: Commit** — `feat: install --replace ownership matrix + marker recovery`

### Task 12: Parent-death + closed-stdio integration tests

Spec §7 "parent death & stdio". Process-lifetime-shaped guarantees need real processes. Model on the existing `ProcessRunnerTests` (app test project) which already drives real children.

**Files:**
- Test: `test/Capacitor.Cli.Tests.Integration/ServiceVerifyProcessTests.cs`

- [ ] **Step 1: Write the tests.** These do NOT run launchctl: they exercise (a) a child `kcap`-like process (use the test-host binary trick: spawn `dotnet` running a tiny inline C# script is NOT available — instead spawn the *built CLI* with a `--verify` invocation against a temp `DaemonLockPaths` dir where the transaction will fail fast on viability; assert the child completes and writes its coded stderr even when the parent kills its own handles first). Concretely:
  - Spawn built CLI (`dotnet run --project src/Capacitor.Cli … -- daemon service start --name ptest --verify` with `KCAP_CONFIG_DIR`+lock-dir env pointed at temp) with redirected stdio; close/dispose our ends of the pipes immediately; assert the process still exits (no hang, no crash from EPIPE) with a coded non-zero exit.
  - Same but kill the intermediate shell (`/bin/sh -c 'exec …'` wrapper as the "parent") after 200 ms; assert the CLI child completes and the service lock file is released (probe `IsHeld` false within 30 s).
- [ ] **Step 2: Run; expect failures if `Say()` isn't broken-pipe-safe — fix in `ServiceVerify` until green.**
- [ ] **Step 3: Commit** — `test: verify transaction survives parent death and closed stdio`

### Task 13: Daemon deliberate-refusal exit codes

Spec decision 6: supervised daemon (existing `SupervisionMode.Supervised`) exits **0** on (a) local name-lock refusal (today `return 2` in `DaemonRunner.RunAsync`) and (b) server `NameInUse` (today `return 3`). Manual daemons keep 2/3 for scripts.

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (the two return sites — find them at the lock-acquire failure (~line 240) and the NameInUse branch (~line 635); both must consult the already-computed supervision mode)
- Modify: `src/Capacitor.Cli.Core/ExitCodes.cs` (document the semantics beside `RestartRequested`)
- Test: `test/Capacitor.Cli.Tests.Unit/Daemon/DeliberateRefusalExitTests.cs`

- [ ] **Step 1: Read `DaemonRunner`** around both sites to find how `SupervisionMode` is resolved *at that point* (the lock site runs before the host builds — check whether the supervised determination (`KCAP_DAEMON_SUPERVISED` == sanitized name) is available pre-host; if it's currently computed later, extract a small pure helper `static bool IsSupervised(string resolvedName)` reading the env var and comparing sanitized values, and use it at both sites).
- [ ] **Step 2: Write failing tests** for the pure helper (env set matching name → true; mismatching → false; unset → false) and, if the exit paths are testable via existing DaemonRunner test seams (check `test/Capacitor.Cli.Tests.Unit/Daemon/` for prior exit-code tests), assert `refusalExit(supervised: true) == 0`, `(false) == 2` / `== 3` via a small extracted function `static int LockRefusalExit(bool supervised)` / `static int NameInUseExit(bool supervised)`.
- [ ] **Step 3: Implement** — extract the two exit decisions into those pure functions; call them at the sites.
- [ ] **Step 4: Run full CLI+daemon unit suite.**
- [ ] **Step 5: Commit** — `feat: supervised daemon exits 0 on deliberate refusal (lock, NameInUse)`

---

## Part C — Core client propagation (Task 14)

### Task 14: Hello `DaemonVersion` → `Unreachable` → `AttachStatus`

Spec decision 6 (client side) + §4.3 triggers: `CycleOutcome` carries the hello version; `Unreachable` gains it; dedupe keys on `(reason, daemonVersion)`; `DaemonClientService.Apply` projects it into `AttachStatus`.

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/LocalControlClient.cs`
- Modify: `src/Capacitor.App/Services/AttachStatus.cs` + `src/Capacitor.App/Services/DaemonClientService.cs`
- Test: extend `test/Capacitor.Cli.Tests.Unit/` LocalControlClient tests (find the existing class covering `daemon_incompatible` and add cases there) + `test/Capacitor.App.Tests.Unit/DaemonClientServiceTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record Unreachable(string Reason, string? DaemonVersion = null) : LocalControlEvent;
public sealed record AttachStatus(AttachState State, string? Reason,
    IReadOnlyList<string>? Capabilities, string? DaemonVersion = null);
```

- [ ] **Step 1: Write failing tests** in the existing LocalControlClient test harness (it hosts fake daemon sockets already):
  - Hello reply without `status/1` but with `DaemonVersion="1.0"` → `Unreachable("daemon_incompatible", "1.0")`.
  - Same reason, version changes `null→"1.0"` and `"1.0"→"2.0"` across cycles → a NEW `Unreachable` is yielded each time (dedupe key is the pair).
  - Transport failure → `Unreachable("daemon_unreachable", null)`.
  - App side: `Apply(Unreachable("daemon_incompatible","1.0"))` → `AttachStatus.DaemonVersion == "1.0"`.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement:** `CycleOutcome` gains `string? DaemonVersion`; the hello leg captures `dto?.DaemonVersion` before the caps gate; `lastReason` (string) becomes `(string Reason, string? Version)? last`; all existing `Unreachable(reason)` constructions pass null version. Update `TrayViewModel`/anything pattern-matching `Unreachable(var reason)` — positional deconstruction with a defaulted second parameter still compiles for `Unreachable(var reason, _)`; fix compile errors as they surface.
- [ ] **Step 4: Run BOTH unit suites (Core changes ripple into app tests).**
- [ ] **Step 5: Commit** — `feat: propagate hello DaemonVersion through Unreachable to AttachStatus`

---

## Part D — the app (Tasks 15–24)

### Task 15: `IProcessRunner` v2 (stdout, env overlay, cancel modes)

Spec §3.6. Existing interface returns `(int ExitCode, string Stderr)`; replace with a result record + options. Update the two existing consumers (`DaemonClientService.StartDaemonAsync`, `AgentActionService`) and their fakes.

**Files:**
- Modify: `src/Capacitor.App/Services/IProcessRunner.cs`
- Modify: `src/Capacitor.App/Services/DaemonClientService.cs` (`ProcessRunner` impl + `StartDaemonAsync` call)
- Modify: `src/Capacitor.App/Services/AgentActionService.cs`
- Test: `test/Capacitor.App.Tests.Unit/ProcessRunnerTests.cs` (extend existing)

**Interfaces (Produces):**
```csharp
public enum CancelMode { AbandonWait, KillTree }
public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr, bool TimedOut);
public sealed record RunOptions(
    IReadOnlyDictionary<string, string>? EnvOverlay = null, // adds/overrides; rest of env untouched
    TimeSpan? Timeout = null,                               // KillTree + await on expiry
    CancelMode CancelMode = CancelMode.AbandonWait);
public interface IProcessRunner {
    Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct);
}
```

- [ ] **Step 1: Write failing tests** (real child processes, existing pattern): stdout captured (`/bin/echo hi` → `Stdout == "hi\n"`); env overlay visible to child (`/usr/bin/env` output contains `KCAP_PROFILE=work`) without clobbering `PATH`; `Timeout` on `/bin/sleep 30` → `TimedOut=true`, returns promptly; `CancelMode.AbandonWait` + cancelled ct → `OperationCanceledException`, child keeps running (existing semantics test stays green).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** in `DaemonClientService.ProcessRunner` (env overlay via `psi.Environment[k] = v`; timeout via linked CTS whose expiry runs `Kill(entireProcessTree: true)` + await, distinct from external ct which abandons the wait). Update callers: `StartDaemonAsync` passes `new RunOptions()` (abandon-wait — unchanged semantics).
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: IProcessRunner v2 — stdout, env overlay, cancel modes`

### Task 16: `CliResolver` + typed CLI facade (`IKcapCli`)

Spec §3.1 + the app side of every CLI call. The facade is what the controller (Task 19+) consumes and what tests fake.

**Files:**
- Create: `src/Capacitor.App/Services/CliResolver.cs`
- Create: `src/Capacitor.App/Services/KcapCli.cs` (`IKcapCli` + impl + `ServiceSnapshot` DTO parse)
- Modify: `src/Capacitor.App/Services/DaemonClientService.cs` (`CreateDefaultAsync` uses `CliResolver`)
- Test: `test/Capacitor.App.Tests.Unit/CliResolverTests.cs`, `test/Capacitor.App.Tests.Unit/KcapCliTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record CliInfo(string? Path, string? Version); // Version null => skew detection off
public static class CliResolver {
    /// KCAP_APP_CLI_PATH → (future AI-1653 bundle arm) → "kcap" on PATH. Pure given env+exists-fn.
    public static string? ResolvePath(Func<string, string?> getEnv, Func<string, bool> fileExists);
    /// Strict: single line "kcap <v>" → "<v>"; anything else (multiline/garbage/"unknown") → null.
    public static string? ParseVersion(string stdout);
}
public sealed record ServiceSnapshot(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath, string? InstallBinaryPath,
    int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive);
public interface IKcapCli {
    string? CliPath { get; }
    Task<string?> VersionAsync(CancellationToken ct);                  // runs `--version --no-update-check`
    Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct);   // null = unknown (nonzero exit/parse fail)
    Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct);
    Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, string profileName, CancellationToken ct);
    Task<ProcessResult> DetachedStartAsync(CancellationToken ct);      // daemon start -d (AbandonWait)
}
```

`KcapCli` builds every call with `RunOptions(EnvOverlay: {KCAP_PROFILE, PATH?})` (PATH only when the probe knew it — injected as `string? terminalPath`), timeout 45 s on mutations (strictly above the CLI's 20 s forward + 10 s reserve), `--profile <p>` additionally on install (spec decision 7). `ServiceSnapshot` parsing uses a `JsonSerializerContext` in the same file (AOT rule).

- [ ] **Step 1: Write failing tests:** `ParseVersion("kcap 1.2.3\n") == "1.2.3"`; multiline/`"kcap unknown"`/garbage → null; `ResolvePath` prefers env override; `KcapCli.ServiceStatusAsync` parses a canned JSON via a fake `IProcessRunner` (assert argv `["daemon","service","status","--name",…,"--json"]` and snake_case fields land); nonzero exit → null; install argv contains `--replace` iff asked, `--profile work`, `--verify`, and the env overlay carries `KCAP_PROFILE=work`.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: CliResolver + typed IKcapCli facade`

### Task 17: Terminal-PATH probe (`LoginShellProbe`)

Spec §3.6. `-lic` with sentinels → `-lc` fallback → `/bin/zsh` fallback; both fail → unknown (null).

**Files:**
- Create: `src/Capacitor.App/Services/LoginShellProbe.cs`
- Test: `test/Capacitor.App.Tests.Unit/LoginShellProbeTests.cs`

**Interfaces (Produces):**
```csharp
public interface ILoginShellProbe {
    /// Terminal PATH or null=unknown. Cached after first call.
    Task<string?> TerminalPathAsync(CancellationToken ct);
    /// True/false when positively determined via `command -v kcap`; null=unknown.
    Task<bool?> KcapOnPathAsync(CancellationToken ct);
}
public sealed class LoginShellProbe(IProcessRunner runner, Func<string,string?> getEnv) : ILoginShellProbe {
    internal const string Sentinel = "<<KCAP-PATH>>";
    internal static string? Parse(string stdout); // between sentinel pair; null if absent/torn
}
```

- [ ] **Step 1: Write failing tests:** `Parse` extracts between sentinels amid chatter (`"motd\n<<KCAP-PATH>>/a:/b<<KCAP-PATH>>\n"` → `"/a:/b"`); missing/single sentinel → null; probe uses `$SHELL` when set, `-lic` first (assert argv via fake runner), falls back to `-lc` when `-lic` exits nonzero/times out, `/bin/zsh` when `SHELL` unset; both fail → `TerminalPathAsync` null; result cached (second call, zero new runner invocations).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** (probe timeout 5 s via `RunOptions.Timeout`; stdin is not connected by our runner — children read `/dev/null` by default with `RedirectStandardInput=false`).
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: interactive-login-shell PATH probe`

### Task 18: `AppStateStore`

Spec §3.5: serialized store, atomic writes, claims persisted before dialogs, corrupt → defaults.

**Files:**
- Create: `src/Capacitor.App/Services/AppStateStore.cs`
- Test: `test/Capacitor.App.Tests.Unit/AppStateStoreTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record AppState(
    bool ShimOffered = false, bool ShimDenied = false,
    IReadOnlyList<string>? DeclinedTakeoverPairs = null); // "daemonV|cliV"
public interface IAppStateStore {
    Task<AppState> LoadAsync();
    /// Serialized read-modify-write; atomic temp+rename; false (logged) on write failure —
    /// caller keeps the claim in memory for the run.
    Task<bool> UpdateAsync(Func<AppState, AppState> mutate);
}
public sealed class AppStateStore(string path) : IAppStateStore; // path: PathHelpers.ConfigPath("app-state.json")
```

- [ ] **Step 1: Write failing tests:** round-trip; corrupt file → defaults (no throw); two concurrent `UpdateAsync` calls both land (run 50 parallel increments into `DeclinedTakeoverPairs`, assert 50 entries — serialization via an internal `SemaphoreSlim(1,1)`); temp file never left behind after success.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** (source-gen JSON context in-file).
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: serialized atomic app state store`

### Task 19: `DaemonLifecycleController` — startup phase, reconciliation, startup matrix

Spec §3.2 + §4.2. The heart of the app side. Consumes `IDaemonClientService` streams, `IKcapCli`, `ILoginShellProbe`, `IAppStateStore`, an `ILifecycleSurface` for all UX outputs.

**Files:**
- Create: `src/Capacitor.App/Services/DaemonLifecycleController.cs`
- Create: `src/Capacitor.App/Services/ILifecycleSurface.cs`
- Test: `test/Capacitor.App.Tests.Unit/DaemonLifecycleControllerTests.cs` (+ `FakeLifecycleSurface.cs`, extend `FakeDaemonClientService`)

**Interfaces (Produces):**
```csharp
/// Everything the controller shows a human. The Avalonia implementation (Task 22/23)
/// renders dialogs/status lines; tests fake it.
public interface ILifecycleSurface {
    void Status(string message);                                   // honest one-liners, message lane
    Task<bool> ConfirmAsync(LifecyclePrompt prompt, CancellationToken ct);
    void Attention(string message);                                // repair-affordance surfaces
}
public sealed record LifecyclePrompt(
    string Kind,           // "restart-update" | "takeover" | "repair"
    string? DaemonVersion, string? CliVersion,
    bool PathDegraded,     // decision-7 disclosure when terminal PATH unknown
    string Disclosure);    // replacement/recapture text
public sealed class DaemonLifecycleController : IAsyncDisposable {
    public DaemonLifecycleController(IDaemonClientService client, IKcapCli cli,
        ILoginShellProbe probe, IAppStateStore store, ILifecycleSurface surface,
        Func<Task<string?>> resolveProfileName,   // null => no valid profile (checks URL validity)
        TimeProvider time);
    public void Start();                          // subscribe BEFORE client.Start() is called by the host
    public Task StartActionAsync(CancellationToken ct); // Task 21 wires the tray to this
    /// App shutdown awaits this: completes when no mutation child is in flight.
    public Task QuiescedAsync();
}
```

- [ ] **Step 1: Write failing tests** (fake client exposes subjects to push `AttachStatus`; fake `IKcapCli` scripts snapshots + records calls; manual `TimeProvider`):
  - **Startup matrix rows** (§4.2 table verbatim): job running → no CLI mutation; loaded-inactive+plist+daemonPid null → exactly one `ServiceStartVerifiedAsync`; same but daemonPid non-null → zero mutations + one `Attention`; orphan label (unitPresent false, state installed) → `Attention` only; no label + plist + pid null → `ServiceStartVerifiedAsync`; nothing + profile ok + PATH known → `ServiceInstallVerifiedAsync(replace:false)`; nothing + no profile → `Status(...)` only; nothing + PATH unknown → `Status(...)` only (silent install suppressed).
  - **Phase closes on any first terminal outcome:** push `Connected` first, then `Unreachable(daemon_unreachable)` → zero mutations. Push `Unreachable(daemon_incompatible)` first, then `daemon_unreachable` → zero mutations. Unknown PATH probe does NOT close the phase (probe unknown, then first unreachable → matrix still runs, minus unit-writing rows).
  - **Once:** two `daemon_unreachable` in a row → matrix runs once (arm claimed before first await: script the status call to hang on a `TaskCompletionSource`, push the second unreachable while pending, then release — still exactly one status query consumed for the matrix).
  - **Reconciliation on immediate Connected:** snapshot shows loaded label + JobPid≠DaemonPid → one `Attention`; `TxnMarker=true,TxnActive=false` → `Attention`; `TxnActive=true` → no attention yet, a re-query is scheduled (advance clock → second `ServiceStatusAsync`).
  - **UX confirmation:** after a successful `ServiceStartVerifiedAsync` (exit 0), a fresh `Connected` pushed after the call → no status message; no fresh Connected within the window (advance clock) → `Status("daemon started, app not yet attached — retrying")` and NO rollback calls exist on `IKcapCli` at all (structurally impossible — assert no extra calls).
  - **Coded failure:** `ServiceStartVerifiedAsync` exits 24 with stderr token → exactly one `Status` containing the token; second unreachable → no retry (once-per-run).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.** One `SemaphoreSlim(1,1)` gate; `_startupPhaseOpen` bool + `_armClaimed` bool both flipped synchronously before awaits; generation int incremented on every `AttachStatus` transition, captured before each status query and compared after. Reconciliation runs once per app run on the first terminal outcome, whatever it is.
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: lifecycle controller — startup phase, matrix, reconciliation`

### Task 20: Skew triggers, classification, decline memory, accept path

Spec §4.3. Extends the controller.

**Files:**
- Modify: `src/Capacitor.App/Services/DaemonLifecycleController.cs`
- Test: extend `test/Capacitor.App.Tests.Unit/DaemonLifecycleControllerTests.cs`

- [ ] **Step 1: Write failing tests:**
  - `Connected` with snapshot version == cached CLI version → nothing. Mismatch → one `ConfirmAsync` with `Kind` = "restart-update" when snapshot `BinaryPath == InstallBinaryPath` (canonical compare) else "takeover"; both prompts carry the disclosure text.
  - `Unreachable("daemon_incompatible","0.9")` with CLI "1.0" → prompt. Version flip "0.9"→"0.95" while incompatible → second prompt allowed only across app runs? No — at most one skew dialog per app run: assert the second trigger in the same run does NOT prompt.
  - Accept → exactly one `ServiceInstallVerifiedAsync(replace: true, …)`; NO other `IKcapCli` mutation calls.
  - Decline → `store.UpdateAsync` persisted the pair BEFORE `ConfirmAsync` resolves false is honored (claim-before-show: assert store contains the pair before the fake surface returns); same pair next trigger (fresh controller with same store) → no prompt; new pair → prompt.
  - Stale consent: between prompt shown and accept returning true, push a new `Connected` with a DIFFERENT version → no mutation, one `Status` explaining the abort.
  - `daemon_unreachable` never triggers prompts.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.** Canonical compare = `Path.GetFullPath` + resolve symlinks via `FileInfo.ResolveLinkTarget(returnFinalTarget: true)` when the file exists, else string compare.
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: skew triggers, classification, decline memory, one-call accept`

### Task 21: Service-aware Start action + repair affordance + shutdown deferral

Spec §4.4 + §3.6 (mutations never abandoned). Wire `StartActionAsync` into the tray (replacing the current direct `StartDaemonAsync` binding) and `QuiescedAsync` into app shutdown.

**Files:**
- Modify: `src/Capacitor.App/Services/DaemonLifecycleController.cs`
- Modify: `src/Capacitor.App/ViewModels/TrayViewModel.cs` (StartDaemonCommand → `StartActionAsync`)
- Modify: `src/Capacitor.App/App.axaml.cs` (compose controller; shutdown awaits `QuiescedAsync` with a 60 s cap)
- Test: extend controller tests + `test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs` (existing file — find the StartDaemonCommand tests and repoint)

- [ ] **Step 1: Write failing tests:**
  - Start branches (§4.4 verbatim): running → no mutation (reattach kick only — assert `RestartLoopAsync` equivalent via the fake client's counter); loaded label+plist+pid null → `ServiceStartVerifiedAsync`; loaded+pid non-null → `ConfirmAsync(Kind:"repair")`; orphan → repair prompt; no label+plist+pid null → `ServiceStartVerifiedAsync`; nothing → `DetachedStartAsync`.
  - Repair accept → `ServiceInstallVerifiedAsync(replace:true)`.
  - Start racing auto-install: hold the gate open with a pending install (TCS-scripted), invoke `StartActionAsync` → it awaits; after release it re-queries (fresh `ServiceStatusAsync` count) before acting.
  - `QuiescedAsync` completes only after a pending mutation TCS resolves.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement + wire.** In `App.axaml.cs`, construct controller before `DaemonClientService.Start()` (subscribe-before-pump); on `ShutdownRequested`, `e.Cancel` once, await `QuiescedAsync` (cap 60 s), then shut down — follow the existing shutdown pattern in `App.axaml.cs` (read it first; integrate, don't restructure).
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: service-aware Start, repair affordance, shutdown deferral`

### Task 22: Lifecycle surface (dialogs + status lane) — Avalonia implementation

`ILifecycleSurface` production implementation: prompts as a modal window styled after `ConsentPromptWindow` (copy its axaml structure/patterns), `Status` → the existing tray/main-window message lane used by `StartDaemonAsync` failures (find where `StartMessage` renders in `MainWindowViewModel`/`TrayViewModel` and reuse), `Attention` → tray attention state (slice-2 machinery in `TrayViewModel`).

**Files:**
- Create: `src/Capacitor.App/Views/LifecyclePromptWindow.axaml` + `.axaml.cs`
- Create: `src/Capacitor.App/ViewModels/LifecyclePromptViewModel.cs`
- Create: `src/Capacitor.App/Services/LifecycleSurface.cs`
- Test: `test/Capacitor.App.Tests.Unit/LifecyclePromptViewModelTests.cs` (Avalonia.Headless, model on `ConsentPromptViewModelTests`)

- [ ] **Step 1: Write failing VM tests:** prompt text renders Kind-specific title ("Restart daemon to update" / "Take over daemon management" / "Repair daemon service"); disclosure always present; PathDegraded adds the degraded-PATH sentence; Accept/Decline complete the `Task<bool>`; dialogs serialized — a second `ConfirmAsync` while one is open waits (assert via two TCS).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** (`SemaphoreSlim(1,1)` for dialog serialization — also satisfies "shim offer never during skew dialog" in Task 24 since the shim uses the same surface).
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: lifecycle prompts + status surface`

### Task 23: `PathShimInstaller`

Spec §5 minus the offer flow (Task 24): pre-flight lstat, osascript argv install, post-install probe, cancel/failure classification, sudo fallback rendering.

**Files:**
- Create: `src/Capacitor.App/Services/PathShimInstaller.cs`
- Test: `test/Capacitor.App.Tests.Unit/PathShimInstallerTests.cs`

**Interfaces (Produces):**
```csharp
public enum ShimPreflight { Installable, AlreadyInstalled, Conflict }
public enum ShimOutcome { Installed, InstalledButNotOnPath, Cancelled, Failed }
public sealed record ShimResult(ShimOutcome Outcome, string? Detail, string? SudoFallback);
public sealed class PathShimInstaller(IProcessRunner runner, ILoginShellProbe probe) {
    public const string Destination = "/usr/local/bin/kcap";
    internal static ShimPreflight Preflight(string destination, string target); // lstat taxonomy, pure-ish (FileInfo)
    internal static string[] OsascriptArgs(string target);                      // target as argv, never interpolated
    internal static string PosixQuote(string s);                                // ' → '"'"'
    internal static bool LooksLikeTarget(string s);                             // rejects \r and \n
    public Task<ShimResult> InstallAsync(string target, CancellationToken ct);
}
```

- [ ] **Step 1: Write failing tests:**
  - `Preflight`: temp dir scenarios — absent → Installable; symlink→target → AlreadyInstalled; symlink→elsewhere / regular file / directory / broken link → Conflict.
  - `OsascriptArgs("/App Space/kcap")` → last element is the raw path; script text contains `quoted form of item 1 of argv` and `ln -s ` without `-f`; `mkdir -p /usr/local/bin` present.
  - `PosixQuote("a'b")` → `'a'"'"'b'`; round-trip through `/bin/sh -c "printf %s <quoted>"` equals input (real process).
  - `LooksLikeTarget` rejects `\n`/`\r`.
  - `InstallAsync`: fake runner exit 1 + stderr containing `(-128)` → `Cancelled`; exit 1 other → `Failed` with `SudoFallback == "sudo mkdir -p /usr/local/bin && sudo ln -s " + PosixQuote(target) + " /usr/local/bin/kcap"`; exit 0 + probe says kcap now resolves → `Installed`; exit 0 + probe says still absent → `InstalledButNotOnPath`.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: PATH shim installer`

### Task 24: Shim offer flow + tray menu item

Spec §5 offer surface: once-ever (claim persisted pre-dialog), after the startup phase closes, serialized with other dialogs; "Install command-line tool…" tray item visible while applicable-but-absent.

**Files:**
- Modify: `src/Capacitor.App/Services/DaemonLifecycleController.cs` (or a small `ShimOfferCoordinator` if the controller file is getting long — prefer the separate file: `src/Capacitor.App/Services/ShimOfferCoordinator.cs` consuming the same startup-phase signal via a `Task PhaseClosed` the controller exposes)
- Modify: `src/Capacitor.App/Views/TrayMenuBuilder.cs` + `src/Capacitor.App/ViewModels/TrayViewModel.cs` (menu item)
- Test: `test/Capacitor.App.Tests.Unit/ShimOfferCoordinatorTests.cs`

**Interfaces:** controller Produces `Task PhaseClosed { get; }` (completes when the startup phase closes, any path).

- [ ] **Step 1: Write failing tests:** offer waits for `PhaseClosed` (immediate-Connected path included); considered only when probe positively says kcap absent AND resolver has an absolute path; `ShimOffered` persisted BEFORE the confirm dialog resolves; cancel → `ShimDenied` persisted, never auto-offered on a fresh coordinator with the same store; store write failure → still not re-offered this run; menu-item visibility observable (`IObservable<bool>` or bool property recomputed on demand): true iff applicable-but-absent and not conflict.
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement + wire the tray item** (follow `TrayMenuBuilder`'s existing item pattern).
- [ ] **Step 4: Run app suite.**
- [ ] **Step 5: Commit** — `feat: shim offer flow + tray menu item`

### Task 25: Docs, help text, AOT gate, E2E checklist

**Files:**
- Modify: `src/Capacitor.Cli.Core/Resources/help-usage.txt` (service usage block: `--json`, `--verify`, `--replace`; the ServiceUsage() string in `DaemonCommands.cs` too)
- Modify: `README.md` (`## CLI commands` daemon-service section: new flags, one paragraph on the app-managed daemon + shim; check `## Getting started` for impact)
- Test: none (docs) — but run everything.

- [ ] **Step 1: Update `ServiceUsage()` in `DaemonCommands.cs`** to document `status [--json]`, `install [--replace] [--verify]`, `start [--verify]`.
- [ ] **Step 2: Update `help-usage.txt` + README** (same content, user-facing wording).
- [ ] **Step 3: Full gates:** both unit suites; integration suite; `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` (no output).
- [ ] **Step 4: Copy the spec §7 manual E2E checklist into the PR description draft** (8 items — they run on the implementer's Mac before the PR is marked ready).
- [ ] **Step 5: Commit** — `docs: service verbs help + README for app-managed daemon`

---

## Self-review notes (already applied)

- Spec coverage: §3.1→T16, §3.2→T19, §3.3/§5→T23–24, §3.4→T1–T12, §3.5→T18, §3.6→T15/T17/T21, §4.1→T19/T20, §4.2→T19, §4.3→T20, §4.4→T21, daemon exits→T13, Core client→T14, docs→T25. The §3.4 marker-durability directory-fsync is implemented best-effort with the BCL limitation documented in code (T5 Step 3) — a deliberate, visible deviation, not an omission.
- Type consistency: `ServiceQuery`/`LabelProbe` (T1) consumed by T4/T9–11; `VerifyExit` codes (T9) consumed by T19 tests; `ServiceSnapshot` (T16) consumed by T19–21; `ILifecycleSurface`/`LifecyclePrompt` (T19) implemented in T22; `RunOptions`/`ProcessResult` (T15) consumed by T16/T17/T23.
- Windows CI: new CLI tests that touch paths use `OverrideDirectoryForTesting` + `Path.Combine`; launchd-specific classes are still *compiled* on Windows — keep tests platform-neutral (classification/marker/lock tests run everywhere; only Task 12's integration tests are macOS-only — gate them with the repo's existing platform-skip attribute, check how existing mac-only tests are skipped and copy it).
