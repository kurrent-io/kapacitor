namespace Capacitor.Cli.Core.Harness.Codex;

public sealed class CodexPaths {
    // CODEX_HOME (when set) replaces ~/.codex wholesale — Codex's own find_codex_home() reads it first.
    public CodexPaths(UserHome home, string? codexHome) =>
        Home = !string.IsNullOrWhiteSpace(codexHome) ? codexHome : Path.Combine(home.Path, ".codex");


    public string Home { get; }

    public string Sessions      => Path.Combine(Home, "sessions");
    public string UserHooksJson => Path.Combine(Home, "hooks.json");
    public string ConfigToml    => Path.Combine(Home, "config.toml");
    public string SkillsDir     => Path.Combine(Home, "skills");

    /// <summary>
    /// Matches Codex's own <c>[projects."&lt;path&gt;"]</c> key form: absolute, and on Windows
    /// lowercased (backslashes kept). TOML keys are case-sensitive, so a raw mixed-case path is
    /// a different key — the pre-trust write is then invisible to Codex and it parks on the
    /// directory-trust prompt. Windows-only: Unix filesystems are case-sensitive and Codex does
    /// not fold case there.
    /// </summary>
    public static string NormalizeProjectKey(string path) {
        var full = Path.GetFullPath(path);

        return OperatingSystem.IsWindows() ? full.ToLowerInvariant() : full;
    }

    /// <summary>
    /// Walk <c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>, optionally pruning
    /// directories below <paramref name="since"/>. Returns one entry per rollout
    /// with the session id parsed from the filename suffix
    /// (<c>rollout-&lt;ISO-ts&gt;-&lt;uuid&gt;.jsonl</c>) and an always-empty
    /// EncodedCwd — the day folder name (e.g. <c>"07"</c>) is NOT a Claude-style
    /// hyphen-encoded absolute path, so feeding it to
    /// <c>Capacitor.Cli.Commands.SessionImporter.DecodeCwdFromDirName</c> would
    /// produce a misleading relative cwd if <c>session_meta</c> parsing fails.
    /// Empty makes the decoder return null and callers degrade to "no cwd" cleanly.
    /// </summary>
    /// <param name="sessionsDir">The <c>~/.codex/sessions</c> root to walk.</param>
    /// <param name="since">Inclusive lower bound — files in date directories before this are skipped.</param>
    public static List<(string SessionId, string FilePath, string EncodedCwd)> Discover(string sessionsDir, DateOnly? since = null) {
        var results = new List<(string, string, string)>();

        if (!Directory.Exists(sessionsDir)) return results;

        foreach (var year in EnumerateNumericDirs(sessionsDir, since?.Year)) {
            foreach (var month in EnumerateNumericDirs(year.Path, MonthBound(since, year.Value))) {
                foreach (var day in EnumerateNumericDirs(month.Path, DayBound(since, year.Value, month.Value))) {
                    foreach (var jsonl in Directory.GetFiles(day.Path, "rollout-*.jsonl")) {
                        var sid = ExtractSessionIdFromFileName(jsonl);

                        if (sid is null) continue;

                        // EncodedCwd is left empty: Codex rollouts have no project-hashed parent
                        // dir, and the day name (e.g. "07") is NOT a Claude-style hyphen-encoded
                        // absolute path. Returning a non-empty value would cause
                        // SessionImporter.DecodeCwdFromDirName to feed a relative-looking string
                        // into RepositoryDetection.DetectRepositoryAsync if session_meta parsing
                        // fails — empty makes the decoder return null so the caller skips the
                        // probe entirely.
                        results.Add((sid, jsonl, ""));
                    }
                }
            }
        }

        return results;
    }

    static IEnumerable<(string Path, int Value)> EnumerateNumericDirs(string parent, int? minInclusive) {
        if (!Directory.Exists(parent)) yield break;

        foreach (var dir in Directory.GetDirectories(parent)) {
            var name = Path.GetFileName(dir);

            if (!int.TryParse(name, out var value)) continue;
            if (minInclusive is { } min && value < min) continue;

            yield return (dir, value);
        }
    }

    static int? MonthBound(DateOnly? since, int year) =>
        since is { } s && s.Year == year ? s.Month : null;

    static int? DayBound(DateOnly? since, int year, int month) =>
        since is { } s && s.Year == year && s.Month == month ? s.Day : null;

    /// <summary>
    /// Parse the trailing UUID from <c>rollout-&lt;ISO-ts&gt;-&lt;uuid&gt;.jsonl</c>.
    /// The UUID is normalized to dashless form so it round-trips with the rest of
    /// the CLI's NormalizeGuidField behaviour.
    /// </summary>
    public static string? ExtractSessionIdFromFileName(string filePath) {
        var name = Path.GetFileNameWithoutExtension(filePath);

        // rollout-2026-05-07T17-50-21-019e0322-05fc-7570-be65-75719c3ea861
        // Last 5 dash-separated chunks form the UUID.
        var parts = name.Split('-');

        if (parts.Length < 5) return null;

        var uuid = string.Join('-', parts[^5..]);

        return Guid.TryParse(uuid, out var guid) ? guid.ToString("N") : null;
    }
}
