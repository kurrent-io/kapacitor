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
public static class PiPaths {
    public static string Root(string? home = null) {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".pi");
    }

    /// <summary>
    /// Agent state dir. <c>PI_CODING_AGENT_DIR</c> (when set) relocates THIS leaf
    /// directly — Pi uses the env value verbatim (tilde-expanded) as the agent
    /// dir; the <c>/agent</c> suffix is appended only on the default fallback.
    /// </summary>
    public static string AgentDir(string? home = null, string? agentDir = null) {
        agentDir ??= Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR");
        if (!string.IsNullOrWhiteSpace(agentDir)) return ExpandTilde(agentDir, home);

        return Path.Combine(Root(home), "agent");
    }

    /// <summary>Expand a leading <c>~</c>/<c>~/</c> against <paramref name="home"/>
    /// (or the OS user profile), matching Pi's <c>expandTildePath</c>.</summary>
    static string ExpandTilde(string path, string? home) {
        if (path != "~" && !path.StartsWith("~/", StringComparison.Ordinal) && !path.StartsWith("~\\", StringComparison.Ordinal)) return path;

        var baseDir = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.Length <= 1 ? baseDir : Path.Combine(baseDir, path[2..]);
    }

    /// <summary>
    /// Session JSONL root — one <c>*.jsonl</c> per session, possibly nested in
    /// per-cwd subdirectories. Each file's first line is the <c>session</c>
    /// header (full uuid <c>id</c>, <c>cwd</c>, ISO <c>timestamp</c>); discovery
    /// walks this tree recursively. Pi's <c>--session-dir</c> can relocate it,
    /// so callers may override.
    /// </summary>
    public static string SessionsDir(string? home = null) => Path.Combine(AgentDir(home), "sessions");

    /// <summary>Auto-discovered extensions dir; kcap installs <see cref="KcapExtension"/> here.</summary>
    public static string ExtensionsDir(string? home = null) => Path.Combine(AgentDir(home), "extensions");

    public static string KcapExtension(string? home = null) => Path.Combine(ExtensionsDir(home), "kcap.ts");

    /// <summary>
    /// The running process's name. Too generic to stand alone (see the note above), so a match must be
    /// corroborated by <see cref="ProcessCommandLineMarker"/>.
    /// </summary>
    public const string ProcessName = "pi";

    /// <summary>
    /// Pi ships as an npm package, so a genuine one runs out of a <c>node_modules</c> tree and its
    /// command line says so. Requiring it is what separates the agent from any other executable that
    /// happens to be called <c>pi</c>.
    /// </summary>
    public const string ProcessCommandLineMarker = "node_modules";

    /// <summary>Marker recording the installed extension version (sibling of kcap.ts).</summary>
    public static string KcapExtensionMarker(string? home = null) => Path.Combine(ExtensionsDir(home), ".kcap-extension-version");

    /// <summary>
    /// The kcap MCP-bridge extension (Pi has no built-in MCP, so kcap ships a second
    /// extension that spawns the <c>kcap mcp &lt;name&gt;</c> servers and registers
    /// their tools). Installed by <see cref="PiMcpExtensionInstaller"/>.
    /// </summary>
    public static string KcapMcpExtension(string? home = null) => Path.Combine(ExtensionsDir(home), "kcap-mcp.ts");

    /// <summary>Marker recording the installed MCP-bridge extension version (sibling of kcap-mcp.ts).</summary>
    public static string KcapMcpExtensionMarker(string? home = null) => Path.Combine(ExtensionsDir(home), ".kcap-mcp-extension-version");

    /// <summary>
    /// Pi's native user-global agent-instructions file (<c>~/.pi/agent/AGENTS.md</c>),
    /// a sibling of <c>extensions/</c>. kcap writes its marker-delimited steering block
    /// here (see <c>AgentInstructionsWriter</c>).
    /// </summary>
    public static string AgentsMd(string? home = null) => Path.Combine(AgentDir(home), "AGENTS.md");

    /// <summary>
    /// Detection by the agent-state dir's presence — Pi creates it on first run.
    /// The binary name <c>pi</c> is too generic for a PATH probe to be the only
    /// signal, so callers that also want the PATH probe OR this with
    /// <c>AgentDetection.BinaryOnPath("pi")</c>. <paramref name="agentDir"/> is a pure
    /// override for <see cref="AgentDir"/>'s <c>PI_CODING_AGENT_DIR</c> env read, so
    /// callers building a fully-injected detection input set never have to touch the
    /// real environment (mirrors <c>KiroPaths.IsInstalled</c>/<c>OpenCodePaths.IsInstalled</c>).
    /// </summary>
    public static bool IsInstalled(string? home = null, string? agentDir = null) =>
        Directory.Exists(AgentDir(home, agentDir));

    /// <summary>Pure variant of <see cref="AgentDir"/> for fully-injected callers (e.g.
    /// <see cref="Setup.AgentDetection"/>) — <paramref name="agentDir"/> null means "not set",
    /// never falls back to a real <c>PI_CODING_AGENT_DIR</c> process-env read.</summary>
    public static string AgentDirPure(string? home, string? agentDir) =>
        !string.IsNullOrWhiteSpace(agentDir) ? ExpandTilde(agentDir, home) : Path.Combine(Root(home), "agent");

    /// <summary>Pure variant of <see cref="IsInstalled"/> — never falls back to the real process
    /// environment for <c>PI_CODING_AGENT_DIR</c>.</summary>
    public static bool IsInstalledPure(string? home, string? agentDir) =>
        Directory.Exists(AgentDirPure(home, agentDir));
}
