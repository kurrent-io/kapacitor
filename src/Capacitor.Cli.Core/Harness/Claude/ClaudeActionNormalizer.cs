namespace Capacitor.Cli.Core.Harness.Claude;

using System.Text.Json;
using Capacitor.Cli.Core.Policy;

/// <summary>
/// Never throws and never skips: any unmappable payload yields kind <see cref="ActionKind.Other"/>
/// with the raw payload, so no Claude tool call escapes policy evaluation.
/// </summary>
public static class ClaudeActionNormalizer {
    const string Vendor = "claude";

    public static CanonicalAction Normalize(string? toolName, JsonElement? toolInput, string? cwd) {
        try { return NormalizeCore(toolName, toolInput, cwd); }
        catch { return Other(toolName, toolInput, cwd); }
    }

    static CanonicalAction NormalizeCore(string? toolName, JsonElement? toolInput, string? cwd) {
        switch (toolName) {
            case "Bash": {
                if (toolInput?.Str("command") is not { Length: > 0 } command) break;
                var analysis = ShellCommandAnalyzer.Analyze(command);
                return new() {
                    Kind = ActionKind.Shell, Vendor = Vendor, Cwd = cwd,
                    Command = command, Analyzed = analysis.Analyzed, Segments = analysis.Segments,
                };
            }
            case "Edit" or "Write" or "MultiEdit":
                return FileAction(ActionKind.FileEdit, toolInput?.Str("file_path"), toolName, toolInput, cwd);
            case "NotebookEdit":
                return FileAction(ActionKind.FileEdit, toolInput?.Str("notebook_path"), toolName, toolInput, cwd);
            case "Read":
                return FileAction(ActionKind.FileRead, toolInput?.Str("file_path"), toolName, toolInput, cwd);
            case "Glob" or "Grep":
                return FileAction(ActionKind.FileRead, toolInput?.Str("path") ?? cwd, toolName, toolInput, cwd);
            case "WebFetch": {
                if (toolInput?.Str("url") is not { Length: > 0 } url) break;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IdnHost.Length == 0) break;
                return new() {
                    Kind = ActionKind.Network, Vendor = Vendor, Cwd = cwd, Url = url,
                    Host = uri.IdnHost.ToLowerInvariant(), Port = uri.IsDefaultPort ? null : uri.Port,
                };
            }
            default: {
                if (toolName is not null && toolName.StartsWith("mcp__", StringComparison.Ordinal)) {
                    var rest = toolName["mcp__".Length..];
                    var split = rest.IndexOf("__", StringComparison.Ordinal);
                    if (split > 0 && split + 2 < rest.Length)
                        return new() {
                            Kind = ActionKind.McpTool, Vendor = Vendor, Cwd = cwd,
                            Server = rest[..split], Tool = rest[(split + 2)..],
                        };
                }
                break;
            }
        }
        return Other(toolName, toolInput, cwd);
    }

    static CanonicalAction FileAction(ActionKind kind, string? path, string? toolName, JsonElement? toolInput, string? cwd) =>
        LexicalPaths.TryResolve(cwd, path) is { } resolved
            ? new() { Kind = kind, Vendor = Vendor, Cwd = cwd, Paths = [resolved] }
            : Other(toolName, toolInput, cwd);

    static CanonicalAction Other(string? toolName, JsonElement? toolInput, string? cwd) => new() {
        Kind = ActionKind.Other, Vendor = Vendor, Cwd = cwd,
        RawToolName = string.IsNullOrEmpty(toolName) ? null : toolName,
        RawPayloadJson = toolInput?.GetRawText(),
    };
}
