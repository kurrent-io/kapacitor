namespace kapacitor.Config;

public record ResolvedProfile(string? ServerUrl, string? ProfileName, Profile? Profile, string? Warning);

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
            return new(AppConfig.NormalizeUrl(cliServerUrl), null, null, null);

        // 2. KAPACITOR_URL env var
        if (!string.IsNullOrEmpty(envUrl))
            return new(AppConfig.NormalizeUrl(envUrl), null, null, null);

        // 3. KAPACITOR_PROFILE env var
        if (!string.IsNullOrEmpty(envProfile))
            return ResolveByName(envProfile);

        // 4. .kapacitor.json in repo
        if (repoConfig?.Profile is { } repoProfileName) {
            if (config.Profiles.TryGetValue(repoProfileName, out var repoProfile)) {
                if (repoConfig.ServerUrl is { } repoUrl
                 && !string.IsNullOrEmpty(repoProfile.ServerUrl)
                 && AppConfig.NormalizeUrl(repoUrl) != AppConfig.NormalizeUrl(repoProfile.ServerUrl!)) {
                    return new(
                        AppConfig.NormalizeUrl(repoProfile.ServerUrl!),
                        repoProfileName,
                        repoProfile,
                        $"Profile '{repoProfileName}' server URL does not match .kapacitor.json (stale config?)"
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
                $"Profile '{repoProfileName}' from .kapacitor.json not found locally. Run: kapacitor profile add {repoProfileName} --server-url {repoConfig.ServerUrl}"
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

        // 6. Profile binding (from `kapacitor use`)
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
