using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.RepoEvidence;

public enum RepoEvidenceKind { Mutation, Read }

/// <summary>
/// Derives a resolved repo from absolute paths in tool-use inputs, for sessions launched
/// outside any repo. Two-slot rule: the first mutation-derived root that resolves to a
/// COMPLETE repo (<paramref name="isComplete"/>) wins slot A immediately; the first
/// read-derived root that resolves complete is remembered as slot B and promoted only at
/// final drain if slot A never landed. A root that fails to resolve, or resolves incomplete
/// (e.g. a local repo with no remote), is skipped — scanning continues past it rather than
/// latching onto something unusable. Each distinct root is resolved at most once (cached), to
/// bound the resolver's cost. Evidence is the tool INPUT — results are never consulted.
/// Fail-open on every parse or resolver error. Generic so Capacitor.Cli.Core never depends on
/// the concrete repo type or how it's resolved — both live in the higher-level Cli project.
/// </summary>
public sealed class RepoEvidenceScanner<TRepo>(
        Func<string, string?>      findRoot,
        Func<string, Task<TRepo?>> resolve,
        Func<TRepo, bool>          isComplete
    ) where TRepo : class {
    readonly Dictionary<string, TRepo?> _resolved = new(StringComparer.Ordinal);

    public TRepo? Attributed   { get; private set; }
    public TRepo? ReadFallback { get; private set; }
    public bool   Done => Attributed is not null;

    public async Task<TRepo?> OnLineAsync(string vendor, string jsonlLine) {
        if (Done || vendor != "claude") return null;

        try {
            foreach (var (path, kind) in RepoEvidencePaths.ExtractClaudePaths(jsonlLine)) {
                var dir  = SafeDirectory(path);
                if (dir is null) continue;
                var root = Safe(() => findRoot(dir));
                if (root is null) continue;

                var repo = await ResolveCachedAsync(root);
                if (repo is null || !SafeIsComplete(repo)) continue;

                if (kind == RepoEvidenceKind.Mutation) {
                    Attributed = repo;
                    return repo;
                }

                ReadFallback ??= repo;
            }
        } catch {
            // fail-open: a bad line must never break watching or import
        }

        return null;
    }

    public async Task<TRepo?> PromoteReadFallbackAsync() {
        if (Done || ReadFallback is null) return null;
        Attributed = ReadFallback;
        return Attributed;
    }

    async Task<TRepo?> ResolveCachedAsync(string root) {
        if (_resolved.TryGetValue(root, out var cached)) return cached;

        TRepo? repo;
        try { repo = await resolve(root); } catch { repo = null; }

        _resolved[root] = repo;
        return repo;
    }

    bool SafeIsComplete(TRepo repo) {
        try { return isComplete(repo); } catch { return false; }
    }

    static string? SafeDirectory(string path) {
        try { return Path.GetDirectoryName(path); } catch { return null; }
    }

    static T? Safe<T>(Func<T?> f) where T : class {
        try { return f(); } catch { return null; }
    }
}

// Not nested inside RepoEvidenceScanner<TRepo>: CA1000 forbids public static members on a
// generic type, and extraction doesn't depend on TRepo anyway.
public static class RepoEvidencePaths {
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
}
