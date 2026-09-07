namespace Capacitor.Cli.Core.Harness.Claude;

/// What a Claude tool call did, in the vendor-neutral vocabulary.
public static class ClaudeToolKinds {
    /// Claude's built-in tools carry their kind in their name, so the table is the whole mapping —
    /// no input inspection. A `Bash` call stays `execute` however its command reads: Claude has real
    /// `Read` and `Grep` tools, so classifying the command would misreport the tool the model chose.
    /// Everything unlisted (subagents, skills, todos, `mcp__*`) is `other`: none of the above, which
    /// is not the same as the null a lane that classifies nothing leaves behind.
    public static string Of(string? toolName) => toolName switch {
        "Read" or "NotebookRead"                           => AcpToolKind.Read,
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => AcpToolKind.Edit,
        "Bash" or "BashOutput" or "KillShell"              => AcpToolKind.Execute,
        "Grep" or "Glob" or "LS"                           => AcpToolKind.Search,
        "WebFetch" or "WebSearch"                          => AcpToolKind.Fetch,
        _                                                  => AcpToolKind.Other,
    };
}
