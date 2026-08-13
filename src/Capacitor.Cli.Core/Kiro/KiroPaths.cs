namespace Capacitor.Cli.Core.Kiro;

/// <summary>
/// Filesystem layout for AWS Kiro CLI (the bun/TUI terminal coding agent). Kiro
/// keeps everything under <c>~/.kiro</c> (relocatable via <c>KIRO_HOME</c>):
/// <list type="bullet">
///   <item><b>Sessions</b> — <c>~/.kiro/sessions/cli/{id}.jsonl</c> (append-only
///     conversation log, one JSON object per line) plus a sibling
///     <c>{id}.json</c> (metadata: cwd, model, title, timestamps). The kcap
///     watcher tails the <c>.jsonl</c> directly and <c>kcap import --kiro</c>
///     replays it — there is no SQLite to read (Kiro also writes a
///     <c>data.sqlite3</c>, but the JSONL is written for every session, so kcap
///     ignores the DB).</item>
///   <item><b>Agents</b> — <c>~/.kiro/agents/*.json</c>; kcap installs its
///     lifecycle hooks into <see cref="KcapAgentJson"/>.</item>
/// </list>
/// </summary>
public static class KiroPaths {
    /// <summary>Kiro's config root (<c>~/.kiro</c>). Relocated by <c>KIRO_HOME</c>.</summary>
    public static string ConfigRoot(string? home = null, string? kiroHome = null) {
        kiroHome ??= Environment.GetEnvironmentVariable("KIRO_HOME");
        if (!string.IsNullOrEmpty(kiroHome)) return kiroHome;

        home ??= PathHelpers.HomeDirectory;
        return Path.Combine(home, ".kiro");
    }

    /// <summary>Per-session JSONL store: <c>~/.kiro/sessions/cli</c>.</summary>
    public static string SessionsDir(string? home = null) =>
        Path.Combine(ConfigRoot(home), "sessions", "cli");

    /// <summary>Conversation log for a session id (the dashed UUID Kiro names the file with).</summary>
    public static string SessionJsonl(string sessionId, string? home = null) =>
        Path.Combine(SessionsDir(home), $"{sessionId}.jsonl");

    /// <summary>Metadata sibling for a session id (cwd / model / title / timestamps).</summary>
    public static string SessionJson(string sessionId, string? home = null) =>
        Path.Combine(SessionsDir(home), $"{sessionId}.json");

    /// <summary>User-level agent config dir (<c>~/.kiro/agents</c>).</summary>
    public static string AgentsDir(string? home = null) => Path.Combine(ConfigRoot(home), "agents");

    /// <summary>
    /// Kiro CLI settings file (<c>~/.kiro/settings/cli.json</c>). Holds dotted
    /// keys like <c>chat.defaultModel</c> and <c>chat.defaultAgent</c> — the
    /// latter is what <c>kiro-cli agent set-default</c> writes, and what kcap
    /// flips to its cloned agent so hooks fire for every session.
    /// </summary>
    public static string SettingsFile(string? home = null) =>
        Path.Combine(ConfigRoot(home), "settings", "cli.json");

    /// <summary>Kiro's user-level MCP config (<c>~/.kiro/settings/mcp.json</c>) — a plain
    /// <c>mcpServers</c> merge, independent of the agent-hooks file.</summary>
    public static string SettingsMcpJson(string? home = null) =>
        Path.Combine(ConfigRoot(home), "settings", "mcp.json");

    /// <summary>
    /// Global skills dir Kiro agents read (<c>~/.kiro/skills</c>). The default agent's
    /// <c>resources</c> include <c>skill:///&lt;home&gt;/.kiro/skills/*/SKILL.md</c>, so kcap installs
    /// its skills here to steer Kiro toward the kcap MCP tools (Kiro does not read the
    /// agent-agnostic <c>~/.agents/skills</c>). Mirrors <c>AntigravityPaths.SkillsDir</c>.
    /// </summary>
    public static string SkillsDir(string? home = null) => Path.Combine(ConfigRoot(home), "skills");

    /// <summary>
    /// kcap's owned agent-hooks file. Mirrors the Copilot model: kcap owns its own
    /// file rather than merging into a user agent, so removal is a clean delete.
    /// Kiro reads every <c>agents/*.json</c>, so the lifecycle hooks here apply to
    /// Kiro sessions.
    /// </summary>
    public static string KcapAgentJson(string? home = null) => Path.Combine(AgentsDir(home), "kcap.json");

    /// <summary>
    /// Detection: the config tree exists. The binary name (<c>kiro</c> /
    /// <c>kiro-cli</c>) is also probed by callers via <c>AgentDetector.IsInstalled</c>;
    /// OR the two for the widest coverage.
    /// </summary>
    public static bool IsInstalled(string? home = null, string? kiroHome = null) =>
        Directory.Exists(ConfigRoot(home, kiroHome));
}
