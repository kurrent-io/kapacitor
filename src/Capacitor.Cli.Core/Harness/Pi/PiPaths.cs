namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>
/// Filesystem layout for Pi (badlogic/pi-mono, the <c>pi</c> CLI). Pi keeps its
/// agent state under <c>~/.pi/agent</c>: sessions as tree-structured JSONL in
/// <c>sessions/</c> (organized by working directory), and auto-discovered
/// TypeScript extensions in <c>extensions/</c>.
///
/// Pi has <b>no shell hooks</b>, so kcap's live integration ships as an
/// extension file (<see cref="KcapExtension"/>, written by <c>kcap plugin --pi</c>)
/// rather than a hooks.json the way Copilot/Cursor do.
/// </summary>
public sealed class PiPaths {
    /// <param name="agentDir">Pi's <c>PI_CODING_AGENT_DIR</c>, which relocates the agent-state
    /// leaf directly — Pi uses the value verbatim (tilde-expanded), appending <c>/agent</c> only
    /// on the default fallback. Null means unset.</param>
    public PiPaths(UserHome home, string? agentDir) {
        Root     = Path.Combine(home.Path, ".pi");
        AgentDir = !string.IsNullOrWhiteSpace(agentDir)
            ? ExpandTilde(agentDir, home.Path)
            : Path.Combine(Root, "agent");
    }


    public string Root     { get; }
    public string AgentDir { get; }

    /// <summary>
    /// Session JSONL root — one <c>*.jsonl</c> per session, possibly nested in
    /// per-cwd subdirectories. Each file's first line is the <c>session</c>
    /// header (full uuid <c>id</c>, <c>cwd</c>, ISO <c>timestamp</c>); discovery
    /// walks this tree recursively. Pi's <c>--session-dir</c> can relocate it,
    /// so callers may override.
    /// </summary>
    public string SessionsDir => Path.Combine(AgentDir, "sessions");

    /// <summary>Auto-discovered extensions dir; kcap installs <see cref="KcapExtension"/> here.</summary>
    public string ExtensionsDir => Path.Combine(AgentDir, "extensions");

    public string KcapExtension => Path.Combine(ExtensionsDir, "kcap.ts");

    /// <summary>Marker recording the installed extension version (sibling of kcap.ts).</summary>
    public string KcapExtensionMarker => Path.Combine(ExtensionsDir, ".kcap-extension-version");

    /// <summary>
    /// The kcap MCP-bridge extension (Pi has no built-in MCP, so kcap ships a second
    /// extension that spawns the <c>kcap mcp &lt;name&gt;</c> servers and registers
    /// their tools). Installed by <see cref="PiMcpExtensionInstaller"/>.
    /// </summary>
    public string KcapMcpExtension => Path.Combine(ExtensionsDir, "kcap-mcp.ts");

    /// <summary>Marker recording the installed MCP-bridge extension version (sibling of kcap-mcp.ts).</summary>
    public string KcapMcpExtensionMarker => Path.Combine(ExtensionsDir, ".kcap-mcp-extension-version");

    /// <summary>
    /// Pi's native user-global agent-instructions file (<c>~/.pi/agent/AGENTS.md</c>),
    /// a sibling of <c>extensions/</c>. kcap writes its marker-delimited steering block
    /// here (see <c>AgentInstructionsWriter</c>).
    /// </summary>
    public string AgentsMd => Path.Combine(AgentDir, "AGENTS.md");

    /// <summary>
    /// Detection by the agent-state dir's presence — Pi creates it on first run.
    /// The binary name <c>pi</c> is too generic for a PATH probe to be the only
    /// signal, so callers that also want the PATH probe OR this with
    /// <c>BinaryProbe.OnPath("pi")</c>.
    /// </summary>
    public bool IsInstalled => Directory.Exists(AgentDir);

    /// <summary>Expand a leading <c>~</c>/<c>~/</c> against <paramref name="home"/>, matching
    /// Pi's <c>expandTildePath</c>.</summary>
    static string ExpandTilde(string path, string home) {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal)) return path;

        return path.Length <= 1 ? home : Path.Combine(home, path[2..]);
    }
}
