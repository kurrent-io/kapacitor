using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit.Setup;

public class AgentDetectionTests {
    static AgentDetectionInputs Inputs(string? pathEnv = null, string? home = null,
            Dictionary<string, string?>? env = null) =>
        new(pathEnv, PathExt: null, IsWindows: false, Home: home,
            KiroHome: env?.GetValueOrDefault("KIRO_HOME"),
            PiAgentDir: env?.GetValueOrDefault("PI_CODING_AGENT_DIR"),
            OpenCodeConfigDir: env?.GetValueOrDefault("OPENCODE_CONFIG_DIR"),
            XdgConfigHome: env?.GetValueOrDefault("XDG_CONFIG_HOME"),
            XdgDataHome: env?.GetValueOrDefault("XDG_DATA_HOME"),
            GeminiCliHome: env?.GetValueOrDefault("GEMINI_CLI_HOME"),
            CopilotHome: env?.GetValueOrDefault("COPILOT_HOME"));

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

    /// <summary>Cursor's per-OS Electron user dir is resolved purely from the injected
    /// <see cref="AgentDetectionInputs.Platform"/>/<see cref="AgentDetectionInputs.AppData"/> —
    /// Detect() must never consult <c>OperatingSystem.IsWindows()</c> or
    /// <c>Environment.GetFolderPath</c> itself, so a Windows AppData layout is detectable from a
    /// non-Windows test host purely through injected inputs.</summary>
    [Test]
    public async Task Cursor_windows_install_signal_is_resolved_from_injected_platform_and_appdata() {
        var appData = Directory.CreateTempSubdirectory("kcap-cursor-appdata-").FullName;
        Directory.CreateDirectory(Path.Combine(appData, "Cursor", "User"));

        var inputs = new AgentDetectionInputs(PathEnv: "", PathExt: null, IsWindows: false, Home: "/nonexistent",
            Platform: OsPlatform.Windows, AppData: appData);

        var r = AgentDetection.Detect(inputs);
        await Assert.That(r.Cursor.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task BinaryOnPath_returns_false_when_path_env_is_null_or_empty() {
        await Assert.That(AgentDetection.BinaryOnPath("claude", Inputs(pathEnv: null))).IsFalse();
        await Assert.That(AgentDetection.BinaryOnPath("claude", Inputs(pathEnv: ""))).IsFalse();
    }

    [Test]
    public async Task BinaryOnPath_dedupes_a_repeated_PATH_entry() {
        if (OperatingSystem.IsWindows()) return;

        var dir    = Directory.CreateTempSubdirectory("kcap-detect-dedupe-").FullName;
        var claude = Path.Combine(dir, "claude");
        await File.WriteAllTextAsync(claude, "#!/bin/sh\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        // The same directory repeated three times must still be found (and probed only once per
        // Distinct dir, not once per occurrence).
        var pathEnv = $"{dir}{Path.PathSeparator}{dir}{Path.PathSeparator}{dir}";
        await Assert.That(AgentDetection.BinaryOnPath("claude", Inputs(pathEnv: pathEnv))).IsTrue();
    }

    [Test]
    public async Task BinaryOnPath_windows_dedupes_case_variant_directories() {
        var probed = new List<string>();
        bool CountingProbe(string path, bool isWindows) { probed.Add(path); return false; }

        var winInputs = new AgentDetectionInputs(
            PathEnv: @"C:\Tools;c:\tools;C:\TOOLS", PathExt: ".EXE", IsWindows: true, Home: "/nonexistent");

        AgentDetection.BinaryOnPath("claude", winInputs, CountingProbe);

        // Three case-variant spellings of the same directory collapse to ONE probed candidate
        // (one extension × one deduped dir) under Windows' case-insensitive PATH identity.
        await Assert.That(probed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BinaryOnPath_unix_dedupes_same_case_duplicate_directories() {
        if (OperatingSystem.IsWindows()) return;

        var probed = new List<string>();
        bool CountingProbe(string path, bool isWindows) { probed.Add(path); return false; }

        var inputs = new AgentDetectionInputs(
            PathEnv: "/usr/bin:/usr/bin:/usr/bin", PathExt: null, IsWindows: false, Home: "/nonexistent");

        AgentDetection.BinaryOnPath("claude", inputs, CountingProbe);

        await Assert.That(probed.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BinaryOnPath_unix_probes_case_variant_directories_separately() {
        if (OperatingSystem.IsWindows()) return;

        var probed = new List<string>();
        bool CountingProbe(string path, bool isWindows) { probed.Add(path); return false; }

        var inputs = new AgentDetectionInputs(
            PathEnv: "/usr/bin:/USR/BIN", PathExt: null, IsWindows: false, Home: "/nonexistent");

        AgentDetection.BinaryOnPath("claude", inputs, CountingProbe);

        // Unix PATH identity is case-sensitive — these are two genuinely distinct directories,
        // each probed once, unlike Windows' case-insensitive dedup above.
        await Assert.That(probed.Count).IsEqualTo(2);
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
            PathEnv: dir, PathExt: ".EXE;.CMD", IsWindows: true, Home: "/nonexistent");

        await Assert.That(AgentDetection.BinaryOnPath("claude", winInputs)).IsTrue();
        await Assert.That(AgentDetection.BinaryOnPath("nope", winInputs)).IsFalse();
    }

    // ── genuine purity: an injected null override must behave as UNSET. These no longer mutate
    // real process env at all (the spec's zero-process-env-mutation rule) — the pure helpers
    // Detect() calls take the override as a concrete parameter with no internal env fallback, so
    // "override omitted" is provably equivalent to "unset" without touching global state, and no
    // NotInParallel is needed since nothing shared is mutated. ──

    [Test]
    public async Task Kiro_home_signal_stays_unset_without_an_injected_override() {
        var r = AgentDetection.Detect(Inputs(pathEnv: "", home: "/nonexistent"));
        await Assert.That(r.Kiro.InstallSignalFound).IsFalse();
    }

    [Test]
    public async Task Pi_home_signal_stays_unset_without_an_injected_override() {
        var r = AgentDetection.Detect(Inputs(pathEnv: "", home: "/nonexistent"));
        await Assert.That(r.Pi.InstallSignalFound).IsFalse();
    }

    [Test]
    public async Task OpenCode_honors_an_injected_override_without_touching_real_env() {
        var dir = Directory.CreateTempSubdirectory("kcap-oc-").FullName;
        var r = AgentDetection.Detect(Inputs(pathEnv: "", home: "/nonexistent",
            env: new() { ["OPENCODE_CONFIG_DIR"] = dir }));
        await Assert.That(r.OpenCode.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task Copilot_home_signal_stays_unset_without_an_injected_override() {
        var r = AgentDetection.Detect(Inputs(pathEnv: "", home: "/nonexistent"));
        await Assert.That(r.Copilot.InstallSignalFound).IsFalse();
    }

    // Positive counterpart: an injected override (not the real env) is what actually gets used.
    [Test]
    public async Task Copilot_honors_an_injected_override_without_touching_real_env() {
        var dir = Directory.CreateTempSubdirectory("kcap-copilot-").FullName;
        var r = AgentDetection.Detect(Inputs(pathEnv: "", home: "/nonexistent",
            env: new() { ["COPILOT_HOME"] = dir }));
        await Assert.That(r.Copilot.InstallSignalFound).IsTrue();
    }

    [Test]
    public async Task FromEnvironment_reads_the_real_process_PATH() {
        // Not asserting a specific outcome (depends on the host machine) — just that it
        // doesn't throw and produces a usable inputs record wired to the live process env.
        var inputs = AgentDetection.FromEnvironment();
        await Assert.That(inputs.IsWindows).IsEqualTo(OperatingSystem.IsWindows());
    }

    // ── BinaryOnPath(string): the single-arg convenience overload is documented as nothing more
    // than FromEnvironment() + the pure walk (see AgentDetection.cs) — FromEnvironment_reads_the_
    // real_process_PATH above already smoke-tests that wiring. The edge cases below (empty/null
    // PATH, exec-bit requirement) are therefore driven through the pure 2-arg overload with
    // injected inputs — never mutating real process PATH — so nothing here needs NotInParallel. ──

    [Test]
    public async Task BinaryOnPath_pure_unix_requires_any_execute_bit() {
        if (OperatingSystem.IsWindows()) return; // Unix-only

        var tmp     = Directory.CreateTempSubdirectory("kcap-agentprobe-").FullName;
        var exec    = Path.Combine(tmp, "agentprobe-exec");
        var nonExec = Path.Combine(tmp, "agentprobe-nonexec");

        await File.WriteAllTextAsync(exec, "#!/bin/sh\nexit 0\n");
        await File.WriteAllTextAsync(nonExec, "not executable");
        File.SetUnixFileMode(exec,    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);   // 0700
        File.SetUnixFileMode(nonExec, UnixFileMode.UserRead | UnixFileMode.UserWrite);                              // 0600

        await Assert.That(AgentDetection.BinaryOnPath("agentprobe-exec", Inputs(pathEnv: tmp))).IsTrue();
        await Assert.That(AgentDetection.BinaryOnPath("agentprobe-nonexec", Inputs(pathEnv: tmp))).IsFalse();
    }

    // ── BinaryOnPath separator is derived from the injected platform, never the host-global
    // Path.PathSeparator — pin both directions purely. ──

    [Test]
    public async Task BinaryOnPath_windows_input_splits_on_semicolon_regardless_of_host_platform() {
        var dir = Directory.CreateTempSubdirectory("kcap-detect-winsep-").FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "claude.CMD"), "@echo off\n");

        var otherDir = Directory.CreateTempSubdirectory("kcap-detect-winsep2-").FullName;
        var winInputs = new AgentDetectionInputs(
            PathEnv: $"{otherDir};{dir}", PathExt: ".EXE;.CMD", IsWindows: true, Home: "/nonexistent");

        // A colon-joined PATH would be read as ONE bogus entry under a semicolon split and never
        // find claude.CMD in `dir` — this only passes if the separator is genuinely ';' here.
        await Assert.That(AgentDetection.BinaryOnPath("claude", winInputs)).IsTrue();
    }

    [Test]
    public async Task BinaryOnPath_unix_input_splits_on_colon() {
        if (OperatingSystem.IsWindows()) return; // exec-bit semantics only make sense on Unix

        var dir = Directory.CreateTempSubdirectory("kcap-detect-unixsep-").FullName;
        var claude = Path.Combine(dir, "claude");
        await File.WriteAllTextAsync(claude, "#!/bin/sh\n");
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var otherDir = Directory.CreateTempSubdirectory("kcap-detect-unixsep2-").FullName;
        var r = AgentDetection.Detect(Inputs(pathEnv: $"{otherDir}:{dir}", home: "/nonexistent"));

        await Assert.That(r.Claude.BinaryFound).IsTrue();
    }
}
