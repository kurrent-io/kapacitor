namespace Capacitor.Cli.Core.Copilot;

/// <summary>
/// Filesystem layout for GitHub Copilot CLI state. Everything lives under a
/// single root: <c>$COPILOT_HOME</c> when set (Copilot relocates its entire
/// tree through that variable — hooks inherit it from the spawning process),
/// otherwise <c>~/.copilot</c> on every OS.
/// </summary>
public static class CopilotPaths {
    public static string Root(string? home = null, string? copilotHome = null) {
        copilotHome ??= Environment.GetEnvironmentVariable("COPILOT_HOME");
        if (!string.IsNullOrEmpty(copilotHome)) return copilotHome;

        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".copilot");
    }

    /// <summary>
    /// Detection by root-dir presence — Copilot CLI creates <c>~/.copilot</c>
    /// on first run, and (unlike Codex) the binary name <c>copilot</c> is too
    /// generic for a PATH probe to be the only signal. Callers that want the
    /// PATH probe too OR this with <c>AgentDetection.BinaryOnPath("copilot")</c>.
    /// </summary>
    public static bool IsInstalled(string? home = null, string? copilotHome = null)
        => Directory.Exists(Root(home, copilotHome));

    /// <summary>Pure variant of <see cref="Root"/> for fully-injected callers (e.g.
    /// <see cref="Setup.AgentDetection"/>) — <paramref name="copilotHome"/> null means "not set",
    /// never falls back to a real <c>COPILOT_HOME</c> process-env read; <paramref name="home"/> is
    /// taken as-is, never falling back to a real user-profile read either — the caller is expected
    /// to have already resolved a concrete value (<see cref="Setup.AgentDetectionInputs.Home"/>).</summary>
    public static string RootPure(string? home, string? copilotHome) =>
        !string.IsNullOrEmpty(copilotHome) ? copilotHome : Path.Combine(home ?? "", ".copilot");

    /// <summary>Pure variant of <see cref="IsInstalled"/> — never falls back to the real process
    /// environment for <c>COPILOT_HOME</c> or the real user profile for <c>home</c>.</summary>
    public static bool IsInstalledPure(string? home, string? copilotHome) =>
        Directory.Exists(RootPure(home, copilotHome));

    /// <summary>
    /// User-level hooks directory. Copilot merges every <c>*.json</c> file in
    /// here at startup, so kcap owns its own file (<see cref="KcapHooksJson"/>)
    /// instead of merging into a shared one the way the Cursor installer must.
    /// </summary>
    public static string HooksDir(string? home = null, string? copilotHome = null)
        => Path.Combine(Root(home, copilotHome), "hooks");

    public static string KcapHooksJson(string? home = null, string? copilotHome = null)
        => Path.Combine(HooksDir(home, copilotHome), "kcap.json");

    /// <summary>
    /// User-level MCP server config (<c>mcpServers</c> object, each entry
    /// <c>type: "stdio"</c>). Copilot reads it from the root dir, so it honors
    /// <c>$COPILOT_HOME</c> like the rest of the layout.
    /// </summary>
    public static string McpConfigJson(string? home = null, string? copilotHome = null)
        => Path.Combine(Root(home, copilotHome), "mcp-config.json");

    /// <summary>
    /// User-level custom-instructions file Copilot loads into context at startup. Honors
    /// <c>$COPILOT_HOME</c> like the rest of the layout.
    /// </summary>
    public static string InstructionsMd(string? home = null, string? copilotHome = null)
        => Path.Combine(Root(home, copilotHome), "copilot-instructions.md");

    /// <summary>
    /// Per-session state root: one subdirectory per session (named with the
    /// dashed session uuid) containing <c>events.jsonl</c> (append-only
    /// transcript), <c>workspace.yaml</c> (cwd/repo/title metadata), and
    /// checkpoint artifacts. Directories WITHOUT an events.jsonl are
    /// failed-startup scaffolding and must be skipped by discovery.
    /// </summary>
    public static string SessionStateDir(string? home = null, string? copilotHome = null)
        => Path.Combine(Root(home, copilotHome), "session-state");

    /// <summary>
    /// Pre-GA session storage (Copilot migrated to <c>session-state/</c> in
    /// late 2025; old sessions are only migrated lazily on resume). Import
    /// walks both roots.
    /// </summary>
    public static string LegacySessionStateDir(string? home = null, string? copilotHome = null)
        => Path.Combine(Root(home, copilotHome), "history-session-state");

    /// <summary>Transcript path for a session dir name (the dashed session uuid).</summary>
    public static string EventsJsonl(string sessionStateDir, string sessionDirName)
        => Path.Combine(sessionStateDir, sessionDirName, "events.jsonl");

    public static string WorkspaceYaml(string sessionStateDir, string sessionDirName)
        => Path.Combine(sessionStateDir, sessionDirName, "workspace.yaml");
}
