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

    // Lexical, not Path.GetDirectoryName: that rewrites/misreads the OTHER OS's separator style
    // (a Unix path fed through Windows' Path becomes garbage, and vice versa), but evidence
    // paths are native to whichever OS the transcript's own agent ran on, not this process's OS.
    static string? SafeDirectory(string path) {
        try {
            var lastSeparator = path.AsSpan().LastIndexOfAny('/', '\\');
            return lastSeparator < 0 ? null : path[..lastSeparator];
        } catch { return null; }
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

            // A tool_use block can sit at the event's top-level `content` too, not only nested
            // under `message.content` — both shapes occur in real Claude transcripts.
            var content = (obj["message"]?["content"] as JsonArray) ?? (obj["content"] as JsonArray);
            if (content is null) return result;

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
                if (path is not null && IsLexicallyAbsolute(path)) result.Add((path, sp.kind));
            }
        } catch {
            // fail-open
        }

        return result;
    }

    /// <summary>Mirrors <c>Capacitor.Sessions.RepoBackfill.RepoAttributionMatcher.IsLexicallyAbsolute</c>
    /// in the server repo (reimplemented locally — Cli.Core takes no dependency on the server).
    /// Unix-rooted, Windows drive-rooted, or UNC — never <c>Path.IsPathRooted</c>/<c>Path.IsPathFullyQualified</c>,
    /// which judge by whichever OS is running the scan, not whichever OS produced the path (a Unix
    /// path is rejected by Windows' Path, and a Windows path by Unix's).</summary>
    internal static bool IsLexicallyAbsolute(string path) {
        if (string.IsNullOrEmpty(path)) return false;
        if (path[0] == '/') return true;                                        // Unix
        if (path.Length >= 2 && path[0] == '\\' && path[1] == '\\') return true; // UNC \\host\share
        if (path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/')) return true;                 // C:\... or C:/...
        return false;
    }

    static bool IsAsciiLetter(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
}
