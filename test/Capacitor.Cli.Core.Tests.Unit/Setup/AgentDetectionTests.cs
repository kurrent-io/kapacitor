using System.Runtime.Versioning;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Setup;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class AgentDetectionTests {
    /// <summary>A bundle whose vendor overrides come from <paramref name="env"/> alone, so an
    /// omitted key is provably UNSET rather than a fall-through to the real process environment —
    /// which is what lets these tests mutate nothing shared and carry no exclusion.</summary>
    static HarnessPaths Layout(string home = "", Dictionary<string, string?>? env = null) {
        var h      = new UserHome(home);
        var gemini = new GeminiPaths(h, env?.GetValueOrDefault("GEMINI_CLI_HOME"));

        return TestHarnessPaths.NoOverrides(h) with {
            Gemini      = gemini,
            Antigravity = new(gemini),
            Kiro        = new(h, env?.GetValueOrDefault("KIRO_HOME")),
            Pi          = new(h, env?.GetValueOrDefault("PI_CODING_AGENT_DIR")),
            Copilot     = new(h, env?.GetValueOrDefault("COPILOT_HOME")),
            OpenCode    = new(h, env?.GetValueOrDefault("OPENCODE_CONFIG_DIR"),
                                 env?.GetValueOrDefault("XDG_CONFIG_HOME"),
                                 env?.GetValueOrDefault("XDG_DATA_HOME")),
        };
    }

    static BinaryProbe Probe(string? searchPath) => BinaryProbe.Searching(searchPath);

    [Test, ExcludeOn(OS.Windows)] // Unix exec-bit semantics only
    [UnsupportedOSPlatform("windows")]
    public async Task Binary_probe_walks_injected_path_with_execute_bit() {
        using var tmp = new TempDir();
        var claude = tmp.PathTo("claude");
        await File.WriteAllTextAsync(claude, "#!/bin/sh\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var r = AgentDetection.Detect(Layout("/nonexistent"), Probe(tmp.Path));
        await Assert.That(r.Claude.BinaryFound).IsTrue();
        await Assert.That(r.Codex.BinaryFound).IsFalse();
    }

    [Test]
    public async Task Gemini_marker_rules_bare_dot_gemini_is_NOT_installed() {
        using var tmp = new TempDir();
        tmp.CreateDir(".gemini"); // bare dir, no markers
        var r = AgentDetection.Detect(Layout(tmp.Path), Probe(""));
        await Assert.That(r.Gemini.InstallSignalFound).IsFalse();

        tmp.CreateFile([".gemini", "settings.json"], "{}");
        var r2 = AgentDetection.Detect(Layout(tmp.Path), Probe(""));
        await Assert.That(r2.Gemini.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task Kiro_binary_probe_includes_kiro_cli_and_home_signal_honors_injected_override() {
        using var tmp = new TempDir();
        var r = AgentDetection.Detect(Layout("/nonexistent", new() { ["KIRO_HOME"] = tmp.Path }), Probe(""));
        await Assert.That(r.Kiro.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task Pi_home_signal_honors_injected_PI_CODING_AGENT_DIR_override() {
        // Detect() must never fall back to a real process-env read for Pi: the override arrives
        // inside PiPaths, so this test mutates no environment variable.
        using var tmp = new TempDir();
        var r = AgentDetection.Detect(Layout("/nonexistent", new() { ["PI_CODING_AGENT_DIR"] = tmp.Path }), Probe(""));
        await Assert.That(r.Pi.InstallSignalFound).IsTrue();
    }

    [Test, ExcludeOn(OS.Windows)] // Unix exec-bit semantics only
    [UnsupportedOSPlatform("windows")]
    public async Task Antigravity_probes_both_agy_and_antigravity_binaries() {
        using var tmp = new TempDir();
        var agy = tmp.PathTo("agy");
        await File.WriteAllTextAsync(agy, "#!/bin/sh\n");
        File.SetUnixFileMode(agy, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var r = AgentDetection.Detect(Layout("/nonexistent"), Probe(tmp.Path));
        await Assert.That(r.Antigravity.BinaryFound).IsTrue();
    }

    [Test]
    public async Task Unreadable_path_entries_do_not_throw() {
        var r = AgentDetection.Detect(Layout("/nonexistent"), Probe("/nonexistent-a:/nonexistent-b"));
        await Assert.That(r.Claude.Detected).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task Cursor_has_no_binary_probe_only_install_signal() {
        // Cursor is detected purely by config-dir presence — no PATH probe exists for it, unlike
        // every other vendor. BinaryFound must stay false even when a "cursor" executable is on
        // the injected PATH.
        using var tmp = new TempDir();
        var cursor = tmp.PathTo("cursor");
        await File.WriteAllTextAsync(cursor, "#!/bin/sh\n");
        File.SetUnixFileMode(cursor, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var r = AgentDetection.Detect(Layout("/nonexistent"), Probe(tmp.Path));
        await Assert.That(r.Cursor.BinaryFound).IsFalse();
        await Assert.That(r.Cursor.Detected).IsFalse();
    }

    /// <summary>Cursor's per-OS Electron user dir comes from the injected <see cref="CursorPaths"/>,
    /// so a Windows AppData layout is detectable from a non-Windows test host: detection never
    /// consults <c>OperatingSystem.IsWindows()</c> or <c>Environment.GetFolderPath</c> itself.</summary>
    [Test]
    public async Task Cursor_windows_install_signal_is_resolved_from_injected_platform_and_appdata() {
        using var tmp = new TempDir();
        tmp.CreateDir("Cursor", "User");

        var home  = new UserHome("/nonexistent");
        var paths = TestHarnessPaths.NoOverrides(home) with { Cursor = new(home, OsPlatform.Windows, tmp.Path) };

        var r = AgentDetection.Detect(paths, Probe(""));
        await Assert.That(r.Cursor.InstallSignalFound).IsTrue();
    }

    // ── An omitted override must behave as UNSET: every vendor's root arrives as a resolved value,
    // so "override omitted" is provably equivalent to "unset" without touching global state. ──

    [Test]
    public async Task Kiro_home_signal_stays_unset_without_an_injected_override() {
        var r = AgentDetection.Detect(Layout("/nonexistent"), Probe(""));
        await Assert.That(r.Kiro.InstallSignalFound).IsFalse();
    }

    [Test]
    public async Task Pi_home_signal_stays_unset_without_an_injected_override() {
        var r = AgentDetection.Detect(Layout("/nonexistent"), Probe(""));
        await Assert.That(r.Pi.InstallSignalFound).IsFalse();
    }

    [Test]
    public async Task OpenCode_honors_an_injected_override_without_touching_real_env() {
        using var tmp = new TempDir();
        var r = AgentDetection.Detect(Layout("/nonexistent", new() { ["OPENCODE_CONFIG_DIR"] = tmp.Path }), Probe(""));
        await Assert.That(r.OpenCode.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task Copilot_home_signal_stays_unset_without_an_injected_override() {
        var r = AgentDetection.Detect(Layout("/nonexistent"), Probe(""));
        await Assert.That(r.Copilot.InstallSignalFound).IsFalse();
    }

    // Positive counterpart: an injected override (not the real env) is what actually gets used.
    [Test]
    public async Task Copilot_honors_an_injected_override_without_touching_real_env() {
        using var tmp = new TempDir();
        var r = AgentDetection.Detect(Layout("/nonexistent", new() { ["COPILOT_HOME"] = tmp.Path }), Probe(""));
        await Assert.That(r.Copilot.InstallSignalFound).IsTrue();
    }

    /// <summary>The separator and the exec-bit test come from the probe, so this pins the arm's
    /// wiring rather than the walk (BinaryProbeTests owns that).</summary>
    [Test, ExcludeOn(OS.Windows)] // exec-bit staging
    [UnsupportedOSPlatform("windows")]
    public async Task A_binary_on_the_injected_path_is_what_a_vendor_arm_reads() {
        using var tmp = new TempDir();
        var dir    = tmp.CreateDir("unixsep");
        var claude = dir.PathTo("claude");
        await File.WriteAllTextAsync(claude, "#!/bin/sh\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var otherDir = tmp.CreateDir("unixsep2");
        var r = AgentDetection.Detect(Layout("/nonexistent"), Probe($"{otherDir}:{dir}"));

        await Assert.That(r.Claude.BinaryFound).IsTrue();
    }
}
