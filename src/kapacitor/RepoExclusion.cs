using System.Text.Json.Nodes;

namespace kapacitor;

static class RepoExclusion {
    /// <summary>
    /// Checks if the session's repository is in the excluded repos list.
    /// Returns true if the repo is excluded (caller should skip processing).
    /// </summary>
    public static async Task<bool> IsExcludedAsync(string body, string[]? excludedRepos) {
        if (excludedRepos is null or { Length: 0 }) return false;

        try {
            var node = JsonNode.Parse(body);

            if (node is null) return false;

            // Try to get repo from the payload's repository field first
            var owner    = node["repository"]?["owner"]?.GetValue<string>();
            var repoName = node["repository"]?["repo_name"]?.GetValue<string>();

            if (owner is not null && repoName is not null) {
                return excludedRepos.Contains($"{owner}/{repoName}", StringComparer.OrdinalIgnoreCase);
            }

            // Fall back to detecting repo from cwd
            var cwd = node["cwd"]?.GetValue<string>();

            if (cwd is null) return false;

            var repo = await RepositoryDetection.DetectRepositoryAsync(cwd);

            if (repo?.Owner is not null && repo.RepoName is not null) {
                return excludedRepos.Contains($"{repo.Owner}/{repo.RepoName}", StringComparer.OrdinalIgnoreCase);
            }
        } catch {
            // Best effort — if detection fails, don't exclude
        }

        return false;
    }
}
