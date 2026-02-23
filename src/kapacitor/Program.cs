using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

const string baseUrl = "http://localhost:5108";

string[] hookCommands = [
    "session-start",
    "session-end",
    "subagent-start",
    "subagent-stop",
    "notification",
    "stop"
];

if (args.Length < 1) {
    PrintUsage();
    return 1;
}

var command = args[0];

if (command is "--help" or "-h" or "help") {
    PrintUsage();
    return 0;
}

void PrintUsage() {
    Console.WriteLine("kapacitor— Claude Code hook forwarder for Kurrent.Capacitor");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  kapacitor <hook-command>              Forward a hook payload (reads JSON from stdin)");
    Console.WriteLine("  kapacitor errors [--chain] <id>       List tool call errors for a session");
    Console.WriteLine("  kapacitor --help                      Show this help");
    Console.WriteLine();
    Console.WriteLine("Hook commands:");
    foreach (var h in hookCommands)
        Console.WriteLine($"  {h}");
}

if (command == "errors") {
    if (args.Length < 2) {
        Console.Error.WriteLine("Usage: kapacitor errors [--chain] <sessionId>");
        return 1;
    }
    var useChain = args.Contains("--chain");
    var errSessionId = args.Skip(1).First(a => a != "--chain");
    return await HandleErrors(errSessionId, useChain);
}

if (!hookCommands.Contains(command)) {
    Console.Error.WriteLine($"Unknown command: {command}");
    return 1;
}

var body = await Console.In.ReadToEndAsync();

// Enrich all hook payloads with repository info
body = await EnrichWithRepositoryInfo(body);

using var client = new HttpClient();
using var content = new StringContent(body, Encoding.UTF8, "application/json");

var response = await client.PostAsync($"{baseUrl}/hooks/{command}", content);

if (!response.IsSuccessStatusCode) {
    Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");
    return 1;
}

return 0;

async Task<string> EnrichWithRepositoryInfo(string json) {
    try {
        var node = JsonNode.Parse(json);
        if (node is not JsonObject obj) return json;

        var cwd = obj["cwd"]?.GetValue<string>();
        if (cwd is null) return json;

        var repo = await DetectRepositoryAsync(cwd);
        if (repo is null) return json;

        var repoNode = new JsonObject();
        if (repo.UserName is not null)    repoNode["user_name"]  = repo.UserName;
        if (repo.UserEmail is not null)   repoNode["user_email"] = repo.UserEmail;
        if (repo.RemoteUrl is not null)   repoNode["remote_url"] = repo.RemoteUrl;
        if (repo.Owner is not null)       repoNode["owner"]      = repo.Owner;
        if (repo.RepoName is not null)    repoNode["repo_name"]  = repo.RepoName;
        if (repo.Branch is not null)      repoNode["branch"]     = repo.Branch;
        if (repo.PrNumber is not null)    repoNode["pr_number"]  = repo.PrNumber;
        if (repo.PrTitle is not null)     repoNode["pr_title"]   = repo.PrTitle;
        if (repo.PrUrl is not null)       repoNode["pr_url"]     = repo.PrUrl;
        if (repo.PrHeadRef is not null)   repoNode["pr_head_ref"] = repo.PrHeadRef;

        obj["repository"] = repoNode;

        return obj.ToJsonString();
    } catch {
        return json; // on any error, forward original payload
    }
}

async Task<RepositoryPayload?> DetectRepositoryAsync(string cwd) {
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
        } else {
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
            SaveCache(cwd, new GitCacheEntry {
                UserName  = userName,
                UserEmail = userEmail,
                RemoteUrl = remoteUrl,
                Owner     = owner,
                RepoName  = repoName,
                CachedAt  = DateTimeOffset.UtcNow
            });
        }

        // Always try fresh PR detection (not cached)
        int? prNumber = null;
        string? prTitle = null, prUrl = null, prHeadRef = null;

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

async Task<string?> RunCommandAsync(string command, string arguments, string cwd, TimeSpan timeout) {
    try {
        var psi = new ProcessStartInfo(command, arguments) {
            WorkingDirectory       = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        using var process = Process.Start(psi);
        if (process is null) return null;

        using var cts = new CancellationTokenSource(timeout);
        var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return process.ExitCode == 0 ? output.Trim() : null;
    } catch {
        return null;
    }
}

string GetCachePath(string cwd) {
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(cwd)))[..16];
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, ".config", "kapacitor", "cache", $"{hash}.json");
}

GitCacheEntry? LoadCache(string cwd) {
    try {
        var path = GetCachePath(cwd);
        if (!File.Exists(path)) return null;

        var json  = File.ReadAllText(path);
        var entry = JsonSerializer.Deserialize(json, CrCliJsonContext.Default.GitCacheEntry);

        if (entry is null) return null;

        // 1-hour TTL
        if (DateTimeOffset.UtcNow - entry.CachedAt > TimeSpan.FromHours(1))
            return null;

        return entry;
    } catch {
        return null;
    }
}

void SaveCache(string cwd, GitCacheEntry entry) {
    try {
        var path = GetCachePath(cwd);
        var dir  = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(entry, CrCliJsonContext.Default.GitCacheEntry));
    } catch {
        // Cache write failure is non-critical
    }
}

async Task<int> HandleErrors(string sessionId, bool chain) {
    using var httpClient = new HttpClient();
    var query = chain ? "?chain=true" : "";
    var response = await httpClient.GetAsync($"{baseUrl}/api/sessions/{sessionId}/errors{query}");

    if (response.StatusCode == System.Net.HttpStatusCode.NotFound) {
        Console.Error.WriteLine($"Session not found: {sessionId}");
        return 1;
    }

    if (!response.IsSuccessStatusCode) {
        Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");
        return 1;
    }

    var json = await response.Content.ReadAsStringAsync();
    var errors = JsonSerializer.Deserialize(json, CrCliJsonContext.Default.ListErrorEntry);

    if (errors is null || errors.Count == 0) {
        Console.WriteLine("No errors found.");
        return 0;
    }

    Console.WriteLine($"Found {errors.Count} error(s):\n");

    foreach (var error in errors) {
        var label = error.SessionSlug ?? error.SessionId;
        var agentTag = error.AgentId is not null ? $" (agent {error.AgentId})" : "";
        var tool = error.ToolName ?? "unknown";
        Console.WriteLine($"  [{label}]{agentTag} #{error.EventNumber} {tool}");
        Console.WriteLine($"    {error.Error}");
        Console.WriteLine();
    }

    return 0;
}

record ErrorEntry(
    string SessionId,
    string? SessionSlug,
    string? AgentId,
    int EventNumber,
    string? ToolName,
    string Error,
    DateTimeOffset Timestamp);

record RepositoryPayload {
    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_email")]
    public string? UserEmail { get; init; }

    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("pr_number")]
    public int? PrNumber { get; init; }

    [JsonPropertyName("pr_title")]
    public string? PrTitle { get; init; }

    [JsonPropertyName("pr_url")]
    public string? PrUrl { get; init; }

    [JsonPropertyName("pr_head_ref")]
    public string? PrHeadRef { get; init; }
}

record GitCacheEntry {
    [JsonPropertyName("user_name")]
    public string? UserName { get; init; }

    [JsonPropertyName("user_email")]
    public string? UserEmail { get; init; }

    [JsonPropertyName("remote_url")]
    public string? RemoteUrl { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }

    [JsonPropertyName("repo_name")]
    public string? RepoName { get; init; }

    [JsonPropertyName("cached_at")]
    public DateTimeOffset CachedAt { get; init; }
}

static partial class GitUrlParser {
    public static (string? Owner, string? RepoName) ParseRemoteUrl(string? url) {
        if (url is null) return (null, null);

        var sshMatch = SshRegex().Match(url);
        if (sshMatch.Success)
            return (sshMatch.Groups["owner"].Value, sshMatch.Groups["repo"].Value);

        var httpsMatch = HttpsRegex().Match(url);
        return httpsMatch.Success
            ? (httpsMatch.Groups["owner"].Value, httpsMatch.Groups["repo"].Value)
            : (null, null);
    }

    [GeneratedRegex(@"https?://[^/]+/(?<owner>[^/]+)/(?<repo>[^/.]+)(?:\.git)?$")]
    internal static partial Regex HttpsRegex();

    [GeneratedRegex(@"git@[\w.-]+:(?<owner>[^/]+)/(?<repo>[^/.]+)(?:\.git)?$")]
    internal static partial Regex SshRegex();
}

[JsonSerializable(typeof(List<ErrorEntry>))]
[JsonSerializable(typeof(RepositoryPayload))]
[JsonSerializable(typeof(GitCacheEntry))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
partial class CrCliJsonContext : JsonSerializerContext;
