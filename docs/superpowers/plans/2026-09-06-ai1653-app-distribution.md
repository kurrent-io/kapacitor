# App Distribution (bundling, signing, DMG, auto-update) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the desktop app as a signed, notarized macOS DMG that bundles the `kcap` CLI and daemon, publishes a self-hosted Velopack feed on R2, and updates itself atomically.

**Architecture:** The release matrix's `osx-arm64` leg signs the daemon before its digest is computed and the CLI after its publish, so npm and the app share one signed trio. A new `app-macos` job publishes the Avalonia app self-contained, pre-signs its binaries, runs `vpk pack --signDisableDeep`, builds and notarizes a DMG, and hands everything to an `app-publish` job that uploads to R2 from the exact artifact bytes. App-side, a Velopack-backed `IAppUpdater` behind an `UpdateCoordinator` checks a prerelease-filtered feed, downloads in the background, prompts once, and applies on exit; the daemon restarts itself after the bundle swap, with the lifecycle controller holding its skew dialog for a grace window.

**Tech Stack:** .NET 10, Avalonia 12 + ReactiveUI, Velopack 1.2.0 (library + `vpk` tool), TUnit, bash scripts with sibling `.test.sh`, GitHub Actions (macos-latest), Cloudflare R2 via `vpk upload s3` and the AWS CLI.

**Spec:** `docs/superpowers/specs/2026-09-06-ai1653-app-distribution-design.md` — the plan argues from it; read both.

## Global Constraints

- Velopack library and `vpk` tool are both pinned to **1.2.0**; `minver-cli` is pinned to **7.0.0** (the repo's MinVer version) and always run as `minver --tag-prefix v`.
- Identity: bundle `Kurrent Capacitor.app`, `CFBundleIdentifier` `io.kurrent.capacitor`, Velopack pack id `KurrentCapacitor`, authors `Kurrent`, title `Kurrent Capacitor`, channel `osx-arm64`, `LSMinimumSystemVersion` `15.0`.
- Feed: `UpdateFeed.BaseUrl = "https://www.kurrent.io/download/desktop/osx-arm64/"`, override env var `KCAP_APP_UPDATE_URL`; R2 prefix `desktop/osx-arm64/`.
- Timings: first update check 30 s after the daemon graph is built, then every 4 h; post-update skew hold 45 s.
- CLI floor `KcapCliCompatibility.Floor = "0.12.0-beta.1"`; the tag `v0.12.0-beta.1` must exist on main before this PR merges (owner's task).
- The daemon `kcap-daemon` is signed **before** its SHA-256 is computed and **never re-signed**; every nested binary is pre-signed by us and `vpk pack` runs with `--signDisableDeep`.
- Entitlements: app executable and runtime dylibs get JIT, unsigned-executable-memory, dyld-environment-variables, disable-library-validation; `kcap` gets disable-library-validation only; `kcap-daemon` and `libpty_shim.dylib` get an empty set.
- Comments: scarce, never historical, no Linear ids (`AI-xxxx`) in C# — `bash scripts/check-linear-ids.sh` must pass. Commit subjects: one imperative clause, ≤ 80 chars, no issue reference unless the user supplies a GitHub issue number.
- Tests: TUnit; app tests live in `test/Capacitor.App.Tests.Unit/`, CLI tests in `test/Capacitor.Cli.Tests.Unit/`, daemon tests in `test/Capacitor.Cli.Daemon.Tests.Unit/`; use `[TempDir]`/`TempDir` from Helpers; console capture via `ConsoleOutput.StartCapture()` with bare `[NotInParallel]`. Run one class with `--treenode-filter "/*/*/ClassName/*"`. Locally export `TMPDIR=/private/tmp` before running suites.
- Build every project a task touches and clear every warning (`TreatWarningsAsErrors` is on; unused usings are errors).
- Shell scripts: `#!/usr/bin/env bash`, `set -euo pipefail`, a sibling `<name>.test.sh` where the spec names one, all runnable on macOS and Ubuntu (`shasum`/`sha256sum` both handled).

## File structure

**CLI (`src/Capacitor.Cli/`)**
- `InstallProvenance.cs` (new): app-bundled detection from the process path.
- `UpdateNotice.cs`: bundled → not human-facing.
- `Commands/UpdateCommand.cs`: bundled branch (message, `--check` JSON).
- `Commands/StatusCommand.cs`: bundled version line.

**Daemon (`src/Capacitor.Cli.Daemon/`)**
- `DaemonRunner.cs`: `--version` handled first.

**App (`src/Capacitor.App/`)**
- `App.axaml`: `Name`.
- `Assets/kcap-icon.svg` (new source), `Assets/kcap-icon.png` (re-rendered 512px), `Assets/kcap-icon.icns` (new).
- `Packaging/Info.plist`, `Packaging/app.entitlements.plist`, `Packaging/cli.entitlements.plist`, `Packaging/daemon.entitlements.plist` (new).
- `Capacitor.App.csproj`: Velopack reference.
- `Program.cs`: Velopack bootstrap, `UpdateRelaunch`.
- `Services/CliResolver.cs`: bundle-sibling arm.
- `Services/Update/UpdateCandidate.cs`, `IAppUpdater.cs`, `InertAppUpdater.cs`, `UpdateFeed.cs`, `PrereleaseFilteringSource.cs`, `VelopackAppUpdater.cs`, `UpdateMenuItem.cs`, `UpdateCoordinator.cs` (new).
- `Services/InstallLocation.cs`, `Services/ApplicationsMover.cs` (new).
- `Services/ILifecycleSurface.cs`, `ViewModels/LifecyclePromptViewModel.cs`: two new prompt kinds.
- `Services/DaemonLifecycleController.cs`: skew hold + version revalidation.
- `ViewModels/TrayModels.cs`, `ViewModels/TrayViewModel.cs`, `Views/TrayMenuBuilder.cs`: update item.
- `App.axaml.cs`: guard, pending-apply, coordinator wiring, apply-on-exit.

**Scripts (`scripts/`)**
- `lib/semver.sh` + `lib/semver.test.sh`, `lib/hash.sh`, `run-shell-tests.sh`.
- `render-app-icons.sh`, `render-info-plist.sh`, `import-signing-keychain.sh`, `sign-macos.sh`, `build-dmg.sh`.
- `assert-app-cli-version.sh`, `assert-bundle-digest.sh`, `desktop-baseline.sh`, `promote-desktop-aliases.sh`, `verify-desktop-immutables.sh`, `verify-npm-trio.sh`, each with a `.test.sh`.

**Workflows:** `.github/workflows/ci.yml` (shell tests step, `app-bundle` job), `.github/workflows/release.yml` (regexp fix, matrix-leg signing, `app-macos`, `app-publish`).

**Docs:** `README.md` (Desktop app section), `docs/CHANGES.md` (entry). **Packages:** `Directory.Packages.props` (Velopack).

---

### Task 1: Daemon `--version` flag

**Files:**
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (top of `RunAsync`, ~line 41)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/DaemonVersionFlagTests.cs`

**Interfaces:**
- Produces: `internal static bool DaemonRunner.TryHandleVersionFlag(string[] args, TextWriter stdout)` — prints `kcap-daemon <AssemblyInformationalVersion>` and returns true only for the exact argv `["--version"]`.

- [ ] **Step 1: Write the failing test**

```csharp
namespace Capacitor.Cli.Daemon.Tests.Unit;

/// The version flag must answer before any config, environment or profile work: the release
/// pipeline runs it as the post-signing smoke on a runner with no daemon setup at all.
public class DaemonVersionFlagTests {
    [Test]
    public async Task Exactly_version_prints_and_is_handled() {
        var output = new StringWriter();

        var handled = DaemonRunner.TryHandleVersionFlag(["--version"], output);

        await Assert.That(handled).IsTrue();
        await Assert.That(output.ToString().TrimEnd()).IsEqualTo($"kcap-daemon {DaemonRunner.ResolveDaemonVersion()}");
    }

    [Test]
    [Arguments(new string[0])]
    [Arguments(new[] { "--name", "x" })]
    [Arguments(new[] { "--version", "--name", "x" })]
    public async Task Anything_else_is_not_handled(string[] args) {
        var output = new StringWriter();

        var handled = DaemonRunner.TryHandleVersionFlag(args, output);

        await Assert.That(handled).IsFalse();
        await Assert.That(output.ToString()).IsEmpty();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Daemon.Tests.Unit/Capacitor.Cli.Daemon.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonVersionFlagTests/*"`
Expected: build error, `TryHandleVersionFlag` does not exist.

- [ ] **Step 3: Implement**

In `DaemonRunner.cs`, add above `RunAsync`:

```csharp
    /// Answers `--version` before any config, environment or profile work, so a signed binary can
    /// be smoke-run on a machine with no daemon setup.
    internal static bool TryHandleVersionFlag(string[] args, TextWriter stdout) {
        if (args.Length != 1 || args[0] != "--version") return false;
        stdout.WriteLine($"kcap-daemon {ResolveDaemonVersion()}");
        return true;
    }
```

And make the first statement of `RunAsync` (before `string? logFile = null;`):

```csharp
        if (TryHandleVersionFlag(args, Console.Out)) return 0;
```

- [ ] **Step 4: Run the test to verify it passes**

Same command as Step 2. Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Daemon/DaemonRunner.cs test/Capacitor.Cli.Daemon.Tests.Unit/DaemonVersionFlagTests.cs
git commit -m "Answer kcap-daemon --version before any daemon setup"
```

---

### Task 2: CLI provenance and the suppressed update notice

**Files:**
- Create: `src/Capacitor.Cli/InstallProvenance.cs`
- Modify: `src/Capacitor.Cli/UpdateNotice.cs` (`IsHumanFacing`, ~line 33)
- Test: `test/Capacitor.Cli.Tests.Unit/InstallProvenanceTests.cs`, `test/Capacitor.Cli.Tests.Unit/UpdateNoticeIsHumanFacingTests.cs`

**Interfaces:**
- Produces: `public static class InstallProvenance { public static bool IsAppBundled(); internal static bool IsAppBundled(string? processPath, Func<string, bool> fileExists); }`
- Produces: `internal static bool UpdateNotice.IsHumanFacing(string command, string[] args, bool appBundled)`; the existing two-argument overload delegates with `InstallProvenance.IsAppBundled()`.

- [ ] **Step 1: Write the failing tests**

`test/Capacitor.Cli.Tests.Unit/InstallProvenanceTests.cs`:

```csharp
namespace Capacitor.Cli.Tests.Unit;

/// The bundle test is a pure path shape plus one file probe; separators are compared with
/// EndsWith so the same cases hold on the Windows CI leg.
public class InstallProvenanceTests {
    static bool PlistExists(string p) => p.Replace('\\', '/').EndsWith("/Contents/Info.plist", StringComparison.Ordinal);

    [Test]
    public async Task Inside_a_bundle_with_a_plist_is_bundled() {
        var bundled = InstallProvenance.IsAppBundled("/Applications/Kurrent Capacitor.app/Contents/MacOS/kcap", PlistExists);

        await Assert.That(bundled).IsTrue();
    }

    [Test]
    public async Task Bundle_shape_without_a_plist_is_not_bundled() {
        var bundled = InstallProvenance.IsAppBundled("/Applications/Kurrent Capacitor.app/Contents/MacOS/kcap", _ => false);

        await Assert.That(bundled).IsFalse();
    }

    [Test]
    [Arguments("/usr/local/lib/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap")]
    [Arguments("/Applications/Kurrent Capacitor.app/Contents/Resources/kcap")]
    [Arguments("/Applications/Kurrent Capacitor/Contents/MacOS/kcap")]
    [Arguments("")]
    public async Task Other_shapes_are_not_bundled(string path) {
        await Assert.That(InstallProvenance.IsAppBundled(path, PlistExists)).IsFalse();
    }

    [Test]
    public async Task Null_process_path_is_not_bundled() {
        await Assert.That(InstallProvenance.IsAppBundled(null, PlistExists)).IsFalse();
    }
}
```

Append to `UpdateNoticeIsHumanFacingTests.cs`:

```csharp
    // --- Suppressed: a CLI bundled inside the desktop app (updates arrive through the app) ---

    [Test]
    public async Task AppBundled_IsSuppressed_ForOtherwiseHumanFacingCommands() {
        await Assert.That(UpdateNotice.IsHumanFacing("status", ["status"], appBundled: true)).IsFalse();
        await Assert.That(UpdateNotice.IsHumanFacing("setup", ["setup"], appBundled: true)).IsFalse();
    }

    [Test]
    public async Task NotBundled_KeepsTheOrdinaryVerdict() {
        await Assert.That(UpdateNotice.IsHumanFacing("status", ["status"], appBundled: false)).IsTrue();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/InstallProvenanceTests/*"`
Expected: build error (type and overload missing).

- [ ] **Step 3: Implement**

`src/Capacitor.Cli/InstallProvenance.cs`:

```csharp
namespace Capacitor.Cli;

/// Whether this CLI is the copy inside the Kurrent Capacitor app bundle. A bundled CLI is updated
/// by the app, so npm-oriented update surfaces switch themselves off on this answer.
public static class InstallProvenance {
    static readonly Lazy<bool> Cached = new(() => IsAppBundled(Environment.ProcessPath, File.Exists));

    public static bool IsAppBundled() => Cached.Value;

    internal static bool IsAppBundled(string? processPath, Func<string, bool> fileExists) {
        if (string.IsNullOrEmpty(processPath)) return false;

        var macos = Path.GetDirectoryName(processPath);
        if (macos is null || Path.GetFileName(macos) != "MacOS") return false;

        var contents = Path.GetDirectoryName(macos);
        if (contents is null || Path.GetFileName(contents) != "Contents") return false;

        var bundle = Path.GetDirectoryName(contents);
        if (bundle is null || !bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return false;

        return fileExists(Path.Combine(contents, "Info.plist"));
    }
}
```

In `UpdateNotice.cs`, replace the `IsHumanFacing` signature and add the guard:

```csharp
    public static bool IsHumanFacing(string command, string[] args) =>
        IsHumanFacing(command, args, InstallProvenance.IsAppBundled());

    /// <paramref name="appBundled"/> short-circuits everything: the app owns updates for a bundled
    /// CLI and its channel may lag npm, so an npm nudge would be wrong on both counts.
    internal static bool IsHumanFacing(string command, string[] args, bool appBundled) {
        if (appBundled) return false;
        if (CrashReporter.FailOpenCommands.Contains(command)) return false;
        if (command is "mcp" or "watch" or "daemon") return false;
        if (command is "update" or "uninstall") return false;
        if (args.Contains("--no-update-check")) return false;

        return true;
    }
```

Keep the existing doc comment on the method; do not restate the list in the new sentence.

- [ ] **Step 4: Run both classes to verify they pass**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/InstallProvenanceTests/*"` and the same with `UpdateNoticeIsHumanFacingTests`. Expected: all passed.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/InstallProvenance.cs src/Capacitor.Cli/UpdateNotice.cs test/Capacitor.Cli.Tests.Unit/InstallProvenanceTests.cs test/Capacitor.Cli.Tests.Unit/UpdateNoticeIsHumanFacingTests.cs
git commit -m "Detect an app-bundled kcap and silence its npm update notice"
```

---

### Task 3: `kcap update` and `kcap status` when bundled

**Files:**
- Modify: `src/Capacitor.Cli/Commands/UpdateCommand.cs` (constructor, top of `HandleAsync`)
- Modify: `src/Capacitor.Cli/Commands/StatusCommand.cs` (constructor, `WriteVersionLineAsync`, ~line 101)
- Test: `test/Capacitor.Cli.Tests.Unit/UpdateCommandBundledTests.cs`, `test/Capacitor.Cli.Tests.Unit/StatusCommandVersionLineTests.cs`

**Interfaces:**
- Produces: `UpdateCommand(ConfigRoot root, ProfileContext profiles, bool? appBundled = null)`, `internal const string UpdateCommand.BundledMessage`, `internal static string UpdateCommand.BundledCheckJson(string? current, string channel)`.
- Produces: `StatusCommand` gains a trailing `bool? appBundled = null` constructor parameter; `internal static string StatusCommand.FormatBundledVersionLine(string current)`.

- [ ] **Step 1: Write the failing tests**

`UpdateCommandBundledTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class UpdateCommandBundledTests {
    [Test]
    public async Task Check_json_reports_not_newer_and_the_app_install_tag() {
        var json = JsonNode.Parse(UpdateCommand.BundledCheckJson("0.12.0-beta.2", "beta"))!.AsObject();

        await Assert.That(json["current"]!.GetValue<string>()).IsEqualTo("0.12.0-beta.2");
        await Assert.That(json["latest"]!.GetValue<string>()).IsEqualTo("0.12.0-beta.2");
        await Assert.That(json["newer"]!.GetValue<bool>()).IsFalse();
        await Assert.That(json["channel"]!.GetValue<string>()).IsEqualTo("beta");
        await Assert.That(json["install_tag"]!.GetValue<string>()).IsEqualTo("app");
    }

    [Test]
    public async Task Bundled_message_names_the_app_and_its_menu_item() {
        await Assert.That(UpdateCommand.BundledMessage).Contains("Kurrent Capacitor");
        await Assert.That(UpdateCommand.BundledMessage).Contains("Check for Updates…");
    }
}
```

`StatusCommandVersionLineTests.cs`:

```csharp
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class StatusCommandVersionLineTests {
    [Test]
    public async Task Bundled_line_carries_the_app_marker_and_no_advisory() {
        await Assert.That(StatusCommand.FormatBundledVersionLine("0.12.0-beta.2"))
            .IsEqualTo("kcap 0.12.0-beta.2 (bundled with Kurrent Capacitor)");
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj -- --treenode-filter "/*/*/UpdateCommandBundledTests/*"`
Expected: build error.

- [ ] **Step 3: Implement the update branch**

In `UpdateCommand.cs`, change the class header and add the members:

```csharp
public sealed class UpdateCommand(ConfigRoot root, ProfileContext profiles, bool? appBundled = null) {
    /// Printed by every `kcap update` invocation of a CLI that lives inside the desktop app.
    internal const string BundledMessage =
        "This kcap is bundled with the Kurrent Capacitor desktop app; updates arrive through the app (\"Check for Updates…\" in the menu bar).";

    readonly bool _appBundled = appBundled ?? InstallProvenance.IsAppBundled();
```

At the top of `HandleAsync`, right after `var checkOnly = args.Contains("--check");`:

```csharp
        if (_appBundled) {
            await Console.Out.WriteLineAsync(checkOnly ? BundledCheckJson(GetCurrentVersion(), channel) : BundledMessage);

            return 0;
        }
```

Add next to `IsNewer`:

```csharp
    /// The `--check` contract for a bundled CLI: the launcher must see "confidently up to date",
    /// and `install_tag` names the channel that owns the install.
    internal static string BundledCheckJson(string? current, string channel) =>
        new JsonObject {
            ["current"]     = current,
            ["latest"]      = current,
            ["newer"]       = false,
            ["channel"]     = channel,
            ["install_tag"] = "app",
        }.ToJsonString();
```

- [ ] **Step 4: Implement the status line**

In `StatusCommand.cs`, add a trailing `bool? appBundled = null` parameter to the constructor (primary or explicit — match the file's existing shape), store `readonly bool _appBundled = appBundled ?? InstallProvenance.IsAppBundled();`, and at the top of `WriteVersionLineAsync`, right after `var current = CapacitorVersion.CurrentDisplay();`:

```csharp
        if (_appBundled) {
            await Console.Out.WriteLineAsync(FormatBundledVersionLine(current));

            return;
        }
```

Add beside `FormatVersionLine`:

```csharp
    internal static string FormatBundledVersionLine(string current) => $"kcap {current} (bundled with Kurrent Capacitor)";
```

Check `src/Capacitor.Cli/Program.cs` still compiles: the `case "status":` and `case "update":` constructions pass no new argument, so the default resolves provenance at runtime.

- [ ] **Step 5: Run the tests and build the CLI**

Run the two test classes (expected: passed) and `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj` (expected: 0 warnings).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.Cli/Commands/UpdateCommand.cs src/Capacitor.Cli/Commands/StatusCommand.cs test/Capacitor.Cli.Tests.Unit/UpdateCommandBundledTests.cs test/Capacitor.Cli.Tests.Unit/StatusCommandVersionLineTests.cs
git commit -m "Point a bundled kcap's update surfaces at the desktop app"
```

---

### Task 4: App identity, icons, packaging files and the Velopack bootstrap

**Files:**
- Modify: `src/Capacitor.App/App.axaml` (root element), `src/Capacitor.App/Program.cs`, `src/Capacitor.App/Capacitor.App.csproj`, `Directory.Packages.props`
- Create: `src/Capacitor.App/Assets/kcap-icon.svg`, `src/Capacitor.App/Assets/kcap-icon.icns`, `scripts/render-app-icons.sh`, `src/Capacitor.App/Packaging/Info.plist`, `src/Capacitor.App/Packaging/app.entitlements.plist`, `src/Capacitor.App/Packaging/cli.entitlements.plist`, `src/Capacitor.App/Packaging/daemon.entitlements.plist`
- Replace: `src/Capacitor.App/Assets/kcap-icon.png` (rendered at 512 px)

**Interfaces:**
- Produces: `public static bool Program.UpdateRelaunch { get; }` — true when Velopack relaunched the process after applying an update.
- Produces: the four packaging files consumed by Tasks 19–22 by path.

- [ ] **Step 1: Add the Velopack package**

In `Directory.Packages.props`, add alphabetically among the `PackageVersion` items:

```xml
    <PackageVersion Include="Velopack" Version="1.2.0" />
```

In `src/Capacitor.App/Capacitor.App.csproj`, add to the `PackageReference` group:

```xml
        <PackageReference Include="Velopack" />
```

Run `dotnet restore src/Capacitor.App/Capacitor.App.csproj`. Expected: restores without error.

- [ ] **Step 2: Bootstrap Velopack first in `Main`**

Replace `src/Capacitor.App/Program.cs` with:

```csharp
using Avalonia;
using ReactiveUI.Avalonia;
using Velopack;

namespace Capacitor.App;

internal static class Program
{
    /// True when Velopack relaunched this process after applying an update — set from its
    /// OnRestarted hook, which fires on a failed apply too, so it means "relaunched", not "updated".
    public static bool UpdateRelaunch { get; private set; }

    [STAThread]
    public static void Main(string[] args) {
        // Velopack's install/update hooks exit from inside Run(); anything before it would re-run
        // during those operations. Auto-apply stays off: pending packages are applied by
        // UpdateCoordinator after the install-location guard and the prerelease rule.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnRestarted(_ => UpdateRelaunch = true)
            .Run();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .UseReactiveUI(_ => { })
            .LogToTrace();
}
```

- [ ] **Step 3: Name the application**

In `src/Capacitor.App/App.axaml`, add `Name="Kurrent Capacitor"` to the `<Application ...>` root element (next to `RequestedThemeVariant`).

- [ ] **Step 4: Add the icon source and the render script**

`src/Capacitor.App/Assets/kcap-icon.svg` (verbatim copy of kcap-web's `public/favicon.svg`):

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">
  <rect width="32" height="32" rx="6" fill="#100D14"/>
  <rect x="8" y="4" width="4" height="24" rx="1.5" fill="#F2EDF8"/>
  <rect x="20" y="4" width="4" height="24" rx="1.5" fill="#F2EDF8"/>
  <path d="M12 10 C16 6, 16 6, 20 10" stroke="#F2EDF8" stroke-width="2" stroke-linecap="round" fill="none"/>
</svg>
```

`scripts/render-app-icons.sh`:

```bash
#!/usr/bin/env bash
# Renders src/Capacitor.App/Assets/kcap-icon.svg into the committed kcap-icon.png (512 px) and
# kcap-icon.icns. Needs rsvg-convert (brew install librsvg) and macOS's iconutil; CI never runs it.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
assets="$here/../src/Capacitor.App/Assets"
svg="$assets/kcap-icon.svg"

command -v rsvg-convert >/dev/null || { echo "rsvg-convert not found (brew install librsvg)" >&2; exit 1; }
command -v iconutil >/dev/null || { echo "iconutil not found (macOS only)" >&2; exit 1; }

rsvg-convert -w 512 -h 512 "$svg" -o "$assets/kcap-icon.png"

iconset="$(mktemp -d)/kcap-icon.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  rsvg-convert -w "$size" -h "$size" "$svg" -o "$iconset/icon_${size}x${size}.png"
  rsvg-convert -w $((size * 2)) -h $((size * 2)) "$svg" -o "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$assets/kcap-icon.icns"
rm -rf "$(dirname "$iconset")"
echo "rendered $assets/kcap-icon.png and $assets/kcap-icon.icns"
```

Run: `chmod +x scripts/render-app-icons.sh && brew list librsvg >/dev/null 2>&1 || brew install librsvg; bash scripts/render-app-icons.sh`
Expected: both files written; `file src/Capacitor.App/Assets/kcap-icon.png` reports `512 x 512`; `file src/Capacitor.App/Assets/kcap-icon.icns` reports `Mac OS X icon`.

- [ ] **Step 5: Add the Info.plist template and entitlements**

`src/Capacitor.App/Packaging/Info.plist` (`{VERSION}` and `{SHORT_VERSION}` are substituted by `scripts/render-info-plist.sh`, Task 19):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>Kurrent Capacitor</string>
  <key>CFBundleDisplayName</key>
  <string>Kurrent Capacitor</string>
  <key>CFBundleExecutable</key>
  <string>Kurrent Capacitor</string>
  <key>CFBundleIdentifier</key>
  <string>io.kurrent.capacitor</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>{SHORT_VERSION}</string>
  <key>CFBundleVersion</key>
  <string>{VERSION}</string>
  <key>CFBundleIconFile</key>
  <string>kcap-icon.icns</string>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>15.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.developer-tools</string>
  <key>NSHumanReadableCopyright</key>
  <string>© Kurrent, Inc.</string>
</dict>
</plist>
```

`src/Capacitor.App/Packaging/app.entitlements.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>com.apple.security.cs.allow-jit</key>
  <true/>
  <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
  <true/>
  <key>com.apple.security.cs.allow-dyld-environment-variables</key>
  <true/>
  <key>com.apple.security.cs.disable-library-validation</key>
  <true/>
</dict>
</plist>
```

`src/Capacitor.App/Packaging/cli.entitlements.plist` (the downloaded `e_sqlite3` is not signed by our team):

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>com.apple.security.cs.disable-library-validation</key>
  <true/>
</dict>
</plist>
```

`src/Capacitor.App/Packaging/daemon.entitlements.plist`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict/>
</plist>
```

Run: `plutil -lint src/Capacitor.App/Packaging/*.plist`. Expected: every file `OK`.

- [ ] **Step 6: Build and run the app once**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` (expected: 0 warnings, including AVLN XAML warnings). Then `dotnet run --project src/Capacitor.App/Capacitor.App.csproj` from a terminal (not a sandboxed shell) and confirm the application menu reads "Kurrent Capacitor" and the dock shows the new mark; quit.

- [ ] **Step 7: Commit**

```bash
git add Directory.Packages.props src/Capacitor.App/Capacitor.App.csproj src/Capacitor.App/Program.cs src/Capacitor.App/App.axaml src/Capacitor.App/Assets scripts/render-app-icons.sh src/Capacitor.App/Packaging
git commit -m "Name the desktop app, render its icon from the favicon and bootstrap Velopack"
```

---

### Task 5: `CliResolver` bundle-sibling arm

**Files:**
- Modify: `src/Capacitor.App/Services/CliResolver.cs`, `src/Capacitor.App/App.axaml.cs:465` and `:796` (the two `CliResolver.ResolvePath(` call sites)
- Test: `test/Capacitor.App.Tests.Unit/CliResolverTests.cs`

**Interfaces:**
- Produces: `public static string? CliResolver.ResolvePath(Func<string, string?> getEnv, Func<string, bool> fileExists, string baseDirectory)` — override → `Path.Combine(baseDirectory, "kcap")` when it exists → `"kcap"`.

- [ ] **Step 1: Update the tests**

In `CliResolverTests.cs`, add `"/nowhere"` as the third argument to the four existing `ResolvePath` calls, and add:

```csharp
    [Test]
    public async Task ResolvePath_uses_the_bundle_sibling_when_present() {
        var sibling = Path.Combine("/Applications/Kurrent Capacitor.app/Contents/MacOS", "kcap");

        var path = CliResolver.ResolvePath(_ => null, p => p == sibling, "/Applications/Kurrent Capacitor.app/Contents/MacOS");

        await Assert.That(path).IsEqualTo(sibling);
    }

    [Test]
    public async Task ResolvePath_override_wins_over_the_bundle_sibling() {
        var path = CliResolver.ResolvePath(_ => "/opt/kcap/kcap", _ => true, "/Applications/Kurrent Capacitor.app/Contents/MacOS");

        await Assert.That(path).IsEqualTo("/opt/kcap/kcap");
    }

    [Test]
    public async Task ResolvePath_missing_sibling_falls_through_to_path() {
        var path = CliResolver.ResolvePath(_ => null, _ => false, "/Applications/Kurrent Capacitor.app/Contents/MacOS");

        await Assert.That(path).IsEqualTo("kcap");
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/CliResolverTests/*"`
Expected: build error (no three-argument overload).

- [ ] **Step 3: Implement**

Replace `ResolvePath` and its doc comment in `CliResolver.cs`:

```csharp
    /// KCAP_APP_CLI_PATH → the `kcap` beside this executable (the app bundle's Contents/MacOS) →
    /// "kcap" on PATH.
    ///
    /// Returns null ONLY when the override is set but the path it names does not exist — a broken
    /// override must not silently fall back, since that would make the dev seam lie about which
    /// binary actually ran. The sibling arm returns an absolute path, which is what lets the shim
    /// offer link to it. Every other case returns bare "kcap": PATH resolution, and "no CLI at
    /// all", are the OS's job at spawn time, surfaced by the caller's own RunAsync handling.
    public static string? ResolvePath(Func<string, string?> getEnv, Func<string, bool> fileExists, string baseDirectory) {
        var overridePath = getEnv("KCAP_APP_CLI_PATH");
        if (!string.IsNullOrEmpty(overridePath)) return fileExists(overridePath) ? overridePath : null;

        var sibling = Path.Combine(baseDirectory, "kcap");
        return fileExists(sibling) ? sibling : "kcap";
    }
```

Update both call sites in `App.axaml.cs` to `CliResolver.ResolvePath(Environment.GetEnvironmentVariable, File.Exists, AppContext.BaseDirectory)`. Also update the class doc comment's "*(future: bundle-relative path arm lands here)*" text to describe the three arms as they now are.

- [ ] **Step 4: Run the tests and build**

Run the class filter (expected: passed) and `dotnet build src/Capacitor.App/Capacitor.App.csproj` (expected: 0 warnings).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/CliResolver.cs src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/CliResolverTests.cs
git commit -m "Resolve the bundled kcap beside the app before falling back to PATH"
```

---

### Task 6: The updater seam — `IAppUpdater`, feed URL, prerelease filter, Velopack adapter

**Files:**
- Create: `src/Capacitor.App/Services/Update/UpdateCandidate.cs`, `IAppUpdater.cs`, `InertAppUpdater.cs`, `UpdateFeed.cs`, `PrereleaseFilteringSource.cs`, `VelopackAppUpdater.cs`
- Test: `test/Capacitor.App.Tests.Unit/UpdateFeedTests.cs`, `test/Capacitor.App.Tests.Unit/PrereleaseFilteringSourceTests.cs`

**Interfaces:**
- Produces (namespace `Capacitor.App.Services.Update`):
  - `public sealed record UpdateCandidate(string Version, bool IsPrerelease);`
  - `public interface IAppUpdater { bool IsAvailable { get; } string? InstalledVersion { get; } UpdateCandidate? PendingRestart { get; } Task<UpdateCandidate?> CheckAsync(CancellationToken ct); Task DownloadAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken ct); void ApplyOnExit(UpdateCandidate candidate); void ApplyNow(UpdateCandidate candidate); }`
  - `public sealed class InertAppUpdater : IAppUpdater` (never available).
  - `public static class UpdateFeed { const string BaseUrl; const string OverrideVariable; static string Resolve(Func<string, string?> getEnv); }`
  - `public sealed class PrereleaseFilteringSource(IUpdateSource inner, Func<bool> allowPrerelease) : IUpdateSource` with `internal static VelopackAsset[] Filter(VelopackAsset[] assets, bool allowPrerelease)`.
  - `public sealed class VelopackAppUpdater(Func<string, string?> getEnv) : IAppUpdater`.

- [ ] **Step 1: Write the failing tests**

`UpdateFeedTests.cs`:

```csharp
using Capacitor.App.Services.Update;

namespace Capacitor.App.Tests.Unit;

public class UpdateFeedTests {
    [Test]
    public async Task Default_is_the_kurrent_desktop_feed() {
        await Assert.That(UpdateFeed.Resolve(_ => null)).IsEqualTo("https://www.kurrent.io/download/desktop/osx-arm64/");
    }

    [Test]
    public async Task Override_variable_replaces_the_feed_url() {
        await Assert.That(UpdateFeed.Resolve(k => k == "KCAP_APP_UPDATE_URL" ? " http://127.0.0.1:8080/feed/ " : null))
            .IsEqualTo("http://127.0.0.1:8080/feed/");
    }

    [Test]
    public async Task Blank_override_is_ignored() {
        await Assert.That(UpdateFeed.Resolve(_ => "   ")).IsEqualTo(UpdateFeed.BaseUrl);
    }
}
```

`PrereleaseFilteringSourceTests.cs`:

```csharp
using Capacitor.App.Services.Update;
using Velopack;

namespace Capacitor.App.Tests.Unit;

public class PrereleaseFilteringSourceTests {
    static VelopackAsset Asset(string version) =>
        new() { PackageId = "KurrentCapacitor", Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full, FileName = $"KurrentCapacitor-{version}-osx-arm64-full.nupkg" };

    [Test]
    public async Task Stable_install_drops_prereleases() {
        var kept = PrereleaseFilteringSource.Filter([Asset("0.12.0"), Asset("0.12.1-beta.1"), Asset("0.12.1")], allowPrerelease: false);

        await Assert.That(kept.Select(a => a.Version.ToString())).IsEquivalentTo(["0.12.0", "0.12.1"]);
    }

    [Test]
    public async Task Prerelease_install_keeps_everything() {
        var kept = PrereleaseFilteringSource.Filter([Asset("0.12.0"), Asset("0.12.1-beta.1")], allowPrerelease: true);

        await Assert.That(kept.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Empty_feed_stays_empty() {
        await Assert.That(PrereleaseFilteringSource.Filter([], allowPrerelease: false)).IsEmpty();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/PrereleaseFilteringSourceTests/*"`
Expected: build error.

- [ ] **Step 3: Implement the seam types**

`UpdateCandidate.cs`:

```csharp
namespace Capacitor.App.Services.Update;

/// One release the feed offers, identified by version; the Velopack asset behind it stays inside
/// the adapter so the coordinator and its tests never see Velopack types.
public sealed record UpdateCandidate(string Version, bool IsPrerelease);
```

`IAppUpdater.cs`:

```csharp
namespace Capacitor.App.Services.Update;

/// The app's view of the updater. Unavailable outside a packed bundle (a `dotnet run` build), in
/// which case nothing else here may be called.
public interface IAppUpdater {
    bool IsAvailable { get; }
    string? InstalledVersion { get; }

    /// A package already downloaded and waiting for a relaunch, or null.
    UpdateCandidate? PendingRestart { get; }

    Task<UpdateCandidate?> CheckAsync(CancellationToken ct);
    Task DownloadAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken ct);

    /// Hands the swap to the updater, which waits for this process to exit; call it last in the
    /// shutdown sequence — its wait is bounded to 60 s.
    void ApplyOnExit(UpdateCandidate candidate);

    /// Applies immediately: exits this process and relaunches the new version.
    void ApplyNow(UpdateCandidate candidate);
}
```

`InertAppUpdater.cs`:

```csharp
namespace Capacitor.App.Services.Update;

/// The updater outside a packed bundle, or when the Velopack adapter failed to construct.
public sealed class InertAppUpdater : IAppUpdater {
    public static readonly InertAppUpdater Instance = new();

    public bool IsAvailable => false;
    public string? InstalledVersion => null;
    public UpdateCandidate? PendingRestart => null;
    public Task<UpdateCandidate?> CheckAsync(CancellationToken ct) => Task.FromResult<UpdateCandidate?>(null);
    public Task DownloadAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken ct) => Task.CompletedTask;
    public void ApplyOnExit(UpdateCandidate candidate) { }
    public void ApplyNow(UpdateCandidate candidate) { }
}
```

`UpdateFeed.cs`:

```csharp
namespace Capacitor.App.Services.Update;

public static class UpdateFeed {
    public const string BaseUrl = "https://www.kurrent.io/download/desktop/osx-arm64/";
    public const string OverrideVariable = "KCAP_APP_UPDATE_URL";

    public static string Resolve(Func<string, string?> getEnv) {
        var overrideUrl = getEnv(OverrideVariable);
        return string.IsNullOrWhiteSpace(overrideUrl) ? BaseUrl : overrideUrl.Trim();
    }
}
```

`PrereleaseFilteringSource.cs`:

```csharp
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Capacitor.App.Services.Update;

/// One feed serves stable and beta installs: a stable install never sees a prerelease entry, a
/// prerelease install sees everything. Evaluated per feed read so the installed version decides.
public sealed class PrereleaseFilteringSource(IUpdateSource inner, Func<bool> allowPrerelease) : IUpdateSource {
    public async Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) {
        var feed = await inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
        return new VelopackAssetFeed { Assets = Filter(feed.Assets, allowPrerelease()) };
    }

    public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
        inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);

    internal static VelopackAsset[] Filter(VelopackAsset[] assets, bool allowPrerelease) =>
        allowPrerelease ? assets : assets.Where(a => !a.Version.IsPrerelease).ToArray();
}
```

`VelopackAppUpdater.cs`:

```csharp
using Velopack;
using Velopack.Sources;

namespace Capacitor.App.Services.Update;

/// IAppUpdater over Velopack's UpdateManager. The prerelease rule reads the installed version at
/// feed time, so a beta install follows betas and a stable install does not.
public sealed class VelopackAppUpdater : IAppUpdater {
    readonly UpdateManager _manager;
    UpdateInfo? _lastCheck;

    public VelopackAppUpdater(Func<string, string?> getEnv) {
        var source = new PrereleaseFilteringSource(
            new SimpleWebSource(UpdateFeed.Resolve(getEnv)),
            () => _manager?.CurrentVersion?.IsPrerelease == true);
        _manager = new UpdateManager(source);
    }

    public bool IsAvailable => _manager.IsInstalled;
    public string? InstalledVersion => _manager.CurrentVersion?.ToString();

    public UpdateCandidate? PendingRestart =>
        _manager.UpdatePendingRestart is { } asset ? new UpdateCandidate(asset.Version.ToString(), asset.Version.IsPrerelease) : null;

    public async Task<UpdateCandidate?> CheckAsync(CancellationToken ct) {
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        _lastCheck = info;
        if (info is null) return null;
        var target = info.TargetFullRelease.Version;
        return new UpdateCandidate(target.ToString(), target.IsPrerelease);
    }

    public Task DownloadAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken ct) {
        var info = _lastCheck;
        if (info is null || info.TargetFullRelease.Version.ToString() != candidate.Version)
            throw new InvalidOperationException($"No check offered {candidate.Version}; check before downloading.");
        return _manager.DownloadUpdatesAsync(info, progress is null ? null : p => progress.Report(p), ct);
    }

    public void ApplyOnExit(UpdateCandidate candidate) =>
        _manager.WaitExitThenApplyUpdates(AssetFor(candidate), silent: true, restart: true);

    public void ApplyNow(UpdateCandidate candidate) =>
        _manager.ApplyUpdatesAndRestart(AssetFor(candidate));

    VelopackAsset AssetFor(UpdateCandidate candidate) {
        if (_manager.UpdatePendingRestart is { } pending && pending.Version.ToString() == candidate.Version) return pending;
        if (_lastCheck is { } info && info.TargetFullRelease.Version.ToString() == candidate.Version) return info.TargetFullRelease;
        throw new InvalidOperationException($"No downloaded package for {candidate.Version}.");
    }
}
```

If the compiler flags `_manager?.` on a non-nullable field, replace the lambda body with `_manager.CurrentVersion?.IsPrerelease == true` — the lambda only runs after the constructor has assigned the field.

- [ ] **Step 4: Run the tests and build**

Run both test classes (expected: passed) and `dotnet build src/Capacitor.App/Capacitor.App.csproj` (expected: 0 warnings).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/Update test/Capacitor.App.Tests.Unit/UpdateFeedTests.cs test/Capacitor.App.Tests.Unit/PrereleaseFilteringSourceTests.cs
git commit -m "Add the app updater seam over Velopack with a prerelease-aware feed"
```

---

### Task 7: Update prompt kinds

**Files:**
- Modify: `src/Capacitor.App/Services/ILifecycleSurface.cs` (`LifecyclePrompt` consts), `src/Capacitor.App/ViewModels/LifecyclePromptViewModel.cs` (constructor, `TitleFor`)
- Test: `test/Capacitor.App.Tests.Unit/LifecyclePromptViewModelTests.cs`

**Interfaces:**
- Produces: `LifecyclePrompt.KindUpdateReady = "update-ready"` (accept "Restart now", decline shown as "Not now") and `LifecyclePrompt.KindUpdateInfo = "update-info"` (confirm-only, "OK").

- [ ] **Step 1: Write the failing tests**

Append to `LifecyclePromptViewModelTests.cs` (match the file's existing construction style — a `LifecyclePrompt` plus a `TaskCompletionSource<bool>`):

```csharp
    [Test]
    public async Task Update_ready_offers_restart_now_and_not_now() {
        var vm = new LifecyclePromptViewModel(
            new LifecyclePrompt(LifecyclePrompt.KindUpdateReady, "0.12.0-beta.3", "0.12.0-beta.2", false, "ready"), new TaskCompletionSource<bool>());

        await Assert.That(vm.Title).IsEqualTo("Update ready");
        await Assert.That(vm.AcceptButtonText).IsEqualTo("Restart now");
        await Assert.That(vm.ShowDeclineButton).IsTrue();
    }

    [Test]
    public async Task Update_info_is_acknowledge_only() {
        var vm = new LifecyclePromptViewModel(
            new LifecyclePrompt(LifecyclePrompt.KindUpdateInfo, null, "0.12.0-beta.2", false, "up to date"), new TaskCompletionSource<bool>());

        await Assert.That(vm.Title).IsEqualTo("Software update");
        await Assert.That(vm.AcceptButtonText).IsEqualTo("OK");
        await Assert.That(vm.ShowDeclineButton).IsFalse();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/LifecyclePromptViewModelTests/*"`
Expected: build error (consts missing).

- [ ] **Step 3: Implement**

In `ILifecycleSurface.cs`, add to the `LifecyclePrompt` record:

```csharp
    public const string KindUpdateReady   = "update-ready";
    public const string KindUpdateInfo    = "update-info";
```

In `LifecyclePromptViewModel.cs`, change the constructor lines:

```csharp
        ShowDeclineButton = prompt.Kind is not (LifecyclePrompt.KindQuarantine or LifecyclePrompt.KindUpdateInfo);
        AcceptButtonText  = prompt.Kind switch {
            LifecyclePrompt.KindQuarantine  => "Acknowledge",
            LifecyclePrompt.KindUpdateInfo  => "OK",
            LifecyclePrompt.KindUpdateReady => "Restart now",
            _                               => "Continue",
        };
```

and the `ShowDeclineButton` doc comment to "False for the acknowledge-only kinds (quarantine, update info)". Add to `TitleFor`:

```csharp
        LifecyclePrompt.KindUpdateReady   => "Update ready",
        LifecyclePrompt.KindUpdateInfo    => "Software update",
```

- [ ] **Step 4: Run the class and build**

Expected: passed, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/ILifecycleSurface.cs src/Capacitor.App/ViewModels/LifecyclePromptViewModel.cs test/Capacitor.App.Tests.Unit/LifecyclePromptViewModelTests.cs
git commit -m "Add update-ready and update-info prompt kinds"
```

---

### Task 8: `UpdateCoordinator`

**Files:**
- Create: `src/Capacitor.App/Services/Update/UpdateMenuItem.cs`, `src/Capacitor.App/Services/Update/UpdateCoordinator.cs`
- Test: `test/Capacitor.App.Tests.Unit/FakeAppUpdater.cs`, `test/Capacitor.App.Tests.Unit/UpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IAppUpdater`, `UpdateCandidate` (Task 6); `ILifecycleSurface`, `LifecyclePrompt.KindUpdateReady/KindUpdateInfo` (Task 7); `Capacitor.Cli.Core.PrereleaseSemver.IsNewer(string?, string?)`.
- Produces: `public sealed record UpdateMenuItem(bool Visible, string Label);` and

```csharp
public sealed class UpdateCoordinator {
    public UpdateCoordinator(IAppUpdater updater, ILifecycleSurface surface, TimeProvider time, Action quit, CancellationToken lifetime,
                             TimeSpan? initialDelay = null, TimeSpan? interval = null);
    public IObservable<UpdateMenuItem> MenuItem { get; }          // hidden when unavailable; "Check for Updates…"; "Restart to update to <v>"
    public static bool TryApplyPendingAtStartup(IAppUpdater updater, bool updateRelaunch);
    public void Start();                                          // schedules the checks; no-op when unavailable
    public Task RunMenuActionAsync();                             // check when idle, restart when ready
    public void ApplyPendingOnExit();                             // called last in the shutdown sequence
}
```

- [ ] **Step 1: Write the fake and the failing tests**

`test/Capacitor.App.Tests.Unit/FakeAppUpdater.cs`:

```csharp
using Capacitor.App.Services.Update;

namespace Capacitor.App.Tests.Unit;

/// Scripted IAppUpdater: every call is counted, the next check's answer is a settable field, and
/// downloads can be held on a TaskCompletionSource so a test can observe the in-flight state.
sealed class FakeAppUpdater : IAppUpdater {
    public bool IsAvailable { get; set; } = true;
    public string? InstalledVersion { get; set; } = "0.12.0-beta.2";
    public UpdateCandidate? PendingRestart { get; set; }

    public UpdateCandidate? NextCheck;
    public Exception? CheckFailure;
    public int CheckCalls;
    public int DownloadCalls;
    public TaskCompletionSource? HoldDownload;
    public readonly List<UpdateCandidate> ApplyOnExitCalls = [];
    public readonly List<UpdateCandidate> ApplyNowCalls = [];

    public Task<UpdateCandidate?> CheckAsync(CancellationToken ct) {
        CheckCalls++;
        if (CheckFailure is { } failure) return Task.FromException<UpdateCandidate?>(failure);
        return Task.FromResult(NextCheck);
    }

    public async Task DownloadAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken ct) {
        DownloadCalls++;
        if (HoldDownload is { } hold) await hold.Task.WaitAsync(ct);
        PendingRestart = candidate;
    }

    public void ApplyOnExit(UpdateCandidate candidate) => ApplyOnExitCalls.Add(candidate);
    public void ApplyNow(UpdateCandidate candidate) => ApplyNowCalls.Add(candidate);
}
```

`test/Capacitor.App.Tests.Unit/UpdateCoordinatorTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.App.Services.Update;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class UpdateCoordinatorTests {
    static async Task WaitUntilAsync(Func<bool> condition, string what) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    sealed class Harness {
        public readonly FakeAppUpdater Updater = new();
        public readonly FakeLifecycleSurface Surface = new();
        public readonly FakeTimeProvider Clock = new(new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero));
        public readonly CancellationTokenSource Lifetime = new();
        public int QuitCalls;
        public UpdateMenuItem? Menu;
        public readonly UpdateCoordinator Coordinator;

        public Harness() {
            Coordinator = new UpdateCoordinator(Updater, Surface, Clock, () => QuitCalls++, Lifetime.Token);
            Coordinator.MenuItem.Subscribe(m => Menu = m);
        }
    }

    static UpdateCandidate Beta3 => new("0.12.0-beta.3", IsPrerelease: true);

    [Test]
    public async Task Unavailable_updater_hides_the_item_and_never_checks() {
        var h = new Harness();
        h.Updater.IsAvailable = false;
        var coordinator = new UpdateCoordinator(h.Updater, h.Surface, h.Clock, () => { }, h.Lifetime.Token);
        UpdateMenuItem? menu = null;
        coordinator.MenuItem.Subscribe(m => menu = m);

        coordinator.Start();
        h.Clock.Advance(TimeSpan.FromHours(9));
        await Task.Delay(50);

        await Assert.That(menu!.Visible).IsFalse();
        await Assert.That(h.Updater.CheckCalls).IsEqualTo(0);
    }

    [Test]
    public async Task First_check_waits_for_the_initial_delay_then_repeats_on_the_interval() {
        var h = new Harness();
        h.Coordinator.Start();

        h.Clock.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50);
        await Assert.That(h.Updater.CheckCalls).IsEqualTo(0);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => h.Updater.CheckCalls == 1, "the first check");

        h.Clock.Advance(TimeSpan.FromHours(4));
        await WaitUntilAsync(() => h.Updater.CheckCalls == 2, "the second check");
        await Assert.That(h.Menu!.Label).IsEqualTo(UpdateCoordinator.CheckLabel);
    }

    [Test]
    public async Task Found_update_downloads_then_prompts_once() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, "the ready prompt");
        await Assert.That(h.Updater.DownloadCalls).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindUpdateReady);
        await Assert.That(h.Surface.Prompts[0].DaemonVersion).IsEqualTo("0.12.0-beta.3");
    }

    [Test]
    public async Task Later_relabels_the_item_and_stops_further_checks() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Menu?.Label == "Restart to update to 0.12.0-beta.3", "the ready label");

        h.Clock.Advance(TimeSpan.FromHours(9));
        await Task.Delay(50);

        await Assert.That(h.Updater.CheckCalls).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.QuitCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Restart_now_records_the_pending_apply_and_quits_and_applies_on_exit_once() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(true);
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.QuitCalls == 1, "the quit request");

        await Assert.That(h.Updater.ApplyOnExitCalls).IsEmpty();
        h.Coordinator.ApplyPendingOnExit();
        h.Coordinator.ApplyPendingOnExit();

        await Assert.That(h.Updater.ApplyOnExitCalls.Select(c => c.Version)).IsEquivalentTo(["0.12.0-beta.3"]);
    }

    [Test]
    public async Task Menu_action_while_ready_restarts_instead_of_checking() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, "the ready prompt");

        await h.Coordinator.RunMenuActionAsync();

        await Assert.That(h.QuitCalls).IsEqualTo(1);
        await Assert.That(h.Updater.CheckCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Manual_check_reports_up_to_date_in_an_info_prompt() {
        var h = new Harness();

        await h.Coordinator.RunMenuActionAsync();

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Kind).IsEqualTo(LifecyclePrompt.KindUpdateInfo);
        await Assert.That(h.Surface.Prompts[0].Disclosure).Contains("up to date");
    }

    [Test]
    public async Task Manual_check_failure_is_reported_and_automatic_failure_is_silent() {
        var h = new Harness();
        h.Updater.CheckFailure = new HttpRequestException("feed down");
        h.Coordinator.Start();
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => h.Updater.CheckCalls == 1, "the automatic check");
        await Assert.That(h.Surface.Prompts).IsEmpty();

        await h.Coordinator.RunMenuActionAsync();

        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
        await Assert.That(h.Surface.Prompts[0].Disclosure).Contains("Could not check for updates");
    }

    [Test]
    public async Task Concurrent_manual_checks_coalesce_onto_one_call() {
        var h = new Harness();
        h.Updater.NextCheck = Beta3;
        h.Updater.HoldDownload = new TaskCompletionSource();
        h.Surface.ConfirmBehavior = (_, _) => Task.FromResult(false);

        var first  = h.Coordinator.RunMenuActionAsync();
        var second = h.Coordinator.RunMenuActionAsync();
        h.Updater.HoldDownload.SetResult();
        await Task.WhenAll(first, second);

        await Assert.That(h.Updater.CheckCalls).IsEqualTo(1);
    }

    [Test]
    public async Task Startup_pending_apply_applies_an_eligible_package() {
        var updater = new FakeAppUpdater { PendingRestart = Beta3 };

        var applied = UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false);

        await Assert.That(applied).IsTrue();
        await Assert.That(updater.ApplyNowCalls.Select(c => c.Version)).IsEquivalentTo(["0.12.0-beta.3"]);
    }

    [Test]
    public async Task Startup_pending_apply_ignores_a_prerelease_under_a_stable_install() {
        var updater = new FakeAppUpdater { InstalledVersion = "0.12.0", PendingRestart = new("0.13.0-beta.1", IsPrerelease: true) };

        await Assert.That(UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false)).IsFalse();
        await Assert.That(updater.ApplyNowCalls).IsEmpty();
    }

    [Test]
    public async Task Startup_pending_apply_ignores_an_older_package() {
        var updater = new FakeAppUpdater { InstalledVersion = "0.12.0-beta.4", PendingRestart = Beta3 };

        await Assert.That(UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false)).IsFalse();
    }

    [Test]
    public async Task Startup_pending_apply_is_skipped_on_an_update_relaunch() {
        var updater = new FakeAppUpdater { PendingRestart = Beta3 };

        await Assert.That(UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: true)).IsFalse();
        await Assert.That(updater.ApplyNowCalls).IsEmpty();
    }

    [Test]
    public async Task Startup_pending_apply_is_inert_when_unavailable() {
        var updater = new FakeAppUpdater { IsAvailable = false, PendingRestart = Beta3 };

        await Assert.That(UpdateCoordinator.TryApplyPendingAtStartup(updater, updateRelaunch: false)).IsFalse();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/UpdateCoordinatorTests/*"`
Expected: build error.

- [ ] **Step 3: Implement**

`UpdateMenuItem.cs`:

```csharp
namespace Capacitor.App.Services.Update;

/// The tray's single update item: hidden, "Check for Updates…", or "Restart to update to <v>".
public sealed record UpdateMenuItem(bool Visible, string Label);
```

`UpdateCoordinator.cs`:

```csharp
using System.Reactive.Subjects;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Update;

/// Owns the update schedule and its UX: silent periodic checks, background download, one prompt
/// when a package is ready, and the hand-off to the updater at the very end of shutdown. Once a
/// package is ready nothing else is checked or downloaded this run — a later download, even a
/// failed one, deletes every other cached package.
public sealed class UpdateCoordinator {
    internal static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(4);
    internal const string CheckLabel = "Check for Updates…";
    internal const string ReadyDisclosure =
        "Restart Kurrent Capacitor to finish installing it. Running agents keep running; the daemon restarts on its own once it is idle.";

    readonly IAppUpdater _updater;
    readonly ILifecycleSurface _surface;
    readonly TimeProvider _time;
    readonly Action _quit;
    readonly CancellationToken _lifetime;
    readonly TimeSpan _initialDelay;
    readonly TimeSpan _interval;
    readonly BehaviorSubject<UpdateMenuItem> _menu;
    readonly Lock _lock = new();

    Task? _inflight;
    bool _manualPending;
    UpdateCandidate? _ready;
    UpdateCandidate? _pendingApply;

    public UpdateCoordinator(
            IAppUpdater updater, ILifecycleSurface surface, TimeProvider time, Action quit, CancellationToken lifetime,
            TimeSpan? initialDelay = null, TimeSpan? interval = null) {
        _updater      = updater;
        _surface      = surface;
        _time         = time;
        _quit         = quit;
        _lifetime     = lifetime;
        _initialDelay = initialDelay ?? DefaultInitialDelay;
        _interval     = interval ?? DefaultInterval;
        _menu         = new BehaviorSubject<UpdateMenuItem>(new UpdateMenuItem(updater.IsAvailable, CheckLabel));
    }

    public IObservable<UpdateMenuItem> MenuItem => _menu;

    /// Applies a package left from a previous run, after the install-location guard and before any
    /// graph exists. Skipped on an update relaunch: a failed apply relaunches the old version with
    /// the same package still cached, and applying it again automatically would loop before any UI
    /// appeared. True means the process is being replaced.
    public static bool TryApplyPendingAtStartup(IAppUpdater updater, bool updateRelaunch) {
        if (!updater.IsAvailable || updateRelaunch) return false;
        if (updater.PendingRestart is not { } pending || !IsEligible(updater, pending)) return false;

        updater.ApplyNow(pending);
        return true;
    }

    internal static bool IsEligible(IAppUpdater updater, UpdateCandidate candidate) {
        var installed = updater.InstalledVersion;
        if (installed is null) return false;
        if (candidate.IsPrerelease && !IsPrerelease(installed)) return false;

        return PrereleaseSemver.IsNewer(candidate.Version, installed);
    }

    static bool IsPrerelease(string version) {
        var core = version.Split('+', 2)[0];
        return core.Contains('-');
    }

    public void Start() {
        if (!_updater.IsAvailable) return;
        _ = RunScheduleAsync();
    }

    /// The tray item's action: a check while idle, the restart once a package is ready.
    public Task RunMenuActionAsync() {
        if (_ready is { } ready) {
            RequestRestart(ready);
            return Task.CompletedTask;
        }

        return CheckAsync(manual: true);
    }

    /// Called by the shutdown sequence immediately before the platform shutdown call, so the
    /// updater's bounded wait only ever covers process exit.
    public void ApplyPendingOnExit() {
        UpdateCandidate? pending;
        lock (_lock) {
            pending = _pendingApply;
            _pendingApply = null;
        }
        if (pending is not null) _updater.ApplyOnExit(pending);
    }

    async Task RunScheduleAsync() {
        try {
            await Task.Delay(_initialDelay, _time, _lifetime).ConfigureAwait(false);
            while (!_lifetime.IsCancellationRequested) {
                await CheckAsync(manual: false).ConfigureAwait(false);
                if (_ready is not null) return;
                await Task.Delay(_interval, _time, _lifetime).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // shutdown
        }
    }

    Task CheckAsync(bool manual) {
        lock (_lock) {
            if (_inflight is { IsCompleted: false } running) {
                if (manual) _manualPending = true;
                return running;
            }

            _inflight = RunCheckAsync(manual);
            return _inflight;
        }
    }

    async Task RunCheckAsync(bool manual) {
        try {
            if (_ready is { } ready) {
                if (manual) await ReportAsync($"Kurrent Capacitor {ready.Version} is ready to install.").ConfigureAwait(false);
                return;
            }

            var candidate = await _updater.CheckAsync(_lifetime).ConfigureAwait(false);
            if (candidate is null) {
                if (ConsumeManual(manual)) await ReportAsync($"You're up to date ({_updater.InstalledVersion}).").ConfigureAwait(false);
                return;
            }

            await _updater.DownloadAsync(candidate, null, _lifetime).ConfigureAwait(false);
            _ready = candidate;
            _menu.OnNext(new UpdateMenuItem(true, $"Restart to update to {candidate.Version}"));

            var prompt = new LifecyclePrompt(
                LifecyclePrompt.KindUpdateReady, candidate.Version, _updater.InstalledVersion, false,
                $"Kurrent Capacitor {candidate.Version} is ready. {ReadyDisclosure}");
            var accepted = await _surface.ConfirmAsync(prompt, _lifetime).ConfigureAwait(false);
            ConsumeManual(manual);
            if (accepted) RequestRestart(candidate);
        } catch (OperationCanceledException) {
            // shutdown
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: update check failed: {ex.Message}");
            if (ConsumeManual(manual)) await ReportAsync("Could not check for updates. Try again later.").ConfigureAwait(false);
        }
    }

    bool ConsumeManual(bool manual) {
        lock (_lock) {
            var pending = _manualPending;
            _manualPending = false;
            return manual || pending;
        }
    }

    void RequestRestart(UpdateCandidate candidate) {
        lock (_lock) _pendingApply = candidate;
        _quit();
    }

    Task ReportAsync(string message) =>
        _surface.ConfirmAsync(new LifecyclePrompt(LifecyclePrompt.KindUpdateInfo, null, _updater.InstalledVersion, false, message), _lifetime);
}
```

- [ ] **Step 4: Run the class and build**

Run the `UpdateCoordinatorTests` filter (expected: 13 passed) and `dotnet build src/Capacitor.App/Capacitor.App.csproj` (expected: 0 warnings). If a timing test is flaky under the fake clock, the fix is a `WaitUntilAsync` on the observable effect, never a longer `Task.Delay`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/Update/UpdateMenuItem.cs src/Capacitor.App/Services/Update/UpdateCoordinator.cs test/Capacitor.App.Tests.Unit/FakeAppUpdater.cs test/Capacitor.App.Tests.Unit/UpdateCoordinatorTests.cs
git commit -m "Add the update coordinator: scheduled checks, one ready prompt, apply on exit"
```

---

### Task 9: The tray update item

**Files:**
- Modify: `src/Capacitor.App/ViewModels/TrayModels.cs` (`TrayMenuModel`), `src/Capacitor.App/ViewModels/TrayViewModel.cs` (constructor, projection), `src/Capacitor.App/Views/TrayMenuBuilder.cs` (`Rebuild`)
- Test: `test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs`, `test/Capacitor.App.Tests.Unit/TrayAdapterTests.cs`

**Interfaces:**
- Consumes: `UpdateMenuItem` (Task 8).
- Produces: `TrayMenuModel(..., bool ShimInstallVisible = false, string? UpdateItemLabel = null)`; `TrayViewModel` constructor gains `IObservable<UpdateMenuItem>? updateMenu = null, Func<Task>? updateAction = null` and exposes `ReactiveCommand<Unit, Unit> UpdateActionCommand`.

- [ ] **Step 1: Write the failing tests**

In `TrayViewModelTests.cs`, after `ShimOfferable_drives_MenuModel_ShimInstallVisible`:

```csharp
    // ---- the update item ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task UpdateMenu_drives_MenuModel_UpdateItemLabel() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var pause = new FakePauseController();
            var actions = NewActions(service);
            var consent = new FakeConsentService();
            var updateMenu = new BehaviorSubject<UpdateMenuItem>(new UpdateMenuItem(false, "Check for Updates…"));
            using var vm = new TrayViewModel(service, pause, actions, consent, updateMenu: updateMenu);

            await Assert.That(vm.MenuModel.UpdateItemLabel).IsNull();

            updateMenu.OnNext(new UpdateMenuItem(true, "Restart to update to 0.12.0-beta.3"));

            await Assert.That(vm.MenuModel.UpdateItemLabel).IsEqualTo("Restart to update to 0.12.0-beta.3");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task UpdateActionCommand_invokes_the_injected_action() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var service = new FakeDaemonClientService();
            var calls = 0;
            using var vm = new TrayViewModel(service, new FakePauseController(), NewActions(service), new FakeConsentService(),
                updateAction: () => { calls++; return Task.CompletedTask; });

            await vm.UpdateActionCommand.Execute().ToTask();

            await Assert.That(calls).IsEqualTo(1);
        });
    }
```

Add `using Capacitor.App.Services.Update;` to the file's usings. In `TrayAdapterTests.cs`, extend the `Model(...)` helper with `string? updateItemLabel = null` passed as the last constructor argument, and add after the shim-item tests:

```csharp
    // ---- the update item ----

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_omits_the_update_item_without_a_label() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var hasItem = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                using var vm = new TrayViewModel(service, new FakePauseController(), NewActions(service), new FakeConsentService());
                var menu = new NativeMenu();

                new TrayMenuBuilder(vm).Rebuild(menu, Model(agents: [], updateItemLabel: null));

                return menu.Items.OfType<NativeMenuItem>().Any(i => i.Header == "Check for Updates…");
            });

            await Assert.That(hasItem).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rebuild_adds_the_update_item_with_its_label_and_command() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var (header, matches) = await AvaloniaSession.DispatchAsync(() => {
                var service = new FakeDaemonClientService();
                using var vm = new TrayViewModel(service, new FakePauseController(), NewActions(service), new FakeConsentService());
                var menu = new NativeMenu();

                new TrayMenuBuilder(vm).Rebuild(menu, Model(agents: [], updateItemLabel: "Restart to update to 0.12.0-beta.3"));

                var item = menu.Items.OfType<NativeMenuItem>().First(i => i.Header == "Restart to update to 0.12.0-beta.3");
                return (item.Header, ReferenceEquals(item.Command, vm.UpdateActionCommand));
            });

            await Assert.That(header).IsEqualTo("Restart to update to 0.12.0-beta.3");
            await Assert.That(matches).IsTrue();
        });
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/TrayViewModelTests/*"`
Expected: build error.

- [ ] **Step 3: Implement**

`TrayModels.cs` — the record:

```csharp
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause, int PendingConsent, bool ShimInstallVisible = false,
    string? UpdateItemLabel = null);
```

`TrayViewModel.cs` — add `using Capacitor.App.Services.Update;`, the property beside `InstallShimCommand`:

```csharp
    // The tray's single update item (check while idle, restart once a package is ready); the
    // coordinator decides which, so this is a plain delegate call like InstallShimCommand.
    public ReactiveCommand<Unit, Unit> UpdateActionCommand { get; }
```

Extend the constructor signature with `IObservable<UpdateMenuItem>? updateMenu = null, Func<Task>? updateAction = null` after `permissions`, then:

```csharp
        UpdateActionCommand = ReactiveCommand.CreateFromTask(updateAction ?? (() => Task.CompletedTask));
```

and, right after `var withShim = ...;`:

```csharp
        // Same shape as the shim item: a narrow CombineLatest so Build stays untouched. Null (most
        // tests) is Observable.Return(hidden), which seeds and completes like the sources above.
        var update = updateMenu ?? Observable.Return(new UpdateMenuItem(false, ""));
        var withUpdate = withShim.CombineLatest(update, (model, item) => model with { UpdateItemLabel = item.Visible ? item.Label : null });
```

Then replace the two later uses of `withShim` (the seed subscription and `_menuModel = withShim...`) with `withUpdate`.

`TrayMenuBuilder.cs` — after the shim item block, before the final separator:

```csharp
        // One item whose label the coordinator owns: "Check for Updates…" while idle, "Restart
        // to update to <v>" once a package is downloaded; absent outside a packed bundle.
        if (model.UpdateItemLabel is { } updateLabel)
            menu.Items.Add(new NativeMenuItem(updateLabel) { Command = vm.UpdateActionCommand });
```

Update the class doc comment's layout sentence to include the update item.

- [ ] **Step 4: Run both classes and build**

Run the `TrayViewModelTests` and `TrayAdapterTests` filters (expected: passed) and `dotnet build src/Capacitor.App/Capacitor.App.csproj` (0 warnings).

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/ViewModels/TrayModels.cs src/Capacitor.App/ViewModels/TrayViewModel.cs src/Capacitor.App/Views/TrayMenuBuilder.cs test/Capacitor.App.Tests.Unit/TrayViewModelTests.cs test/Capacitor.App.Tests.Unit/TrayAdapterTests.cs
git commit -m "Add the tray update item driven by the coordinator"
```

---

### Task 10: Apply the update last in the shutdown sequence

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (`DisposeUiThenConfirmShutdownAsync` ~line 1357, `DisposeAndConfirmShutdownAsync` ~line 1390)
- Test: `test/Capacitor.App.Tests.Unit/AppStartupTests.cs`

**Interfaces:**
- Produces: `internal static Task App.DisposeAndConfirmShutdownAsync(Func<ValueTask>? disposeAsync, Action markConfirmed, IClassicDesktopStyleApplicationLifetime desktop, int exitCode, Action? applyOnExit = null)` and the same trailing parameter on `DisposeUiThenConfirmShutdownAsync`. Order inside the `finally`: `markConfirmed` → `applyOnExit` → `TryShutdown`.

- [ ] **Step 1: Write the failing test**

Append to `AppStartupTests.cs` beside the other `DisposeAndConfirmShutdownAsync` tests:

```csharp
    /// The updater's own wait is bounded, so it must be launched after every disposal has run and
    /// immediately before the platform shutdown call — never earlier.
    [Test]
    public async Task DisposeAndConfirmShutdownAsync_applies_the_update_after_disposal_and_before_shutdown() {
        var (desktop, fake) = FakeClassicDesktopLifetime.Create();
        var order = new List<string>();

        await AppUnderTest.DisposeAndConfirmShutdownAsync(
            disposeAsync: () => { order.Add("dispose"); return ValueTask.CompletedTask; },
            markConfirmed: () => order.Add("confirm"),
            desktop,
            exitCode: 0,
            applyOnExit: () => order.Add(fake.ShutdownCalls.Count == 0 ? "apply-before-shutdown" : "apply-after-shutdown"));

        await Assert.That(order).IsEquivalentTo(["dispose", "confirm", "apply-before-shutdown"], CollectionOrdering.Matching);
        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([0], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DisposeAndConfirmShutdownAsync_still_shuts_down_when_apply_throws() {
        var (desktop, fake) = FakeClassicDesktopLifetime.Create();

        await AppUnderTest.DisposeAndConfirmShutdownAsync(
            disposeAsync: null, markConfirmed: () => { }, desktop, exitCode: 0,
            applyOnExit: () => throw new InvalidOperationException("apply-boom"));

        await Assert.That(fake.ShutdownCalls).IsEquivalentTo([0], CollectionOrdering.Matching);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/AppStartupTests/*"`
Expected: build error (no `applyOnExit` parameter).

- [ ] **Step 3: Implement**

Change both signatures to take a trailing `Action? applyOnExit = null` (`DisposeUiThenConfirmShutdownAsync` forwards it) and make the `finally` of `DisposeAndConfirmShutdownAsync`:

```csharp
        } finally {
            markConfirmed();
            // Last, after every disposal: the updater waits at most 60 s for this process to exit,
            // and the quiesce above can use all of that on its own.
            try {
                applyOnExit?.Invoke();
            } catch (Exception ex) {
                Console.Error.WriteLine($"kcap app failed to hand the pending update to the updater: {ex}");
            }
            desktop.TryShutdown(exitCode);
        }
```

The composition-root call in `DisposeAndShutdownAsync` is wired in Task 13; leave it for now.

- [ ] **Step 4: Run the class and build**

Expected: passed, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/App.axaml.cs test/Capacitor.App.Tests.Unit/AppStartupTests.cs
git commit -m "Hand a pending update to the updater as the last shutdown step"
```

---

### Task 11: Lifecycle controller — post-update skew hold and version revalidation

**Files:**
- Modify: `src/Capacitor.App/Services/DaemonLifecycleController.cs` (constructor ~line 100, `RunSkewCheckAsync` ~line 412)
- Test: `test/Capacitor.App.Tests.Unit/DaemonLifecycleControllerTests.cs` (`Harness` + new tests)

**Interfaces:**
- Produces: constructor gains a trailing `bool holdSkewForUpdate = false`; `internal static readonly TimeSpan DaemonLifecycleController.SkewHoldAfterUpdate = TimeSpan.FromSeconds(45)`.

- [ ] **Step 1: Extend the harness and write the failing tests**

In the `Harness` class, add a constructor parameter `bool holdSkewForUpdate = false` and pass it as the controller's last argument. Then add tests in the `§4.3 skew` region:

```csharp
    [Test]
    public async Task Skew_hold_after_update_defers_the_prompt_until_the_window_closes() {
        await using var h = new Harness(holdSkewForUpdate: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(
            unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/opt/kcap/kcapd"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushSnapshot("0.9.0");
        h.PushConnected();
        await Task.Delay(50);
        await Assert.That(h.Surface.Prompts).IsEmpty();

        h.Clock.Advance(DaemonLifecycleController.SkewHoldAfterUpdate);

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the deferred skew prompt");
        await Assert.That(h.Surface.Prompts[0].DaemonVersion).IsEqualTo("0.9.0");
    }

    [Test]
    public async Task Skew_hold_ends_quietly_when_the_daemon_restarted_itself() {
        await using var h = new Harness(holdSkewForUpdate: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushSnapshot("0.9.0");
        h.PushConnected();
        h.PushSnapshot("1.0.0"); // the daemon's own restart-after-update landed during the hold
        h.Clock.Advance(DaemonLifecycleController.SkewHoldAfterUpdate);
        await Task.Delay(50);

        await Assert.That(h.Surface.Prompts).IsEmpty();
    }

    [Test]
    public async Task Skew_hold_keeps_incompatible_hello_evidence_and_prompts_once() {
        await using var h = new Harness(holdSkewForUpdate: true);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushUnreachable(reason: "daemon_incompatible", daemonVersion: "0.9");
        await Task.Delay(50);
        await Assert.That(h.Surface.Prompts).IsEmpty();

        h.Clock.Advance(DaemonLifecycleController.SkewHoldAfterUpdate);

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the deferred incompatible prompt");
        await Assert.That(h.Surface.Prompts[0].DaemonVersion).IsEqualTo("0.9");
        await Task.Delay(50);
        await Assert.That(h.Surface.Prompts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Skew_without_hold_prompts_immediately() {
        await using var h = new Harness(holdSkewForUpdate: false);
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(unitPresent: true, state: "installed"));
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushSnapshot("2.0.0");
        h.PushConnected();

        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the immediate skew prompt");
    }

    [Test]
    public async Task Skew_accept_after_the_daemon_caught_up_is_stale_and_retracts_the_claim() {
        await using var h = new Harness();
        h.Cli.StatusBehavior = _ => Task.FromResult<ServiceSnapshot?>(Snap(
            unitPresent: true, state: "installed", installBinaryPath: "/opt/kcap/kcapd", binaryPath: "/opt/kcap/kcapd"));
        var answer = new TaskCompletionSource<bool>();
        h.Surface.ConfirmBehavior = (_, _) => answer.Task;
        h.Start();
        await WaitUntilAsync(() => h.Cli.VersionCallCount == 1, what: "the version cache");

        h.PushSnapshot("2.0.0");
        h.PushConnected();
        await WaitUntilAsync(() => h.Surface.Prompts.Count == 1, what: "the skew prompt");

        h.PushSnapshot("1.0.0"); // the daemon restarted itself while the dialog was open
        answer.SetResult(true);
        await WaitUntilAsync(() => h.Surface.StatusMessages.Count == 1, what: "the stale-consent status");

        await Assert.That(h.Lane.Requests).IsEmpty();
        var state = await h.Store.LoadAsync();
        await Assert.That(state.DeclinedTakeoverPairs ?? []).IsEmpty();
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/DaemonLifecycleControllerTests/*"`
Expected: build error (no `holdSkewForUpdate`).

- [ ] **Step 3: Implement the hold**

Fields and the constructor:

```csharp
    internal static readonly TimeSpan SkewHoldAfterUpdate = TimeSpan.FromSeconds(45);

    DateTimeOffset? _skewHoldUntil;
    string?         _heldSkewVersion;
    bool            _holdRecheckScheduled;
```

Add the trailing constructor parameter `bool holdSkewForUpdate = false` and, in the body:

```csharp
        // After an update relaunch the daemon restarts itself within a poll interval once idle;
        // offering a restart before that would ask for something already under way.
        _skewHoldUntil = holdSkewForUpdate ? time.GetUtcNow() + SkewHoldAfterUpdate : null;
```

In `RunSkewCheckAsync`, after `if (_cli.CliPath is null) return;` insert `if (TryHoldSkew(daemonVersion)) return;`, and add:

```csharp
    /// During the post-update hold every trigger is retained rather than dropped — an incompatible
    /// daemon never produces a snapshot, and the attach client will not repeat an identical event.
    bool TryHoldSkew(string? daemonVersion) {
        lock (_lock) {
            if (_skewHoldUntil is not { } until || _time.GetUtcNow() >= until) return false;

            _heldSkewVersion = daemonVersion;
            if (_holdRecheckScheduled) return true;

            _holdRecheckScheduled = true;
            _ = RunHeldSkewRecheckAsync(until - _time.GetUtcNow());
            return true;
        }
    }

    async Task RunHeldSkewRecheckAsync(TimeSpan delay) {
        try {
            await Task.Delay(delay, _time, _lifetime.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }

        string? held;
        lock (_lock) {
            _skewHoldUntil = null;
            held = _heldSkewVersion;
        }

        // A snapshot that arrived during the hold is fresher than the held evidence: an idle
        // daemon has restarted by now and its new version ends the matter without a dialog.
        await RunSkewCheckAsync(LatestSnapshotVersion() ?? held).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Implement the version revalidation**

In `RunSkewCheckAsync`, change the `ConfirmAndTakeoverAsync` call's `revalidate` argument to:

```csharp
                prompt, revalidate: fresh => ClassifyTakeover(fresh) == kind && !VersionsNowEqual(),
```

and add:

```csharp
    // The daemon may have restarted itself onto the new binary while the dialog was open — an
    // accept then has nothing to do, and must not replace a unit that is already current.
    bool VersionsNowEqual() => LatestSnapshotVersion() is { } current && current == CliVersion;
```

- [ ] **Step 5: Run the class and build**

Run the `DaemonLifecycleControllerTests` filter (expected: all passed, including the pre-existing skew tests) and `dotnet build src/Capacitor.App/Capacitor.App.csproj` (0 warnings).

- [ ] **Step 6: Commit**

```bash
git add src/Capacitor.App/Services/DaemonLifecycleController.cs test/Capacitor.App.Tests.Unit/DaemonLifecycleControllerTests.cs
git commit -m "Hold the skew dialog after an update and treat a caught-up daemon as stale"
```

---

### Task 12: Install-location classification and Move to Applications

**Files:**
- Create: `src/Capacitor.App/Services/InstallLocation.cs`, `src/Capacitor.App/Services/ApplicationsMover.cs`
- Test: `test/Capacitor.App.Tests.Unit/InstallLocationTests.cs`, `test/Capacitor.App.Tests.Unit/ApplicationsMoverTests.cs`

**Interfaces:**
- Consumes: `Capacitor.Cli.Core.IProcessRunner`, `RunOptions`, `ProcessResult`.
- Produces:
  - `public enum InstallLocationKind { NotABundle, Applications, UserApplications, DmgVolume, Translocated, Other }`
  - `public static class InstallLocation { static string? BundleRoot(string? processPath); static InstallLocationKind Classify(string? bundleRoot, string home); static bool Passes(InstallLocationKind kind); }`
  - `public sealed record MoveOutcome(bool Moved, string? InstalledPath, string? Error);`
  - `public sealed partial class ApplicationsMover(IProcessRunner runner, Func<string, string, bool> promote, string applicationsDir = "/Applications") { Task<MoveOutcome> MoveAsync(string bundleRoot, CancellationToken ct); static bool PromoteExclusive(string from, string to); }`

- [ ] **Step 1: Write the failing tests**

`InstallLocationTests.cs`:

```csharp
using Capacitor.App.Services;

namespace Capacitor.App.Tests.Unit;

public class InstallLocationTests {
    const string Home = "/Users/dev";

    [Test]
    public async Task BundleRoot_is_the_app_ancestor_of_the_executable() {
        await Assert.That(InstallLocation.BundleRoot("/Applications/Kurrent Capacitor.app/Contents/MacOS/Kurrent Capacitor"))
            .IsEqualTo("/Applications/Kurrent Capacitor.app");
    }

    [Test]
    [Arguments("/Users/dev/src/kcap-cli/src/Capacitor.App/bin/Debug/net10.0/Kurrent Capacitor")]
    [Arguments("")]
    [Arguments(null)]
    public async Task BundleRoot_is_null_outside_a_bundle(string? path) {
        await Assert.That(InstallLocation.BundleRoot(path)).IsNull();
    }

    [Test]
    [Arguments("/Applications/Kurrent Capacitor.app", InstallLocationKind.Applications)]
    [Arguments("/Users/dev/Applications/Kurrent Capacitor.app", InstallLocationKind.UserApplications)]
    [Arguments("/Volumes/Kurrent Capacitor/Kurrent Capacitor.app", InstallLocationKind.DmgVolume)]
    [Arguments("/private/var/folders/xy/T/AppTranslocation/1F2E-3D4C/d/Kurrent Capacitor.app", InstallLocationKind.Translocated)]
    [Arguments("/Users/dev/Downloads/Kurrent Capacitor.app", InstallLocationKind.Other)]
    [Arguments(null, InstallLocationKind.NotABundle)]
    public async Task Classify_recognises_each_shape(string? root, InstallLocationKind expected) {
        await Assert.That(InstallLocation.Classify(root, Home)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(InstallLocationKind.NotABundle, true)]
    [Arguments(InstallLocationKind.Applications, true)]
    [Arguments(InstallLocationKind.UserApplications, true)]
    [Arguments(InstallLocationKind.DmgVolume, false)]
    [Arguments(InstallLocationKind.Translocated, false)]
    [Arguments(InstallLocationKind.Other, false)]
    public async Task Passes_only_for_installed_or_unbundled(InstallLocationKind kind, bool expected) {
        await Assert.That(InstallLocation.Passes(kind)).IsEqualTo(expected);
    }
}
```

`ApplicationsMoverTests.cs`:

```csharp
using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

/// The fake runner stands in for `ditto`: it materialises whatever the test says a copy produced,
/// at the staging path the mover chose. Promotion is injected so the rename semantics stay a
/// macOS-only test below; here it is a plain Directory.Move.
public class ApplicationsMoverTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class FakeDitto(Action<string> populate) : IProcessRunner {
        public int Calls;

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Calls++;
            populate(args[1]);
            return Task.FromResult(new ProcessResult(0, "", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct) =>
            throw new NotImplementedException();
    }

    static void CompleteBundle(string root) {
        Directory.CreateDirectory(Path.Combine(root, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(root, "Contents", "Info.plist"), "<plist/>");
        File.WriteAllText(Path.Combine(root, "Contents", "MacOS", "Kurrent Capacitor"), "exe");
    }

    static bool MovePromote(string from, string to) {
        if (Directory.Exists(to)) return false;
        Directory.Move(from, to);
        return true;
    }

    [Test]
    public async Task Complete_copy_is_promoted_and_nothing_is_left_staged() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsTrue();
        await Assert.That(outcome.InstalledPath).IsEqualTo(Path.Combine(apps, "Kurrent Capacitor.app"));
        await Assert.That(Directory.GetDirectories(apps).Length).IsEqualTo(1);
    }

    [Test]
    public async Task Incomplete_copy_is_removed_and_reported() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        var mover = new ApplicationsMover(new FakeDitto(root => Directory.CreateDirectory(Path.Combine(root, "Contents"))), MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("incomplete");
        await Assert.That(Directory.GetDirectories(apps)).IsEmpty();
    }

    [Test]
    public async Task Existing_destination_refuses_before_copying() {
        var apps = Tmp.CreateDir("Applications");
        Tmp.CreateDir("Applications/Kurrent Capacitor.app");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        var ditto = new FakeDitto(CompleteBundle);
        var mover = new ApplicationsMover(ditto, MovePromote, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("already exists");
        await Assert.That(ditto.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Destination_appearing_mid_move_fails_promotion_and_cleans_staging() {
        var apps = Tmp.CreateDir("Applications");
        var source = Tmp.CreateDir("Downloads/Kurrent Capacitor.app");
        var mover = new ApplicationsMover(new FakeDitto(CompleteBundle), (_, _) => false, apps);

        var outcome = await mover.MoveAsync(source, CancellationToken.None);

        await Assert.That(outcome.Moved).IsFalse();
        await Assert.That(outcome.Error).Contains("appeared");
        await Assert.That(Directory.GetDirectories(apps)).IsEmpty();
    }

    /// renamex_np is macOS-only; elsewhere this test is a no-op. An EMPTY existing destination is
    /// the case a plain rename would silently replace.
    [Test]
    public async Task PromoteExclusive_refuses_an_empty_existing_destination() {
        if (!OperatingSystem.IsMacOS()) return;
        var from = Tmp.CreateDir("staging");
        var to = Tmp.CreateDir("target");

        await Assert.That(ApplicationsMover.PromoteExclusive(from, to)).IsFalse();
        await Assert.That(Directory.Exists(from)).IsTrue();
    }

    [Test]
    public async Task PromoteExclusive_moves_when_the_destination_is_absent() {
        if (!OperatingSystem.IsMacOS()) return;
        var from = Tmp.CreateDir("staging");
        var to = Tmp.PathTo("target");

        await Assert.That(ApplicationsMover.PromoteExclusive(from, to)).IsTrue();
        await Assert.That(Directory.Exists(to)).IsTrue();
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/InstallLocationTests/*"`
Expected: build error.

- [ ] **Step 3: Implement**

`InstallLocation.cs` (string operations only, so the classification is identical on every CI leg):

```csharp
namespace Capacitor.App.Services;

public enum InstallLocationKind { NotABundle, Applications, UserApplications, DmgVolume, Translocated, Other }

/// Where the running bundle lives. The shim symlink and the LaunchAgent bake the CLI's path, and
/// the updater cannot swap a bundle on a read-only volume, so only an installed copy may proceed.
public static class InstallLocation {
    public static string? BundleRoot(string? processPath) {
        if (string.IsNullOrEmpty(processPath)) return null;
        var index = processPath.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        return index > 0 ? processPath[..(index + 4)] : null;
    }

    public static InstallLocationKind Classify(string? bundleRoot, string home) {
        if (bundleRoot is null) return InstallLocationKind.NotABundle;

        var root = bundleRoot.TrimEnd('/');
        var slash = root.LastIndexOf('/');
        var parent = slash <= 0 ? "/" : root[..slash];

        if (parent == "/Applications") return InstallLocationKind.Applications;
        if (parent == home.TrimEnd('/') + "/Applications") return InstallLocationKind.UserApplications;
        if (root.StartsWith("/Volumes/", StringComparison.Ordinal)) return InstallLocationKind.DmgVolume;
        if (root.Contains("/AppTranslocation/", StringComparison.Ordinal)) return InstallLocationKind.Translocated;

        return InstallLocationKind.Other;
    }

    public static bool Passes(InstallLocationKind kind) =>
        kind is InstallLocationKind.NotABundle or InstallLocationKind.Applications or InstallLocationKind.UserApplications;
}
```

`ApplicationsMover.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

public sealed record MoveOutcome(bool Moved, string? InstalledPath, string? Error);

/// Copies the bundle into a staging sibling on the same volume, verifies the copy, then promotes
/// it with a no-replace rename — a partial copy can never sit at the final path.
public sealed partial class ApplicationsMover(IProcessRunner runner, Func<string, string, bool> promote, string applicationsDir = "/Applications") {
    static readonly TimeSpan CopyTimeout = TimeSpan.FromMinutes(2);

    public async Task<MoveOutcome> MoveAsync(string bundleRoot, CancellationToken ct) {
        var name = Path.GetFileName(bundleRoot.TrimEnd('/'));
        var target = Path.Combine(applicationsDir, name);
        if (Directory.Exists(target) || File.Exists(target))
            return new MoveOutcome(false, target, $"{name} already exists in {applicationsDir}. Open that copy instead.");

        var staging = Path.Combine(applicationsDir, $"{name}.staging-{Guid.NewGuid():N}");
        try {
            var copy = await runner.RunAsync("ditto", [bundleRoot, staging], new RunOptions(Timeout: CopyTimeout), ct).ConfigureAwait(false);
            if (copy.ExitCode != 0) return Fail(staging, $"Copying failed: {copy.Stderr.Trim()}");

            if (!File.Exists(Path.Combine(staging, "Contents", "Info.plist")) ||
                !File.Exists(Path.Combine(staging, "Contents", "MacOS", "Kurrent Capacitor")))
                return Fail(staging, "The copy is incomplete.");

            if (!promote(staging, target))
                return Fail(staging, $"{name} appeared in {applicationsDir} while copying. Open that copy instead.");

            return new MoveOutcome(true, target, null);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return Fail(staging, ex.Message);
        }
    }

    static MoveOutcome Fail(string staging, string error) {
        try {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        } catch {
            // the staging copy is the only thing that could be left behind; reporting the move failure matters more
        }
        return new MoveOutcome(false, null, error);
    }

    /// A plain rename replaces an EMPTY existing directory; RENAME_EXCL fails on any existing entry.
    [SupportedOSPlatform("macos")]
    public static bool PromoteExclusive(string from, string to) => renamex_np(from, to, RENAME_EXCL) == 0;

    const uint RENAME_EXCL = 0x4;

    [LibraryImport("libc", EntryPoint = "renamex_np", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int renamex_np(string from, string to, uint flags);
}
```

If the analyzer objects to the `catch { }` without a variable, name it `catch (Exception)` and keep the comment.

- [ ] **Step 4: Run both classes and build**

Expected: passed on macOS (the two `PromoteExclusive` tests return early elsewhere), 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.App/Services/InstallLocation.cs src/Capacitor.App/Services/ApplicationsMover.cs test/Capacitor.App.Tests.Unit/InstallLocationTests.cs test/Capacitor.App.Tests.Unit/ApplicationsMoverTests.cs
git commit -m "Classify the bundle's install location and move it to Applications safely"
```

---

### Task 13: App composition — guard, pending apply, coordinator, tray and shutdown wiring

**Files:**
- Modify: `src/Capacitor.App/App.axaml.cs` (`StartAsync` ~line 154, `BuildDaemonGraph` ~lines 255–380, `BuildLifecycleController` ~line 795, `DisposeAndShutdownAsync` ~line 1256)

**Interfaces:**
- Consumes: `Program.UpdateRelaunch` (Task 4), `IAppUpdater`/`VelopackAppUpdater`/`InertAppUpdater` (Task 6), `UpdateCoordinator` (Task 8), `TrayViewModel` update parameters (Task 9), `DisposeAndConfirmShutdownAsync(applyOnExit)` (Task 10), `holdSkewForUpdate` (Task 11), `InstallLocation`/`ApplicationsMover` (Task 12).
- Produces: `internal static Window App.BuildInstallLocationWindow(InstallLocationKind kind, Func<Task<MoveOutcome>> move, Action quit)`.

- [ ] **Step 1: Add the fields and the updater factory**

Near the other service fields in `App.axaml.cs` add:

```csharp
    IAppUpdater _updater = InertAppUpdater.Instance;
    UpdateCoordinator? _updates;
```

and a factory:

```csharp
    // Outside a packed bundle Velopack reports not-installed and the coordinator stays inert; a
    // constructor failure degrades the same way rather than taking startup down.
    static IAppUpdater CreateUpdater() {
        try {
            return new VelopackAppUpdater(Environment.GetEnvironmentVariable);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap app: updater unavailable: {ex.Message}");
            return InertAppUpdater.Instance;
        }
    }
```

Add `using Capacitor.App.Services.Update;` to the file.

- [ ] **Step 2: Guard and pending apply at the top of `StartAsync`**

As the first statements inside the `try` of `StartAsync`, before the lane is constructed:

```csharp
            if (await RunInstallLocationGuardAsync(desktop)) return; // the guard window owns the rest of this run
            _updater = CreateUpdater();
            if (UpdateCoordinator.TryApplyPendingAtStartup(_updater, Program.UpdateRelaunch)) return; // the process is being replaced
```

Add the guard:

```csharp
    // macOS only: elsewhere there is no bundle. Returns true when a window was shown and startup
    // must stop here; the window's buttons end the process.
    async Task<bool> RunInstallLocationGuardAsync(IClassicDesktopStyleApplicationLifetime desktop) {
        if (!OperatingSystem.IsMacOS()) return false;
        var root = InstallLocation.BundleRoot(Environment.ProcessPath);
        var kind = InstallLocation.Classify(root, UserHome.FromEnvironment().Path);
        if (InstallLocation.Passes(kind)) return false;

        var mover = new ApplicationsMover(new ProcessRunner(), ApplicationsMover.PromoteExclusive);
        var window = BuildInstallLocationWindow(
            kind,
            move: async () => {
                var outcome = await mover.MoveAsync(root!, _shutdown.Token);
                if (outcome.Moved) {
                    Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-n", outcome.InstalledPath! }, UseShellExecute = false });
                    desktop.Shutdown(0);
                }
                return outcome;
            },
            quit: () => desktop.Shutdown(0));
        desktop.MainWindow = window;
        window.Show();
        await Task.CompletedTask;
        return true;
    }

    internal static Window BuildInstallLocationWindow(InstallLocationKind kind, Func<Task<MoveOutcome>> move, Action quit) {
        var where = kind switch {
            InstallLocationKind.DmgVolume   => "It is running from the disk image.",
            InstallLocationKind.Translocated => "It is running from a temporary location.",
            _                                => "It is not in the Applications folder.",
        };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed, IsVisible = false };
        var moveButton = new Button { Content = "Move to Applications", IsDefault = true };
        var quitButton = new Button { Content = "Quit", IsCancel = true };
        moveButton.Click += async (_, _) => {
            moveButton.IsEnabled = false;
            var outcome = await move();
            if (!outcome.Moved) {
                error.Text = outcome.Error;
                error.IsVisible = true;
                moveButton.IsEnabled = true;
            }
        };
        quitButton.Click += (_, _) => quit();

        return new Window {
            Title = "Kurrent Capacitor",
            Icon = ProductIcon.WindowIcon,
            Width = 460,
            Height = 220,
            CanResize = false,
            Content = new StackPanel {
                Margin = new Thickness(24),
                Spacing = 12,
                Children = {
                    new TextBlock { Text = "Move Kurrent Capacitor to your Applications folder to continue.", FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"{where} The command-line tool and the background service need a permanent location.", TextWrapping = TextWrapping.Wrap },
                    error,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { quitButton, moveButton } },
                },
            },
        };
    }
```

Add the usings the snippet needs (`System.Diagnostics`, `Avalonia.Layout`, `Avalonia.Media`, `Capacitor.Cli.Core`) if not already present.

- [ ] **Step 3: Build the coordinator inside the daemon graph and wire the tray**

In `BuildDaemonGraph`, right after `consentFlip.Start(); _consentFlip = consentFlip;`:

```csharp
        // After the graph, never during the wizard: the first check waits its own delay, and every
        // dialog goes through the same serialized surface as the skew and shim dialogs.
        _updates = new UpdateCoordinator(_updater, lifecycleSurface, TimeProvider.System, quit: () => desktop.TryShutdown(), _shutdown.Token);
        _updates.Start();
```

Extend the `TrayViewModel` construction at the end of `BuildDaemonGraph` with `updateMenu: _updates.MenuItem, updateAction: _updates.RunMenuActionAsync`.

- [ ] **Step 4: Pass the skew hold to the lifecycle controller**

In `BuildLifecycleController`, pass `holdSkewForUpdate: Program.UpdateRelaunch` as the `DaemonLifecycleController` constructor's last argument.

- [ ] **Step 5: Apply on exit**

In `DisposeAndShutdownAsync`, change the `DisposeUiThenConfirmShutdownAsync(...)` call to pass `applyOnExit: () => _updates?.ApplyPendingOnExit()` after `_exitCode`. In `HandleStartupFailureAsync`'s path nothing changes: no update can be pending there.

- [ ] **Step 6: Build, run the full app suite, and smoke from source**

Run: `dotnet build src/Capacitor.App/Capacitor.App.csproj` (0 warnings), then `TMPDIR=/private/tmp dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj` (expected: green). Then `dotnet run --project src/Capacitor.App/Capacitor.App.csproj` from a terminal: the app starts normally (no guard — not a bundle), the tray shows no update item (updater inert), and Cmd+Q exits cleanly.

- [ ] **Step 7: Commit**

```bash
git add src/Capacitor.App/App.axaml.cs
git commit -m "Wire the install-location guard, updater and tray item into the app"
```

---

### Task 14: Shell libraries and the shell-test runner

**Files:**
- Create: `scripts/lib/semver.sh`, `scripts/lib/semver.test.sh`, `scripts/lib/hash.sh`, `scripts/run-shell-tests.sh`
- Modify: `.github/workflows/ci.yml` (`build-and-test` job, after "Run npm wrapper tests")

**Interfaces:**
- Produces: `semver_cmp <a> <b>` prints `-1|0|1` (SemVer 2 precedence, build metadata ignored); `semver_strip_build <v>`; `semver_is_prerelease <v>` (exit 0 when prerelease); `sha256_of <file>` prints the lowercase hex digest on macOS and Linux.
- Produces: `scripts/run-shell-tests.sh` runs every `scripts/*.test.sh` and `scripts/lib/*.test.sh` and fails if any fails.

- [ ] **Step 1: Write the failing semver test**

`scripts/lib/semver.test.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=semver.sh
source "$here/semver.sh"

fail=0
cmp() {
  local got; got="$(semver_cmp "$1" "$2")"
  if [ "$got" != "$3" ]; then echo "FAIL: semver_cmp '$1' '$2' -> '$got' (want '$3')"; fail=1; fi
}
cmp "0.12.0"          "0.12.0"          0
cmp "0.12.1"          "0.12.0"          1
cmp "0.11.38"         "0.12.0-beta.1"   -1
cmp "0.12.0-beta.1"   "0.12.0"          -1   # release above its own prereleases
cmp "0.12.0-beta.2"   "0.12.0-beta.1"   1
cmp "0.12.0-beta.10"  "0.12.0-beta.9"   1    # numeric identifiers compare numerically
cmp "0.12.0-beta.1.5" "0.12.0-beta.1"   1    # more identifiers rank higher (MinVer height)
cmp "0.12.0-alpha.9"  "0.12.0-beta.1"   -1   # alphanumerics compare lexically
cmp "0.12.0-beta.0"   "0.12.0-beta.1"   -1
cmp "0.12.0+abc"      "0.12.0+def"      0    # build metadata ignored
cmp "0.13.0-beta.1"   "0.12.2"          1

pre() {
  if semver_is_prerelease "$1"; then got=yes; else got=no; fi
  if [ "$got" != "$2" ]; then echo "FAIL: semver_is_prerelease '$1' -> $got (want $2)"; fail=1; fi
}
pre "0.12.0-beta.1" yes
pre "0.12.0"        no
pre "0.12.0+sha"    no

[ "$(semver_strip_build "1.2.3+build.5")" = "1.2.3" ] || { echo "FAIL: strip_build"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
```

Run: `bash scripts/lib/semver.test.sh`. Expected: fails (`semver.sh` missing).

- [ ] **Step 2: Implement the libraries**

`scripts/lib/semver.sh`:

```bash
#!/usr/bin/env bash
# SemVer 2 precedence for the shapes MinVer produces (core, optional prerelease identifiers,
# optional +build). Sourced by the release scripts; semver.test.sh pins every rule.

semver_strip_build() { printf '%s' "${1%%+*}"; }

# Exit 0 when the version carries a prerelease part (build metadata ignored).
semver_is_prerelease() {
  local v; v="$(semver_strip_build "$1")"
  [[ "$v" == *-* ]]
}

# Prints -1, 0 or 1 for a<b, a=b, a>b.
semver_cmp() {
  local a b acore bcore apre="" bpre=""
  a="$(semver_strip_build "$1")"; b="$(semver_strip_build "$2")"
  acore="${a%%-*}"; bcore="${b%%-*}"
  [ "$acore" != "$a" ] && apre="${a#*-}"
  [ "$bcore" != "$b" ] && bpre="${b#*-}"

  local a1 a2 a3 b1 b2 b3
  IFS=. read -r a1 a2 a3 <<<"$acore"
  IFS=. read -r b1 b2 b3 <<<"$bcore"
  local x y
  for pair in "$a1:$b1" "$a2:$b2" "$a3:$b3"; do
    x="${pair%%:*}"; y="${pair##*:}"
    if (( x < y )); then echo -1; return; fi
    if (( x > y )); then echo 1; return; fi
  done

  if [ -z "$apre" ] && [ -z "$bpre" ]; then echo 0; return; fi
  if [ -z "$apre" ]; then echo 1; return; fi
  if [ -z "$bpre" ]; then echo -1; return; fi

  local -a ai bi
  IFS=. read -ra ai <<<"$apre"
  IFS=. read -ra bi <<<"$bpre"
  local n=${#ai[@]}; (( ${#bi[@]} > n )) && n=${#bi[@]}
  local i
  for ((i = 0; i < n; i++)); do
    x="${ai[i]-}"; y="${bi[i]-}"
    if [ -z "$x" ]; then echo -1; return; fi
    if [ -z "$y" ]; then echo 1; return; fi
    if [[ "$x" =~ ^[0-9]+$ && "$y" =~ ^[0-9]+$ ]]; then
      if (( x < y )); then echo -1; return; fi
      if (( x > y )); then echo 1; return; fi
    elif [[ "$x" =~ ^[0-9]+$ ]]; then echo -1; return
    elif [[ "$y" =~ ^[0-9]+$ ]]; then echo 1; return
    else
      if [[ "$x" < "$y" ]]; then echo -1; return; fi
      if [[ "$x" > "$y" ]]; then echo 1; return; fi
    fi
  done
  echo 0
}
```

`scripts/lib/hash.sh`:

```bash
#!/usr/bin/env bash
# One sha256 spelling for macOS (shasum) and Linux (sha256sum).
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
  else shasum -a 256 "$1" | cut -d' ' -f1
  fi
}
```

`scripts/run-shell-tests.sh`:

```bash
#!/usr/bin/env bash
# Runs every scripts/*.test.sh and scripts/lib/*.test.sh; any failure fails the run.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
status=0
for t in "$here"/*.test.sh "$here"/lib/*.test.sh; do
  [ -e "$t" ] || continue
  printf '%s: ' "${t#"$here"/}"
  if bash "$t"; then :; else status=1; fi
done
exit "$status"
```

Run: `chmod +x scripts/run-shell-tests.sh scripts/lib/*.sh && bash scripts/run-shell-tests.sh`. Expected: every existing test and the new one print `ok`, exit 0.

- [ ] **Step 3: Run the shell tests in CI**

In `.github/workflows/ci.yml`, after the "Run npm wrapper tests" step:

```yaml
      - name: Run shell script tests
        if: runner.os == 'Linux'
        run: bash scripts/run-shell-tests.sh
```

- [ ] **Step 4: Commit**

```bash
git add scripts/lib scripts/run-shell-tests.sh .github/workflows/ci.yml
git commit -m "Add the semver and hash shell libraries and run shell tests in CI"
```

---

### Task 15: `assert-app-cli-version.sh`

**Files:**
- Create: `scripts/assert-app-cli-version.sh`, `scripts/assert-app-cli-version.test.sh`

**Interfaces:**
- Consumes: `scripts/lib/semver.sh`.
- Produces: `assert-app-cli-version.sh <kcap-binary> <expected-version> [floor]` — exit 0 when the binary prints exactly one line `kcap <version>` equal to the expected version (build metadata ignored) and at or above the floor (default: read from `KcapCliCompatibility.cs`); non-zero with a reason otherwise.

- [ ] **Step 1: Write the failing test**

`scripts/assert-app-cli-version.test.sh` uses a fake `kcap` that echoes whatever `FAKE_VERSION_OUTPUT` holds:

```bash
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/assert-app-cli-version.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
fake="$tmp/kcap"
printf '#!/usr/bin/env bash\nprintf "%%s\\n" "$FAKE_VERSION_OUTPUT"\n' > "$fake"; chmod +x "$fake"

fail=0
assert() {
  local output="$1" expected="$2" want_rc="$3"
  local rc
  set +e
  FAKE_VERSION_OUTPUT="$output" bash "$sh" "$fake" "$expected" "0.12.0-beta.1" >/dev/null 2>&1
  rc=$?
  set -e
  if [ "$rc" != "$want_rc" ]; then echo "FAIL: output='$output' expected='$expected' -> rc=$rc (want $want_rc)"; fail=1; fi
}
assert "kcap 0.12.0-beta.2+abc"        "0.12.0-beta.2"        0   # match, build metadata ignored
assert "kcap 0.12.0-beta.1.7+abc"      "0.12.0-beta.1.7"      0   # height-suffixed beta is above the floor
assert "kcap 0.12.0-beta.2"            "0.12.0-beta.3"        1   # version mismatch
assert "kcap 0.11.38"                  "0.11.38"              1   # below the floor
assert "kcap 0.12.0-beta.0"            "0.12.0-beta.0"        1   # a prerelease of the floor is below it
assert $'kcap 0.12.0-beta.2\nextra'    "0.12.0-beta.2"        1   # more than one line
assert "0.12.0-beta.2"                 "0.12.0-beta.2"        1   # missing prefix
[ "$fail" -eq 0 ] && echo "ok" || exit 1
```

Run: `bash scripts/assert-app-cli-version.test.sh`. Expected: fails (script missing).

- [ ] **Step 2: Implement**

`scripts/assert-app-cli-version.sh`:

```bash
#!/usr/bin/env bash
# Asserts a bundled kcap reports the expected version and satisfies the app's CLI floor.
# Usage: assert-app-cli-version.sh <kcap-binary> <expected-version> [floor]
# The floor defaults to KcapCliCompatibility.Floor, read from the app source.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/semver.sh
source "$here/lib/semver.sh"

kcap="${1:?usage: assert-app-cli-version.sh <kcap-binary> <expected-version> [floor]}"
expected="${2:?usage: assert-app-cli-version.sh <kcap-binary> <expected-version> [floor]}"
floor="${3:-}"
if [ -z "$floor" ]; then
  floor="$(grep -oE 'Floor = "[^"]+"' "$here/../src/Capacitor.App/Services/KcapCliCompatibility.cs" | sed -E 's/Floor = "([^"]+)"/\1/')"
fi
[ -n "$floor" ] || { echo "could not read KcapCliCompatibility.Floor" >&2; exit 1; }

output="$("$kcap" --version --no-update-check)"
lines="$(printf '%s\n' "$output" | grep -c . || true)"
[ "$lines" -eq 1 ] || { echo "expected one line from --version, got: $output" >&2; exit 1; }
[[ "$output" == kcap\ * ]] || { echo "unexpected --version output: $output" >&2; exit 1; }
actual="${output#kcap }"

if [ "$(semver_strip_build "$actual")" != "$(semver_strip_build "$expected")" ]; then
  echo "bundled kcap reports $actual, expected $expected" >&2; exit 1
fi
if [ "$(semver_cmp "$actual" "$floor")" -lt 0 ]; then
  echo "bundled kcap $actual is below the app's CLI floor $floor" >&2; exit 1
fi
echo "bundled kcap $actual matches $expected and satisfies floor $floor"
```

Run: `chmod +x scripts/assert-app-cli-version.sh && bash scripts/assert-app-cli-version.test.sh`. Expected: `ok`.

- [ ] **Step 3: Commit**

```bash
git add scripts/assert-app-cli-version.sh scripts/assert-app-cli-version.test.sh
git commit -m "Assert the bundled kcap's version and floor at package time"
```

---

### Task 16: `assert-bundle-digest.sh`

**Files:**
- Create: `scripts/assert-bundle-digest.sh`, `scripts/assert-bundle-digest.test.sh`

**Interfaces:**
- Consumes: `scripts/lib/hash.sh`.
- Produces: `assert-bundle-digest.sh <bundle.app> <daemon.sha256-file>` — exit 0 when `Contents/MacOS/kcap-daemon` hashes to the recorded digest AND `Contents/MacOS/kcap` embeds that digest as UTF-16LE and not the 64-zero placeholder.

- [ ] **Step 1: Write the failing test**

`scripts/assert-bundle-digest.test.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/assert-bundle-digest.sh"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

make_bundle() { # <dir> <daemon-content> <embedded-digest-or-empty>
  local app="$1"; mkdir -p "$app/Contents/MacOS"
  printf '%s' "$2" > "$app/Contents/MacOS/kcap-daemon"
  { printf 'prefix-bytes'; [ -n "$3" ] && python3 -c 'import sys; sys.stdout.buffer.write(sys.argv[1].encode("utf-16-le"))' "$3"; printf 'suffix'; } > "$app/Contents/MacOS/kcap"
}

fail=0
assert() { # <label> <want-rc> <bundle> <digest-file>
  local rc; set +e; bash "$sh" "$3" "$4" >/dev/null 2>&1; rc=$?; set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2)"; fail=1; fi
}

make_bundle "$tmp/good.app" "daemon-bytes" ""
digest="$(sha256_of "$tmp/good.app/Contents/MacOS/kcap-daemon")"
printf '%s\n' "$digest" > "$tmp/daemon.sha256"
make_bundle "$tmp/good.app" "daemon-bytes" "$digest"
assert "matching pair" 0 "$tmp/good.app" "$tmp/daemon.sha256"

make_bundle "$tmp/swapped.app" "different-daemon-bytes" "$digest"
assert "substituted daemon" 1 "$tmp/swapped.app" "$tmp/daemon.sha256"

placeholder="$(printf '0%.0s' $(seq 1 64))"
make_bundle "$tmp/placeholder.app" "daemon-bytes" "$placeholder"
assert "placeholder cli" 1 "$tmp/placeholder.app" "$tmp/daemon.sha256"

make_bundle "$tmp/unembedded.app" "daemon-bytes" ""
assert "cli without the digest" 1 "$tmp/unembedded.app" "$tmp/daemon.sha256"

[ "$fail" -eq 0 ] && echo "ok" || exit 1
```

Run: `bash scripts/assert-bundle-digest.test.sh`. Expected: fails (script missing).

- [ ] **Step 2: Implement**

`scripts/assert-bundle-digest.sh`:

```bash
#!/usr/bin/env bash
# Asserts the packed bundle still carries the daemon-digest invariant: the daemon inside hashes to
# the digest the release recorded, and the CLI inside embeds that digest (NativeAOT stores a C#
# string constant as UTF-16LE) rather than the all-zero placeholder a dev build carries.
# Usage: assert-bundle-digest.sh <bundle.app> <daemon.sha256-file>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"

bundle="${1:?usage: assert-bundle-digest.sh <bundle.app> <daemon.sha256-file>}"
digest_file="${2:?usage: assert-bundle-digest.sh <bundle.app> <daemon.sha256-file>}"
expected="$(tr -d '[:space:]' < "$digest_file")"
[[ "$expected" =~ ^[0-9a-f]{64}$ ]] || { echo "recorded digest is not 64 hex chars: '$expected'" >&2; exit 1; }

daemon="$bundle/Contents/MacOS/kcap-daemon"
cli="$bundle/Contents/MacOS/kcap"
[ -f "$daemon" ] || { echo "missing $daemon" >&2; exit 1; }
[ -f "$cli" ] || { echo "missing $cli" >&2; exit 1; }

actual="$(sha256_of "$daemon")"
[ "$actual" = "$expected" ] || { echo "packed daemon hashes to $actual, recorded digest is $expected" >&2; exit 1; }

placeholder="$(printf '0%.0s' $(seq 1 64))"
python3 - "$cli" "$expected" "$placeholder" <<'PY'
import sys
data = open(sys.argv[1], "rb").read()
expected = sys.argv[2].encode("utf-16-le")
placeholder = sys.argv[3].encode("utf-16-le")
if expected not in data:
    sys.stderr.write("packed kcap does not embed the recorded daemon digest\n"); sys.exit(1)
if placeholder in data:
    sys.stderr.write("packed kcap still embeds the placeholder digest\n"); sys.exit(1)
PY
echo "packed daemon matches the recorded digest and packed kcap embeds it"
```

Run: `chmod +x scripts/assert-bundle-digest.sh && bash scripts/assert-bundle-digest.test.sh`. Expected: `ok`.

- [ ] **Step 3: Commit**

```bash
git add scripts/assert-bundle-digest.sh scripts/assert-bundle-digest.test.sh
git commit -m "Assert the packed bundle keeps the daemon digest invariant"
```

---

### Task 17: `desktop-baseline.sh` and `promote-desktop-aliases.sh`

**Files:**
- Create: `scripts/desktop-baseline.sh`, `scripts/desktop-baseline.test.sh`, `scripts/promote-desktop-aliases.sh`, `scripts/promote-desktop-aliases.test.sh`

**Interfaces:**
- Consumes: `scripts/lib/semver.sh`; `jq`, `unzip`, `zip` (present on macOS and Ubuntu runners).
- Produces: `desktop-baseline.sh <releases-dir> <candidate-version>` — keeps the one `*-full.nupkg` only when its nuspec version is strictly below the candidate, deletes it otherwise, no-op when none; `promote-desktop-aliases.sh <releases.json> <candidate-version>` — prints `beta` when the candidate is the highest version in the manifest and `stable` when it is stable and the highest stable one; exit 1 if the manifest lacks the candidate.

- [ ] **Step 1: Write the failing tests**

`scripts/desktop-baseline.test.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/desktop-baseline.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

make_pkg() { # <dir> <version>  — the real download layout: one full nupkg, no manifest beside it
  local dir="$1" v="$2" work; work="$(mktemp -d)"
  printf '<?xml version="1.0"?><package><metadata><id>KurrentCapacitor</id><version>%s</version></metadata></package>' "$v" > "$work/KurrentCapacitor.nuspec"
  (cd "$work" && zip -q "$dir/KurrentCapacitor-$v-osx-arm64-full.nupkg" KurrentCapacitor.nuspec)
  rm -rf "$work"
}

fail=0
count() { find "$1" -name '*-full.nupkg' | wc -l | tr -d ' '; }

d="$tmp/lower"; mkdir -p "$d"; make_pkg "$d" "0.12.1"
bash "$sh" "$d" "0.12.2" >/dev/null
[ "$(count "$d")" = "1" ] || { echo "FAIL: lower baseline should be kept"; fail=1; }

d="$tmp/higher"; mkdir -p "$d"; make_pkg "$d" "0.13.0-beta.1"
bash "$sh" "$d" "0.12.2" >/dev/null
[ "$(count "$d")" = "0" ] || { echo "FAIL: higher baseline (beta above a stable patch) should be deleted"; fail=1; }

d="$tmp/equal"; mkdir -p "$d"; make_pkg "$d" "0.12.2"
bash "$sh" "$d" "0.12.2" >/dev/null
[ "$(count "$d")" = "0" ] || { echo "FAIL: equal baseline should be deleted"; fail=1; }

d="$tmp/empty"; mkdir -p "$d"
bash "$sh" "$d" "0.12.2" >/dev/null || { echo "FAIL: empty download should be a no-op"; fail=1; }

[ "$fail" -eq 0 ] && echo "ok" || exit 1
```

`scripts/promote-desktop-aliases.test.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/promote-desktop-aliases.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

manifest() { # <file> <versions...>  — Velopack's releases.<channel>.json shape
  local file="$1"; shift
  { printf '{"Assets":['; local first=1
    for v in "$@"; do
      [ $first -eq 1 ] || printf ','; first=0
      printf '{"PackageId":"KurrentCapacitor","Version":"%s","Type":"Full","FileName":"KurrentCapacitor-%s-osx-arm64-full.nupkg","SHA1":"","SHA256":"","Size":1}' "$v" "$v"
    done
    printf ']}'; } > "$file"
}

fail=0
assert() { # <label> <manifest> <candidate> <want-output> <want-rc>
  local got rc; set +e; got="$(bash "$sh" "$2" "$3" 2>/dev/null)"; rc=$?; set -e
  if [ "$got" != "$4" ] || [ "$rc" != "$5" ]; then echo "FAIL: $1 -> out='$got' rc=$rc (want '$4' rc=$5)"; fail=1; fi
}

manifest "$tmp/m1.json" "0.12.0-beta.1" "0.12.0-beta.2"
assert "newest beta moves the beta alias only"      "$tmp/m1.json" "0.12.0-beta.2" "beta" 0
assert "older beta published late moves nothing"    "$tmp/m1.json" "0.12.0-beta.1" ""     0

manifest "$tmp/m2.json" "0.12.0-beta.2" "0.12.0"
assert "first stable moves both aliases"            "$tmp/m2.json" "0.12.0" $'beta\nstable' 0

manifest "$tmp/m3.json" "0.12.0" "0.13.0-beta.1" "0.12.2"
assert "stable patch below a beta moves stable only" "$tmp/m3.json" "0.12.2" "stable" 0
assert "beta above the stable moves beta only"       "$tmp/m3.json" "0.13.0-beta.1" "beta" 0

assert "candidate missing from the manifest fails"  "$tmp/m3.json" "9.9.9" "" 1

[ "$fail" -eq 0 ] && echo "ok" || exit 1
```

Run both: expected to fail (scripts missing).

- [ ] **Step 2: Implement `desktop-baseline.sh`**

```bash
#!/usr/bin/env bash
# `vpk download` leaves the channel's highest full package in the releases dir (no manifest), and
# `vpk pack` refuses a version at or below that baseline. Keep the package only when it is strictly
# below the candidate; otherwise delete it so the pack runs full-only, without a delta.
# Usage: desktop-baseline.sh <releases-dir> <candidate-version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/semver.sh
source "$here/lib/semver.sh"

dir="${1:?usage: desktop-baseline.sh <releases-dir> <candidate-version>}"
candidate="${2:?usage: desktop-baseline.sh <releases-dir> <candidate-version>}"

shopt -s nullglob
pkgs=("$dir"/*-full.nupkg)
shopt -u nullglob
if [ "${#pkgs[@]}" -eq 0 ]; then echo "no baseline package; packing full-only"; exit 0; fi
[ "${#pkgs[@]}" -eq 1 ] || { echo "expected one full package in $dir, found ${#pkgs[@]}" >&2; exit 1; }

pkg="${pkgs[0]}"
base="$(unzip -p "$pkg" '*.nuspec' | grep -oE '<version>[^<]+</version>' | head -1 | sed -E 's#</?version>##g')"
[ -n "$base" ] || { echo "no <version> in the nuspec of $pkg" >&2; exit 1; }

if [ "$(semver_cmp "$base" "$candidate")" -lt 0 ]; then
  echo "baseline $base kept for delta generation against $candidate"
else
  rm -f "$pkg"
  echo "baseline $base is not below $candidate; discarded, packing full-only"
fi
```

- [ ] **Step 3: Implement `promote-desktop-aliases.sh`**

```bash
#!/usr/bin/env bash
# Decides which DMG aliases a just-published version may take, from the merged feed manifest:
# "beta" when it is the highest version of any kind, "stable" when it is stable and the highest
# stable one. Pure decision — the workflow does the copies. An older tag published late, or a
# re-run, therefore never regresses an alias.
# Usage: promote-desktop-aliases.sh <releases.json> <candidate-version>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/semver.sh
source "$here/lib/semver.sh"

manifest="${1:?usage: promote-desktop-aliases.sh <releases.json> <candidate-version>}"
candidate="${2:?usage: promote-desktop-aliases.sh <releases.json> <candidate-version>}"

versions="$(jq -r '.Assets[] | select(.Type == "Full") | .Version' "$manifest")"
grep -qxF "$candidate" <<<"$versions" || { echo "$candidate is not in $manifest" >&2; exit 1; }

highest_all=""; highest_stable=""
while IFS= read -r v; do
  [ -n "$v" ] || continue
  if [ -z "$highest_all" ] || [ "$(semver_cmp "$v" "$highest_all")" -gt 0 ]; then highest_all="$v"; fi
  if ! semver_is_prerelease "$v"; then
    if [ -z "$highest_stable" ] || [ "$(semver_cmp "$v" "$highest_stable")" -gt 0 ]; then highest_stable="$v"; fi
  fi
done <<<"$versions"

[ "$candidate" = "$highest_all" ] && echo beta
if ! semver_is_prerelease "$candidate" && [ "$candidate" = "$highest_stable" ]; then echo stable; fi
exit 0
```

Run: `chmod +x scripts/desktop-baseline.sh scripts/promote-desktop-aliases.sh && bash scripts/run-shell-tests.sh`. Expected: all `ok`.

- [ ] **Step 4: Commit**

```bash
git add scripts/desktop-baseline.sh scripts/desktop-baseline.test.sh scripts/promote-desktop-aliases.sh scripts/promote-desktop-aliases.test.sh
git commit -m "Select the delta baseline and decide DMG alias promotion by version"
```

---

### Task 18: `verify-desktop-immutables.sh` and `verify-npm-trio.sh`

**Files:**
- Create: `scripts/verify-desktop-immutables.sh`, `scripts/verify-desktop-immutables.test.sh`, `scripts/verify-npm-trio.sh`, `scripts/verify-npm-trio.test.sh`

**Interfaces:**
- Consumes: `scripts/lib/hash.sh`.
- Produces: `verify-desktop-immutables.sh <local-dir> <version> <fetch-script>` where `<fetch-script> <object-name> <out-file>` exits non-zero when the object is absent; exit 0 when every immutable object of the version that exists remotely is byte-identical to the local one, 1 on a mismatch. `verify-npm-trio.sh <version> <kcap.sha256-file> <daemon.sha256-file> [--tarball <path>]` — exit 0 when the published `@kurrent/kcap-darwin-arm64@<version>` tarball's `bin/kcap` and `bin/kcap-daemon` hash to the recorded values.

- [ ] **Step 1: Write the failing tests**

`scripts/verify-desktop-immutables.test.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/verify-desktop-immutables.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
v="0.12.0-beta.2"
local_dir="$tmp/local"; remote="$tmp/remote"; mkdir -p "$local_dir" "$remote"
printf 'full' > "$local_dir/KurrentCapacitor-$v-osx-arm64-full.nupkg"
printf 'dmg'  > "$local_dir/Kurrent-Capacitor-$v-osx-arm64.dmg"
fetch="$tmp/fetch.sh"
printf '#!/usr/bin/env bash\n[ -f "%s/$1" ] || exit 44\ncp "%s/$1" "$2"\n' "$remote" "$remote" > "$fetch"; chmod +x "$fetch"

fail=0
assert() { local rc; set +e; bash "$sh" "$local_dir" "$v" "$fetch" >/dev/null 2>&1; rc=$?; set -e
  if [ "$rc" != "$2" ]; then echo "FAIL: $1 -> rc=$rc (want $2)"; fail=1; fi; }

assert "nothing published yet passes" 0
cp "$local_dir/KurrentCapacitor-$v-osx-arm64-full.nupkg" "$remote/"
assert "identical bytes already published passes (retry)" 0
printf 'other-bytes' > "$remote/Kurrent-Capacitor-$v-osx-arm64.dmg"
assert "different bytes already published fails" 1
[ "$fail" -eq 0 ] && echo "ok" || exit 1
```

`scripts/verify-npm-trio.test.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
sh="$here/verify-npm-trio.sh"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

make_tarball() { # <out.tgz> <kcap-content> <daemon-content>
  local work; work="$(mktemp -d)"; mkdir -p "$work/package/bin"
  printf '%s' "$2" > "$work/package/bin/kcap"; printf '%s' "$3" > "$work/package/bin/kcap-daemon"
  tar -czf "$1" -C "$work" package; rm -rf "$work"
}
make_tarball "$tmp/match.tgz" "cli-bytes" "daemon-bytes"
printf 'cli-bytes' > "$tmp/kcap"; printf 'daemon-bytes' > "$tmp/kcap-daemon"
sha256_of "$tmp/kcap" > "$tmp/kcap.sha256"; sha256_of "$tmp/kcap-daemon" > "$tmp/daemon.sha256"
make_tarball "$tmp/other-daemon.tgz" "cli-bytes" "other-daemon"
make_tarball "$tmp/other-cli.tgz" "other-cli" "daemon-bytes"

fail=0
assert() { local rc; set +e; bash "$sh" "0.12.0-beta.2" "$tmp/kcap.sha256" "$tmp/daemon.sha256" --tarball "$2" >/dev/null 2>&1; rc=$?; set -e
  if [ "$rc" != "$3" ]; then echo "FAIL: $1 -> rc=$rc (want $3)"; fail=1; fi; }
assert "matching tarball passes"      "$tmp/match.tgz"        0
assert "different daemon fails"       "$tmp/other-daemon.tgz" 1
assert "different cli fails"          "$tmp/other-cli.tgz"    1
assert "missing tarball fails"        "$tmp/absent.tgz"       1
[ "$fail" -eq 0 ] && echo "ok" || exit 1
```

Run both: expected to fail (scripts missing).

- [ ] **Step 2: Implement `verify-desktop-immutables.sh`**

```bash
#!/usr/bin/env bash
# Inside the publish concurrency group, before any upload: every immutable object of this version
# that already exists in the bucket must be byte-identical to the artifact about to be published.
# Identical bytes are a retry; different bytes mean a second build of the same version won a race,
# and this run must not overwrite what clients may already hold.
# Usage: verify-desktop-immutables.sh <local-dir> <version> <fetch-script>
#   <fetch-script> <object-name> <out-file> must exit non-zero when the object does not exist.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"

local_dir="${1:?usage: verify-desktop-immutables.sh <local-dir> <version> <fetch-script>}"
version="${2:?usage: verify-desktop-immutables.sh <local-dir> <version> <fetch-script>}"
fetch="${3:?usage: verify-desktop-immutables.sh <local-dir> <version> <fetch-script>}"

names=(
  "KurrentCapacitor-$version-osx-arm64-full.nupkg"
  "KurrentCapacitor-$version-osx-arm64-delta.nupkg"
  "Kurrent-Capacitor-$version-osx-arm64.dmg"
)
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
for name in "${names[@]}"; do
  [ -f "$local_dir/$name" ] || continue
  if ! "$fetch" "$name" "$tmp/$name" >/dev/null 2>&1; then echo "$name: not published yet"; continue; fi
  local_hash="$(sha256_of "$local_dir/$name")"; remote_hash="$(sha256_of "$tmp/$name")"
  if [ "$local_hash" != "$remote_hash" ]; then
    echo "$name is already published with different bytes (remote $remote_hash, local $local_hash); refusing to overwrite an immutable object — cut a new version" >&2
    exit 1
  fi
  echo "$name: already published with identical bytes"
done
echo "immutable objects for $version are consistent"
```

- [ ] **Step 3: Implement `verify-npm-trio.sh`**

```bash
#!/usr/bin/env bash
# The app must ship the exact CLI and daemon bytes npm published for this version. Downloads the
# platform package from the registry (or takes --tarball for tests) and compares both hashes.
# Usage: verify-npm-trio.sh <version> <kcap.sha256-file> <daemon.sha256-file> [--tarball <path>]
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/hash.sh
source "$here/lib/hash.sh"

version="${1:?usage: verify-npm-trio.sh <version> <kcap.sha256-file> <daemon.sha256-file> [--tarball <path>]}"
cli_digest="$(tr -d '[:space:]' < "${2:?kcap.sha256 file}")"
daemon_digest="$(tr -d '[:space:]' < "${3:?daemon.sha256 file}")"
tarball="${5:-}"
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

if [ "${4:-}" != "--tarball" ]; then
  (cd "$tmp" && npm pack "@kurrent/kcap-darwin-arm64@$version" --registry https://registry.npmjs.org --pack-destination "$tmp" >/dev/null)
  tarball="$(ls "$tmp"/*.tgz | head -1)"
fi
[ -f "$tarball" ] || { echo "no tarball for @kurrent/kcap-darwin-arm64@$version" >&2; exit 1; }

mkdir -p "$tmp/x" && tar -xzf "$tarball" -C "$tmp/x"
actual_cli="$(sha256_of "$tmp/x/package/bin/kcap")"
actual_daemon="$(sha256_of "$tmp/x/package/bin/kcap-daemon")"
[ "$actual_cli" = "$cli_digest" ] || { echo "npm kcap hashes to $actual_cli, artifact recorded $cli_digest" >&2; exit 1; }
[ "$actual_daemon" = "$daemon_digest" ] || { echo "npm kcap-daemon hashes to $actual_daemon, artifact recorded $daemon_digest" >&2; exit 1; }
echo "npm @kurrent/kcap-darwin-arm64@$version carries the artifact's kcap and kcap-daemon"
```

Run: `chmod +x scripts/verify-desktop-immutables.sh scripts/verify-npm-trio.sh && bash scripts/run-shell-tests.sh`. Expected: all `ok`.

- [ ] **Step 4: Commit**

```bash
git add scripts/verify-desktop-immutables.sh scripts/verify-desktop-immutables.test.sh scripts/verify-npm-trio.sh scripts/verify-npm-trio.test.sh
git commit -m "Gate desktop publication on immutable objects and the npm trio"
```

---

### Task 19: Packaging helpers — plist rendering, keychain import, signing, DMG

**Files:**
- Create: `scripts/render-info-plist.sh`, `scripts/import-signing-keychain.sh`, `scripts/sign-macos.sh`, `scripts/build-dmg.sh`

**Interfaces:**
- Produces: `render-info-plist.sh <version> <out-file>` (substitutes `{VERSION}` and `{SHORT_VERSION}` in `src/Capacitor.App/Packaging/Info.plist`); `import-signing-keychain.sh` (env in: `APPLE_CERTIFICATE_P12`, `APPLE_CERTIFICATE_PASSWORD`, optional `APPLE_NOTARY_KEY_P8`, `APPLE_NOTARY_KEY_ID`, `APPLE_NOTARY_ISSUER_ID`; writes `KEYCHAIN` to `GITHUB_ENV`; stores the notary profile `kcap-notary` when the key vars are set); `sign-macos.sh <identity> <keychain> <entitlements> <file...>`; `build-dmg.sh <bundle.app> <out.dmg>`.

- [ ] **Step 1: `render-info-plist.sh`**

```bash
#!/usr/bin/env bash
# Usage: render-info-plist.sh <version> <out-file>
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
version="${1:?usage: render-info-plist.sh <version> <out-file>}"
out="${2:?usage: render-info-plist.sh <version> <out-file>}"
short="${version%%-*}"; short="${short%%+*}"
sed -e "s/{VERSION}/${version%%+*}/" -e "s/{SHORT_VERSION}/$short/" "$here/../src/Capacitor.App/Packaging/Info.plist" > "$out"
plutil -lint "$out" >/dev/null
```

- [ ] **Step 2: `import-signing-keychain.sh`**

```bash
#!/usr/bin/env bash
# Creates a per-run keychain, imports the Developer ID Application certificate, and (when the
# App Store Connect key variables are set) stores the notarytool profile "kcap-notary" in it.
# Every secret is required to be non-empty: a tag never falls back to unsigned.
set -euo pipefail
: "${APPLE_CERTIFICATE_P12:?APPLE_CERTIFICATE_P12 (base64 .p12) is required}"
: "${APPLE_CERTIFICATE_PASSWORD:?APPLE_CERTIFICATE_PASSWORD is required}"
: "${RUNNER_TEMP:?RUNNER_TEMP is required}"

KEYCHAIN="$RUNNER_TEMP/kcap-signing.keychain-db"
KEYCHAIN_PASSWORD="$(openssl rand -hex 16)"
cert="$RUNNER_TEMP/kcap-cert.p12"
printf '%s' "$APPLE_CERTIFICATE_P12" | base64 --decode > "$cert"

security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 21600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import "$cert" -P "$APPLE_CERTIFICATE_PASSWORD" -A -t cert -f pkcs12 -k "$KEYCHAIN"
security list-keychain -d user -s "$KEYCHAIN"
rm -f "$cert"

if [ -n "${APPLE_NOTARY_KEY_P8:-}" ]; then
  : "${APPLE_NOTARY_KEY_ID:?APPLE_NOTARY_KEY_ID is required with APPLE_NOTARY_KEY_P8}"
  : "${APPLE_NOTARY_ISSUER_ID:?APPLE_NOTARY_ISSUER_ID is required with APPLE_NOTARY_KEY_P8}"
  key="$RUNNER_TEMP/kcap-notary.p8"
  printf '%s' "$APPLE_NOTARY_KEY_P8" | base64 --decode > "$key"
  xcrun notarytool store-credentials kcap-notary --key "$key" --key-id "$APPLE_NOTARY_KEY_ID" --issuer "$APPLE_NOTARY_ISSUER_ID" --keychain "$KEYCHAIN"
  rm -f "$key"
fi

echo "KEYCHAIN=$KEYCHAIN" >> "${GITHUB_ENV:?GITHUB_ENV is required}"
```

- [ ] **Step 3: `sign-macos.sh`**

```bash
#!/usr/bin/env bash
# Signs each file with hardened runtime, a secure timestamp and the given entitlements — the same
# flags Velopack uses, so pre-signed binaries and the outer bundle agree.
# Usage: sign-macos.sh <identity> <keychain> <entitlements.plist> <file...>
set -euo pipefail
identity="${1:?usage: sign-macos.sh <identity> <keychain> <entitlements> <file...>}"
keychain="${2:?usage: sign-macos.sh <identity> <keychain> <entitlements> <file...>}"
entitlements="${3:?usage: sign-macos.sh <identity> <keychain> <entitlements> <file...>}"
shift 3
[ -n "$identity" ] && [ -n "$keychain" ] || { echo "signing identity and keychain must be non-empty" >&2; exit 1; }
[ -f "$entitlements" ] || { echo "entitlements file not found: $entitlements" >&2; exit 1; }
[ "$#" -gt 0 ] || { echo "no files to sign" >&2; exit 1; }
for file in "$@"; do
  codesign --force --timestamp --options runtime --entitlements "$entitlements" --sign "$identity" --keychain "$keychain" "$file"
  codesign --verify --strict "$file"
done
```

- [ ] **Step 4: `build-dmg.sh`**

```bash
#!/usr/bin/env bash
# A plain drag-to-Applications DMG: the stapled bundle plus an Applications symlink.
# Usage: build-dmg.sh <bundle.app> <out.dmg>
set -euo pipefail
bundle="${1:?usage: build-dmg.sh <bundle.app> <out.dmg>}"
out="${2:?usage: build-dmg.sh <bundle.app> <out.dmg>}"
staging="$(mktemp -d)"; trap 'rm -rf "$staging"' EXIT
ditto "$bundle" "$staging/$(basename "$bundle")"
ln -s /Applications "$staging/Applications"
rm -f "$out"
hdiutil create -volname "Kurrent Capacitor" -srcfolder "$staging" -ov -format UDZO "$out" >/dev/null
echo "built $out"
```

- [ ] **Step 5: Smoke what can run locally**

Run: `chmod +x scripts/render-info-plist.sh scripts/import-signing-keychain.sh scripts/sign-macos.sh scripts/build-dmg.sh && bash scripts/render-info-plist.sh 0.12.0-beta.2+abc /tmp/kcap-Info.plist && grep -c '0.12.0-beta.2' /tmp/kcap-Info.plist && grep -c '<string>0.12.0</string>' /tmp/kcap-Info.plist`. Expected: `1` and `1`. The signing and keychain scripts need the certificate; they are exercised by the first tag build.

- [ ] **Step 6: Commit**

```bash
git add scripts/render-info-plist.sh scripts/import-signing-keychain.sh scripts/sign-macos.sh scripts/build-dmg.sh
git commit -m "Add the macOS packaging helpers: plist, keychain, signing, DMG"
```

---

### Task 20: PR bundle job and the corrected release wait

**Files:**
- Modify: `.github/workflows/ci.yml` (new job `app-bundle`), `.github/workflows/release.yml:26` (`check-regexp`)

**Interfaces:**
- Consumes: Tasks 15–17, 19 scripts; `src/Capacitor.App/Packaging/*`.
- Produces: the `App bundle (osx-arm64)` check and the `app-osx-arm64-unsigned` artifact.

- [ ] **Step 1: Add the job to `ci.yml`**

Append after `aot-check`:

```yaml
  app-bundle:
    name: App bundle (osx-arm64)
    runs-on: macos-latest
    steps:
      # Full history and tags: MinVer derives the version from the nearest tag, and the app
      # refuses a bundled CLI below its floor — a shallow checkout would compute a below-floor
      # default version and fail the assertion below on every PR.
      - uses: actions/checkout@v7
        with:
          fetch-depth: 0
          fetch-tags: true

      - name: Setup .NET
        uses: actions/setup-dotnet@v6

      - name: Install tools
        run: |
          dotnet tool install -g minver-cli --version 7.0.0
          dotnet tool install -g vpk --version 1.2.0

      # One version for the daemon, the CLI, the app and the package, so the floor assertion and
      # Velopack's SemVer check see the same value. The standalone tool does not read
      # MinVerTagPrefix from Directory.Build.props, hence the explicit prefix.
      - name: Compute version
        id: version
        shell: bash
        run: |
          set -euo pipefail
          FULL="$(minver --tag-prefix v)"
          echo "VERSION=${FULL%%+*}" >> "$GITHUB_OUTPUT"
          echo "computed $FULL"

      - name: Publish daemon
        run: >
          dotnet publish src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj -c Release -r osx-arm64
          -p:MinVerVersionOverride=${{ steps.version.outputs.VERSION }} -o publish/daemon/

      - name: Compute daemon digest
        id: daemon-digest
        shell: bash
        run: |
          set -euo pipefail
          source scripts/lib/hash.sh
          DIGEST="$(sha256_of publish/daemon/kcap-daemon)"
          echo "DIGEST=$DIGEST" >> "$GITHUB_OUTPUT"
          echo "$DIGEST" > publish/daemon.sha256

      - name: Publish CLI
        run: >
          dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release -r osx-arm64
          -p:MinVerVersionOverride=${{ steps.version.outputs.VERSION }}
          -p:KcapDaemonDigest=${{ steps.daemon-digest.outputs.DIGEST }} -o publish/cli/

      - name: Publish app
        run: >
          dotnet publish src/Capacitor.App/Capacitor.App.csproj -c Release -r osx-arm64 --self-contained
          -p:MinVerVersionOverride=${{ steps.version.outputs.VERSION }} -o publish/app/

      - name: Assemble pack directory
        shell: bash
        run: |
          set -euo pipefail
          cp publish/cli/kcap publish/daemon/kcap-daemon publish/daemon/libpty_shim.dylib publish/app/
          chmod +x publish/app/kcap publish/app/kcap-daemon "publish/app/Kurrent Capacitor"
          bash scripts/render-info-plist.sh "${{ steps.version.outputs.VERSION }}" publish/Info.plist

      - name: Pack (unsigned)
        shell: bash
        run: >
          vpk pack --packId KurrentCapacitor --packVersion "${{ steps.version.outputs.VERSION }}"
          --packTitle "Kurrent Capacitor" --packAuthors Kurrent --mainExe "Kurrent Capacitor"
          --packDir publish/app --plist publish/Info.plist --icon src/Capacitor.App/Assets/kcap-icon.icns
          --channel osx-arm64 --noInst --outputDir releases

      - name: Assert the bundle
        shell: bash
        run: |
          set -euo pipefail
          mkdir -p extracted
          ditto -x -k releases/KurrentCapacitor-osx-arm64-Portable.zip extracted
          APP="extracted/Kurrent Capacitor.app"
          bash scripts/assert-app-cli-version.sh "$APP/Contents/MacOS/kcap" "${{ steps.version.outputs.VERSION }}"
          bash scripts/assert-bundle-digest.sh "$APP" publish/daemon.sha256
          "$APP/Contents/MacOS/kcap" --version --no-update-check
          "$APP/Contents/MacOS/kcap-daemon" --version
          # Pins the manifest field names the alias-promotion script depends on.
          bash scripts/promote-desktop-aliases.sh releases/releases.osx-arm64.json "${{ steps.version.outputs.VERSION }}" | grep -qx beta

      - name: Upload unsigned bundle
        uses: actions/upload-artifact@v7
        with:
          name: app-osx-arm64-unsigned
          path: releases/KurrentCapacitor-osx-arm64-Portable.zip
```

- [ ] **Step 2: Fix the release wait**

In `release.yml`, replace the `check-regexp` value with:

```yaml
          check-regexp: '^(Build and test \(.*\)|AOT publish check \(.*\)|App bundle \(osx-arm64\))$'
```

and replace the comment above the `ci` job's step (if any) with one line: `# Matrix jobs carry their (matrix) suffix in the check name; the pattern must match the suffixed names.`

- [ ] **Step 3: Validate the YAML and push a PR build**

Run: `python3 -c 'import yaml,sys; yaml.safe_load(open(".github/workflows/ci.yml")); yaml.safe_load(open(".github/workflows/release.yml")); print("ok")'` (install PyYAML with `pip3 install pyyaml` if missing). Expected: `ok`. Push the branch and open a draft PR; the `App bundle (osx-arm64)` check must go green before continuing (its version will read `0.12.0-beta.1.N` once the floor tag exists — if the tag has not been cut yet, the floor assertion fails and the owner must cut it before this job can pass).

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/ci.yml .github/workflows/release.yml
git commit -m "Build the unsigned app bundle on every PR and gate releases on it"
```

---

### Task 21: Sign the CLI and daemon in the release matrix

**Files:**
- Modify: `.github/workflows/release.yml` (`build` job, `osx-arm64` steps around "Publish daemon AOT binary", "Compute daemon digest", "Publish CLI AOT binary", "Create release archive")

**Interfaces:**
- Consumes: Task 19 scripts; secrets `APPLE_CERTIFICATE_P12`, `APPLE_CERTIFICATE_PASSWORD`, `APPLE_SIGNING_IDENTITY`.
- Produces: signed `kcap`, `kcap-daemon`, `libpty_shim.dylib` in the npm package and the `kcap-osx-arm64.tar.gz` archive, plus `daemon.sha256` and `kcap.sha256` inside that archive.

- [ ] **Step 1: Insert the signing steps**

Right after "Publish daemon AOT binary" and before "Compute daemon digest":

```yaml
      - name: Import signing keychain (macOS)
        if: startsWith(matrix.rid, 'osx-')
        shell: bash
        env:
          APPLE_CERTIFICATE_P12: ${{ secrets.APPLE_CERTIFICATE_P12 }}
          APPLE_CERTIFICATE_PASSWORD: ${{ secrets.APPLE_CERTIFICATE_PASSWORD }}
        run: bash scripts/import-signing-keychain.sh

      # Before the digest: signing rewrites the daemon's bytes, and the CLI embeds the digest of
      # the bytes it will actually launch. The daemon is never signed again after this.
      - name: Sign daemon and shim (macOS)
        if: startsWith(matrix.rid, 'osx-')
        shell: bash
        run: >
          bash scripts/sign-macos.sh "${{ secrets.APPLE_SIGNING_IDENTITY }}" "$KEYCHAIN"
          src/Capacitor.App/Packaging/daemon.entitlements.plist
          publish/daemon/${{ matrix.daemon-binary }} publish/daemon/libpty_shim.dylib
```

Delete the stale comment line `# When app bundling/signing lands, sign the daemon BEFORE this step (signing changes the bytes).` above "Compute daemon digest". Right after "Publish CLI AOT binary":

```yaml
      - name: Sign CLI (macOS)
        if: startsWith(matrix.rid, 'osx-')
        shell: bash
        run: >
          bash scripts/sign-macos.sh "${{ secrets.APPLE_SIGNING_IDENTITY }}" "$KEYCHAIN"
          src/Capacitor.App/Packaging/cli.entitlements.plist publish/cli/${{ matrix.cli-binary }}

      - name: Smoke signed binaries (macOS)
        if: startsWith(matrix.rid, 'osx-')
        shell: bash
        run: |
          set -euo pipefail
          publish/cli/kcap --version --no-update-check
          publish/daemon/kcap-daemon --version
```

- [ ] **Step 2: Record both hashes in the archive**

In "Create release archive", after the copies into `archive/` and before `cd archive`:

```bash
          if [[ "${{ matrix.rid }}" == osx-* ]]; then
            source scripts/lib/hash.sh
            echo "${{ steps.daemon-digest.outputs.DIGEST }}" > archive/daemon.sha256
            sha256_of publish/cli/${{ matrix.cli-binary }} > archive/kcap.sha256
          fi
```

- [ ] **Step 3: Validate**

Run the YAML load from Task 20 Step 3. Expected: `ok`. The steps themselves run only on a tag; the first beta tag after merge is their test — check its `osx-arm64` leg logs for `codesign --verify --strict` passing on all three files.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "Sign the macOS daemon before its digest and the CLI after its publish"
```

---

### Task 22: `app-macos` and `app-publish` release jobs

**Files:**
- Modify: `.github/workflows/release.yml` (two new jobs after `github-release`)

**Interfaces:**
- Consumes: Tasks 15–19 scripts; the `release-osx-arm64` artifact with `daemon.sha256`/`kcap.sha256` (Task 21); secrets `APPLE_*`, `R2_ENDPOINT`, `R2_ACCESS_KEY_ID`, `R2_SECRET_ACCESS_KEY`, `R2_BUCKET`.
- Produces: the `app-osx-arm64-signed` artifact; the R2 objects of §5.1; the DMG on the GitHub Release.

- [ ] **Step 1: Add `app-macos`**

```yaml
  app-macos:
    name: Build and sign the desktop app
    needs: build
    runs-on: macos-latest
    steps:
      - uses: actions/checkout@v7

      - name: Setup .NET
        uses: actions/setup-dotnet@v6

      - name: Extract version from tag
        id: version
        shell: bash
        run: echo "VERSION=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"

      - name: Install tools
        run: dotnet tool install -g vpk --version 1.2.0

      - name: Download the signed CLI and daemon
        uses: actions/download-artifact@v8
        with:
          name: release-osx-arm64
          path: trio/

      - name: Unpack the trio
        shell: bash
        run: |
          set -euo pipefail
          mkdir -p trio/bin && tar xzf trio/kcap-osx-arm64.tar.gz -C trio/bin
          chmod +x trio/bin/kcap trio/bin/kcap-daemon
          test -f trio/bin/daemon.sha256 && test -f trio/bin/kcap.sha256

      - name: Import signing keychain and notary profile
        shell: bash
        env:
          APPLE_CERTIFICATE_P12: ${{ secrets.APPLE_CERTIFICATE_P12 }}
          APPLE_CERTIFICATE_PASSWORD: ${{ secrets.APPLE_CERTIFICATE_PASSWORD }}
          APPLE_NOTARY_KEY_P8: ${{ secrets.APPLE_NOTARY_KEY_P8 }}
          APPLE_NOTARY_KEY_ID: ${{ secrets.APPLE_NOTARY_KEY_ID }}
          APPLE_NOTARY_ISSUER_ID: ${{ secrets.APPLE_NOTARY_ISSUER_ID }}
        run: bash scripts/import-signing-keychain.sh

      # The cheap first line of the immutability rule; app-publish repeats it inside its
      # concurrency group against the actual bytes.
      - name: Refuse an already-published version
        shell: bash
        env:
          AWS_ACCESS_KEY_ID: ${{ secrets.R2_ACCESS_KEY_ID }}
          AWS_SECRET_ACCESS_KEY: ${{ secrets.R2_SECRET_ACCESS_KEY }}
          AWS_DEFAULT_REGION: auto
        run: |
          set -euo pipefail
          KEY="desktop/osx-arm64/KurrentCapacitor-${{ steps.version.outputs.VERSION }}-osx-arm64-full.nupkg"
          if aws s3api head-object --endpoint-url "${{ secrets.R2_ENDPOINT }}" --bucket "${{ secrets.R2_BUCKET }}" --key "$KEY" >/dev/null 2>&1; then
            echo "::error::$KEY is already published. A published desktop version is never rebuilt — cut a new version."
            exit 1
          fi

      - name: Publish app
        run: >
          dotnet publish src/Capacitor.App/Capacitor.App.csproj -c Release -r osx-arm64 --self-contained
          -p:MinVerVersionOverride=${{ steps.version.outputs.VERSION }} -o publish/app/

      - name: Sign the app's binaries
        shell: bash
        run: |
          set -euo pipefail
          machos=()
          while IFS= read -r f; do
            if file "$f" | grep -q 'Mach-O'; then machos+=("$f"); fi
          done < <(find publish/app -type f)
          bash scripts/sign-macos.sh "${{ secrets.APPLE_SIGNING_IDENTITY }}" "$KEYCHAIN" src/Capacitor.App/Packaging/app.entitlements.plist "${machos[@]}"

      # The trio arrives already signed; it is copied, never touched by codesign again.
      - name: Assemble pack directory
        shell: bash
        run: |
          set -euo pipefail
          cp trio/bin/kcap trio/bin/kcap-daemon trio/bin/libpty_shim.dylib publish/app/
          chmod +x publish/app/kcap publish/app/kcap-daemon "publish/app/Kurrent Capacitor"
          bash scripts/render-info-plist.sh "${{ steps.version.outputs.VERSION }}" publish/Info.plist

      - name: Fetch the delta baseline
        shell: bash
        run: |
          mkdir -p releases
          vpk download s3 --channel osx-arm64 --outputDir releases --endpoint "${{ secrets.R2_ENDPOINT }}" --bucket "${{ secrets.R2_BUCKET }}" --prefix desktop/osx-arm64 --keyId "${{ secrets.R2_ACCESS_KEY_ID }}" --secret "${{ secrets.R2_SECRET_ACCESS_KEY }}" \
            || echo "no previous release to diff against"
          bash scripts/desktop-baseline.sh releases "${{ steps.version.outputs.VERSION }}"

      - name: Pack, sign the bundle, notarize
        shell: bash
        run: >
          vpk pack --packId KurrentCapacitor --packVersion "${{ steps.version.outputs.VERSION }}"
          --packTitle "Kurrent Capacitor" --packAuthors Kurrent --mainExe "Kurrent Capacitor"
          --packDir publish/app --plist publish/Info.plist --icon src/Capacitor.App/Assets/kcap-icon.icns
          --channel osx-arm64 --noInst --outputDir releases
          --signAppIdentity "${{ secrets.APPLE_SIGNING_IDENTITY }}" --signEntitlements src/Capacitor.App/Packaging/app.entitlements.plist
          --signDisableDeep --notaryProfile kcap-notary --keychain "$KEYCHAIN"

      - name: Assert and smoke the packed bundle
        shell: bash
        run: |
          set -euo pipefail
          mkdir -p extracted
          ditto -x -k releases/KurrentCapacitor-osx-arm64-Portable.zip extracted
          APP="extracted/Kurrent Capacitor.app"
          bash scripts/assert-app-cli-version.sh "$APP/Contents/MacOS/kcap" "${{ steps.version.outputs.VERSION }}"
          bash scripts/assert-bundle-digest.sh "$APP" trio/bin/daemon.sha256
          "$APP/Contents/MacOS/kcap" --version --no-update-check
          "$APP/Contents/MacOS/kcap-daemon" --version
          # JIT under hardened runtime: the signed app must still be alive after 15 s.
          SCRATCH="$(mktemp -d)"
          KCAP_CONFIG_DIR="$SCRATCH" KCAP_APP_UPDATE_URL="http://127.0.0.1:9/" "$APP/Contents/MacOS/Kurrent Capacitor" &
          APP_PID=$!
          sleep 15
          kill -0 "$APP_PID" || { echo "::error::the signed app exited within 15 s"; exit 1; }
          kill "$APP_PID"; wait "$APP_PID" || true

      - name: Build, sign, notarize and staple the DMG
        shell: bash
        run: |
          set -euo pipefail
          DMG="releases/Kurrent-Capacitor-${{ steps.version.outputs.VERSION }}-osx-arm64.dmg"
          bash scripts/build-dmg.sh "extracted/Kurrent Capacitor.app" "$DMG"
          codesign --force --timestamp --sign "${{ secrets.APPLE_SIGNING_IDENTITY }}" --keychain "$KEYCHAIN" "$DMG"
          xcrun notarytool submit "$DMG" --keychain-profile kcap-notary --keychain "$KEYCHAIN" --wait
          xcrun stapler staple "$DMG"
          spctl --assess -t open --context context:primary-signature -v "$DMG"
          cp trio/bin/daemon.sha256 trio/bin/kcap.sha256 releases/

      - name: Upload the signed release set
        uses: actions/upload-artifact@v7
        with:
          name: app-osx-arm64-signed
          path: releases/
```

The version is derived from the tag inside the job (the "Extract version from tag" step); the `build` job exposes no output for it.

- [ ] **Step 2: Add `app-publish`**

```yaml
  app-publish:
    name: Publish the desktop app
    needs: [app-macos, publish-npm, github-release]
    runs-on: ubuntu-latest
    # Every publication into the bucket runs alone: the feed manifest is read-merge-write.
    concurrency:
      group: desktop-publish
      cancel-in-progress: false
    env:
      AWS_ACCESS_KEY_ID: ${{ secrets.R2_ACCESS_KEY_ID }}
      AWS_SECRET_ACCESS_KEY: ${{ secrets.R2_SECRET_ACCESS_KEY }}
      AWS_DEFAULT_REGION: auto
      R2: --endpoint-url ${{ secrets.R2_ENDPOINT }}
      DEST: s3://${{ secrets.R2_BUCKET }}/desktop/osx-arm64
    steps:
      - uses: actions/checkout@v7

      - name: Setup .NET
        uses: actions/setup-dotnet@v6

      - name: Install vpk
        run: dotnet tool install -g vpk --version 1.2.0

      - name: Extract version from tag
        id: version
        shell: bash
        run: echo "VERSION=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"

      - name: Download the signed release set
        uses: actions/download-artifact@v8
        with:
          name: app-osx-arm64-signed
          path: releases/

      # The app ships only the trio npm shipped: a run that lost the npm race stops here.
      - name: Verify the npm trio
        run: bash scripts/verify-npm-trio.sh "${{ steps.version.outputs.VERSION }}" releases/kcap.sha256 releases/daemon.sha256

      - name: Verify immutable objects
        shell: bash
        run: |
          set -euo pipefail
          cat > fetch.sh <<'EOF'
          #!/usr/bin/env bash
          aws s3 cp $R2 "$DEST/$1" "$2" >/dev/null 2>&1
          EOF
          chmod +x fetch.sh
          bash scripts/verify-desktop-immutables.sh releases "${{ steps.version.outputs.VERSION }}" ./fetch.sh

      - name: Upload the feed and packages
        run: >
          vpk upload s3 --channel osx-arm64 --outputDir releases --endpoint "${{ secrets.R2_ENDPOINT }}"
          --bucket "${{ secrets.R2_BUCKET }}" --prefix desktop/osx-arm64
          --keyId "${{ secrets.R2_ACCESS_KEY_ID }}" --secret "${{ secrets.R2_SECRET_ACCESS_KEY }}"

      - name: Upload the DMG and promote aliases
        shell: bash
        run: |
          set -euo pipefail
          V="${{ steps.version.outputs.VERSION }}"
          DMG="releases/Kurrent-Capacitor-$V-osx-arm64.dmg"
          aws s3 cp $R2 "$DMG" "$DEST/Kurrent-Capacitor-$V-osx-arm64.dmg" --cache-control "public, max-age=31536000, immutable"
          aws s3 cp $R2 "$DEST/releases.osx-arm64.json" merged.json
          for alias in $(bash scripts/promote-desktop-aliases.sh merged.json "$V"); do
            case "$alias" in
              beta)   aws s3 cp $R2 "$DMG" "$DEST/Kurrent-Capacitor-osx-arm64-beta.dmg" --cache-control "public, max-age=300" ;;
              stable) aws s3 cp $R2 "$DMG" "$DEST/Kurrent-Capacitor-osx-arm64.dmg" --cache-control "public, max-age=300" ;;
            esac
          done

      - name: Attach the DMG to the GitHub Release
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: gh release upload "${{ github.ref_name }}" "releases/Kurrent-Capacitor-${{ steps.version.outputs.VERSION }}-osx-arm64.dmg" --clobber
```

- [ ] **Step 3: Validate and record the operator checklist**

Run the YAML load from Task 20 Step 3 (expected `ok`). Add to the PR description's checklist the four out-of-repo prerequisites from spec §8 (secrets, R2, kcap-web route, `v0.12.0-beta.1`), so the first tag after merge is understood to be the live test of these two jobs.

- [ ] **Step 4: Commit**

```bash
git add .github/workflows/release.yml
git commit -m "Pack, notarize and publish the desktop app from the release workflow"
```

---

### Task 23: README and change notes

**Files:**
- Modify: `README.md` (after `### 1. Install the CLI`, before `### 2. Run setup`; and the `### Daemon` section's mention of app-managed starts if it names the dev seam), `docs/CHANGES.md` (new top entry)

- [ ] **Step 1: README**

Insert after the CLI install subsection (before `### 2. Run setup`):

```markdown
### Desktop app (macOS)

Download `Kurrent-Capacitor-osx-arm64.dmg` from https://www.kurrent.io/download/mac (Apple silicon, macOS 15 or later), open it and drag **Kurrent Capacitor** to **Applications**. The app bundles its own `kcap` CLI and daemon: you do not need the npm install as well, and the first run offers to link `kcap` onto your terminal PATH and to install the daemon as a background service.

The app must run from the Applications folder — launched from the disk image or from Downloads it offers to move itself there first, because the terminal link and the background service point at its location.

Updates arrive through the app: it checks a few times a day, downloads in the background and asks before restarting ("Check for Updates…" in the menu bar checks now). A bundled `kcap update` reports this and does nothing else. The bundled CLI follows the app's channel; the npm package stays the headless/CI channel.
```

In the `Requirements` section add a line: `- Desktop app: macOS 15 (Sequoia) or later on Apple silicon.` Search the README for `KCAP_APP_CLI_PATH` and, where it describes running the app from source, add that a packed bundle resolves the CLI beside itself first.

- [ ] **Step 2: `docs/CHANGES.md`**

Insert a new entry at the top (below the file's preamble), following the existing entry style:

```markdown
## The desktop app ships as a signed DMG that updates itself

The app bundles `kcap`, `kcap-daemon` and the PTY shim in `Contents/MacOS`, so the CLI beside the
app is the one the shim links and the LaunchAgent runs, at a path that survives updates. Velopack
packs and updates the bundle; one Velopack channel carries every release, and the app itself drops
prerelease entries when the installed version is stable.

**The daemon is signed before its digest is computed, and never again.** The CLI embeds the
daemon's SHA-256 and refuses a mismatch on app-managed starts; signing rewrites the bytes, so the
order is fixed and Velopack runs with deep signing disabled. npm and the app receive the same signed
trio, and the app is published only after its bytes are confirmed on the registry.

**The daemon's restart is its own.** It already restarts itself when its binary changes and it is
idle, which the bundle swap triggers; the app holds its skew dialog for 45 s after an update
relaunch so that restart can land, and offers the dialog only to a daemon that stays busy.

**Startup auto-apply is off.** A cached package is applied by the app after the install-location
guard and the prerelease rule, and never on an update relaunch — a failed apply relaunches the old
version with the same package still cached.
```

- [ ] **Step 3: Check the README render and the lint**

Run: `bash scripts/check-linear-ids.sh` (expected: clean) and skim the README section in a Markdown preview.

- [ ] **Step 4: Commit**

```bash
git add README.md docs/CHANGES.md
git commit -m "Document the desktop app's DMG install and update flow"
```

---

## Finishing

After Task 23: run the full solution build and test suite (`TMPDIR=/private/tmp dotnet test --solution Capacitor.slnx`), `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` (expected: no output), `bash scripts/run-shell-tests.sh`, and `bash scripts/check-linear-ids.sh`. Open the PR from `.github/PULL_REQUEST_TEMPLATE.md` with `Closes #<github-issue>` and `AI-1653` on its reference line, and the spec §8 prerequisites as a checklist. The first tag at or after `v0.12.0-beta.2` is the live test of Tasks 21–22; run the spec §10 first-release gate on that build before the website links the DMG.
