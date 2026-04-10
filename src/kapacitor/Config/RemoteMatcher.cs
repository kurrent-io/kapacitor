using System.Text.RegularExpressions;

namespace kapacitor.Config;

public static partial class RemoteMatcher {
    /// <summary>
    /// Normalizes a git remote URL to "host/owner/path" form.
    /// Strips protocol, auth, .git suffix, and normalizes SSH colon syntax.
    /// Returns null if the URL is not a recognized git remote format.
    /// </summary>
    public static string? NormalizeRemoteUrl(string url) {
        if (string.IsNullOrWhiteSpace(url)) return null;

        // SSH: git@host:owner/repo(.git)?
        var sshMatch = SshRemoteRegex().Match(url);
        if (sshMatch.Success) {
            var host = sshMatch.Groups["host"].Value;
            var path = sshMatch.Groups["path"].Value;
            return $"{host}/{StripGitSuffix(path)}";
        }

        // SSH with protocol: ssh://git@host/owner/repo(.git)?
        var sshProtoMatch = SshProtoRemoteRegex().Match(url);
        if (sshProtoMatch.Success) {
            var host = sshProtoMatch.Groups["host"].Value;
            var path = sshProtoMatch.Groups["path"].Value;
            return $"{host}/{StripGitSuffix(path)}";
        }

        // HTTPS/HTTP: https://(user:pass@)?host/owner/repo(.git)?
        var httpsMatch = HttpsRemoteRegex().Match(url);
        if (httpsMatch.Success) {
            var host = httpsMatch.Groups["host"].Value;
            var path = httpsMatch.Groups["path"].Value;
            return $"{host}/{StripGitSuffix(path)}";
        }

        return null;
    }

    static string StripGitSuffix(string path) =>
        path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? path[..^4]
            : path;

    /// <summary>
    /// Finds the profile whose remote patterns match one of the given repo remote URLs.
    /// Returns the profile name, or null if no match.
    /// Throws if multiple profiles match.
    /// </summary>
    public static string? FindMatchingProfile(
        Dictionary<string, Profile> profiles,
        string[] remoteUrls
    ) {
        var normalized = remoteUrls
            .Select(NormalizeRemoteUrl)
            .Where(u => u is not null)
            .ToList();

        if (normalized.Count == 0) return null;

        var matches = new List<string>();

        foreach (var (name, profile) in profiles) {
            if (profile.Remotes is not { Length: > 0 }) continue;

            foreach (var pattern in profile.Remotes) {
                if (normalized.Any(url => GlobMatch(url!, pattern))) {
                    matches.Add(name);
                    break;
                }
            }
        }

        return matches.Count switch {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException(
                $"Multiple profiles match this repo's remotes: {string.Join(", ", matches)}. " +
                "Add a .kapacitor.json to the repo to disambiguate.")
        };
    }

    static bool GlobMatch(string input, string pattern) {
        var regexPattern = "^" + Regex.Escape(pattern).Replace(@"\*", "[^/]*") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase);
    }

    [GeneratedRegex(@"^git@(?<host>[\w.-]+):(?<path>.+)$")]
    private static partial Regex SshRemoteRegex();

    [GeneratedRegex(@"^ssh://(?:[^@]+@)?(?<host>[\w.-]+)/(?<path>.+)$")]
    private static partial Regex SshProtoRemoteRegex();

    [GeneratedRegex(@"^https?://(?:[^@]+@)?(?<host>[\w.-]+)/(?<path>.+)$")]
    private static partial Regex HttpsRemoteRegex();
}
