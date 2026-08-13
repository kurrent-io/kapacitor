using Capacitor.Cli.Core.Antigravity;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.Core.Pi;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// Everything <see cref="AgentDetection"/> reads, injected instead of touched directly, so
/// tests never need to mutate process-wide PATH/HOME/env state. <see cref="Env"/> covers the
/// per-vendor overrides each <c>*Paths</c> type accepts as a pure parameter: <c>KIRO_HOME</c>,
/// <c>OPENCODE_CONFIG_DIR</c>, <c>XDG_CONFIG_HOME</c>, <c>XDG_DATA_HOME</c>,
/// <c>GEMINI_CLI_HOME</c>, <c>PI_CODING_AGENT_DIR</c>. Copilot is the one exception:
/// <c>CopilotPaths.IsInstalled()</c> has no override parameter and reads <c>COPILOT_HOME</c>
/// from the real environment internally (sanctioned — see its arm below).
/// </summary>
public sealed record AgentDetectionInputs(
    string? PathEnv, string? PathExt, bool IsWindows, string? Home, Func<string, string?> Env);

/// <summary>
/// One vendor's two independent detection signals: a PATH binary probe and a filesystem
/// install-marker probe (a vendor's <c>*Paths.IsInstalled</c>). <see cref="Detected"/> is the
/// OR that <c>SetupCommand</c>'s wizard actually consumes — most vendors need both, because a
/// fresh install has no on-disk state yet and an IDE-launched vendor has no CLI on PATH.
/// </summary>
public sealed record DetectedAgent(bool BinaryFound, bool InstallSignalFound) {
    public bool Detected => BinaryFound || InstallSignalFound;
}

public sealed record AgentDetectionResult(
    DetectedAgent Claude, DetectedAgent Codex, DetectedAgent Cursor, DetectedAgent Copilot,
    DetectedAgent Gemini, DetectedAgent Kiro, DetectedAgent Pi, DetectedAgent OpenCode,
    DetectedAgent Antigravity);

/// <summary>
/// Detects installed coding-agent CLIs by composing a PATH binary probe with each vendor's
/// filesystem install-marker check. Mirrors, verbatim, the per-vendor probe composition
/// <c>SetupCommand</c> ran inline before this moved to Core (AI-1655) — see each arm's comment
/// for why that vendor needs the signals it has.
/// </summary>
public static class AgentDetection {
    public static AgentDetectionResult Detect(AgentDetectionInputs i) {
        bool Bin(string name) => BinaryOnPath(name, i);
        var home          = i.Home;
        var geminiCliHome = i.Env("GEMINI_CLI_HOME");

        return new(
            // Claude/Codex: PATH probe only — no on-disk install marker is checked today.
            Claude: new(Bin("claude"), false),
            Codex:  new(Bin("codex"), false),
            // Cursor: config-dir presence only (design, Q7) — no PATH probe exists for it.
            Cursor: new(false, CursorPaths.IsInstalled(home)),
            // Dir presence covers users who launch Copilot through an IDE wrapper; the PATH
            // probe covers fresh installs that haven't run yet (no ~/.copilot until first launch).
            // CopilotPaths.IsInstalled() has no override parameter — it reads COPILOT_HOME from
            // the real environment internally, unlike every other vendor's pure IsInstalled arm.
            Copilot: new(Bin("copilot"), CopilotPaths.IsInstalled()),
            // Dir presence covers IDE-launched Gemini; the PATH probe covers a fresh install
            // that hasn't created ~/.gemini yet.
            Gemini: new(Bin("gemini"), GeminiPaths.IsInstalled(home, geminiCliHome)),
            // Same dual signal for Kiro: the ~/.kiro tree or the conversation DB covers
            // IDE-launched users; the PATH probe (kiro / kiro-cli) covers fresh CLI installs.
            Kiro: new(Bin("kiro") || Bin("kiro-cli"), KiroPaths.IsInstalled(home, i.Env("KIRO_HOME"))),
            // Pi keeps state under ~/.pi/agent (relocatable via PI_CODING_AGENT_DIR); the PATH
            // probe covers fresh installs that haven't created it yet.
            Pi: new(Bin("pi"), PiPaths.IsInstalled(home, i.Env("PI_CODING_AGENT_DIR"))),
            // OpenCode keeps config under ~/.config/opencode + data under
            // ~/.local/share/opencode; the PATH probe covers fresh installs.
            OpenCode: new(Bin("opencode"),
                OpenCodePaths.IsInstalled(home, i.Env("OPENCODE_CONFIG_DIR"), i.Env("XDG_CONFIG_HOME"), i.Env("XDG_DATA_HOME"))),
            // Antigravity is one vendor over two surfaces: the GUI (state under
            // ~/.gemini/antigravity) and the `agy` CLI (state under ~/.gemini/antigravity-cli).
            // IsInstalled covers either root; the PATH probes cover a fresh install that has
            // not created a root yet — and the CLI binary is `agy`, not `antigravity`, so both
            // names must be probed or an agy-only machine goes undetected.
            Antigravity: new(Bin("antigravity") || Bin("agy"), AntigravityPaths.IsInstalled(home, geminiCliHome)));
    }

    /// <summary>Current-process defaults: real PATH/PATHEXT/HOME/env, matching what the CLI
    /// binary actually sees when it runs.</summary>
    public static AgentDetectionInputs FromEnvironment() => new(
        PathEnv:   Environment.GetEnvironmentVariable("PATH"),
        PathExt:   Environment.GetEnvironmentVariable("PATHEXT"),
        IsWindows: OperatingSystem.IsWindows(),
        Home:      PathHelpers.HomeDirectory,
        Env:       Environment.GetEnvironmentVariable);

    /// <summary>
    /// Probes <paramref name="i"/>'s PATH for <paramref name="binaryName"/>. Returns false on a
    /// null/empty PATH. On Unix, requires at least one of the user/group/other execute bits; on
    /// Windows, walks PATHEXT (defaulting to .EXE/.CMD/.BAT) and accepts any file that exists.
    /// </summary>
    public static bool BinaryOnPath(string binaryName, AgentDetectionInputs i) {
        if (string.IsNullOrEmpty(i.PathEnv)) return false;

        var paths      = i.PathEnv.Split(Path.PathSeparator);
        var extensions = i.IsWindows ? WindowsExtensions(i.PathExt) : [""];

        return paths.Where(dir => !string.IsNullOrEmpty(dir))
            .Any(dir => extensions.Select(ext => Path.Combine(dir, binaryName + ext)).Any(path => IsExecutable(path, i.IsWindows)));
    }

    static string[] WindowsExtensions(string? pathExt) {
        var raw = string.IsNullOrEmpty(pathExt) ? ".EXE;.CMD;.BAT" : pathExt;
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    static bool IsExecutable(string path, bool isWindows) {
        if (!File.Exists(path)) return false;
        if (isWindows) return true; // PATHEXT already filtered the candidates

        // Unix: any of UGO execute bits is enough — an intentional heuristic.
        // True access(X_OK) would require P/Invoke against the effective UID/GID.
        // The rare false positive (binary with execute bits but unrelated owner)
        // degrades to the same outcome as a runtime-broken binary.
        const UnixFileMode anyExecute =
            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        try {
            // isWindows is an injected value (not a direct OperatingSystem.IsWindows() call,
            // deliberately — tests simulate Windows PATHEXT behavior on any host), so the
            // platform-compat analyzer can't see the guard above as unreachable-on-Windows proof.
            // It IS: production always derives isWindows from OperatingSystem.IsWindows() itself
            // (see FromEnvironment), so this line never runs on a real Windows host.
#pragma warning disable CA1416
            return (File.GetUnixFileMode(path) & anyExecute) != 0;
#pragma warning restore CA1416
        } catch {
            // TOCTOU race (file removed between File.Exists and GetUnixFileMode),
            // permission denied, or other I/O failure — treat as not executable
            // so detection doesn't abort the wizard.
            return false;
        }
    }
}
