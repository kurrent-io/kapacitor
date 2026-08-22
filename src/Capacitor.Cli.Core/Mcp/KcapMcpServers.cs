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

    /// <summary>The one server whose registration carries a driver stamp (see <see cref="ForHarness"/>).</summary>
    internal const string FlowsServerName = "kcap-flows";

    /// <summary>The flag that stamps the driving harness's vendor into the flows registration.</summary>
    internal const string DriverArg = "--driver";

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

    /// <summary>Codex receives the full set. Kept as a named per-harness seam so a future
    /// divergence has a home, but today it is the whole `All` list — `kcap-workitems` is now
    /// registered everywhere (its session id resolves from an explicit arg / `KCAP_SESSION_ID` /
    /// `CODEX_THREAD_ID`, and its breakdown/relation tools need no session id at all). Flows remains
    /// non-read-only and is never auto-approved.</summary>
    public static IReadOnlyList<KcapMcpServer> ForCodex => All;

    /// <summary>The bare (pre-stamp) set for every non-Claude JSON harness (Cursor, Copilot,
    /// OpenCode, Kiro, Gemini, Antigravity) — the full `All` list, `kcap-workitems` included.
    /// <see cref="ForHarness"/> derives each harness's actual registration from this by stamping
    /// the flows entry with that harness's driver vendor.</summary>
    public static IReadOnlyList<KcapMcpServer> ForCursor => All;

    /// <summary>The server set one JSON harness registers, with its <c>kcap-flows</c> entry stamped
    /// <c>--driver &lt;vendor&gt;</c>. The stamp is the ONLY per-process signal for the driving harness's
    /// identity on the six JSON harnesses, which — unlike Claude Code and Codex — export no distinctive
    /// env var into the long-lived MCP server child (see <c>DriverVendor</c> / <c>HarnessRequesterContext</c>).
    /// The extra argv reaches the SAME <c>kcap mcp flows</c> subcommand and therefore the same tool
    /// schema; it only tells the server which vendor is driving, so a reviewer can be recommended that
    /// differs from it. Claude/Codex stay on env inference (unstamped), so their registrations are
    /// unchanged.</summary>
    public static IReadOnlyList<KcapMcpServer> ForHarness(string vendor) =>
        [.. ForCursor.Select(s => s.Name == FlowsServerName
            ? s with { Args = [.. s.Args, DriverArg, vendor] }
            : s)];
}
