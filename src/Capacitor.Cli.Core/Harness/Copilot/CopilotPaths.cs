namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// Filesystem layout for GitHub Copilot CLI state. Everything lives under a
/// single root: <c>$COPILOT_HOME</c> when set (Copilot relocates its entire
/// tree through that variable — hooks inherit it from the spawning process),
/// otherwise <c>~/.copilot</c> on every OS.
/// </summary>
public sealed class CopilotPaths {
    public CopilotPaths(UserHome home, string? copilotHome) =>
        Root = !string.IsNullOrEmpty(copilotHome) ? copilotHome : Path.Combine(home.Path, ".copilot");

    public string Root { get; }

    /// <summary>
    /// Detection by root-dir presence — Copilot CLI creates <c>~/.copilot</c>
    /// on first run, and (unlike Codex) the binary name <c>copilot</c> is too
    /// generic for a PATH probe to be the only signal. Callers that want the
    /// PATH probe too OR this with <c>BinaryProbe.OnPath("copilot")</c>.
    /// </summary>
    public bool IsInstalled => Directory.Exists(Root);

    /// <summary>
    /// User-level hooks directory. Copilot merges every <c>*.json</c> file in
    /// here at startup, so kcap owns its own file (<see cref="KcapHooksJson"/>)
    /// instead of merging into a shared one the way the Cursor installer must.
    /// </summary>
    public string HooksDir => Path.Combine(Root, "hooks");

    public string KcapHooksJson => Path.Combine(HooksDir, "kcap.json");

    /// <summary>
    /// User-level MCP server config (<c>mcpServers</c> object, each entry
    /// <c>type: "stdio"</c>). Copilot reads it from the root dir, so it honors
    /// <c>$COPILOT_HOME</c> like the rest of the layout.
    /// </summary>
    public string McpConfigJson => Path.Combine(Root, "mcp-config.json");

    /// <summary>
    /// User-level custom-instructions file Copilot loads into context at startup. Honors
    /// <c>$COPILOT_HOME</c> like the rest of the layout.
    /// </summary>
    public string InstructionsMd => Path.Combine(Root, "copilot-instructions.md");

    /// <summary>
    /// Per-session state root: one subdirectory per session (named with the
    /// dashed session uuid) containing <c>events.jsonl</c> (append-only
    /// transcript), <c>workspace.yaml</c> (cwd/repo/title metadata), and
    /// checkpoint artifacts. Directories WITHOUT an events.jsonl are
    /// failed-startup scaffolding and must be skipped by discovery.
    /// </summary>
    public string SessionStateDir => Path.Combine(Root, "session-state");

    /// <summary>
    /// Pre-GA session storage (Copilot migrated to <c>session-state/</c> in
    /// late 2025; old sessions are only migrated lazily on resume). Import
    /// walks both roots.
    /// </summary>
    public string LegacySessionStateDir => Path.Combine(Root, "history-session-state");

    /// <summary>Both session-state roots, current first: a session migrated on resume exists in
    /// both, and <c>session-state/</c> holds the longer transcript.</summary>
    public IReadOnlyList<string> SessionStateDirs => [SessionStateDir, LegacySessionStateDir];

    /// <summary>Transcript path for a session dir name (the dashed session uuid) under whichever of
    /// <see cref="SessionStateDirs"/> the caller is walking.</summary>
    public string EventsJsonl(string sessionStateDir, string sessionDirName)
        => Path.Combine(sessionStateDir, sessionDirName, "events.jsonl");

    public string WorkspaceYaml(string sessionStateDir, string sessionDirName)
        => Path.Combine(sessionStateDir, sessionDirName, "workspace.yaml");
}
