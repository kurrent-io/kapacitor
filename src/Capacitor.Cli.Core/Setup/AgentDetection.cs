using Capacitor.Cli.Core.Dsh;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Core.Setup;

/// <summary>
/// Everything <see cref="AgentDetection"/> reads, injected instead of touched directly, so
/// tests never need to mutate process-wide PATH/HOME/env state. Every per-vendor override
/// (<see cref="KiroHome"/>, <see cref="PiAgentDir"/>, <see cref="OpenCodeConfigDir"/>,
/// <see cref="XdgConfigHome"/>, <see cref="XdgDataHome"/>, <see cref="GeminiCliHome"/>,
/// <see cref="CopilotHome"/>, <see cref="Platform"/>, <see cref="AppData"/>) is its OWN resolved
/// value here — <see cref="AgentDetection.Detect"/> passes each straight into a vendor's no-fallback <c>*Pure</c>
/// helper, so a null override is genuinely UNSET rather than a request to fall through to the real
/// process environment or OS globals underneath. <see cref="Home"/> is resolved to a concrete
/// non-null value by <see cref="AgentDetection.FromEnvironment"/>, and every helper's pure core takes it as-is —
/// none of them re-derives it from a real user-profile read.
/// </summary>
public sealed record AgentDetectionInputs(
    string? PathEnv, string? PathExt, bool IsWindows, string? Home,
    string? KiroHome = null, string? PiAgentDir = null, string? OpenCodeConfigDir = null,
    string? XdgConfigHome = null, string? XdgDataHome = null, string? GeminiCliHome = null,
    string? CopilotHome = null, OsPlatform Platform = OsPlatform.Linux, string? AppData = null,
    string? DshHome = null);

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
    DetectedAgent Antigravity, DetectedAgent Dsh);

/// <summary>
/// Detects installed coding-agent CLIs by composing a PATH binary probe with each vendor's
/// filesystem install-marker check. Mirrors, verbatim, the per-vendor probe composition
/// <c>SetupCommand</c> ran inline before this moved to Core — see each arm's comment
/// for why that vendor needs the signals it has.
/// </summary>
public static class AgentDetection {
    public static AgentDetectionResult Detect(AgentDetectionInputs i) {
        bool Bin(string name) => BinaryOnPath(name, i);
        var home = i.Home ?? "";

        return new(
            // Claude/Codex: PATH probe only — no on-disk install marker is checked today.
            Claude: new(Bin("claude"), false),
            Codex:  new(Bin("codex"), false),
            // Cursor: config-dir presence only (design, Q7) — no PATH probe exists for it. Pure:
            // platform/appData are concrete inputs, never OperatingSystem.* or
            // Environment.GetFolderPath resolved internally.
            Cursor: new(false, CursorPaths.IsInstalledPure(home, i.Platform, i.AppData)),
            // Dir presence covers users who launch Copilot through an IDE wrapper; the PATH
            // probe covers fresh installs that haven't run yet (no ~/.copilot until first launch).
            // Pure: never falls back to a real COPILOT_HOME process-env read — i.CopilotHome null
            // means genuinely unset, not "go check the real environment".
            Copilot: new(Bin("copilot"), CopilotPaths.IsInstalledPure(home, i.CopilotHome)),
            // Dir presence covers IDE-launched Gemini; the PATH probe covers a fresh install
            // that hasn't created ~/.gemini yet.
            Gemini: new(Bin("gemini"), GeminiPaths.IsInstalledPure(home, i.GeminiCliHome)),
            // Same dual signal for Kiro: the ~/.kiro tree or the conversation DB covers
            // IDE-launched users; the PATH probe (kiro / kiro-cli) covers fresh CLI installs.
            Kiro: new(Bin("kiro") || Bin("kiro-cli"), KiroPaths.IsInstalledPure(home, i.KiroHome)),
            // Pi keeps state under ~/.pi/agent (relocatable via PI_CODING_AGENT_DIR); the PATH
            // probe covers fresh installs that haven't created it yet.
            Pi: new(Bin("pi"), PiPaths.IsInstalledPure(home, i.PiAgentDir)),
            // OpenCode keeps config under ~/.config/opencode + data under
            // ~/.local/share/opencode; the PATH probe covers fresh installs.
            OpenCode: new(Bin("opencode"),
                OpenCodePaths.IsInstalledPure(home, i.OpenCodeConfigDir, i.XdgConfigHome, i.XdgDataHome)),
            // Antigravity is one vendor over two surfaces: the GUI (state under
            // ~/.gemini/antigravity) and the `agy` CLI (state under ~/.gemini/antigravity-cli).
            // IsInstalled covers either root; the PATH probes cover a fresh install that has
            // not created a root yet — and the CLI binary is `agy`, not `antigravity`, so both
            // names must be probed or an agy-only machine goes undetected.
            Antigravity: new(Bin("antigravity") || Bin("agy"), AntigravityPaths.IsInstalledPure(home, i.GeminiCliHome)),
            // dsh keeps its Cordis profile + plugin under ~/.dsh (relocatable via DSH_HOME);
            // the PATH probe covers a fresh install that hasn't created it yet.
            Dsh: new(Bin("dsh"), DshPaths.IsInstalledPure(home, i.DshHome)));
    }

    /// <summary>Current-process defaults: real PATH/PATHEXT/HOME/env, matching what the CLI
    /// binary actually sees when it runs. Every per-vendor override is resolved HERE, once, into
    /// its own concrete value — <see cref="Detect"/> never re-reads process env underneath.</summary>
    public static AgentDetectionInputs FromEnvironment() => new(
        PathEnv:   Environment.GetEnvironmentVariable("PATH"),
        PathExt:   Environment.GetEnvironmentVariable("PATHEXT"),
        IsWindows: OperatingSystem.IsWindows(),
        Home:      PathHelpers.HomeDirectory,
        KiroHome:          Environment.GetEnvironmentVariable("KIRO_HOME"),
        PiAgentDir:        Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR"),
        OpenCodeConfigDir: Environment.GetEnvironmentVariable("OPENCODE_CONFIG_DIR"),
        XdgConfigHome:     Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        XdgDataHome:       Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        GeminiCliHome:     Environment.GetEnvironmentVariable("GEMINI_CLI_HOME"),
        CopilotHome:       Environment.GetEnvironmentVariable("COPILOT_HOME"),
        Platform: OperatingSystem.IsMacOS()   ? OsPlatform.MacOs
                : OperatingSystem.IsWindows() ? OsPlatform.Windows
                :                               OsPlatform.Linux,
        AppData: OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : null,
        DshHome: Environment.GetEnvironmentVariable("DSH_HOME"));

    /// <summary>
    /// Probes <paramref name="i"/>'s PATH for <paramref name="binaryName"/>. Returns false on a
    /// null/empty PATH. On Unix, requires at least one of the user/group/other execute bits; on
    /// Windows, walks PATHEXT (defaulting to .EXE/.CMD/.BAT) and accepts any file that exists.
    /// </summary>
    public static bool BinaryOnPath(string binaryName, AgentDetectionInputs i) =>
        BinaryOnPath(binaryName, i, IsExecutable);

    /// <summary>Stubbing seam for <see cref="BinaryOnPath(string, AgentDetectionInputs)"/>; separator/
    /// extensions/comparer are all derived from <paramref name="i"/>'s platform, never the host's.</summary>
    internal static bool BinaryOnPath(string binaryName, AgentDetectionInputs i, Func<string, bool, bool> isExecutable) {
        if (string.IsNullOrEmpty(i.PathEnv)) return false;

        var separator  = i.IsWindows ? ';' : ':';
        var paths      = i.PathEnv.Split(separator);
        var extensions = i.IsWindows ? WindowsExtensions(i.PathExt) : [""];
        var comparer   = i.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        return paths.Where(dir => !string.IsNullOrEmpty(dir))
            .Distinct(comparer)
            .Any(dir => extensions.Select(ext => Path.Combine(dir, binaryName + ext)).Any(path => isExecutable(path, i.IsWindows)));
    }

    /// <summary>Convenience overload for CLI call sites that only need a single-binary current-
    /// process PATH probe (e.g. "is kcap on PATH") — <see cref="FromEnvironment"/> plus
    /// <see cref="BinaryOnPath(string, AgentDetectionInputs)"/> in one call.</summary>
    public static bool BinaryOnPath(string binaryName) => BinaryOnPath(binaryName, FromEnvironment());

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
