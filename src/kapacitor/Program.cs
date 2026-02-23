using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

var baseUrl = Environment.GetEnvironmentVariable("KAPACITOR_URL") ?? "http://localhost:5108";

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
    Console.WriteLine("kapacitor — Claude Code hook forwarder for Kurrent.Capacitor");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  kapacitor <hook-command>                                      Forward a hook payload (reads JSON from stdin)");
    Console.WriteLine("  kapacitor watch <sessionId> <path> [--agent-id <agentId>]     Watch a transcript file and POST lines to server");
    Console.WriteLine("  kapacitor errors [--chain] <id>                               List tool call errors for a session");
    Console.WriteLine("  kapacitor --help                                              Show this help");
    Console.WriteLine();
    Console.WriteLine("Hook commands:");
    foreach (var h in hookCommands)
        Console.WriteLine($"  {h}");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  KAPACITOR_URL    Server URL (default: http://localhost:5108)");
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

if (command == "watch") {
    if (args.Length < 3) {
        Console.Error.WriteLine("Usage: kapacitor watch <sessionId> <transcriptPath> [--agent-id <agentId>] [--cwd <cwd>]");
        return 1;
    }
    var watchSessionId    = args[1];
    var watchPath         = args[2];
    string? watchAgentId  = null;
    string? watchCwd      = null;
    var agentIdIdx        = Array.IndexOf(args, "--agent-id");
    if (agentIdIdx >= 0 && agentIdIdx + 1 < args.Length)
        watchAgentId = args[agentIdIdx + 1];
    var cwdIdx = Array.IndexOf(args, "--cwd");
    if (cwdIdx >= 0 && cwdIdx + 1 < args.Length)
        watchCwd = args[cwdIdx + 1];

    return await RunWatch(watchSessionId, watchPath, watchAgentId, watchCwd);
}

if (!hookCommands.Contains(command)) {
    Console.Error.WriteLine($"Unknown command: {command}");
    return 1;
}

var body = await Console.In.ReadToEndAsync();

// Enrich all hook payloads with repository info
body = await EnrichWithRepositoryInfo(body);

// For session-end and subagent-stop: kill watcher BEFORE posting hook
// so transcript is fully drained before server computes stats
if (command is "session-end") {
    var node = JsonNode.Parse(body);
    var sessionId = node?["session_id"]?.GetValue<string>();
    if (sessionId is not null)
        await KillWatcher(sessionId);
}
else if (command is "subagent-stop") {
    var node = JsonNode.Parse(body);
    var sessionId = node?["session_id"]?.GetValue<string>();
    var agentId = node?["agent_id"]?.GetValue<string>();
    if (sessionId is not null && agentId is not null)
        await KillWatcher($"{sessionId}-{agentId}");
}

using var client = new HttpClient();
using var content = new StringContent(body, Encoding.UTF8, "application/json");

var response = await client.PostAsync($"{baseUrl}/hooks/{command}", content);

if (!response.IsSuccessStatusCode) {
    Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");
    return 1;
}

// For session-start and subagent-start: spawn watcher AFTER posting hook
if (command is "session-start") {
    var node = JsonNode.Parse(body);
    var sessionId      = node?["session_id"]?.GetValue<string>();
    var transcriptPath = node?["transcript_path"]?.GetValue<string>();
    var sessionCwd     = node?["cwd"]?.GetValue<string>();
    if (sessionId is not null && transcriptPath is not null)
        SpawnWatcher(sessionId, transcriptPath, agentId: null, cwd: sessionCwd);
}
else if (command is "subagent-start") {
    var node = JsonNode.Parse(body);
    var sessionId      = node?["session_id"]?.GetValue<string>();
    var agentId        = node?["agent_id"]?.GetValue<string>();
    var transcriptPath = node?["transcript_path"]?.GetValue<string>();
    if (sessionId is not null && agentId is not null && transcriptPath is not null) {
        var sessionDir          = Path.ChangeExtension(transcriptPath, null);
        var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
        SpawnWatcher($"{sessionId}-{agentId}", agentTranscriptPath, agentId, sessionId);
    }
}

return 0;

// --- Watch command ---

async Task<int> RunWatch(string sessionId, string transcriptPath, string? agentId, string? cwd) {
    using var cts = new CancellationTokenSource();

    // Handle SIGTERM/SIGINT for graceful shutdown
    Console.CancelKeyPress += (_, e) => {
        e.Cancel = true;
        cts.Cancel();
    };
    PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => cts.Cancel());

    using var httpClient = new HttpClient();
    var state = new WatchState();

    // Detect repository info upfront if cwd is provided (session watchers only, not agents)
    if (cwd is not null) {
        state.Repository = await DetectRepositoryAsync(cwd);
        state.LastRepoDetection = DateTimeOffset.UtcNow;
    }

    Console.Error.WriteLine($"[watch] Watching {transcriptPath} for session {sessionId}" + (agentId is not null ? $" agent {agentId}" : ""));

    try {
        while (!cts.Token.IsCancellationRequested) {
            // Periodically refresh repository info (every 60s)
            if (cwd is not null && DateTimeOffset.UtcNow - state.LastRepoDetection > TimeSpan.FromSeconds(60)) {
                state.Repository = await DetectRepositoryAsync(cwd);
                state.LastRepoDetection = DateTimeOffset.UtcNow;
            }

            await DrainNewLines(httpClient, sessionId, transcriptPath, agentId, state);

            try {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
            } catch (OperationCanceledException) {
                break;
            }
        }
    } catch (OperationCanceledException) {
        // Expected
    }

    // Final drain before exit
    Console.Error.WriteLine($"[watch] Draining remaining lines...");
    await DrainNewLines(httpClient, sessionId, transcriptPath, agentId, state);
    Console.Error.WriteLine($"[watch] Done. {state.LinesProcessed} total lines processed.");

    return 0;
}

async Task DrainNewLines(HttpClient httpClient, string sessionId, string transcriptPath, string? agentId, WatchState state) {
    try {
        if (!File.Exists(transcriptPath)) return;

        var newLines = new List<string>();

        await using var stream = new FileStream(
            transcriptPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );
        using var reader = new StreamReader(stream);

        var lineIndex = 0;
        while (await reader.ReadLineAsync() is { } line) {
            if (lineIndex++ < state.LinesProcessed) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;
            newLines.Add(line);
        }
        state.LinesProcessed = lineIndex;

        if (newLines.Count == 0) return;

        // POST batch to server
        var batch = new TranscriptBatch {
            SessionId  = sessionId,
            AgentId    = agentId,
            Lines      = newLines.ToArray(),
            Repository = state.Repository
        };

        var json = JsonSerializer.Serialize(batch, KapacitorJsonContext.Default.TranscriptBatch);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        try {
            var resp = await httpClient.PostAsync($"{baseUrl}/hooks/transcript", httpContent);
            Console.Error.WriteLine(resp.IsSuccessStatusCode ? $"[watch] Posted {newLines.Count} line(s)" : $"[watch] Server returned HTTP {(int)resp.StatusCode} for {newLines.Count} line(s)");
        } catch (HttpRequestException ex) {
            Console.Error.WriteLine($"[watch] Server unreachable: {ex.Message}");
        }
    } catch (IOException ex) {
        Console.Error.WriteLine($"[watch] Error reading file: {ex.Message}");
    }
}

// --- Watcher spawning/killing ---

string GetWatcherDir() {
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    return Path.Combine(home, ".config", "kapacitor", "watchers");
}

string GetPidFilePath(string key) => Path.Combine(GetWatcherDir(), $"{key}.pid");

void SpawnWatcher(string key, string transcriptPath, string? agentId, string? sessionIdOverride = null, string? cwd = null) {
    try {
        var watcherDir = GetWatcherDir();
        Directory.CreateDirectory(watcherDir);

        // Resolve kapacitor binary path (same as current process)
        var kapacitorPath = Environment.ProcessPath ?? "kapacitor";
        var sessionId = sessionIdOverride ?? key;

        var arguments = agentId is not null
            ? $"watch {sessionId} \"{transcriptPath}\" --agent-id {agentId}"
            : $"watch {key} \"{transcriptPath}\"";

        if (cwd is not null)
            arguments += $" --cwd \"{cwd}\"";

        var psi = new ProcessStartInfo(kapacitorPath, arguments) {
            RedirectStandardOutput = false,
            RedirectStandardInput  = false,
            RedirectStandardError  = false,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment = {
                ["KAPACITOR_URL"] = baseUrl
            }
        };

        var process = Process.Start(psi);
        if (process is null) {
            Console.Error.WriteLine($"Failed to spawn watcher for {key}");
            return;
        }

        File.WriteAllText(GetPidFilePath(key), process.Id.ToString());
        Console.Error.WriteLine($"Spawned watcher for {key} (PID {process.Id})");
    } catch (Exception ex) {
        Console.Error.WriteLine($"Failed to spawn watcher for {key}: {ex.Message}");
    }
}

async Task KillWatcher(string key) {
    var pidFile = GetPidFilePath(key);
    if (!File.Exists(pidFile)) return;

    try {
        var pidText = File.ReadAllText(pidFile).Trim();
        if (!int.TryParse(pidText, out var pid)) {
            File.Delete(pidFile);
            return;
        }

        try {
            var process = Process.GetProcessById(pid);

            // Send SIGTERM
            process.Kill(entireProcessTree: false);

            // Wait up to 5 seconds for graceful exit
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try {
                await process.WaitForExitAsync(cts.Token);
                Console.Error.WriteLine($"Watcher {key} (PID {pid}) exited gracefully");
            } catch (OperationCanceledException) {
                // Force kill if it didn't exit in time
                process.Kill(entireProcessTree: true);
                Console.Error.WriteLine($"Watcher {key} (PID {pid}) force-killed after timeout");
            }
        } catch (ArgumentException) {
            // Process already exited
            Console.Error.WriteLine($"Watcher {key} (PID {pid}) already exited");
        }
    } catch (Exception ex) {
        Console.Error.WriteLine($"Error killing watcher {key}: {ex.Message}");
    } finally {
        try { File.Delete(pidFile); } catch { /* ignore */ }
    }
}

// --- Repository detection ---

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

async Task<string?> RunCommandAsync(string cmd, string arguments, string cwd, TimeSpan timeout) {
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

        using var cts = new CancellationTokenSource(timeout);
        var output = await process.StandardOutput.ReadToEndAsync(cts.Token);
        await process.WaitForExitAsync(cts.Token);

        return process.ExitCode == 0 ? output.Trim() : null;
    } catch {
        return null;
    }
}

// --- Git cache ---

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
        var entry = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.GitCacheEntry);

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
        File.WriteAllText(path, JsonSerializer.Serialize(entry, KapacitorJsonContext.Default.GitCacheEntry));
    } catch {
        // Cache write failure is non-critical
    }
}

// --- Errors command ---

async Task<int> HandleErrors(string sessionId, bool chain) {
    using var httpClient = new HttpClient();
    var query = chain ? "?chain=true" : "";
    var resp = await httpClient.GetAsync($"{baseUrl}/api/sessions/{sessionId}/errors{query}");

    if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) {
        Console.Error.WriteLine($"Session not found: {sessionId}");
        return 1;
    }

    if (!resp.IsSuccessStatusCode) {
        Console.Error.WriteLine($"HTTP {(int)resp.StatusCode}");
        return 1;
    }

    var json = await resp.Content.ReadAsStringAsync();
    var errors = JsonSerializer.Deserialize(json, KapacitorJsonContext.Default.ListErrorEntry);

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

// --- Helper classes ---

class WatchState {
    public int                LinesProcessed    { get; set; }
    public RepositoryPayload? Repository        { get; set; }
    public DateTimeOffset     LastRepoDetection { get; set; }
}

// --- Records ---

record TranscriptBatch {
    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("agent_id")]
    public string? AgentId { get; init; }

    [JsonPropertyName("lines")]
    public required string[] Lines { get; init; }

    [JsonPropertyName("repository")]
    public RepositoryPayload? Repository { get; init; }
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
[JsonSerializable(typeof(TranscriptBatch))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
partial class KapacitorJsonContext : JsonSerializerContext;
