using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit.Setup;

public class AgentDetectionTests {
    static AgentDetectionInputs Inputs(string? pathEnv = null, string? home = null,
            Dictionary<string, string?>? env = null) =>
        new(pathEnv, PathExt: null, IsWindows: false, Home: home,
            Env: k => env?.GetValueOrDefault(k));

    [Test]
    public async Task Binary_probe_walks_injected_path_with_execute_bit() {
        if (OperatingSystem.IsWindows()) return; // Unix exec-bit semantics only

        var dir    = Directory.CreateTempSubdirectory("kcap-detect-").FullName;
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
    public async Task Pi_home_signal_honors_injected_PI_CODING_AGENT_DIR_override() {
        // Detect() must never fall back to a real process-env read for Pi — everything comes
        // through Inputs.Env, so this test never mutates Environment.GetEnvironmentVariable.
        var agentDir = Directory.CreateTempSubdirectory("kcap-pi-agent-").FullName;
        var r = AgentDetection.Detect(Inputs(pathEnv: "", home: "/nonexistent",
            env: new() { ["PI_CODING_AGENT_DIR"] = agentDir }));
        await Assert.That(r.Pi.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task Antigravity_probes_both_agy_and_antigravity_binaries() {
        if (OperatingSystem.IsWindows()) return; // Unix exec-bit semantics only

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

    [Test]
    public async Task Cursor_has_no_binary_probe_only_install_signal() {
        // Cursor is detected purely by config-dir presence today (design, Q7) — no PATH
        // probe exists for it, unlike every other vendor. BinaryFound must stay false even
        // when a "cursor" executable is on the injected PATH.
        if (OperatingSystem.IsWindows()) return;

        var dir    = Directory.CreateTempSubdirectory("kcap-cursor-").FullName;
        var cursor = Path.Combine(dir, "cursor");
        await File.WriteAllTextAsync(cursor, "#!/bin/sh\n");
        File.SetUnixFileMode(cursor, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var r = AgentDetection.Detect(Inputs(pathEnv: dir, home: "/nonexistent"));
        await Assert.That(r.Cursor.BinaryFound).IsFalse();
        await Assert.That(r.Cursor.Detected).IsFalse();
    }

    [Test]
    public async Task BinaryOnPath_returns_false_when_path_env_is_null_or_empty() {
        await Assert.That(AgentDetection.BinaryOnPath("claude", Inputs(pathEnv: null))).IsFalse();
        await Assert.That(AgentDetection.BinaryOnPath("claude", Inputs(pathEnv: ""))).IsFalse();
    }

    [Test]
    public async Task BinaryOnPath_skips_empty_path_entries_without_throwing() {
        if (OperatingSystem.IsWindows()) return;

        var dir    = Directory.CreateTempSubdirectory("kcap-detect-").FullName;
        var claude = Path.Combine(dir, "claude");
        await File.WriteAllTextAsync(claude, "#!/bin/sh\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var pathEnv = $"{Path.PathSeparator}{dir}"; // leading empty entry
        await Assert.That(AgentDetection.BinaryOnPath("claude", Inputs(pathEnv: pathEnv))).IsTrue();
    }

    [Test]
    public async Task BinaryOnPath_windows_walks_pathext_and_rejects_bare_name() {
        var dir = Directory.CreateTempSubdirectory("kcap-detect-win-").FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "claude.CMD"), "@echo off\n");

        var winInputs = new AgentDetectionInputs(
            PathEnv: dir, PathExt: ".EXE;.CMD", IsWindows: true, Home: "/nonexistent",
            Env: _ => null);

        await Assert.That(AgentDetection.BinaryOnPath("claude", winInputs)).IsTrue();
        await Assert.That(AgentDetection.BinaryOnPath("nope", winInputs)).IsFalse();
    }

    [Test]
    public async Task FromEnvironment_reads_the_real_process_PATH() {
        // Not asserting a specific outcome (depends on the host machine) — just that it
        // doesn't throw and produces a usable inputs record wired to the live process env.
        var inputs = AgentDetection.FromEnvironment();
        await Assert.That(inputs.IsWindows).IsEqualTo(OperatingSystem.IsWindows());
    }
}
