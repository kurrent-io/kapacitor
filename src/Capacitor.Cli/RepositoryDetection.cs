using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli;

static class RepositoryDetection {
    // Bump whenever the cached shape/derivation changes so stale entries are ignored.
    // v2: added Host + provider-aware parsing.
    // v3: multi-segment owner parsing (nested GitLab groups) — v2 entries for
    //     nested repos have owner=null and must re-derive.
    internal const int CacheSchemaVersion = 3;

    internal static CommandRunner DefaultRunner => RunCommandAsync;

    public static async Task<string> EnrichWithRepositoryInfo(
            ConfigRoot config, string json, TimeSpan? budget = null, bool detectPullRequest = true,
            CommandRunner? run = null) {
        try {
            var node = JsonNode.Parse(json);

            if (node is not JsonObject obj) {
                return json;
            }

            var cwd = obj["cwd"]?.GetValue<string>();

            if (cwd is null) {
                return json;
            }

            var repo = await DetectRepositoryAsync(config, cwd, budget, detectPullRequest, run);

            if (repo is null) {
                return json;
            }

            // Skip enrichment if repo info hasn't changed since last emit for this cwd
            var lastEmitted = LoadLastEmitted(config, cwd);

            if (lastEmitted is not null && RepoPayloadEquals(repo, lastEmitted)) {
                return json;
            }

            obj["repository"] = BuildRepositoryNode(repo);

            SaveLastEmitted(config, cwd, repo);

            return obj.ToJsonString();
        } catch {
            return json; // on any error, forward original payload
        }
    }

    /// <summary>
    /// cwd-explicit enrichment. Same as <see cref="EnrichWithRepositoryInfo"/> but the
    /// working directory is supplied by the caller rather than read from a <c>cwd</c> field —
    /// used by the Cursor hook path, whose payloads carry <c>workspace_roots</c> instead of
    /// <c>cwd</c>. Always attaches when a repo is detected (no last-emitted dedup): callers use
    /// this for session-start, where each session needs its own RepositoryDetected event.
    /// Fail-open: forwards the original payload unchanged on any error or non-git dir.
    /// </summary>
    public static async Task<string> EnrichWithRepositoryInfoFromCwd(
            ConfigRoot config, string json, string cwd, TimeSpan? budget = null) {
        try {
            if (string.IsNullOrEmpty(cwd)) return json;
            if (JsonNode.Parse(json) is not JsonObject obj) return json;

            var repo = await DetectRepositoryAsync(config, cwd, budget);
            if (repo is null) return json;

            obj["repository"] = BuildRepositoryNode(repo);
            return obj.ToJsonString();
        } catch {
            return json;
        }
    }

    /// <summary>
    /// Builds the <c>repository</c> JSON node from a detected payload. Only non-null fields are
    /// emitted so the server-side <c>RepositoryInfoPayload</c> deserialises cleanly. Shared by
    /// <see cref="EnrichWithRepositoryInfo"/> and <see cref="EnrichWithRepositoryInfoFromCwd"/>,
    /// and reused by the Cursor import path.
    /// </summary>
    public static JsonObject BuildRepositoryNode(RepositoryPayload repo) {
        var repoNode = new JsonObject();

#pragma warning disable IDE0011
        if (repo.UserName is not null) repoNode["user_name"]    = repo.UserName;
        if (repo.UserEmail is not null) repoNode["user_email"]  = repo.UserEmail;
        if (repo.RemoteUrl is not null) repoNode["remote_url"]  = repo.RemoteUrl;
        if (repo.Host is not null) repoNode["host"]             = repo.Host;
        if (repo.Owner is not null) repoNode["owner"]           = repo.Owner;
        if (repo.RepoName is not null) repoNode["repo_name"]    = repo.RepoName;
        if (repo.Branch is not null) repoNode["branch"]         = repo.Branch;
        if (repo.PrNumber is not null) repoNode["pr_number"]    = repo.PrNumber;
        if (repo.PrTitle is not null) repoNode["pr_title"]      = repo.PrTitle;
        if (repo.PrUrl is not null) repoNode["pr_url"]          = repo.PrUrl;
        if (repo.PrHeadRef is not null) repoNode["pr_head_ref"] = repo.PrHeadRef;
#pragma warning restore IDE0011

        return repoNode;
    }

    static bool RepoPayloadEquals(RepositoryPayload a, RepositoryPayload b) =>
        a.Owner     == b.Owner
     && a.RepoName  == b.RepoName
     && a.Host      == b.Host
     && a.Branch    == b.Branch
     && a.PrNumber  == b.PrNumber
     && a.PrUrl     == b.PrUrl
     && a.PrTitle   == b.PrTitle
     && a.UserName  == b.UserName
     && a.UserEmail == b.UserEmail;

    // detectPullRequest=false skips the live PR/MR provider detection (the `gh pr view` / `glab api`
    // round-trip) while still resolving base repo info (owner/repo/user/branch/host). Bulk import
    // passes false: it never emits PR fields, so that per-cwd round-trip is pure wasted latency
    // `run` is an injectable command runner (defaults to the real process spawner) so the
    // git/provider spawns are unit-testable.
    public static async Task<RepositoryPayload?> DetectRepositoryAsync(
            ConfigRoot config, string cwd, TimeSpan? budget = null, bool detectPullRequest = true,
            CommandRunner? run = null) {
        if (budget is { } b0 && b0 <= TimeSpan.Zero) return null;
        run ??= DefaultRunner;
        try {
            // Bound the git/gh probes by the remaining hook budget so a slow repo never
            // overruns the deadline. When no budget is set, keep the historical 5s/2s caps.
            var sw     = Stopwatch.StartNew();
            var gitCap = budget is { } b
                ? TimeSpan.FromSeconds(Math.Min(5, Math.Max(0, b.TotalSeconds)))
                : TimeSpan.FromSeconds(5);

            // Try loading cached base info
            var cache = LoadCache(config, cwd);

            string? userName, userEmail, remoteUrl, owner, repoName, branch, host;

            // Always detect branch fresh — it changes frequently during a session
            var branchTask = run("git", "branch --show-current", cwd, gitCap);

            if (cache is not null) {
                userName  = cache.UserName;
                userEmail = cache.UserEmail;
                remoteUrl = cache.RemoteUrl;
                owner     = cache.Owner;
                repoName  = cache.RepoName;
                host      = cache.Host;
                branch    = await branchTask;
            } else {
                // Run git commands in parallel
                var userNameTask  = run("git", "config user.name", cwd, gitCap);
                var userEmailTask = run("git", "config user.email", cwd, gitCap);
                var remoteUrlTask = run("git", "remote get-url origin", cwd, gitCap);

                await Task.WhenAll(userNameTask, userEmailTask, remoteUrlTask, branchTask);

                userName  = userNameTask.Result;
                userEmail = userEmailTask.Result;
                remoteUrl = remoteUrlTask.Result;
                branch    = branchTask.Result;

                // Not a git repo if we can't get branch or remote
                if (branch is null && remoteUrl is null) {
                    return null;
                }

                (owner, repoName) = GitUrlParser.ParseRemoteUrl(remoteUrl);
                host = remoteUrl is null ? null : RemoteMatcher.ExtractHost(remoteUrl);

                // Save to cache (without branch — it's always detected fresh)
                SaveCache(
                    config,
                    cwd,
                    new() {
                        UserName      = userName,
                        UserEmail     = userEmail,
                        RemoteUrl     = remoteUrl,
                        Owner         = owner,
                        RepoName      = repoName,
                        Host          = host,
                        SchemaVersion = CacheSchemaVersion,
                        CachedAt      = DateTimeOffset.UtcNow
                    }
                );
            }

            // Always try fresh PR detection (not cached). Bound by whatever budget remains after
            // the git probes; skip entirely if the budget is already exhausted.
            int?    prNumber = null;
            string? prTitle  = null, prUrl = null, prHeadRef = null;

            var ghCap = TimeSpan.FromSeconds(2);
            if (budget is { } bgh) {
                var remainingBudget = bgh - sw.Elapsed;
                ghCap = TimeSpan.FromSeconds(Math.Min(2, Math.Max(0, remainingBudget.TotalSeconds)));
            }

            // Effective provider budget: the post-git remainder when the caller passed a budget
            // (only ClaudeHookCommand does), else the historical 2s cap. Covers probe + detector.
            var providerCap = ghCap;

            // Import passes detectPullRequest:false to skip the provider round-trip (it discards
            // PR fields). ResolveAndDetectPrAsync (from #261) owns the probe+detector budget split;
            // thread the injectable `run` through so the git/provider spawns stay unit-testable.
            if (detectPullRequest && providerCap > TimeSpan.Zero && host is not null) {
                var pr = await ResolveAndDetectPrAsync(host, owner, repoName, branch, cwd, providerCap, run);

                if (pr is not null) {
                    prNumber  = pr.Number;
                    prTitle   = pr.Title;
                    prUrl     = pr.Url;
                    prHeadRef = pr.HeadRef;
                }
            }

            return new() {
                UserName  = userName,
                UserEmail = userEmail,
                RemoteUrl = remoteUrl,
                Host      = host,
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

    /// <summary>
    /// Resolves the git provider for <paramref name="host"/> and detects the current PR/MR within a
    /// single <paramref name="providerCap"/> ceiling shared by the probe and the detector. The
    /// detector is given only the budget the probe left behind — never the full cap — so probe +
    /// detector together can't overrun the deadline. <paramref name="getTimestamp"/> is a seam for
    /// tests (defaults to <see cref="Stopwatch.GetTimestamp"/>).
    /// </summary>
    internal static async Task<PrInfo?> ResolveAndDetectPrAsync(
            string        host,
            string?       owner,
            string?       repoName,
            string?       branch,
            string        cwd,
            TimeSpan      providerCap,
            CommandRunner run,
            Func<long>?   getTimestamp = null
        ) {
        if (providerCap <= TimeSpan.Zero) return null;

        var getTs = getTimestamp ?? Stopwatch.GetTimestamp;
        var start = getTs();
        var kind  = await GitProviderRouter.ResolveAsync(host, cwd, providerCap, run);

        // The remainder after the probe — NOT the full providerCap. Passing providerCap here would
        // let a slow probe + a full-length detector overrun the caller's deadline. Clamp the elapsed
        // time to >= 0 so a non-monotonic timestamp seam can never inflate detectCap past providerCap
        // (the invariant is "detector budget <= providerCap, always"). Production uses the monotonic
        // Stopwatch.GetTimestamp, so this only hardens the injectable test seam.
        var elapsed = Stopwatch.GetElapsedTime(start, getTs());
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

        var detectCap = providerCap - elapsed;
        if (detectCap <= TimeSpan.Zero) return null;

        return kind switch {
            GitProviderKind.GitHub => await GitHubPrDetector.DetectAsync(cwd, detectCap, run),
            GitProviderKind.GitLab when owner is not null && repoName is not null
                => await GitLabPrDetector.DetectAsync(host, owner, repoName, branch, cwd, detectCap, run),
            _ => null
        };
    }

    static async Task<string?> RunCommandAsync(string cmd, string arguments, string cwd, TimeSpan timeout) {
        Process? process = null;
        try {
            var psi = new ProcessStartInfo(cmd, arguments) {
                WorkingDirectory       = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            process = Process.Start(psi);

            if (process is null) {
                return null;
            }

            using var cts    = new CancellationTokenSource(timeout);
            var       output = await process.StandardOutput.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            return process.ExitCode == 0 ? output.Trim() : null;
        } catch {
            // Timed out or failed — kill the child (and its tree) so a slow git/gh probe doesn't
            // outlive the hook and accumulate across repeated invocations. Disposing alone only
            // releases handles; it does not stop the process.
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            return null;
        } finally {
            process?.Dispose();
        }
    }

    static string GetCachePath(ConfigRoot config, string cwd) {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16];

        return Path.Combine(config.Path("cache"), $"{hash}.json");
    }

    static GitCacheEntry? LoadCache(ConfigRoot config, string cwd) {
        try {
            var path = GetCachePath(config, cwd);

            if (!File.Exists(path)) {
                return null;
            }

            var json  = File.ReadAllText(path);
            var entry = JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.GitCacheEntry);

            if (entry is null || entry.SchemaVersion != CacheSchemaVersion) {
                return null;
            }

            // 1-hour TTL
            return DateTimeOffset.UtcNow - entry.CachedAt > TimeSpan.FromHours(1) ? null : entry;
        } catch {
            return null;
        }
    }

    static void SaveCache(ConfigRoot config, string cwd, GitCacheEntry entry) {
        try {
            var path = GetCachePath(config, cwd);
            var dir  = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(entry, CapacitorJsonContext.Default.GitCacheEntry));
        } catch {
            // Cache write failure is non-critical
        }
    }

    static string GetLastEmittedPath(ConfigRoot config, string cwd) {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16];

        return Path.Combine(config.Path("cache"), $"{hash}.repo-emitted.json");
    }

    static RepositoryPayload? LoadLastEmitted(ConfigRoot config, string cwd) {
        try {
            var path = GetLastEmittedPath(config, cwd);

            if (!File.Exists(path)) {
                return null;
            }

            var json = File.ReadAllText(path);

            return JsonSerializer.Deserialize(json, CapacitorJsonContext.Default.RepositoryPayload);
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Clear the last-emitted cache for a cwd so the next enrichment always includes repo info.
    /// Must be called on session-start — each new session needs its own RepositoryDetected event.
    /// </summary>
    public static void ClearLastEmitted(ConfigRoot config, string cwd) {
        try {
            var path = GetLastEmittedPath(config, cwd);

            if (File.Exists(path)) {
                File.Delete(path);
            }
        } catch {
            // Non-critical
        }
    }

    static void SaveLastEmitted(ConfigRoot config, string cwd, RepositoryPayload payload) {
        try {
            var path = GetLastEmittedPath(config, cwd);
            var dir  = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.RepositoryPayload));
        } catch {
            // Cache write failure is non-critical
        }
    }
}
