using Capacitor.Models.Transcripts.Harness.Codex;

namespace Capacitor.Cli.Core.Harness.Codex;

/// What a codex tool call did, in the vendor-neutral vocabulary. The one place that rule lives: the
/// daemon's hosted lane and an imported rollout must not answer differently for the same call.
public static class CodexToolKinds {
    /// Codex's shell tools have no name of their own for a read or a search — `sed -n`, `cat` and
    /// `rg` all arrive as the same tool — so the command itself decides. `exec` is Codex's
    /// JavaScript sandbox rather than a shell, so it is never handed to the classifier. MCP tools
    /// arrive under their bare server-side name, indistinguishable from any other unrecognised
    /// tool, so both land on `other` — as does `tool_search`, deliberately: `search` means
    /// searching for content, and counting a tool-registry lookup as one would skew every consumer
    /// measuring how much the agent searched the workspace.
    public static string Of(string? toolName, string? inputJson) => toolName switch {
        "apply_patch"                              => AcpToolKind.Edit,
        "shell" or "exec_command" or "local_shell" => Shell(ToolCommandText.From(inputJson)),
        "write_stdin" or "exec"                    => AcpToolKind.Execute,
        "web_search"                               => AcpToolKind.Fetch,
        _                                          => AcpToolKind.Other,
    };

    /// The kind of a shell call from its command alone. An unclassifiable command is `execute`: a
    /// shell call did run something.
    public static string Shell(string? command) => CodexCommandClassifier.Classify(command)?.Type switch {
        "read"                   => AcpToolKind.Read,
        "search" or "list_files" => AcpToolKind.Search,
        _                        => AcpToolKind.Execute,
    };
}
