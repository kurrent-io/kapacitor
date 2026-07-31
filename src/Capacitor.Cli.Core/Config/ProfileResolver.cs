namespace Capacitor.Cli.Core.Config;

/// <param name="Source">
/// Which configuration input supplied <paramref name="ServerUrl"/>. Needed because remediation
/// differs: `kcap config set server_url` does NOT repair a malformed KCAP_URL or --server-url, both
/// of which outrank the profile. Defaults to Profile — every branch below except the CLI flag and the
/// env var resolves through a profile, whichever way that profile was selected.
/// </param>
public record ResolvedProfile(
    string? ServerUrl, string? ProfileName, Profile? Profile, string? Warning,
    UrlSource Source = UrlSource.Profile);

public class ProfileResolver(
        ProfileConfig config,
        string?       cliServerUrl,
        string?       envUrl,
        string?       envProfile,
        RepoConfig?   repoConfig,
        string[]      repoRemoteUrls,
        string?       repoPath
    ) {
    public ResolvedProfile Resolve() {
        // 1. CLI --server-url flag
        if (!string.IsNullOrEmpty(cliServerUrl))
            return new(AppConfig.NormalizeUrl(cliServerUrl), null, null, null, UrlSource.CommandLine);

        // 2. KCAP_URL env var
        if (!string.IsNullOrEmpty(envUrl))
            return new(AppConfig.NormalizeUrl(envUrl), null, null, null, UrlSource.Environment);

        // 3. KCAP_PROFILE env var
        if (!string.IsNullOrEmpty(envProfile))
            return ResolveByName(envProfile);

        // 4. .kcap.json in repo
        if (repoConfig?.Profile is { } repoProfileName) {
            if (config.Profiles.TryGetValue(repoProfileName, out var repoProfile)) {
                if (repoConfig.ServerUrl is { } repoUrl
                 && !string.IsNullOrEmpty(repoProfile.ServerUrl)
                 && AppConfig.NormalizeUrl(repoUrl) != AppConfig.NormalizeUrl(repoProfile.ServerUrl!)) {
                    return new(
                        AppConfig.NormalizeUrl(repoProfile.ServerUrl!),
                        repoProfileName,
                        repoProfile,
                        $"Profile '{repoProfileName}' server URL does not match .kcap.json (stale config?)"
                    );
                }

                return new(
                    repoProfile.ServerUrl is not null ? AppConfig.NormalizeUrl(repoProfile.ServerUrl) : null,
                    repoProfileName,
                    repoProfile,
                    null
                );
            }

            return new(
                null,
                null,
                null,
                $"Profile '{repoProfileName}' from .kcap.json not found locally. Run: kcap profile add {repoProfileName} --server-url {repoConfig.ServerUrl}"
            );
        }

        // 5. Git remote match
        string? remoteMatch;

        try {
            remoteMatch = RemoteMatcher.FindMatchingProfile(config.Profiles, repoRemoteUrls);
        } catch (InvalidOperationException ex) {
            return new(null, null, null, ex.Message);
        }

        if (remoteMatch is not null)
            return ResolveByName(remoteMatch);

        // 6. Profile binding (from `kcap use`)
        if (repoPath is not null && config.ProfileBindings.TryGetValue(repoPath, out var boundName))
            return ResolveByName(boundName);

        // 7. Active profile fallback
        return ResolveByName(config.ActiveProfile);
    }

    ResolvedProfile ResolveByName(string name) {
        if (config.Profiles.TryGetValue(name, out var profile)) {
            return new(
                profile.ServerUrl is not null ? AppConfig.NormalizeUrl(profile.ServerUrl) : null,
                name,
                profile,
                null
            );
        }

        return new(null, null, null, $"Profile '{name}' not found.");
    }
}
