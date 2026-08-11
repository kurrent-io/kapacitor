namespace Capacitor.Cli.Core.Telemetry;

/// <summary>
/// Facts about the machine and build a run is happening on, as pure functions over an injected
/// environment — the same seam <see cref="TelemetrySettings"/> uses, so the provider table is
/// testable without mutating the real process environment.
/// </summary>
public static class TelemetryEnvironment {
    // Presence-based providers. Jenkins, TeamCity and Azure Pipelines are the load-bearing
    // entries: none of them exports the generic `CI`, so a run on any of them used to be
    // indistinguishable from a human at a terminal.
    static readonly string[] ProviderVariables = [
        "GITHUB_ACTIONS", "GITLAB_CI", "BUILDKITE", "CIRCLECI", "TRAVIS",
        "JENKINS_URL", "TEAMCITY_VERSION", "TF_BUILD", "APPVEYOR",
        "BITBUCKET_BUILD_NUMBER", "CODEBUILD_BUILD_ID", "DRONE", "WOODPECKER_CI",
        "SEMAPHORE", "HEROKU_TEST_RUN_ID", "NETLIFY", "VERCEL",
    ];

    /// <summary>
    /// CI machines are ephemeral and mint a fresh device id per run, so they are tagged rather
    /// than dropped — funnel insights filter is_ci = false.
    /// </summary>
    public static bool IsCi(IReadOnlyDictionary<string, string?> env) {
        // The generic flag is the one tools deliberately set to "false" to opt OUT, so its value
        // is meaningful. Provider variables below are presence-based instead: nothing sets
        // GITHUB_ACTIONS=false to mean "not GitHub Actions".
        if (env.TryGetValue("CI", out var ci) && !string.IsNullOrWhiteSpace(ci) && !IsNegative(ci))
            return true;

        foreach (var name in ProviderVariables)
            if (env.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw))
                return true;

        return false;
    }

    /// <summary>Live resolution against the real environment.</summary>
    public static bool IsCi() => IsCi(ReadEnv());

    /// <summary>
    /// "release" | "prerelease" | "unknown". Exists so insights can exclude dev-loop noise with a
    /// property filter rather than a `cli_version NOT LIKE '%alpha%'` string match that every
    /// future query has to remember to include. Local dev and Aspire-spawned dev daemons run
    /// prerelease builds against throwaway KCAP_CONFIG_DIRs, minting a device id per run, and
    /// they are not tagged is_ci — the version is the only thing that separates them from users.
    /// </summary>
    public static string BuildChannel(string? version) {
        if (string.IsNullOrWhiteSpace(version)) return "unknown";

        var s = version.Trim();
        if (string.Equals(s, "unknown", StringComparison.Ordinal)) return "unknown";

        // Build metadata may itself contain a hyphen (`+feature-branch.1`), so it has to go
        // before the prerelease test — otherwise a release build reads as a prerelease.
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];

        return s.Contains('-') ? "prerelease" : "release";
    }

    static IReadOnlyDictionary<string, string?> ReadEnv() {
        var env = new Dictionary<string, string?>(ProviderVariables.Length + 1) {
            ["CI"] = Environment.GetEnvironmentVariable("CI"),
        };

        foreach (var name in ProviderVariables)
            env[name] = Environment.GetEnvironmentVariable(name);

        return env;
    }

    static bool IsNegative(string raw) =>
        raw.Trim().ToLowerInvariant() is "false" or "0" or "no" or "off";
}
