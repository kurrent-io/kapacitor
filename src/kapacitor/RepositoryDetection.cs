using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace kapacitor;

static class RepositoryDetection {
    public static async Task<string> EnrichWithRepositoryInfo(string json) {
        try {
            var node = JsonNode.Parse(json);

            if (node is not JsonObject obj) return json;

            var cwd = obj["cwd"]?.GetValue<string>();

            if (cwd is null) return json;

            var repo = await DetectRepositoryAsync(cwd);

            if (repo is null) return json;

            // Skip enrichment if repo info hasn't changed since last emit for this cwd
            var lastEmitted = LoadLastEmitted(cwd);

            if (lastEmitted is not null && RepoPayloadEquals(repo, lastEmitted))
                return json;

            var repoNode                                            = new JsonObject();
            if (repo.UserName is not null) repoNode["user_name"]    = repo.UserName;
            if (repo.UserEmail is not null) repoNode["user_email"]  = repo.UserEmail;
            if (repo.RemoteUrl is not null) repoNode["remote_url"]  = repo.RemoteUrl;
            if (repo.Owner is not null) repoNode["owner"]           = repo.Owner;
            if (repo.RepoName is not null) repoNode["repo_name"]    = repo.RepoName;
            if (repo.Branch is not null) repoNode["branch"]         = repo.Branch;
            if (repo.PrNumber is not null) repoNode["pr_number"]    = repo.PrNumber;
            if (repo.PrTitle is not null) repoNode["pr_title"]      = repo.PrTitle;
            if (repo.PrUrl is not null) repoNode["pr_url"]          = repo.PrUrl;
            if (repo.PrHeadRef is not null) repoNode["pr_head_ref"] = repo.PrHeadRef;

            obj["repository"] = repoNode;

            SaveLastEmitted(cwd, repo);

            return obj.ToJsonString();
        } catch {
            return json; // on any error, forward original payload
        }
    }

    static bool RepoPayloadEquals(RepositoryPayload a, RepositoryPayload b) =>
        a.Owner     == b.Owner
     && a.RepoName  == b.RepoName
     && a.Branch    == b.Branch
     && a.PrNumber  == b.PrNumber
     && a.PrUrl     == b.PrUrl
     && a.PrTitle   == b.PrTitle
     && a.UserName  == b.UserName
     && a.UserEmail == b.UserEmail;

    public static async Task<RepositoryPayload?> DetectRepositoryAsync(string cwd) {
        try {
            // Try loading cached base info
            var cache = LoadCache(cwd);

            string? userName, userEmail, remoteUrl, owner, repoName, branch;

            // Always detect branch fresh — it changes frequently during a session
            var branchTask = RunCommandAsync("git", "branch --show-current", cwd, TimeSpan.FromSeconds(5));

            if (cache is not null) {
                userName  = cache.UserName;
                userEmail = cache.UserEmail;
                remoteUrl = cache.RemoteUrl;
                owner     = cache.Owner;
                repoName  = cache.RepoName;
                branch    = await branchTask;
            }
            else {
                // Run git commands in parallel
                var userNameTask  = RunCommandAsync("git", "config user.name", cwd, TimeSpan.FromSeconds(5));
                var userEmailTask = RunCommandAsync("git", "config user.email", cwd, TimeSpan.FromSeconds(5));
                var remoteUrlTask = RunCommandAsync("git", "remote get-url origin", cwd, TimeSpan.FromSeconds(5));

                await Task.WhenAll(userNameTask, userEmailTask, remoteUrlTask, branchTask);

                userName  = userNameTask.Result;
                userEmail = userEmailTask.Result;
                remoteUrl = remoteUrlTask.Result;
                branch    = branchTask.Result;

                // Not a git repo if we can't get branch or remote
                if (branch is null && remoteUrl is null)
                    return null;

                (owner, repoName) = GitUrlParser.ParseRemoteUrl(remoteUrl);

                // Save to cache (without branch — it's always detected fresh)
                SaveCache(
                    cwd,
                    new GitCacheEntry {
                        UserName  = userName,
                        UserEmail = userEmail,
                        RemoteUrl = remoteUrl,
                        Owner     = owner,
                        RepoName  = repoName,
                        CachedAt  = DateTimeOffset.UtcNow
                    }
                );
            }

            // Always try fresh PR detection (not cached)
            int?    prNumber = null;
            string? prTitle  = null, prUrl = null, prHeadRef = null;

            try {
                var prJson = await RunCommandAsync("gh", "pr view --json number,title,url,headRefName", cwd, TimeSpan.FromSeconds(2));

                if (prJson is not null) {
                    var prNode = JsonNode.Parse(prJson);

                    if (prNode is JsonObject prObj) {
                        prNumber  = prObj["number"]?.GetValue<int>();
                        prTitle   = prObj["title"]?.GetValue<string>();
                        prUrl     = prObj["url"]?.GetValue<string>();
                        prHeadRef = prObj["headRefName"]?.GetValue<string>();
                    }
                }
            } catch {
                // PR detection is best-effort
            }

            return new RepositoryPayload {
                UserName  = userName,
                UserEmail = userEmail,
                RemoteUrl = remoteUrl,
                Owner     = owner,
                RepoName  = repoName,
                Branch    = branch,
                PrNumber  = prNumber,
                PrTitle   = prTitle,
                PrUrl     = prUrl,
                PrHeadRef = prHeadRef
            };
        } catch {
            return null;
        }
    }

    static async Task<string?> RunCommandAsync(string cmd, string arguments, string cwd, TimeSpan timeout) {
        try {
            var psi = new ProcessStartInfo(cmd, arguments) {
                WorkingDirectory       = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using var process = Process.Start(psi);

            if (process is null) return null;

            using var cts    = new CancellationTokenSource(timeout);
            var       output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return process.ExitCode == 0 ? output.Trim() : null;
        } catch {
            return null;
        }
    }

    static string GetCachePath(string cwd) {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(home, ".config", "kapacitor", "cache", $"{hash}.json");
    }

    static GitCacheEntry? LoadCache(string cwd) {
        try {
            var path = GetCachePath(cwd);

            if (!File.Exists(path)) return null;

            var json  = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.GitCacheEntry);

            if (entry is null) {
                return null;
            }

            // 1-hour TTL
            return DateTimeOffset.UtcNow - entry.CachedAt > TimeSpan.FromHours(1) ? null : entry;
        } catch {
            return null;
        }
    }

    static void SaveCache(string cwd, GitCacheEntry entry) {
        try {
            var path = GetCachePath(cwd);
            var dir  = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(entry, KapacitorJsonContext.Default.GitCacheEntry));
        } catch {
            // Cache write failure is non-critical
        }
    }

    static string GetLastEmittedPath(string cwd) {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16];
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Path.Combine(home, ".config", "kapacitor", "cache", $"{hash}.repo-emitted.json");
    }

    static RepositoryPayload? LoadLastEmitted(string cwd) {
        try {
            var path = GetLastEmittedPath(cwd);

            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);

            return JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.RepositoryPayload);
        } catch {
            return null;
        }
    }

    static void SaveLastEmitted(string cwd, RepositoryPayload payload) {
        try {
            var path = GetLastEmittedPath(cwd);
            var dir  = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(payload, KapacitorJsonContext.Default.RepositoryPayload));
        } catch {
            // Cache write failure is non-critical
        }
    }
}
