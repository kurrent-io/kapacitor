# CLI Telemetry & Signup-Funnel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the kcap CLI PostHog telemetry that measures where people abandon `kcap setup`, plus which commands and MCP tools they actually use.

**Architecture:** A new `Capacitor.Cli.Core/Telemetry/` namespace holds four units — settings resolution, on-disk device state, event construction, and an HTTP client — behind one static facade (`CliTelemetry`) that swallows every failure. Events go straight to `https://phog.kurrent.io/batch/` with a public ingest token. Setup funnel steps flush eagerly; everything else flushes once from a `ProcessExit` handler.

**Tech Stack:** .NET 10, NativeAOT, TUnit (Microsoft Testing Platform), `System.Text.Json` source generation, `System.Net.Http`.

**Spec:** `docs/superpowers/specs/2026-08-08-cli-telemetry-signup-funnel-design.md`

## Global Constraints

- **Telemetry must never throw.** An exception escaping to the NativeAOT runtime aborts the process with SIGABRT (see the comment at `src/Capacitor.Cli/Program.cs:113`). Every public entry point on `CliTelemetry` wraps its body in `try { … } catch { }`.
- **Telemetry must never write to stdout.** Only the first-run notice and `KCAP_TELEMETRY_DEBUG=1` output go to stderr.
- **AOT-safe JSON only.** Serialize via source-generated `CapacitorJsonContext` (declared at `src/Capacitor.Cli.Core/Models.cs:1058`) or `JsonNode.ToJsonString()`.
  - Never use a `JsonArray` collection expression (`[a, b]`) — it compiles to `Add<T>()` and requires dynamic code.
  - **Avoiding the collection expression is not sufficient.** `JsonArray.Add<T>(T)` carries both `RequiresUnreferencedCode` and `RequiresDynamicCode`, and plain `arr.Add(x)` binds to it for *any* argument whose static type is narrower than `JsonNode?` — including `string` and `JsonObject`, and including `JsonValue.Create(x)`, since exact-type overload betterness still prefers the generic. The non-generic `Add(JsonNode?)` is only selected when the argument's static type is exactly `JsonNode?`:
    ```csharp
    JsonNode? node = JsonValue.Create(f);   // or the JsonObject you are appending
    arr.Add(node);                          // binds Add(JsonNode?), no IL2026/IL3050
    ```
  - This is invisible to `dotnet build`. Only `dotnet publish -c Release` surfaces it, which is why it must be checked before a task is called done, not only in Task 12.
- **Ingest endpoint:** `https://phog.kurrent.io` — **Token:** `phc_DeHBgHGersY4LmDlADnPrsCPOAmMO7QFOH8f4DVEVmD`. Public write-only ingest key, already a source literal in `kcap-web/src/config.ts`; not a secret.
- **PostHog group key is `organization`**, attached **only** when the server URL host ends in `.kcap.ai`.
- **No CLI event may be named `cli_setup_completed`, `user_registered`, `user_logged_in`, `session_ingest_started`, `session_ingest_ended`, `eval_ran`, `fact_retained`, `daemon_connected`, `daemon_disconnected`, `hosted_agent_started`, or `hosted_agent_ended`** — all are emitted by the server.
- **Never collected:** argv values, file paths, repo names/URLs, session ids, prompt/transcript content, env var values, usernames, emails.
- **Tests:** TUnit. Assertions are `await Assert.That(x).IsTrue()` / `.IsEqualTo(y)` / `.IsNull()`. Run with `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`. Filter with `--treenode-filter "/*/*/ClassName/*"` (NOT `--filter`; a bare `*ClassName*` glob matches zero tests).
- **Paths in tests** use `Path.Combine`, never separator literals — there is a windows-latest CI leg.

---

### Task 1: Opt-out precedence

**Files:**
- Create: `src/Capacitor.Cli.Core/Telemetry/TelemetrySettings.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/TelemetrySettingsTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `TelemetryDecision(bool Enabled, string Reason)`; `TelemetrySettings.Resolve(IReadOnlyDictionary<string, string?> env, bool? persisted) → TelemetryDecision`

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class TelemetrySettingsTests {
    static TelemetryDecision Resolve(bool? persisted = null, params (string Key, string? Value)[] env) =>
        TelemetrySettings.Resolve(env.ToDictionary(e => e.Key, e => e.Value), persisted);

    [Test]
    public async Task Enabled_by_default() {
        await Assert.That(Resolve().Enabled).IsTrue();
    }

    [Test]
    [Arguments("0")]
    [Arguments("off")]
    [Arguments("false")]
    [Arguments("no")]
    [Arguments("OFF")]
    public async Task Kcap_telemetry_disables(string value) {
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", value)).Enabled).IsFalse();
    }

    [Test]
    [Arguments("1")]
    [Arguments("on")]
    [Arguments("true")]
    [Arguments("yes")]
    public async Task Kcap_telemetry_enables(string value) {
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", value)).Enabled).IsTrue();
    }

    [Test]
    public async Task Do_not_track_disables() {
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "1")).Enabled).IsFalse();
    }

    [Test]
    public async Task Do_not_track_zero_does_not_disable() {
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "0")).Enabled).IsTrue();
    }

    // Documented precedence: the kcap-specific variable is the deliberate, more specific
    // statement and is the only way to opt back in on a machine with a blanket DO_NOT_TRACK.
    [Test]
    public async Task Kcap_telemetry_outranks_do_not_track_in_both_directions() {
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "1"), ("KCAP_TELEMETRY", "1")).Enabled).IsTrue();
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "0"), ("KCAP_TELEMETRY", "0")).Enabled).IsFalse();
    }

    [Test]
    public async Task Persisted_flag_applies_when_no_env_override() {
        await Assert.That(Resolve(persisted: false).Enabled).IsFalse();
        await Assert.That(Resolve(persisted: true).Enabled).IsTrue();
    }

    [Test]
    public async Task Env_outranks_persisted_flag() {
        await Assert.That(Resolve(persisted: false, ("KCAP_TELEMETRY", "1")).Enabled).IsTrue();
        await Assert.That(Resolve(persisted: true, ("DO_NOT_TRACK", "1")).Enabled).IsFalse();
    }

    [Test]
    public async Task Blank_env_values_are_ignored() {
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", ""), ("DO_NOT_TRACK", "")).Enabled).IsTrue();
    }

    [Test]
    public async Task Unparseable_kcap_telemetry_falls_through_to_default() {
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", "banana")).Enabled).IsTrue();
    }

    [Test]
    public async Task Reason_names_the_winning_source() {
        await Assert.That(Resolve(null, ("DO_NOT_TRACK", "1")).Reason).IsEqualTo("DO_NOT_TRACK");
        await Assert.That(Resolve(null, ("KCAP_TELEMETRY", "0")).Reason).IsEqualTo("KCAP_TELEMETRY");
        await Assert.That(Resolve(persisted: false).Reason).IsEqualTo("config");
        await Assert.That(Resolve().Reason).IsEqualTo("default");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TelemetrySettingsTests/*"`
Expected: FAIL — `TelemetrySettings` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Capacitor.Cli.Core.Telemetry;

/// <summary>Outcome of opt-out resolution. <paramref name="Reason"/> names the winning
/// source so `kcap config show` and KCAP_TELEMETRY_DEBUG can explain themselves.</summary>
public readonly record struct TelemetryDecision(bool Enabled, string Reason);

/// <summary>
/// Resolves whether telemetry is on. Pure over an injected environment so the precedence
/// table is testable without mutating the real process environment.
///
/// Precedence, highest first: KCAP_TELEMETRY (explicit, either direction) > DO_NOT_TRACK >
/// persisted config > enabled. KCAP_TELEMETRY deliberately outranks DO_NOT_TRACK in both
/// directions: it is the kcap-specific, deliberate statement, and the only way a user with a
/// blanket DO_NOT_TRACK in their shell profile can opt back in.
/// </summary>
public static class TelemetrySettings {
    public static TelemetryDecision Resolve(IReadOnlyDictionary<string, string?> env, bool? persisted) {
        if (TryReadBool(env, "KCAP_TELEMETRY", out var explicitChoice))
            return new TelemetryDecision(explicitChoice, "KCAP_TELEMETRY");

        if (IsDoNotTrackSet(env)) return new TelemetryDecision(false, "DO_NOT_TRACK");

        if (persisted is { } stored) return new TelemetryDecision(stored, "config");

        return new TelemetryDecision(true, "default");
    }

    /// <summary>Live resolution against the real environment and the persisted flag.</summary>
    public static TelemetryDecision Resolve(bool? persisted) =>
        Resolve(ReadEnv(), persisted);

    static IReadOnlyDictionary<string, string?> ReadEnv() =>
        new Dictionary<string, string?> {
            ["KCAP_TELEMETRY"] = Environment.GetEnvironmentVariable("KCAP_TELEMETRY"),
            ["DO_NOT_TRACK"]   = Environment.GetEnvironmentVariable("DO_NOT_TRACK"),
        };

    // DO_NOT_TRACK is "set to anything meaningful except 0". The consoledonottrack.com
    // convention is presence-based, but treating an explicit "0" as opt-out would make it
    // impossible to neutralise an inherited value.
    static bool IsDoNotTrackSet(IReadOnlyDictionary<string, string?> env) =>
        env.TryGetValue("DO_NOT_TRACK", out var raw)
        && !string.IsNullOrWhiteSpace(raw)
        && raw.Trim() != "0";

    static bool TryReadBool(IReadOnlyDictionary<string, string?> env, string key, out bool value) {
        value = false;
        if (!env.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return false;

        switch (raw.Trim().ToLowerInvariant()) {
            case "1" or "on" or "true" or "yes":  value = true;  return true;
            case "0" or "off" or "false" or "no": value = false; return true;
            default:                              return false;   // unparseable → fall through
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TelemetrySettingsTests/*"`
Expected: PASS — all 18 test cases.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/TelemetrySettings.cs test/Capacitor.Cli.Tests.Unit/Telemetry/TelemetrySettingsTests.cs
git commit -m "Add telemetry opt-out precedence resolution"
```

---

### Task 2: Device state on disk

**Files:**
- Create: `src/Capacitor.Cli.Core/Telemetry/TelemetryState.cs`
- Modify: `src/Capacitor.Cli.Core/Models.cs` — register `TelemetryStateFile` with the source-gen context
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/TelemetryStateTests.cs`

**Interfaces:**
- Consumes: `TelemetrySettings.Resolve` (Task 1); `PathHelpers.ConfigPath(string)`
- Produces: `TelemetryStateFile(string? Id, bool? Enabled, bool NoticeShown)`; `TelemetryState.Read()`, `.GetOrCreateDeviceId()`, `.SetEnabled(bool)`, `.MarkNoticeShown()`, `.PersistedEnabled()`

**Context:** `PathHelpers.ConfigPath` resolves `KCAP_CONFIG_DIR` or `~/.config/kcap`, so tests set `KCAP_CONFIG_DIR` to a temp directory. `PathHelpers` caches the directory in a `static readonly` field at first touch, so tests must set the variable in a fixture that runs before any other config access, or run in their own process. Use a per-test temp dir and accept the cached-root caveat by writing through an internal path override (`TelemetryState.PathOverride`) instead — that keeps these tests hermetic.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class TelemetryStateTests {
    static string NewTempPath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-telemetry-{Guid.NewGuid():N}", "telemetry.json");

    [Test]
    public async Task Read_of_missing_file_is_all_defaults() {
        TelemetryState.PathOverride = NewTempPath();

        var state = TelemetryState.Read();

        await Assert.That(state.Id).IsNull();
        await Assert.That(state.Enabled).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    [Test]
    public async Task Device_id_is_created_once_and_is_stable() {
        TelemetryState.PathOverride = NewTempPath();

        var first  = TelemetryState.GetOrCreateDeviceId();
        var second = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
        await Assert.That(TelemetryState.Read().Id).IsEqualTo(first);
    }

    [Test]
    public async Task Device_id_is_a_bare_guid_with_no_hyphens() {
        TelemetryState.PathOverride = NewTempPath();

        var id = TelemetryState.GetOrCreateDeviceId()!;

        await Assert.That(id.Length).IsEqualTo(32);
        await Assert.That(id.Contains('-')).IsFalse();
    }

    // Opting out before first run must not mint an analytics identifier at all.
    [Test]
    public async Task No_device_id_is_written_while_disabled() {
        TelemetryState.PathOverride = NewTempPath();
        TelemetryState.SetEnabled(false);

        var id = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(id).IsNull();
        await Assert.That(TelemetryState.Read().Id).IsNull();
    }

    [Test]
    public async Task Set_enabled_persists_and_survives_reread() {
        TelemetryState.PathOverride = NewTempPath();

        TelemetryState.SetEnabled(false);
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)false);

        TelemetryState.SetEnabled(true);
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)true);
    }

    [Test]
    public async Task Set_enabled_preserves_existing_device_id() {
        TelemetryState.PathOverride = NewTempPath();
        var id = TelemetryState.GetOrCreateDeviceId();

        TelemetryState.SetEnabled(false);

        await Assert.That(TelemetryState.Read().Id).IsEqualTo(id);
    }

    [Test]
    public async Task Notice_shown_marker_persists() {
        TelemetryState.PathOverride = NewTempPath();

        await Assert.That(TelemetryState.Read().NoticeShown).IsFalse();
        TelemetryState.MarkNoticeShown();
        await Assert.That(TelemetryState.Read().NoticeShown).IsTrue();
    }

    [Test]
    public async Task Corrupt_file_reads_as_defaults_and_does_not_throw() {
        var path = NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        TelemetryState.PathOverride = path;

        var state = TelemetryState.Read();

        await Assert.That(state.Id).IsNull();
        await Assert.That(state.NoticeShown).IsFalse();
    }

    [Test]
    public async Task Corrupt_file_heals_on_device_id_creation() {
        var path = NewTempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        TelemetryState.PathOverride = path;

        var first  = TelemetryState.GetOrCreateDeviceId();
        var second = TelemetryState.GetOrCreateDeviceId();

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsEqualTo(first);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TelemetryStateTests/*"`
Expected: FAIL — `TelemetryState` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/Capacitor.Cli.Core/Telemetry/TelemetryState.cs`:

```csharp
using System.Text.Json;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>On-disk shape of <c>telemetry.json</c>.</summary>
public readonly record struct TelemetryStateFile(string? Id, bool? Enabled, bool NoticeShown);

/// <summary>
/// Owns <c>telemetry.json</c> in the CLI config directory: the anonymous device id, the
/// persisted enable flag, and the first-run-notice marker.
///
/// Deliberately NOT <see cref="MachineId"/>'s <c>machine.json</c>. That file is an
/// auth-relevant identifier sent to the Capacitor server to prove machine identity;
/// an analytics id is a different purpose with a different lifetime, and keeping it separate
/// means opting out can delete it without touching authentication.
/// </summary>
public static class TelemetryState {
    /// <summary>Test seam. Null in production, where the path resolves under the config dir.</summary>
    public static string? PathOverride { get; set; }

    static string Path => PathOverride ?? PathHelpers.ConfigPath("telemetry.json");

    public static TelemetryStateFile Read() {
        var path = Path;
        if (!File.Exists(path)) return default;

        try {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.TelemetryStateFile);
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return default;   // corrupt or transiently locked → defaults, never throw
        }
    }

    public static bool? PersistedEnabled() => Read().Enabled;

    /// <summary>
    /// Returns the stable device id, creating one on first call. Returns null — and writes
    /// nothing — when telemetry is disabled, so an opted-out user never has an analytics
    /// identifier minted for them.
    /// </summary>
    public static string? GetOrCreateDeviceId() {
        var state = Read();
        if (state.Enabled is false) return null;
        if (!string.IsNullOrWhiteSpace(state.Id)) return state.Id;

        var id = Guid.NewGuid().ToString("N");
        Write(state with { Id = id });

        return Read().Id ?? id;   // a peer may have won the race; adopt whatever landed
    }

    public static void SetEnabled(bool enabled) => Write(Read() with { Enabled = enabled });

    public static void MarkNoticeShown() => Write(Read() with { NoticeShown = true });

    static void Write(TelemetryStateFile state) {
        try {
            var path = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(state, CapacitorJsonContext.Default.TelemetryStateFile);
            File.WriteAllText(path, json);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort. A device id we fail to persist just means a new one next run,
            // which skews counts slightly — never a reason to fail the user's command.
        }
    }
}
```

In `src/Capacitor.Cli.Core/Models.cs`, add the serializable registration next to the other `[JsonSerializable]` attributes on `CapacitorJsonContext` (they sit immediately above the `partial class CapacitorJsonContext` declaration at line 1062):

```csharp
[JsonSerializable(typeof(TelemetryStateFile))]
```

Add `using Capacitor.Cli.Core.Telemetry;` to the top of `Models.cs` if it is not already present.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TelemetryStateTests/*"`
Expected: PASS — 9 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/TelemetryState.cs src/Capacitor.Cli.Core/Models.cs test/Capacitor.Cli.Tests.Unit/Telemetry/TelemetryStateTests.cs
git commit -m "Add telemetry device state (telemetry.json)"
```

---

### Task 3: Command event construction and redaction

**Files:**
- Create: `src/Capacitor.Cli.Core/Telemetry/CommandEvents.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/CommandEventsTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `CommandEvents.IsReportable(string command) → bool`; `CommandEvents.Subcommand(string command, string[] args) → string?`; `CommandEvents.Flags(string[] args) → string[]`

**Context:** This is the privacy-critical unit. `kcap recap <sessionId>`, `kcap ignore <path>`, `kcap remap <path>` and `kcap hide <sessionId>` all put identifying data in `args[1]`, which is exactly where a subcommand would live for `kcap daemon start`. Hence a per-verb allowlist for subcommands and a shape rule for flags.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class CommandEventsTests {
    [Test]
    [Arguments("hook")]
    [Arguments("watch")]
    [Arguments("mcp")]
    [Arguments("permission-request")]
    [Arguments("generate-whats-done")]
    [Arguments("set-title")]
    [Arguments("copilot-finalize")]
    [Arguments("cursor-verify-appendonly")]
    public async Task Machine_driven_verbs_are_not_reportable(string command) {
        await Assert.That(CommandEvents.IsReportable(command)).IsFalse();
    }

    [Test]
    [Arguments("setup")]
    [Arguments("recap")]
    [Arguments("daemon")]
    [Arguments("status")]
    [Arguments("import")]
    public async Task Human_verbs_are_reportable(string command) {
        await Assert.That(CommandEvents.IsReportable(command)).IsTrue();
    }

    [Test]
    public async Task Known_subcommands_are_reported() {
        await Assert.That(CommandEvents.Subcommand("daemon", ["daemon", "start"])).IsEqualTo("start");
        await Assert.That(CommandEvents.Subcommand("plugin", ["plugin", "install"])).IsEqualTo("install");
        await Assert.That(CommandEvents.Subcommand("config", ["config", "set", "server_url", "https://acme.kcap.ai"])).IsEqualTo("set");
        await Assert.That(CommandEvents.Subcommand("curate", ["curate", "apply"])).IsEqualTo("apply");
    }

    [Test]
    public async Task Unknown_subcommand_token_is_dropped() {
        await Assert.That(CommandEvents.Subcommand("daemon", ["daemon", "frobnicate"])).IsNull();
    }

    // The whole point of the allowlist: verbs whose positional is user data.
    [Test]
    public async Task Session_ids_and_paths_in_positionals_never_survive() {
        await Assert.That(CommandEvents.Subcommand("recap", ["recap", "0b9c1f4e-2a77-4d19-9f0e-1c2d3e4f5a6b"])).IsNull();
        await Assert.That(CommandEvents.Subcommand("hide",  ["hide",  "0b9c1f4e-2a77-4d19-9f0e-1c2d3e4f5a6b"])).IsNull();
        await Assert.That(CommandEvents.Subcommand("ignore", ["ignore", Path.Combine("Users", "alexey", "secret")])).IsNull();
        await Assert.That(CommandEvents.Subcommand("remap", ["remap", "git@github.com:acme/private.git"])).IsNull();
    }

    [Test]
    public async Task Verbs_with_no_subcommands_report_none() {
        await Assert.That(CommandEvents.Subcommand("status", ["status"])).IsNull();
        await Assert.That(CommandEvents.Subcommand("setup",  ["setup"])).IsNull();
    }

    [Test]
    public async Task Flags_are_collected_sorted_and_deduplicated() {
        var flags = CommandEvents.Flags(["setup", "--no-prompt", "--skip-codex-hooks", "--no-prompt"]);

        await Assert.That(flags.Length).IsEqualTo(2);
        await Assert.That(flags[0]).IsEqualTo("--no-prompt");
        await Assert.That(flags[1]).IsEqualTo("--skip-codex-hooks");
    }

    [Test]
    public async Task Flag_values_are_stripped_and_never_reported() {
        var flags = CommandEvents.Flags(["setup", "--server-url=https://internal.corp.example", "--server-url", "https://internal.corp.example"]);

        await Assert.That(flags.Length).IsEqualTo(1);
        await Assert.That(flags[0]).IsEqualTo("--server-url");
    }

    [Test]
    public async Task Non_flag_tokens_are_dropped_entirely() {
        var flags = CommandEvents.Flags(["recap", "0b9c1f4e-2a77-4d19-9f0e-1c2d3e4f5a6b", "some/path"]);

        await Assert.That(flags.Length).IsEqualTo(0);
    }

    // Shape rule: the pattern cannot express a path, URL, GUID, or email.
    [Test]
    [Arguments("--Bad-Upper")]
    [Arguments("--1leading-digit")]
    [Arguments("--has/slash")]
    [Arguments("--has.dot")]
    [Arguments("--has@at")]
    [Arguments("--")]
    [Arguments("-short")]
    [Arguments("--this-flag-name-is-far-too-long-to-be-a-real-flag-name")]
    public async Task Malformed_flag_shapes_are_rejected(string token) {
        await Assert.That(CommandEvents.Flags(["setup", token]).Length).IsEqualTo(0);
    }

    [Test]
    public async Task Flag_list_is_capped() {
        var many = new[] { "setup" }
            .Concat(Enumerable.Range(0, 40).Select(i => $"--flag-{i}"))
            .ToArray();

        await Assert.That(CommandEvents.Flags(many).Length).IsEqualTo(12);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CommandEventsTests/*"`
Expected: FAIL — `CommandEvents` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Decides what may be said about a command invocation. Everything here is allow-by-exception:
/// an unrecognised subcommand or a malformed flag is dropped, so new commands and new flags are
/// silent until deliberately added rather than leaking by default.
/// </summary>
public static partial class CommandEvents {
    // Machine-driven surfaces. `hook` runs on every tool use of every recorded session —
    // thousands per user per day, inline in the agent's critical path — and the rest are
    // spawned by agents or long-lived processes rather than typed by a person.
    static readonly HashSet<string> Denylisted = new(StringComparer.Ordinal) {
        "hook", "watch", "mcp", "permission-request", "generate-whats-done",
        "set-title", "copilot-finalize", "cursor-verify-appendonly",
    };

    // Verbs whose args[1] is a known literal rather than user data. Verbs absent from this
    // map report no subcommand at all — which is what keeps `recap <sessionId>`,
    // `ignore <path>`, `hide <sessionId>` and `remap <path>` from ever reporting a positional.
    static readonly Dictionary<string, HashSet<string>> Subcommands = new(StringComparer.Ordinal) {
        ["daemon"]  = new(StringComparer.Ordinal) { "start", "stop", "status", "restart", "logs", "consent", "reviewer" },
        ["plugin"]  = new(StringComparer.Ordinal) { "install", "remove", "status" },
        ["config"]  = new(StringComparer.Ordinal) { "show", "set", "unset" },
        ["profile"] = new(StringComparer.Ordinal) { "list", "add", "remove", "show" },
        ["curate"]  = new(StringComparer.Ordinal) { "apply" },
        ["agent"]   = new(StringComparer.Ordinal) { "start", "stop", "list", "status" },
    };

    const int MaxFlags = 12;

    public static bool IsReportable(string command) => !Denylisted.Contains(command);

    public static string? Subcommand(string command, string[] args) {
        if (args.Length < 2) return null;
        if (!Subcommands.TryGetValue(command, out var known)) return null;

        return known.Contains(args[1]) ? args[1] : null;
    }

    /// <summary>
    /// Flag NAMES only, sorted and deduplicated. Admitted by shape, not by a name allowlist:
    /// the pattern cannot express a path, URL, GUID, or email address, so nothing identifying
    /// survives regardless of what future commands introduce.
    ///
    /// The 37-character bound is load-bearing and sits in a narrow window. At 40 this admitted
    /// `--`-prefixed GUIDs — a UUID's alphabet is lowercase hex plus hyphen, exactly this
    /// character class, so any GUID starting with a hex letter matched. The window's floor is
    /// the longest real kcap flag, `--skip-antigravity-instructions` (31); its ceiling is a
    /// GUID token (38). Both edges are pinned by tests: relaxing re-admits GUIDs, tightening
    /// below 31 silently drops a real flag.
    /// </summary>
    public static string[] Flags(string[] args) =>
        args.Where(a => a.StartsWith("--", StringComparison.Ordinal))
            .Select(a => a.Split('=', 2)[0])
            .Where(a => FlagShape().IsMatch(a))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(MaxFlags)
            .ToArray();

    [GeneratedRegex(@"^--[a-z][a-z0-9-]{0,34}$")]
    private static partial Regex FlagShape();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CommandEventsTests/*"`
Expected: PASS — 30 test cases.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/CommandEvents.cs test/Capacitor.Cli.Tests.Unit/Telemetry/CommandEventsTests.cs
git commit -m "Add command event construction with allowlisted subcommands and shape-checked flags"
```

---

### Task 4: Event model and PostHog batch payload

**Files:**
- Create: `src/Capacitor.Cli.Core/Telemetry/TelemetryEvent.cs`
- Create: `src/Capacitor.Cli.Core/Telemetry/PostHogPayload.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/PostHogPayloadTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `TelemetryEvent(string Name, JsonObject Properties, DateTimeOffset Timestamp)`; `PostHogPayload.OrgGroup(string? serverUrl) → string?`; `PostHogPayload.Build(IReadOnlyList<TelemetryEvent> events, string token, string distinctId, string? orgGroup) → string`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class PostHogPayloadTests {
    static TelemetryEvent Event(string name) =>
        new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UnixEpoch);

    static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Batch_carries_token_and_events() {
        var json = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: null);
        var root = Parse(json);

        await Assert.That(root["api_key"]!.GetValue<string>()).IsEqualTo("phc_test");
        await Assert.That(root["batch"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(root["batch"]![0]!["event"]!.GetValue<string>()).IsEqualTo("cli_command");
    }

    [Test]
    public async Task Every_event_carries_distinct_id_and_suppresses_geoip() {
        var json  = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: null);
        var props = Parse(json)["batch"]![0]!["properties"]!.AsObject();

        await Assert.That(props["distinct_id"]!.GetValue<string>()).IsEqualTo("device-1");
        await Assert.That(props["$ip"]).IsNull();
        await Assert.That(props.ContainsKey("$ip")).IsTrue();
    }

    [Test]
    public async Task Org_group_and_property_are_attached_together() {
        var json  = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: "acme");
        var props = Parse(json)["batch"]![0]!["properties"]!.AsObject();

        await Assert.That(props["$groups"]!["organization"]!.GetValue<string>()).IsEqualTo("acme");
        await Assert.That(props["org"]!.GetValue<string>()).IsEqualTo("acme");
    }

    [Test]
    public async Task Org_group_and_property_are_both_absent_when_null() {
        var json  = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: null);
        var props = Parse(json)["batch"]![0]!["properties"]!.AsObject();

        await Assert.That(props.ContainsKey("$groups")).IsFalse();
        await Assert.That(props.ContainsKey("org")).IsFalse();
    }

    [Test]
    public async Task Existing_properties_survive() {
        var json  = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", orgGroup: null);
        var props = Parse(json)["batch"]![0]!["properties"]!.AsObject();

        await Assert.That(props["source"]!.GetValue<string>()).IsEqualTo("cli");
    }

    [Test]
    public async Task Timestamp_is_round_trip_iso8601() {
        var json = PostHogPayload.Build([Event("cli_command")], "phc_test", "device-1", null);
        var ts   = Parse(json)["batch"]![0]!["timestamp"]!.GetValue<string>();

        await Assert.That(DateTimeOffset.Parse(ts)).IsEqualTo(DateTimeOffset.UnixEpoch);
    }

    // The org group is only sound where the Helm chart guarantees Tenant__Name == slug.
    [Test]
    [Arguments("https://acme.kcap.ai", "acme")]
    [Arguments("https://acme.kcap.ai/", "acme")]
    [Arguments("https://ACME.kcap.ai", "acme")]
    public async Task Saas_urls_yield_the_slug(string url, string expected) {
        await Assert.That(PostHogPayload.OrgGroup(url)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://capacitor.internal.corp")]
    [Arguments("http://localhost:5000")]
    [Arguments("https://kcap.ai")]
    [Arguments("not a url")]
    [Arguments(null)]
    public async Task Non_saas_urls_yield_no_group(string? url) {
        await Assert.That(PostHogPayload.OrgGroup(url)).IsNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/PostHogPayloadTests/*"`
Expected: FAIL — `PostHogPayload` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/Capacitor.Cli.Core/Telemetry/TelemetryEvent.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>One captured event. Properties are a <see cref="JsonObject"/> rather than a typed
/// record because the property set varies per event name; serialisation goes through
/// <c>JsonNode.ToJsonString()</c>, which is AOT-safe.</summary>
public sealed record TelemetryEvent(string Name, JsonObject Properties, DateTimeOffset Timestamp);
```

Create `src/Capacitor.Cli.Core/Telemetry/PostHogPayload.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>Builds the PostHog <c>/batch/</c> request body.</summary>
public static class PostHogPayload {
    const string SaasSuffix = ".kcap.ai";

    /// <summary>
    /// The `organization` group value, or null when it cannot be derived soundly.
    ///
    /// The server sets the group from `Tenant:Name`, which the Helm chart populates from the
    /// tenant slug — and a SaaS tenant is served at {slug}.kcap.ai, so the host label IS the
    /// group. That correspondence exists ONLY for SaaS: on a self-hosted deployment
    /// `Tenant:Name` defaults to "local" and is otherwise operator-chosen with no relationship
    /// to the hostname, so deriving a group there would produce one that looks joined to the
    /// server's but is not.
    /// </summary>
    public static string? OrgGroup(string? serverUrl) {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant();
        if (!host.EndsWith(SaasSuffix, StringComparison.Ordinal)) return null;

        var slug = host[..^SaasSuffix.Length];

        return slug.Length == 0 || slug.Contains('.') ? null : slug;
    }

    public static string Build(
            IReadOnlyList<TelemetryEvent> events, string token, string distinctId, string? orgGroup) {
        var batch = new JsonArray();

        foreach (var e in events) {
            var props = (JsonObject)e.Properties.DeepClone();
            props["distinct_id"] = distinctId;
            // Both are needed. $ip alone does NOT suppress geo-IP: PostHog populates it from the
            // incoming connection and runs GeoIP off that, so a null value falls back to the
            // request IP. $geoip_disable is the documented switch.
            props["$ip"]            = null;
            props["$geoip_disable"] = true;

            // Group and property travel together, and only for SaaS. Deriving an `org` from a
            // self-hosted host label would put an internal hostname fragment in the data for no
            // analytical gain — it has no relationship to the server's own Tenant:Name.
            if (orgGroup is not null) {
                props["$groups"] = new JsonObject { ["organization"] = orgGroup };
                props["org"]     = orgGroup;
            }

            // Typed as JsonNode? deliberately: a JsonObject argument would bind the generic
            // JsonArray.Add<T>, which is RequiresDynamicCode and fails the AOT publish.
            JsonNode? entry = new JsonObject {
                ["event"]      = e.Name,
                ["properties"] = props,
                ["timestamp"]  = e.Timestamp.ToString("o"),
            };
            batch.Add(entry);
        }

        return new JsonObject {
            ["api_key"] = token,
            ["batch"]   = batch,
        }.ToJsonString();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/PostHogPayloadTests/*"`
Expected: PASS — 14 test cases.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/TelemetryEvent.cs src/Capacitor.Cli.Core/Telemetry/PostHogPayload.cs test/Capacitor.Cli.Tests.Unit/Telemetry/PostHogPayloadTests.cs
git commit -m "Add PostHog batch payload with SaaS-only org group derivation"
```

---

### Task 5: Bounded spool

**Files:**
- Create: `src/Capacitor.Cli.Core/Telemetry/TelemetrySpool.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/TelemetrySpoolTests.cs`

**Interfaces:**
- Consumes: `TelemetryEvent` (Task 4)
- Produces: `TelemetrySpool(string path)` with `.Append(IReadOnlyList<TelemetryEvent>)`, `.DrainAll() → IReadOnlyList<TelemetryEvent>`, `.Clear()`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class TelemetrySpoolTests {
    static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-spool-{Guid.NewGuid():N}", "telemetry-spool.jsonl");

    static TelemetryEvent Event(string name) =>
        new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UnixEpoch);

    [Test]
    public async Task Drain_of_missing_file_is_empty() {
        var spool = new TelemetrySpool(NewPath());

        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Appended_events_round_trip() {
        var spool = new TelemetrySpool(NewPath());
        spool.Append([Event("a"), Event("b")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(2);
        await Assert.That(drained[0].Name).IsEqualTo("a");
        await Assert.That(drained[1].Name).IsEqualTo("b");
        await Assert.That(drained[0].Properties["source"]!.GetValue<string>()).IsEqualTo("cli");
    }

    [Test]
    public async Task Appends_accumulate_across_instances() {
        var path = NewPath();
        new TelemetrySpool(path).Append([Event("a")]);
        new TelemetrySpool(path).Append([Event("b")]);

        await Assert.That(new TelemetrySpool(path).DrainAll().Count).IsEqualTo(2);
    }

    [Test]
    public async Task Clear_empties_the_spool() {
        var path  = NewPath();
        var spool = new TelemetrySpool(path);
        spool.Append([Event("a")]);
        spool.Clear();

        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Corrupt_lines_are_skipped_not_fatal() {
        var path = NewPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json\n");
        var spool = new TelemetrySpool(path);
        spool.Append([Event("good")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(1);
        await Assert.That(drained[0].Name).IsEqualTo("good");
    }

    // Drop-oldest keeps the newest events, which are the ones most likely to still matter.
    [Test]
    public async Task Oldest_events_are_dropped_past_the_cap() {
        var path  = NewPath();
        var spool = new TelemetrySpool(path, maxEvents: 10);

        for (var i = 0; i < 25; i++) spool.Append([Event($"e{i}")]);

        var drained = spool.DrainAll();

        await Assert.That(drained.Count).IsEqualTo(10);
        await Assert.That(drained[0].Name).IsEqualTo("e15");
        await Assert.That(drained[^1].Name).IsEqualTo("e24");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TelemetrySpoolTests/*"`
Expected: FAIL — `TelemetrySpool` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Failure fallback for the in-memory queue: events that could not be delivered land here and
/// are replayed by the next successful flush from any kcap process. Bounded and drop-oldest, so
/// a permanently offline machine can never grow the file without limit.
/// </summary>
public sealed class TelemetrySpool(string path, int maxEvents = 2000) {
    public void Append(IReadOnlyList<TelemetryEvent> events) {
        if (events.Count == 0) return;

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var lines = events.Select(Serialize).ToList();
            File.AppendAllLines(path, lines);
            Trim();
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort — losing spooled telemetry is never worth failing a command.
        }
    }

    public IReadOnlyList<TelemetryEvent> DrainAll() {
        if (!File.Exists(path)) return [];

        try {
            return File.ReadAllLines(path)
                       .Select(Deserialize)
                       .OfType<TelemetryEvent>()
                       .ToList();
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    public void Clear() {
        try {
            if (File.Exists(path)) File.Delete(path);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            // Best effort. A spool we fail to clear replays duplicates next time, which
            // over-counts slightly — strictly better than failing the user's command.
        }
    }

    void Trim() {
        var lines = File.ReadAllLines(path);
        if (lines.Length <= maxEvents) return;

        File.WriteAllLines(path, lines[^maxEvents..]);
    }

    static string Serialize(TelemetryEvent e) =>
        new JsonObject {
            ["event"]      = e.Name,
            ["properties"] = e.Properties.DeepClone(),
            ["timestamp"]  = e.Timestamp.ToString("o"),
        }.ToJsonString();

    static TelemetryEvent? Deserialize(string line) {
        try {
            if (JsonNode.Parse(line) is not JsonObject o) return null;
            var name = o["event"]?.GetValue<string>();
            var ts   = o["timestamp"]?.GetValue<string>();
            if (name is null || ts is null || o["properties"] is not JsonObject props) return null;

            return new TelemetryEvent(name, (JsonObject)props.DeepClone(), DateTimeOffset.Parse(ts));
        } catch (Exception e) when (e is System.Text.Json.JsonException or FormatException) {
            return null;   // a torn or hand-edited line is skipped, never fatal
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TelemetrySpoolTests/*"`
Expected: PASS — 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/TelemetrySpool.cs test/Capacitor.Cli.Tests.Unit/Telemetry/TelemetrySpoolTests.cs
git commit -m "Add bounded drop-oldest telemetry spool"
```

---

### Task 6: Client — queue, budgeted flush, spill and replay

**Files:**
- Create: `src/Capacitor.Cli.Core/Telemetry/TelemetryClient.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/TelemetryClientTests.cs`

**Interfaces:**
- Consumes: `TelemetryEvent`, `PostHogPayload` (Task 4); `TelemetrySpool` (Task 5)
- Produces: `TelemetryClient(HttpMessageHandler handler, TelemetrySpool spool, string token, string endpoint)` with `.Enqueue(TelemetryEvent)`, `.FlushAsync(string distinctId, string? orgGroup, TimeSpan budget) → Task<bool>`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class TelemetryClientTests {
    sealed class StubHandler(HttpStatusCode status, Exception? throws = null) : HttpMessageHandler {
        public int    Calls    { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (throws is not null) throw throws;

            return new HttpResponseMessage(status);
        }
    }

    static string NewSpoolPath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-client-{Guid.NewGuid():N}", "spool.jsonl");

    static TelemetryEvent Event(string name) =>
        new(name, new JsonObject { ["source"] = "cli" }, DateTimeOffset.UnixEpoch);

    static TelemetryClient Client(StubHandler handler, out TelemetrySpool spool) {
        spool = new TelemetrySpool(NewSpoolPath());
        return new TelemetryClient(handler, spool, "phc_test", "https://phog.example");
    }

    [Test]
    public async Task Flush_with_empty_queue_makes_no_request() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client  = Client(handler, out _);

        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(handler.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Flush_posts_queued_events() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client  = Client(handler, out _);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsTrue();
        await Assert.That(handler.Calls).IsEqualTo(1);
        await Assert.That(handler.LastBody!.Contains("cli_command")).IsTrue();
        await Assert.That(handler.LastBody!.Contains("device-1")).IsTrue();
    }

    [Test]
    public async Task Successful_flush_empties_the_queue() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client  = Client(handler, out _);
        client.Enqueue(Event("cli_command"));

        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));
        await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(handler.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Failed_flush_spills_to_the_spool() {
        var handler = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var client  = Client(handler, out var spool);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsFalse();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Network_exception_spills_rather_than_propagating() {
        var handler = new StubHandler(HttpStatusCode.OK, new HttpRequestException("offline"));
        var client  = Client(handler, out var spool);
        client.Enqueue(Event("cli_command"));

        var ok = await client.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(ok).IsFalse();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(1);
    }

    [Test]
    public async Task Spooled_events_are_replayed_on_the_next_flush() {
        var failing = new StubHandler(HttpStatusCode.ServiceUnavailable);
        var spool   = new TelemetrySpool(NewSpoolPath());

        var first = new TelemetryClient(failing, spool, "phc_test", "https://phog.example");
        first.Enqueue(Event("offline_event"));
        await first.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        var ok      = new StubHandler(HttpStatusCode.OK);
        var second  = new TelemetryClient(ok, spool, "phc_test", "https://phog.example");
        second.Enqueue(Event("fresh_event"));
        var flushed = await second.FlushAsync("device-1", null, TimeSpan.FromSeconds(2));

        await Assert.That(flushed).IsTrue();
        await Assert.That(ok.LastBody!.Contains("offline_event")).IsTrue();
        await Assert.That(ok.LastBody!.Contains("fresh_event")).IsTrue();
        await Assert.That(spool.DrainAll().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Org_group_reaches_the_payload() {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client  = Client(handler, out _);
        client.Enqueue(Event("cli_command"));

        await client.FlushAsync("device-1", "acme", TimeSpan.FromSeconds(2));

        await Assert.That(handler.LastBody!.Contains("organization")).IsTrue();
        await Assert.That(handler.LastBody!.Contains("acme")).IsTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TelemetryClientTests/*"`
Expected: FAIL — `TelemetryClient` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Net.Http.Headers;
using System.Text;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Queues events and ships them to PostHog's <c>/batch/</c> endpoint under a wall-clock budget.
/// A flush that fails for any reason spills to the spool instead of retrying inline — the
/// caller is on a user's command path and must not wait on a broken network.
/// </summary>
public sealed class TelemetryClient(
        HttpMessageHandler handler, TelemetrySpool spool, string token, string endpoint) {
    readonly List<TelemetryEvent> _queue = [];

    public void Enqueue(TelemetryEvent e) {
        lock (_queue) _queue.Add(e);
    }

    /// <summary>Ships queued + previously spooled events. Returns false when nothing reached
    /// PostHog, in which case everything has been spooled for a later attempt.</summary>
    public async Task<bool> FlushAsync(string distinctId, string? orgGroup, TimeSpan budget) {
        List<TelemetryEvent> queued;
        lock (_queue) {
            queued = [.. _queue];
            _queue.Clear();
        }

        // Drained OUTSIDE the lock: it is file I/O, and blocking a concurrent Enqueue on disk
        // is not what the queue lock is for.
        //
        // Spooled and queued events are kept apart deliberately. DrainAll is a read, not a take
        // — only Clear() empties the file — so re-appending the spooled ones on failure would
        // duplicate them on every retry, and the eventual success would ship the duplicates to
        // PostHog. Only the freshly-queued events need spilling; the spooled ones are on disk
        // already.
        var spooled = spool.DrainAll();
        var pending = new List<TelemetryEvent>(spooled.Count + queued.Count);
        pending.AddRange(spooled);   // spool first: previously-failed events keep their place in the funnel
        pending.AddRange(queued);

        if (pending.Count == 0) return true;

        try {
            var body = PostHogPayload.Build(pending, token, distinctId, orgGroup);

            using var http = new HttpClient(handler, disposeHandler: false) { Timeout = budget };
            using var cts  = new CancellationTokenSource(budget);
            using var content = new StringContent(body, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            var response = await http.PostAsync($"{endpoint.TrimEnd('/')}/batch/", content, cts.Token);

            if (!response.IsSuccessStatusCode) {
                spool.Append(queued);
                return false;
            }

            spool.Clear();
            return true;
        } catch (Exception) {
            // Broad by design, same as TelemetrySpool.Deserialize: "never throw" is absolute here,
            // and an enumerated filter has already missed ArgumentOutOfRangeException (a
            // non-positive budget reaches HttpClient.Timeout and the CTS ctor) once.
            spool.Append(queued);
            return false;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/TelemetryClientTests/*"`
Expected: PASS — 7 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/TelemetryClient.cs test/Capacitor.Cli.Tests.Unit/Telemetry/TelemetryClientTests.cs
git commit -m "Add telemetry client with budgeted flush and spill-on-failure"
```

---

### Task 7: The `CliTelemetry` facade

**Files:**
- Create: `src/Capacitor.Cli.Core/Telemetry/CliTelemetry.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/CliTelemetryTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1–6
- Produces: `CliTelemetry.Initialize(string command, string? serverUrl, bool loggedIn)`; `.Capture(string name, JsonObject props)`; `.CaptureNow(string name, JsonObject props)`; `.RecordCommand(string command, string[] args, int exitCode, long durationMs)`; `.FlushAndClose()`; `CliTelemetry.Enabled`

**Context:** This is the only type call sites touch. Every method swallows — an exception escaping here would abort the NativeAOT process. `TestSink` lets later tasks assert on emitted event names without a network.

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class CliTelemetryTests {
    static string NewStatePath() =>
        Path.Combine(Path.GetTempPath(), $"kcap-facade-{Guid.NewGuid():N}", "telemetry.json");

    static List<TelemetryEvent> StartCapturing(string command = "setup", string? serverUrl = null) {
        TelemetryState.PathOverride = NewStatePath();
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize(command, serverUrl, loggedIn: false);

        return sink;
    }

    [Test]
    public async Task Capture_records_the_event_with_shared_properties() {
        var sink = StartCapturing();

        CliTelemetry.Capture("cli_setup_started", new JsonObject { ["no_prompt"] = false });

        await Assert.That(sink.Count).IsEqualTo(1);
        await Assert.That(sink[0].Name).IsEqualTo("cli_setup_started");
        await Assert.That(sink[0].Properties["source"]!.GetValue<string>()).IsEqualTo("cli");
        await Assert.That(sink[0].Properties.ContainsKey("cli_version")).IsTrue();
        await Assert.That(sink[0].Properties.ContainsKey("os")).IsTrue();
        await Assert.That(sink[0].Properties.ContainsKey("arch")).IsTrue();
        await Assert.That(sink[0].Properties.ContainsKey("is_ci")).IsTrue();
        await Assert.That(sink[0].Properties["no_prompt"]!.GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Record_command_emits_cli_command_with_exit_code() {
        var sink = StartCapturing("daemon");

        CliTelemetry.RecordCommand("daemon", ["daemon", "start", "--foreground"], exitCode: 0, durationMs: 42);

        var e = sink.Single(x => x.Name == "cli_command");
        await Assert.That(e.Properties["command"]!.GetValue<string>()).IsEqualTo("daemon");
        await Assert.That(e.Properties["subcommand"]!.GetValue<string>()).IsEqualTo("start");
        await Assert.That(e.Properties["exit_code"]!.GetValue<int>()).IsEqualTo(0);
        await Assert.That(e.Properties["duration_ms"]!.GetValue<long>()).IsEqualTo(42L);
    }

    [Test]
    public async Task Denylisted_commands_emit_nothing() {
        var sink = StartCapturing("hook");

        CliTelemetry.RecordCommand("hook", ["hook", "--claude"], exitCode: 0, durationMs: 5);

        await Assert.That(sink.Any(x => x.Name == "cli_command")).IsFalse();
    }

    [Test]
    public async Task Disabled_telemetry_captures_nothing() {
        TelemetryState.PathOverride = NewStatePath();
        TelemetryState.SetEnabled(false);
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("setup", null, loggedIn: false);

        CliTelemetry.Capture("cli_setup_started", new JsonObject());
        CliTelemetry.RecordCommand("setup", ["setup"], 0, 1);

        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(sink.Count).IsEqualTo(0);
    }

    // An uninitialised facade must be inert, not merely non-throwing: a swallowed exception and
    // a correctly-skipped capture look identical from the outside unless state is asserted.
    [Test]
    public async Task Capture_before_initialize_is_inert() {
        CliTelemetry.Reset();
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;

        CliTelemetry.Capture("orphan", new JsonObject());
        CliTelemetry.RecordCommand("status", ["status"], 0, 1);
        await CliTelemetry.FlushAndClose();

        await Assert.That(CliTelemetry.Enabled).IsFalse();
        await Assert.That(sink.Count).IsEqualTo(0);
    }

    [Test]
    public async Task First_run_emits_cli_first_run_once_per_device() {
        var path = NewStatePath();

        TelemetryState.PathOverride = path;
        var firstSink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = firstSink;
        CliTelemetry.Initialize("setup", null, loggedIn: false);

        TelemetryState.PathOverride = path;
        var secondSink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = secondSink;
        CliTelemetry.Initialize("status", null, loggedIn: false);

        await Assert.That(firstSink.Any(e => e.Name == "cli_first_run")).IsTrue();
        await Assert.That(secondSink.Any(e => e.Name == "cli_first_run")).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CliTelemetryTests/*"`
Expected: FAIL — `CliTelemetry` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// The only telemetry surface call sites touch. Every method swallows every exception:
/// an exception escaping to the NativeAOT runtime aborts the process (see Program.cs), so a
/// telemetry bug must never become a crash-on-every-command regression.
/// </summary>
public static class CliTelemetry {
    const string Endpoint = "https://phog.kurrent.io";
    const string Token    = "phc_DeHBgHGersY4LmDlADnPrsCPOAmMO7QFOH8f4DVEVmD";

    static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(1.5);

    static TelemetryClient? _client;
    static string?          _deviceId;
    static string?          _orgGroup;
    static JsonObject       _shared = new();
    static bool             _debug;

    /// <summary>Test seam: when set, events are collected here instead of being queued.</summary>
    public static List<TelemetryEvent>? TestSink { get; set; }

    public static bool Enabled { get; private set; }

    public static void Reset() {
        _client = null; _deviceId = null; _orgGroup = null;
        _shared = new JsonObject(); Enabled = false; TestSink = null;
    }

    public static void Initialize(string command, string? serverUrl, bool loggedIn) {
        try {
            Enabled = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled()).Enabled
                   && CommandEvents.IsReportable(command);
            if (!Enabled) return;

            _debug    = Environment.GetEnvironmentVariable("KCAP_TELEMETRY_DEBUG") == "1";
            _deviceId = TelemetryState.GetOrCreateDeviceId();
            if (_deviceId is null) { Enabled = false; return; }

            _orgGroup = PostHogPayload.OrgGroup(serverUrl);
            _shared   = new JsonObject {
                ["source"]      = "cli",
                ["cli_version"] = Version(),
                ["os"]          = OS(),
                ["arch"]        = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                ["is_ci"]       = IsCi(),
                ["is_headless"] = Auth.HeadlessEnvironment.IsHeadless(),
                ["has_server"]  = serverUrl is not null,
                ["logged_in"]   = loggedIn,
            };

            if (TestSink is null)
                _client = new TelemetryClient(new HttpClientHandler(), Spool(), Token, Endpoint);

            NoticeAndFirstRun();
        } catch {
            Enabled = false;
        }
    }

    /// <summary>Queue an event for the exit flush.</summary>
    public static void Capture(string name, JsonObject properties) {
        try {
            if (!Enabled) return;

            foreach (var (key, value) in _shared)
                properties[key] ??= value?.DeepClone();

            var e = new TelemetryEvent(name, properties, DateTimeOffset.UtcNow);

            if (_debug) Console.Error.WriteLine($"[telemetry] {name} {properties.ToJsonString()}");

            if (TestSink is not null) TestSink.Add(e);
            else                      _client?.Enqueue(e);
        } catch { }
    }

    /// <summary>Queue an event and flush immediately. Used for setup funnel steps, where the
    /// run may be abandoned before it ever reaches the exit flush.</summary>
    public static void CaptureNow(string name, JsonObject properties) {
        Capture(name, properties);
        FlushAndClose().GetAwaiter().GetResult();
    }

    public static void RecordCommand(string command, string[] args, int exitCode, long durationMs) {
        try {
            if (!Enabled || !CommandEvents.IsReportable(command)) return;

            var props = new JsonObject {
                ["command"]     = command,
                ["exit_code"]   = exitCode,
                ["duration_ms"] = durationMs,
            };

            if (CommandEvents.Subcommand(command, args) is { } sub) props["subcommand"] = sub;

            var flags = CommandEvents.Flags(args);
            if (flags.Length > 0) {
                var arr = new JsonArray();
                // Typed as JsonNode? deliberately: arr.Add(f) on a string binds the generic
                // JsonArray.Add<T>, which is RequiresDynamicCode and fails the AOT publish.
                foreach (var f in flags) {
                    JsonNode? node = JsonValue.Create(f);
                    arr.Add(node);
                }
                props["flags"] = arr;
            }

            Capture("cli_command", props);
        } catch { }
    }

    public static async Task FlushAndClose() {
        try {
            if (_client is null || _deviceId is null) return;
            await _client.FlushAsync(_deviceId, _orgGroup, FlushBudget);
        } catch { }
    }

    static void NoticeAndFirstRun() {
        if (TelemetryState.Read().NoticeShown) return;

        Console.Error.WriteLine(
            "kcap collects anonymous usage data — command names only, never arguments, file paths, or");
        Console.Error.WriteLine(
            "transcript content. Opt out: kcap config set telemetry off (or DO_NOT_TRACK=1).");
        Console.Error.WriteLine("https://capacitor.kurrent.io/privacy");

        TelemetryState.MarkNoticeShown();
        Capture("cli_first_run", new JsonObject());
    }

    static TelemetrySpool Spool() => new(PathHelpers.ConfigPath("telemetry-spool.jsonl"));

    static string Version() =>
        typeof(CliTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    static string OS() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)   ? "macos"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
        : "other";

    // CI machines are ephemeral and mint a fresh device id per run, so they are tagged rather
    // than dropped — funnel insights filter is_ci = false.
    static bool IsCi() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"))
     || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CliTelemetryTests/*"`
Expected: PASS — 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/CliTelemetry.cs test/Capacitor.Cli.Tests.Unit/Telemetry/CliTelemetryTests.cs
git commit -m "Add CliTelemetry facade with first-run notice"
```

---

### Task 8: Wire `Program.cs`

**Files:**
- Modify: `src/Capacitor.Cli/Program.cs` — after the `baseUrl` resolution at line 78, and a `ProcessExit` handler
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/CommandTimingTests.cs`

**Interfaces:**
- Consumes: `CliTelemetry.Initialize`, `.RecordCommand`, `.FlushAndClose` (Task 7)
- Produces: nothing consumed by later tasks

**Context:** A spike confirmed `ProcessExit` observes `Environment.ExitCode` set by a top-level `Main`'s `return`, so the ~630-line dispatch switch does not need restructuring. `Stopwatch.GetTimestamp()` is already captured at line 66 as `hookProcessStart`.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class CommandTimingTests {
    [Test]
    public async Task Elapsed_ms_is_derived_from_stopwatch_ticks() {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Thread.Sleep(15);

        var elapsed = CommandTiming.ElapsedMs(start);

        await Assert.That(elapsed >= 10).IsTrue();
        await Assert.That(elapsed < 5_000).IsTrue();
    }

    [Test]
    public async Task Elapsed_ms_is_never_negative() {
        await Assert.That(CommandTiming.ElapsedMs(System.Diagnostics.Stopwatch.GetTimestamp() + 1_000_000) >= 0).IsTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CommandTimingTests/*"`
Expected: FAIL — `CommandTiming` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/Capacitor.Cli.Core/Telemetry/CommandTiming.cs`:

```csharp
using System.Diagnostics;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>Wall-clock duration of a command, in milliseconds, from a
/// <see cref="Stopwatch.GetTimestamp"/> reading. Clamped at zero so a clock adjustment can
/// never produce a negative duration in the data.</summary>
public static class CommandTiming {
    public static long ElapsedMs(long startTimestamp) {
        var elapsed = Stopwatch.GetElapsedTime(startTimestamp, Stopwatch.GetTimestamp());

        return Math.Max(0, (long)elapsed.TotalMilliseconds);
    }
}
```

In `src/Capacitor.Cli/Program.cs`, add `using Capacitor.Cli.Core.Telemetry;` to the top, then insert immediately after the `baseUrl` assignment (line 78):

```csharp
// Telemetry: initialised once the server URL is known (it decides the `organization` group) and
// torn down from ProcessExit, which observes the exit code returned by top-level Main. Every
// call swallows, so nothing here can fail a command.
var commandStart = System.Diagnostics.Stopwatch.GetTimestamp();

// TokenStore.LoadAsync() is the LOCAL read (src/Capacitor.Cli.Core/Auth/TokenStore.cs:211) —
// deliberately not GetValidTokensAsync(), which can refresh over the network. `logged_in` is a
// cheap fact about disk, never a reason to make a request on the command path.
var loggedIn = false;
try { loggedIn = await TokenStore.LoadAsync() is not null; } catch { }

CliTelemetry.Initialize(command, baseUrl, loggedIn);

AppDomain.CurrentDomain.ProcessExit += (_, _) => {
    CliTelemetry.RecordCommand(command, args, Environment.ExitCode, CommandTiming.ElapsedMs(commandStart));
    CliTelemetry.FlushAndClose().GetAwaiter().GetResult();
};
```

Add `using Capacitor.Cli.Core.Auth;` if `Program.cs` does not already import it.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/CommandTimingTests/*"`
Expected: PASS — 2 tests.

Then verify end-to-end manually:

```bash
dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj
KCAP_TELEMETRY_DEBUG=1 KCAP_TELEMETRY=1 dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- status
```
Expected: a `[telemetry] cli_command …` line on stderr containing `"command":"status"`.

```bash
KCAP_TELEMETRY_DEBUG=1 DO_NOT_TRACK=1 dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- status
```
Expected: no `[telemetry]` output at all.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/CommandTiming.cs src/Capacitor.Cli/Program.cs test/Capacitor.Cli.Tests.Unit/Telemetry/CommandTimingTests.cs
git commit -m "Wire cli_command telemetry into the CLI entry point"
```

---

### Task 9: Setup funnel instrumentation

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs` — `RunDiscoveryAsync` (line 789) and the setup entry point
- Modify: `src/Capacitor.Cli/Commands/SpectreTenantProvisioner.cs` — the offer, request, and poll outcomes
- Create: `src/Capacitor.Cli.Core/Telemetry/SetupFunnel.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/SetupFunnelTests.cs`

**Interfaces:**
- Consumes: `CliTelemetry.CaptureNow` (Task 7)
- Produces: `SetupFunnel.Started(bool, bool, bool)`, `.SigninOpened(string, string)`, `.SigninCompleted(string)`, `.SigninFailed(string)`, `.TenantNone(string)`, `.WorkspaceOffered()`, `.WorkspaceDeclined()`, `.WorkspaceRequested()`, `.WorkspaceProvisioned()`, `.WorkspaceFailed(string)`, `.Succeeded(int)`

**Context:** These flush eagerly. The cohort being measured never runs `kcap` again, so anything deferred to the exit flush — let alone to a later invocation — is lost.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class SetupFunnelTests {
    static List<TelemetryEvent> StartCapturing() {
        TelemetryState.PathOverride =
            Path.Combine(Path.GetTempPath(), $"kcap-funnel-{Guid.NewGuid():N}", "telemetry.json");
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("setup", null, loggedIn: false);
        sink.Clear();   // drop cli_first_run

        return sink;
    }

    [Test]
    public async Task Happy_path_emits_the_full_sequence() {
        var sink = StartCapturing();

        SetupFunnel.Started(hasExistingProfile: false, serverUrlProvided: false, noPrompt: false);
        SetupFunnel.SigninOpened("browser", "workos");
        SetupFunnel.SigninCompleted("workos");
        SetupFunnel.TenantNone("workos");
        SetupFunnel.WorkspaceOffered();
        SetupFunnel.WorkspaceRequested();
        SetupFunnel.WorkspaceProvisioned();
        SetupFunnel.Succeeded(agentsConfigured: 3);

        await Assert.That(sink.Select(e => e.Name).ToArray()).IsEquivalentTo(new[] {
            "cli_setup_started", "cli_setup_signin_opened", "cli_setup_signin_completed",
            "cli_setup_tenant_none", "cli_setup_workspace_offered", "cli_setup_workspace_requested",
            "cli_setup_workspace_provisioned", "cli_setup_succeeded",
        });
    }

    [Test]
    public async Task Abandoned_at_signup_stops_after_the_offer() {
        var sink = StartCapturing();

        SetupFunnel.Started(false, false, false);
        SetupFunnel.SigninCompleted("workos");
        SetupFunnel.TenantNone("workos");
        SetupFunnel.WorkspaceOffered();
        SetupFunnel.WorkspaceDeclined();

        await Assert.That(sink[^1].Name).IsEqualTo("cli_setup_workspace_declined");
        await Assert.That(sink.Any(e => e.Name == "cli_setup_succeeded")).IsFalse();
    }

    [Test]
    public async Task Provisioning_failure_carries_a_reason() {
        var sink = StartCapturing();

        SetupFunnel.WorkspaceFailed("slug_taken");

        await Assert.That(sink[^1].Name).IsEqualTo("cli_setup_workspace_failed");
        await Assert.That(sink[^1].Properties["reason"]!.GetValue<string>()).IsEqualTo("slug_taken");
    }

    [Test]
    public async Task Started_carries_its_entry_conditions() {
        var sink = StartCapturing();

        SetupFunnel.Started(hasExistingProfile: true, serverUrlProvided: true, noPrompt: true);

        var props = sink[0].Properties;
        await Assert.That(props["has_existing_profile"]!.GetValue<bool>()).IsTrue();
        await Assert.That(props["server_url_provided"]!.GetValue<bool>()).IsTrue();
        await Assert.That(props["no_prompt"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Succeeded_reports_a_count_not_vendor_names() {
        var sink = StartCapturing();

        SetupFunnel.Succeeded(agentsConfigured: 4);

        await Assert.That(sink[^1].Properties["agents_configured"]!.GetValue<int>()).IsEqualTo(4);
    }

    // Guards the collision with the server's own cli_setup_completed.
    [Test]
    public async Task No_funnel_event_collides_with_a_server_event_name() {
        string[] serverEvents = [
            "user_registered", "user_logged_in", "cli_setup_completed", "session_ingest_started",
            "session_ingest_ended", "eval_ran", "fact_retained", "daemon_connected",
            "daemon_disconnected", "hosted_agent_started", "hosted_agent_ended",
        ];

        var sink = StartCapturing();
        SetupFunnel.Started(false, false, false);
        SetupFunnel.SigninOpened("browser", "workos");
        SetupFunnel.SigninCompleted("workos");
        SetupFunnel.SigninFailed("timeout");
        SetupFunnel.TenantNone("workos");
        SetupFunnel.WorkspaceOffered();
        SetupFunnel.WorkspaceDeclined();
        SetupFunnel.WorkspaceRequested();
        SetupFunnel.WorkspaceProvisioned();
        SetupFunnel.WorkspaceFailed("poll_timeout");
        SetupFunnel.Succeeded(1);

        foreach (var e in sink)
            await Assert.That(serverEvents.Contains(e.Name)).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SetupFunnelTests/*"`
Expected: FAIL — `SetupFunnel` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/Capacitor.Cli.Core/Telemetry/SetupFunnel.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// The signup funnel. Every step flushes eagerly rather than waiting for the exit flush: the
/// population this exists to measure abandons setup and never runs kcap again, so a deferred
/// event is a lost event.
///
/// Names deliberately avoid `cli_setup_completed`, which the SERVER already emits — a second
/// producer of that name would double-count across two different persons.
/// </summary>
public static class SetupFunnel {
    public static void Started(bool hasExistingProfile, bool serverUrlProvided, bool noPrompt) =>
        Emit("cli_setup_started", new JsonObject {
            ["has_existing_profile"] = hasExistingProfile,
            ["server_url_provided"]  = serverUrlProvided,
            ["no_prompt"]            = noPrompt,
        });

    public static void SigninOpened(string mode, string provider) =>
        Emit("cli_setup_signin_opened", new JsonObject { ["mode"] = mode, ["provider"] = provider });

    public static void SigninCompleted(string provider) =>
        Emit("cli_setup_signin_completed", new JsonObject { ["provider"] = provider });

    public static void SigninFailed(string reason) =>
        Emit("cli_setup_signin_failed", new JsonObject { ["reason"] = reason });

    public static void TenantNone(string provider) =>
        Emit("cli_setup_tenant_none", new JsonObject { ["provider"] = provider });

    public static void WorkspaceOffered()     => Emit("cli_setup_workspace_offered", new JsonObject());
    public static void WorkspaceDeclined()    => Emit("cli_setup_workspace_declined", new JsonObject());
    public static void WorkspaceRequested()   => Emit("cli_setup_workspace_requested", new JsonObject());
    public static void WorkspaceProvisioned() => Emit("cli_setup_workspace_provisioned", new JsonObject());

    public static void WorkspaceFailed(string reason) =>
        Emit("cli_setup_workspace_failed", new JsonObject { ["reason"] = reason });

    public static void Succeeded(int agentsConfigured) =>
        Emit("cli_setup_succeeded", new JsonObject { ["agents_configured"] = agentsConfigured });

    static void Emit(string name, JsonObject props) => CliTelemetry.CaptureNow(name, props);
}
```

Now add the call sites.

In `src/Capacitor.Cli/Commands/SetupCommand.cs`, add `using Capacitor.Cli.Core.Telemetry;`. At the top of the setup entry point (the public `HandleAsync`), immediately after arguments are parsed:

```csharp
SetupFunnel.Started(
    hasExistingProfile: (await AppConfig.LoadProfileConfig()).Profiles.Count > 0,
    serverUrlProvided:  args.Contains("--server-url"),
    noPrompt:           args.Contains("--no-prompt"));
```

In `RunDiscoveryAsync` (line 789), after `provider` is chosen (line 803):

```csharp
SetupFunnel.SigninOpened(HeadlessEnvironment.IsHeadless() ? "device" : "browser", provider);
```

The WorkOS sign-in events go **inside `WorkOSDiscovery.RunAsync`**, not in `SetupCommand` after
`RunWithLiveAuthAsync` returns. That method performs sign-in, tenant enumeration *and* provisioning
internally, so anchoring on its return is wrong twice over:

- **Ordering:** `signin_completed` would arrive after `tenant_none` and `workspace_provisioned`, so an
  ordered PostHog funnel converts 0% past the sign-in step — destroying the "split by last step
  reached" this feature exists for.
- **Meaning:** its `ExitCode` is non-zero for a declined offer, a provisioning failure, "no tenant
  selected", and the retarget path (deliberately `ExitCode = 1`, and it can still reach
  `cli_setup_succeeded`). Keying `signin_failed` on it would make that metric mostly people whose
  sign-in worked fine.

Anchor on the point where live auth has succeeded and enumeration has not begun — the `auth is null`
branch and the line immediately after it:

```csharp
// inside the auth-failure branch
SetupFunnel.SigninFailed("workos_signin_failed");

// immediately after, before DiscoverWorkOSTenantsAsync
SetupFunnel.SigninCompleted(AuthProvider.WorkOS);
```

In the GitHub branch, after `AcquireGitHubTokenAsync` (line 848):

```csharp
if (ghToken is null) { SetupFunnel.SigninFailed("github_token_denied"); return null; }
SetupFunnel.SigninCompleted(AuthProvider.GitHubApp);
```

and after `discovery.RunAsync` (line 851):

```csharp
if (outcome.Tenants.Length == 0) SetupFunnel.TenantNone(AuthProvider.GitHubApp);
```

In `src/Capacitor.Cli/Commands/SpectreTenantProvisioner.cs`, add `using Capacitor.Cli.Core.Telemetry;` and:

- Immediately before the `SelectionPrompt` at line 26: `SetupFunnel.WorkspaceOffered();`
- On the `return ProvisionOffer.Declined;` path at line 51 and any other decline/cancel return: `SetupFunnel.WorkspaceDeclined();`
- Immediately before `client.ProvisionAsync` at line 61: `SetupFunnel.WorkspaceRequested();`
- On the successful `ProvisionOffer.Created(...)` return at line 64 and the successful `PollAsync` outcome: `SetupFunnel.WorkspaceProvisioned();`
- In `Reason409` handling at line 71: `SetupFunnel.WorkspaceFailed(outcome.Body?.Reason ?? "conflict");`
- On a poll timeout inside `PollAsync`: `SetupFunnel.WorkspaceFailed("poll_timeout");`

In `src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs`, the no-tenant fork is the branch at **line 82**, `if (result.Tenants.Length == 0) {`. Add as its first statement:

```csharp
SetupFunnel.TenantNone(AuthProvider.WorkOS);
```

This is the single most important event in the feature — it is the denominator for "reached signup". Note it fires *before* the `provisioner is null` check at line 83, so headless runs (which get a null provisioner and the "ask your admin" dead-end) are still counted as having reached the fork.

Finally, at the end of `SetupCommand.HandleAsync`, on the success return path:

```csharp
SetupFunnel.Succeeded(agentsConfigured: configuredAgentCount);
```

`configuredAgentCount` is the number of agents `CodingAgentsStep` configured. Read that step's return value; if it does not currently surface a count, pass the count of vendors whose hooks were installed as tracked locally in `HandleAsync`. Do not invent a number — if no count is reachable without restructuring, pass `0` and add a line to the Follow-ups section rather than guessing.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/SetupFunnelTests/*"`
Expected: PASS — 6 tests.

Then: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: builds clean, no warnings.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/SetupFunnel.cs src/Capacitor.Cli/Commands/SetupCommand.cs src/Capacitor.Cli/Commands/SpectreTenantProvisioner.cs src/Capacitor.Cli.Core/Auth/WorkOSDiscovery.cs test/Capacitor.Cli.Tests.Unit/Telemetry/SetupFunnelTests.cs
git commit -m "Instrument the setup signup funnel"
```

---

### Task 10: MCP tool-call events

**Files:**
- Create: `src/Capacitor.Cli.Core/Telemetry/McpTelemetry.cs`
- Modify: the nine MCP servers handling `tools/call` in `src/Capacitor.Cli/Commands/Mcp*Server.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/McpTelemetryTests.cs`

**Interfaces:**
- Consumes: `CliTelemetry.Capture` (Task 7)
- Produces: `McpTelemetry.ToolCalled(string server, string tool, bool ok, long durationMs)`

**Context:** MCP servers are long-lived, so they are denylisted for `cli_command` but *are* the place recap and memory usage shows up. `CliTelemetry.Initialize` is called with command `"mcp"`, which `CommandEvents.IsReportable` rejects — so `Initialize` must be given a reportable pseudo-command for MCP processes. Use `CliTelemetry.Initialize("mcp-server", …)` from each server's startup, which is not in the denylist.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class McpTelemetryTests {
    static List<TelemetryEvent> StartCapturing() {
        TelemetryState.PathOverride =
            Path.Combine(Path.GetTempPath(), $"kcap-mcp-{Guid.NewGuid():N}", "telemetry.json");
        var sink = new List<TelemetryEvent>();
        CliTelemetry.TestSink = sink;
        CliTelemetry.Initialize("mcp-server", null, loggedIn: false);
        sink.Clear();

        return sink;
    }

    [Test]
    public async Task Tool_call_records_server_tool_and_outcome() {
        var sink = StartCapturing();

        McpTelemetry.ToolCalled("kcap-memory", "search_memories", ok: true, durationMs: 120);

        var e = sink.Single();
        await Assert.That(e.Name).IsEqualTo("mcp_tool_called");
        await Assert.That(e.Properties["server"]!.GetValue<string>()).IsEqualTo("kcap-memory");
        await Assert.That(e.Properties["tool"]!.GetValue<string>()).IsEqualTo("search_memories");
        await Assert.That(e.Properties["ok"]!.GetValue<bool>()).IsTrue();
        await Assert.That(e.Properties["duration_ms"]!.GetValue<long>()).IsEqualTo(120L);
    }

    [Test]
    public async Task Failed_tool_call_is_recorded_as_not_ok() {
        var sink = StartCapturing();

        McpTelemetry.ToolCalled("kcap-sessions", "get_turn", ok: false, durationMs: 5);

        await Assert.That(sink.Single().Properties["ok"]!.GetValue<bool>()).IsFalse();
    }

    // Tool arguments can contain repo paths, prompts, and session ids.
    [Test]
    public async Task No_argument_data_is_carried() {
        var sink = StartCapturing();

        McpTelemetry.ToolCalled("kcap-memory", "save_memory", ok: true, durationMs: 1);

        var keys = sink.Single().Properties.Select(p => p.Key).ToArray();
        await Assert.That(keys.Contains("arguments")).IsFalse();
        await Assert.That(keys.Contains("params")).IsFalse();
        await Assert.That(keys.Contains("input")).IsFalse();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/McpTelemetryTests/*"`
Expected: FAIL — `McpTelemetry` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `src/Capacitor.Cli.Core/Telemetry/McpTelemetry.cs`:

```csharp
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Per-tool-call telemetry for the kcap MCP servers. The interesting unit is the CALL, not the
/// process: recap and memory are used through MCP rather than as terminal verbs, so a
/// process-start event would say nothing about usage.
///
/// Tool arguments are never recorded — they carry repo paths, prompts, and session ids.
/// </summary>
public static class McpTelemetry {
    public static void ToolCalled(string server, string tool, bool ok, long durationMs) =>
        CliTelemetry.Capture("mcp_tool_called", new JsonObject {
            ["server"]      = server,
            ["tool"]        = tool,
            ["ok"]          = ok,
            ["duration_ms"] = durationMs,
        });
}
```

Nine files handle `tools/call` — verified by grep, and note this is one more than an earlier draft of
this plan listed:

| File | `server` label |
|---|---|
| `McpMemoryServer.cs` | `kcap-memory` |
| `McpSessionsServer.cs` | `kcap-sessions` |
| `McpReviewServer.cs` | `kcap-review` |
| `McpFlowsServer.cs` | `kcap-flows` |
| `McpWorkItemsServer.cs` | `kcap-workitems` |
| `McpAnalyticsServer.cs` | `kcap-analytics` |
| `McpFlowResultServer.cs` | `kcap-flow-result` |
| `McpJudgeServer.cs` | `kcap-judge` |
| ~~`McpReviewContextServer.cs`~~ | **excluded — see below** |

The first six match `KcapMcpServers.All` registry names. The rest are internal servers with no
registry entry (they serve hosted reviewers and flows rather than a user's harness); their labels are
coined here to match the registry's naming convention.

**`McpReviewContextServer` is deliberately excluded.** It is a review-context sidecar whose own code
says "No backend URL or auth here — never any": it is spawned with a single 127.0.0.1 capability URL,
performs exactly one GET, and has no config authority. Instrumenting it broke that contract three
ways — it wrote `telemetry.json` into the config dir, and the flush added an outbound POST to
`phog.kurrent.io` from a process designed to reach nothing but its capability URL, which matters
because borrowed review runs under an OS sandbox with `(deny default)`. An integration test
(`Daemon_context_mode_starts_without_backend_and_performs_one_exact_get`) asserts the contract and
caught it.

The data lost is negligible — it exposes one tool, serving hosted reviewers rather than humans — and
it was already the one server needing a bespoke flush because it bypasses `Program.cs`'s exit
handler. Two special cases and a broken isolation contract for a metric nobody would query is a bad
trade. Do not re-add it.

In each, add `using Capacitor.Cli.Core.Telemetry;` and wrap the `tools/call` dispatch. For `McpMemoryServer.cs` the switch arm at line 82 becomes:

```csharp
"tools/call" => await TimedDispatchAsync(id, request),
```

and add alongside `DispatchToolCallAsync`:

```csharp
// Records which MCP tools agents actually reach for. Never touches the response path:
// the result (or the exception) is returned exactly as DispatchToolCallAsync produced it.
static async Task<JsonObject> TimedDispatchAsync(JsonNode? id, JsonObject request) {
    var start = System.Diagnostics.Stopwatch.GetTimestamp();
    var tool  = request["params"]?["name"]?.GetValue<string>() ?? "unknown";
    var ok    = false;

    try {
        var response = await DispatchToolCallAsync(id, request);
        ok = true;
        return response;
    } finally {
        McpTelemetry.ToolCalled("kcap-memory", tool, ok, CommandTiming.ElapsedMs(start));
    }
}
```

Repeat per server with its own server name (`kcap-sessions`, `kcap-review`, `kcap-flows`, `kcap-workitems`, `kcap-analytics`, `kcap-review-context`, `kcap-judge`). Adapt the request/response types to each server's actual signature — they differ; read the existing `DispatchToolCallAsync` in each file rather than assuming the shape above.

**The first-run notice must be suppressed for `"mcp-server"`.** Before this task, `"mcp"` was
denylisted so `Initialize` returned before the notice ran for any MCP process. Initialising with
`"mcp-server"` changes that — and since kcap-memory/sessions/flows/review auto-spawn on every agent
session, the first-ever `kcap` process on a fresh machine is plausibly an MCP server, whose stderr
most agent hosts never surface to a human. It would print the notice where nobody reads it *and*
consume the once-per-device marker, so the human never sees it — reproducing the silent-by-default
posture Decision 5 explicitly rejected. Skip the notice, the marker, and `cli_first_run` entirely for
`"mcp-server"`, leaving the first human-invoked command to do the disclosure.

Each MCP server's startup must also call `CliTelemetry.Initialize("mcp-server", baseUrl, loggedIn)` and register a flush. Note that `McpReviewContextServer` short-circuits in `Program.cs` *before* the `ProcessExit` flush registration, so it needs its own flush in a `finally` around its loop — without one it exposes a single tool called a handful of times per review round, never reaches the 20-call periodic flush, and reports nothing at all. Since these are long-lived, add a periodic flush: after every 20th tool call, call `await CliTelemetry.FlushAndClose()`. Add that counter inside `McpTelemetry`:

```csharp
static int _sinceFlush;

public static void ToolCalled(string server, string tool, bool ok, long durationMs) {
    CliTelemetry.Capture("mcp_tool_called", new JsonObject {
        ["server"] = server, ["tool"] = tool, ["ok"] = ok, ["duration_ms"] = durationMs,
    });

    // Long-lived process: without a periodic flush these would only leave on exit, and an
    // MCP server that is killed with its harness would never report at all.
    if (Interlocked.Increment(ref _sinceFlush) % 20 == 0)
        _ = CliTelemetry.FlushAndClose();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/McpTelemetryTests/*"`
Expected: PASS — 3 tests.

Then: `dotnet build src/Capacitor.Cli/Capacitor.Cli.csproj`
Expected: builds clean.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli.Core/Telemetry/McpTelemetry.cs src/Capacitor.Cli/Commands/
git commit -m "Record MCP tool calls"
```

---

### Task 11: `kcap config set telemetry`

**Files:**
- Modify: `src/Capacitor.Cli/Commands/ConfigCommand.cs` — `Set` (line 38), `Unset` (line 77), `Show` (line 28), `SetUsage` (line 134)
- Test: `test/Capacitor.Cli.Tests.Unit/Telemetry/ConfigTelemetryKeyTests.cs`

**Interfaces:**
- Consumes: `TelemetryState.SetEnabled`, `.PersistedEnabled` (Task 2); `TelemetrySettings.Resolve` (Task 1)
- Produces: `ConfigCommand.TryApplyTelemetry(string key, string value) → bool`

**Context:** Telemetry is **machine-scoped, not profile-scoped**, so it must NOT go through `ApplySet` (which returns a `Profile`). It is special-cased ahead of the profile load, the same way `server_url` is special-cased for normalisation.

- [ ] **Step 1: Write the failing test**

```csharp
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;   // Profile, for the not-a-profile-key guard
using Capacitor.Cli.Core.Telemetry;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Capacitor.Cli.Tests.Unit.Telemetry;

public class ConfigTelemetryKeyTests {
    static void FreshState() =>
        TelemetryState.PathOverride =
            Path.Combine(Path.GetTempPath(), $"kcap-cfg-{Guid.NewGuid():N}", "telemetry.json");

    [Test]
    [Arguments("off")]
    [Arguments("false")]
    [Arguments("0")]
    [Arguments("no")]
    public async Task Telemetry_off_persists_disabled(string value) {
        FreshState();

        await Assert.That(ConfigCommand.TryApplyTelemetry("telemetry", value)).IsTrue();
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)false);
    }

    [Test]
    [Arguments("on")]
    [Arguments("true")]
    [Arguments("1")]
    [Arguments("yes")]
    public async Task Telemetry_on_persists_enabled(string value) {
        FreshState();

        await Assert.That(ConfigCommand.TryApplyTelemetry("telemetry", value)).IsTrue();
        await Assert.That(TelemetryState.PersistedEnabled()).IsEqualTo((bool?)true);
    }

    [Test]
    public async Task Other_keys_are_not_claimed() {
        FreshState();

        await Assert.That(ConfigCommand.TryApplyTelemetry("server_url", "https://acme.kcap.ai")).IsFalse();
    }

    [Test]
    public async Task Invalid_telemetry_value_throws_with_an_actionable_message() {
        FreshState();

        var ex = Assert.Throws<ArgumentException>(() => ConfigCommand.TryApplyTelemetry("telemetry", "banana"));

        await Assert.That(ex!.Message.Contains("on")).IsTrue();
        await Assert.That(ex.Message.Contains("off")).IsTrue();
    }

    // Machine-scoped, so it must not have been written into the active profile.
    [Test]
    public async Task Telemetry_is_not_a_profile_key() {
        var ex = Assert.Throws<ArgumentException>(() => ConfigCommand.ApplySet(new Profile(), "telemetry", "off"));

        await Assert.That(ex!.Message.Contains("Unknown config key")).IsTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ConfigTelemetryKeyTests/*"`
Expected: FAIL — `TryApplyTelemetry` does not exist.

- [ ] **Step 3: Write minimal implementation**

In `src/Capacitor.Cli/Commands/ConfigCommand.cs`, add `using Capacitor.Cli.Core.Telemetry;` and:

```csharp
/// <summary>
/// Handles the machine-scoped `telemetry` key, which deliberately does NOT live in the active
/// profile: telemetry consent is a property of the machine, not of whichever workspace happens
/// to be selected. Returns false when the key is not ours, so Set() falls through to the
/// profile path. Pure enough to test; exposed for that reason.
/// </summary>
public static bool TryApplyTelemetry(string key, string value) {
    if (key != "telemetry") return false;

    var enabled = value.Trim().ToLowerInvariant() switch {
        "on" or "true" or "1" or "yes"   => true,
        "off" or "false" or "0" or "no"  => false,
        _ => throw new ArgumentException($"Invalid value for telemetry: '{value}'. Must be on or off."),
    };

    TelemetryState.SetEnabled(enabled);

    return true;
}
```

At the top of `Set` (line 38), before the `server_url` special case:

```csharp
if (TryApplyTelemetry(key, value)) {
    await Console.Out.WriteLineAsync($"Set telemetry = {(TelemetryState.PersistedEnabled() is true ? "on" : "off")} (machine-wide)");

    return 0;
}
```

In `Show` (line 28), after the config path line:

```csharp
var decision = TelemetrySettings.Resolve(TelemetryState.PersistedEnabled());
await Console.Out.WriteLineAsync($"  Telemetry: {(decision.Enabled ? "on" : "off")} (source: {decision.Reason})");
```

In `SetUsage` (line 134), add to the key list, keeping the existing column alignment:

```csharp
Console.Error.WriteLine("  telemetry                   Anonymous CLI usage reporting, machine-wide (on/off)");
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj --treenode-filter "/*/*/ConfigTelemetryKeyTests/*"`
Expected: PASS — 10 test cases.

Manual check:

```bash
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- config set telemetry off
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- config show
```
Expected: `Telemetry: off (source: config)`.

- [ ] **Step 5: Commit**

```bash
git add src/Capacitor.Cli/Commands/ConfigCommand.cs test/Capacitor.Cli.Tests.Unit/Telemetry/ConfigTelemetryKeyTests.cs
git commit -m "Add machine-scoped telemetry config key"
```

---

### Task 12: Docs, full suite, and AOT verification

**Files:**
- Modify: `README.md` — `## Getting started` and the `## CLI commands` `config` section
- Modify: `src/Capacitor.Cli.Core/Resources/help-config.txt`
- Test: full unit + integration suites, then an AOT publish

**Interfaces:**
- Consumes: everything
- Produces: nothing

**Context:** The repo has a standing rule that any user-facing CLI change updates `README.md` in the *same* PR — this has been missed twice and required doc-only follow-ups (#60, #61). Updating `help-*.txt` alone is not enough.

- [ ] **Step 1: Add the README telemetry section**

Add a `### Telemetry` subsection under `## CLI commands` → `config`, and a one-line pointer in `## Getting started`:

```markdown
### Telemetry

kcap reports anonymous usage data so we can see which commands people use and where setup goes
wrong. It records command names, exit codes, durations, and MCP tool names. It never records
command arguments, file paths, repo names, session ids, or transcript content.

Turn it off in any of three ways:

```bash
kcap config set telemetry off   # persisted, machine-wide
export KCAP_TELEMETRY=0         # this shell only
export DO_NOT_TRACK=1           # honoured by kcap and other tools
```

`KCAP_TELEMETRY` takes precedence over `DO_NOT_TRACK` in both directions, so `KCAP_TELEMETRY=1`
re-enables reporting on a machine that sets `DO_NOT_TRACK` globally.

`kcap config show` reports the current state and which setting decided it.
```

- [ ] **Step 2: Update `help-config.txt`**

Add `telemetry` to the key list in `src/Capacitor.Cli.Core/Resources/help-config.txt`, matching the file's existing formatting:

```
  telemetry                   Anonymous CLI usage reporting, machine-wide (on/off)
```

- [ ] **Step 3: Run the full unit suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Unit/Capacitor.Cli.Tests.Unit.csproj`
Expected: PASS, with no pre-existing tests newly broken. If `ConfigCommandTests` asserts on the exact `SetUsage` output, update that expectation to include the new `telemetry` line.

- [ ] **Step 4: Run the integration suite**

Run: `dotnet run --project test/Capacitor.Cli.Tests.Integration/Capacitor.Cli.Tests.Integration.csproj`
Expected: PASS.

- [ ] **Step 5: Verify AOT publish is clean**

Run:
```bash
dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'
```
Expected: **no output**. Any IL3050/IL2026 hit means a reflection-based serialization path slipped in — most likely a `JsonArray` collection expression or a `JsonSerializer.Serialize` overload without a `JsonTypeInfo`. Fix it before committing; `dotnet build` will not surface these.

- [ ] **Step 6: Verify the notice fires exactly once**

```bash
export KCAP_CONFIG_DIR=$(mktemp -d)
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- status 2>&1 | grep -c "anonymous usage data"
dotnet run --project src/Capacitor.Cli/Capacitor.Cli.csproj -- status 2>&1 | grep -c "anonymous usage data"
```
Expected: `1` then `0`.

- [ ] **Step 7: Commit**

```bash
git add README.md src/Capacitor.Cli.Core/Resources/help-config.txt test/
git commit -m "Document CLI telemetry and its opt-outs"
```

---

## Follow-ups (not this PR)

- **kcap-web privacy policy.** `src/pages/privacy.astro` describes web and server collection only and needs a CLI paragraph. Different repo, so a companion PR.
- **PR references.** Per `CLAUDE.md`, the PR description must reference both a GitHub issue (with a closing keyword) and a Linear issue. Neither exists for this work yet — open the GitHub issue first and let Linear auto-import it.
- ~~**`$ip: null` behaviour.**~~ **Resolved during the final branch review, and it did not hold.**
  `$ip` alone does not suppress geo-IP — PostHog populates it from the incoming connection and runs
  GeoIP off that, so a null value falls back to the request IP. Without `$geoip_disable: true` every
  event would have carried country, city and coordinates derived from the user's real IP, on an EU
  project whose privacy policy claims an IP-discard posture. Both properties now ship. This is the
  one open question in this plan that turned out to be a real defect rather than a formality — worth
  remembering that "confirm rather than assume" items deserve a verification step, not a note.
