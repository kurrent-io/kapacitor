using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.RepoEvidence;

public enum RepoEvidenceKind { Mutation, Read }

/// <summary>
/// Derives a git root from absolute paths in tool-use inputs, for sessions
/// launched outside any repo. Two-slot rule: the first mutation-derived root
/// attributes immediately; the first read-derived root is promoted only at
/// final drain if no mutation ever landed. Evidence is the tool INPUT —
/// results are never consulted. Fail-open on every parse or resolver error.
/// </summary>
public sealed class RepoEvidenceScanner(Func<string, string?> findRoot) {
    public string? AttributedRoot   { get; private set; }
    public string? ReadFallbackRoot { get; private set; }
    public bool    Done => AttributedRoot is not null;

    public string? OnLine(string vendor, string jsonlLine) {
        if (Done || vendor != "claude") return null;

        try {
            foreach (var (path, kind) in ExtractClaudePaths(jsonlLine)) {
                var dir  = SafeDirectory(path);
                if (dir is null) continue;
                var root = Safe(() => findRoot(dir));
                if (root is null) continue;

                if (kind == RepoEvidenceKind.Mutation) {
                    AttributedRoot = root;
                    return root;
                }

                ReadFallbackRoot ??= root;
            }
        } catch {
            // fail-open: a bad line must never break watching or import
        }

        return null;
    }

    public string? PromoteReadFallback() {
        if (Done || ReadFallbackRoot is null) return null;
        AttributedRoot = ReadFallbackRoot;
        return AttributedRoot;
    }

    public static IReadOnlyList<(string Path, RepoEvidenceKind Kind)> ExtractClaudePaths(string jsonlLine) {
        var result = new List<(string, RepoEvidenceKind)>();

        try {
            if (JsonNode.Parse(jsonlLine) is not JsonObject obj) return result;
            if (obj["type"]?.GetValue<string>() != "assistant") return result;
            if (obj["message"]?["content"] is not JsonArray content) return result;

            foreach (var item in content) {
                if (item is not JsonObject block) continue;
                if (block["type"]?.GetValue<string>() != "tool_use") continue;

                var name  = block["name"]?.GetValue<string>();
                var input = block["input"] as JsonObject;
                if (name is null || input is null) continue;

                (string key, RepoEvidenceKind kind)? spec = name switch {
                    "Edit" or "MultiEdit" or "Write" => ("file_path", RepoEvidenceKind.Mutation),
                    "NotebookEdit"                   => ("notebook_path", RepoEvidenceKind.Mutation),
                    "Read"                           => ("file_path", RepoEvidenceKind.Read),
                    "Glob" or "Grep"                 => ("path", RepoEvidenceKind.Read),
                    _                                => null,
                };
                if (spec is not { } sp) continue;

                var path = input[sp.key]?.GetValue<string>();
                if (path is not null && path.StartsWith('/')) result.Add((path, sp.kind));
            }
        } catch {
            // fail-open
        }

        return result;
    }

    static string? SafeDirectory(string path) {
        try { return Path.GetDirectoryName(path); } catch { return null; }
    }

    static T? Safe<T>(Func<T?> f) where T : class {
        try { return f(); } catch { return null; }
    }
}
