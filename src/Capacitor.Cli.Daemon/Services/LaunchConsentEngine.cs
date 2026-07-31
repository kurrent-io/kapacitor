using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

internal readonly record struct LaunchConsentInput(
    string? RequesterUserId,
    bool RequesterIsOwner,
    string Kind,
    string RepoPath,
    string Vendor);

internal enum LaunchConsentVerdict { Allow, Deny, Prompt }

/// Source: "owner" | "rule[i]" | "default" — recorded verbatim in the decision log.
internal readonly record struct LaunchConsentDecision(LaunchConsentVerdict Verdict, string Source);

internal static class LaunchConsentEngine {
    public static string KindToken(LaunchKind kind) => kind switch {
        LaunchKind.Review => "review",
        LaunchKind.ReviewFlow => "review-flow",
        _ => "agent",
    };

    public static LaunchConsentDecision Evaluate(LaunchConsentPolicy policy, in LaunchConsentInput input) {
        if (input.RequesterIsOwner) return new(LaunchConsentVerdict.Allow, "owner");
        for (var i = 0; i < policy.Rules.Count; i++) {
            var r = policy.Rules[i];
            if (!Matches(r, input)) continue;
            var verdict = string.Equals(r.Action, "deny", StringComparison.Ordinal)
                ? LaunchConsentVerdict.Deny : LaunchConsentVerdict.Allow;
            return new(verdict, $"rule[{i}]");
        }
        return new(policy.Default switch {
            LaunchConsentDefault.Deny => LaunchConsentVerdict.Deny,
            LaunchConsentDefault.Prompt => LaunchConsentVerdict.Prompt,
            _ => LaunchConsentVerdict.Allow,
        }, "default");
    }

    static bool Matches(LaunchConsentRule r, in LaunchConsentInput x) =>
        (r.Requester is null || string.Equals(r.Requester, x.RequesterUserId, StringComparison.Ordinal)) &&
        (r.Kind is null || string.Equals(r.Kind, x.Kind, StringComparison.Ordinal)) &&
        (r.Vendor is null || string.Equals(r.Vendor, x.Vendor, StringComparison.OrdinalIgnoreCase)) &&
        (r.Repo is null || RepoMatches(r.Repo, x.RepoPath));

    static bool RepoMatches(string pattern, string repoPath) {
        // Compare with forward slashes so a "/*" wildcard and prefix matching work regardless of
        // the host's native separator (Windows canonical paths use '\'). No-op on POSIX. Mirrors
        // DaemonConfig.IsRepoAllowed.
        var normalizedPattern  = pattern.Replace('\\', '/');
        var normalizedRepoPath = repoPath.Replace('\\', '/');

        if (normalizedPattern.EndsWith("/*", StringComparison.Ordinal)) {
            var prefix = normalizedPattern[..^1];
            // Match both subpaths ("/allowed/proj") and the directory itself ("/allowed")
            return normalizedRepoPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedRepoPath, normalizedPattern[..^2], StringComparison.OrdinalIgnoreCase);
        }
        // Exact match with defensive trailing separator normalization
        // (differs from DaemonConfig which doesn't trim, but required by the test spec)
        return string.Equals(
            Path.TrimEndingDirectorySeparator(normalizedPattern),
            Path.TrimEndingDirectorySeparator(normalizedRepoPath),
            StringComparison.OrdinalIgnoreCase);
    }
}
