namespace Capacitor.Cli.Core.Mcp;

/// <summary>One kcap MCP server, described semantically (no harness field names).
/// <paramref name="ReadOnly"/> marks a server whose tools are all side-effect-free (pure reads),
/// so it is safe to auto-approve on registration where the harness supports per-server trust —
/// see <see cref="McpConfigShape.Trust"/>. Servers that write (kcap-memory's save) or launch work
/// (kcap-flows' start_review_flow spawns a paid hosted reviewer) are NOT read-only and keep
/// prompting.</summary>
public sealed record KcapMcpServer(string Name, string[] Args, bool NeedsProjectCwd, string? Description, bool ReadOnly = false);

/// <summary>The single source of truth for the kcap MCP servers. Every writer
/// (Codex TOML, the JSON harnesses, the bundled `.mcp.json`) derives from this.</summary>
public static class KcapMcpServers {
    public const string Command = "kcap";

    public static readonly IReadOnlyList<KcapMcpServer> All = [
        new("kcap-review",   ["mcp", "review"],   NeedsProjectCwd: false,
            "PR review context tools — query implementation session transcripts.", ReadOnly: true),
        new("kcap-sessions", ["mcp", "sessions"], NeedsProjectCwd: true,
            "Search and recall past Kurrent Capacitor sessions — the reasoning behind prior work (why / what-was-tried / who-decided). Repo-aware; reach for it before git log or grep for history questions.", ReadOnly: true),
        new("kcap-flows",    ["mcp", "flows"],    NeedsProjectCwd: true,
            "Structured AI agent flows — launches a SEPARATE hosted participant agent; requires login + a running daemon."),
        new("kcap-memory",   ["mcp", "memory"],   NeedsProjectCwd: true,
            "Team memory — search, read, and save durable learnings."),
        new("kcap-workitems", ["mcp", "workitems"], NeedsProjectCwd: true,
            "Attach the current session to a work item (issue, PR, or a brand-new item), and list what a session is attached to."),
        new("kcap-analytics", ["mcp", "analytics"], NeedsProjectCwd: true,
            "Query the org's AI coding-agent analytics (sessions, tools, tokens, cost, commits, PRs, evals) with read-only SQL. Repo-aware: defaults to the current repo; pass scope 'global' for org-wide.", ReadOnly: true),
    ];

    /// <summary>Codex receives flows so any driver can explicitly route a reserved review alias
    /// to any certified reviewer vendor. Flows remains non-read-only and is never auto-approved.
    /// Workitems remains Claude-plugin-only.</summary>
    public static IReadOnlyList<KcapMcpServer> ForCodex =>
        All.Where(s => s.Name != "kcap-workitems").ToArray();

    /// <summary>The shared set for every non-Claude JSON harness (Cursor, Copilot, OpenCode,
    /// Kiro, Gemini, Antigravity) — omits only `kcap-workitems` (Claude Code plugin only).</summary>
    public static IReadOnlyList<KcapMcpServer> ForCursor =>
        All.Where(s => s.Name != "kcap-workitems").ToArray();
}
