using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using kapacitor;
using kapacitor.Auth;
using kapacitor.Commands;
using kapacitor.Config;
using WatchCommand = kapacitor.Commands.WatchCommand;

// Skip all processing when spawned inside a headless claude invocation (e.g., title generation)
// to prevent infinite hook loops
if (Environment.GetEnvironmentVariable("KAPACITOR_SKIP") is "1") {
    return 0;
}

var baseUrl = await AppConfig.ResolveServerUrl(args);

// Fire-and-forget update check (prints hint to stderr after command finishes)
var   noUpdateCheck   = args.Contains("--no-update-check");
Task? updateCheckTask = null;

if (!noUpdateCheck) {
    updateCheckTask = Task.Run(UpdateCommand.PrintUpdateHintIfAvailable);
}

string[] hookCommands = [
    "session-start",
    "session-end",
    "subagent-start",
    "subagent-stop",
    "notification",
    "stop",
    "pre-compact"
];

if (args.Length < 1) {
    await PrintUsage();

    return 1;
}

var command = args[0];

if (command is "--help" or "-h" or "help") {
    await PrintUsage();

    return 0;
}

// Per-command help: kapacitor <command> --help / -h
if (args.Skip(1).Any(a => a is "--help" or "-h")) {
    return await PrintCommandHelp(command);
}

// Commands that don't need a server URL
string[] offlineCommands = ["--help", "-h", "help", "--version", "-v", "logout", "cleanup", "config", "agent", "setup", "status", "update", "plugin", "profile", "use"];

if (baseUrl is null && !offlineCommands.Contains(command)) {
    Console.Error.WriteLine("No server configured. Run `kapacitor setup` or set KAPACITOR_URL.");

    return 1;
}

switch (command) {
    case "--version" or "-v": {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?
            .InformationalVersion ?? "unknown";
        await Console.Out.WriteLineAsync($"kapacitor {version}");

        return 0;
    }
    case "errors": {
        var useChain     = args.Contains("--chain");
        var errSessionId = ResolveSessionId(args, skipCount: 1);

        if (errSessionId is null) {
            Console.Error.WriteLine("Usage: kapacitor errors [--chain] [sessionId]");
            Console.Error.WriteLine("  No session ID provided and KAPACITOR_SESSION_ID not set.");

            return 1;
        }

        return await ErrorsCommand.HandleErrors(baseUrl!, errSessionId, useChain);
    }
    case "recap": {
        var useChain = args.Contains("--chain");
        var useFull  = args.Contains("--full");
        var useRepo  = args.Contains("--repo");

        if (useRepo) {
            return await RecapCommand.HandleRepoRecap(baseUrl!);
        }

        var recapSessionId = ResolveSessionId(args);

        if (recapSessionId is null) {
            Console.Error.WriteLine("Usage: kapacitor recap [--chain] [--full] [--repo] [sessionId]");
            Console.Error.WriteLine("  No session ID provided and KAPACITOR_SESSION_ID not set.");
            Console.Error.WriteLine("  Use --repo to see recent session summaries for the current repository.");

            return 1;
        }

        return await RecapCommand.HandleRecap(baseUrl!, recapSessionId, useChain, useFull);
    }
    case "validate-plan": {
        var vpSessionId = ResolveSessionId(args);

        if (vpSessionId is null) {
            Console.Error.WriteLine("Usage: kapacitor validate-plan [sessionId]");
            Console.Error.WriteLine("  No session ID provided and KAPACITOR_SESSION_ID not set.");

            return 1;
        }

        return await ValidatePlanCommand.Handle(baseUrl!, vpSessionId);
    }
    case "generate-whats-done" when args.Length < 2:
        Console.Error.WriteLine("Usage: kapacitor generate-whats-done <sessionId>");

        return 1;
    case "generate-whats-done": {
        var wdSessionId = args[1].Replace("-", "");

        return await WhatsDoneCommand.HandleGenerateWhatsDone(baseUrl!, wdSessionId);
    }
    case "login": {
        return await OAuthLoginFlow.LoginWithDiscoveryAsync(baseUrl!);
    }
    case "logout": {
        TokenStore.Delete();
        await Console.Out.WriteLineAsync("Logged out.");

        return 0;
    }
    case "whoami": {
        var provider = await HttpClientExtensions.DiscoverProviderAsync(baseUrl!);

        if (provider == "None") {
            await Console.Out.WriteLineAsync("Provider: None (no authentication)");
            await Console.Out.WriteLineAsync($"Server:   {baseUrl!}");

            return 0;
        }

        var tokens = await TokenStore.LoadAsync();

        if (tokens is null) {
            Console.Error.WriteLine("Not authenticated. Run `kapacitor login`.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Username: {tokens.GitHubUsername}");
        await Console.Out.WriteLineAsync($"Provider: {tokens.Provider}");
        await Console.Out.WriteLineAsync($"Expires:  {tokens.ExpiresAt:u}");
        await Console.Out.WriteLineAsync($"Server:   {baseUrl!}");
        await Console.Out.WriteLineAsync($"Expired:  {(tokens.IsExpired ? "yes" : "no")}");

        return 0;
    }
    case "agent":
        return await AgentCommands.HandleAsync(args);
    case "setup":
        return await SetupCommand.HandleAsync(args);
    case "plugin":
        return await PluginCommand.HandleAsync(args);
    case "profile":
        return await ProfileCommand.HandleAsync(args);
    case "use":
        return await UseCommand.HandleAsync(args);
    case "status":
        return await StatusCommand.HandleAsync(baseUrl);
    case "config":
        return await ConfigCommand.HandleAsync(args);
    case "update":
        return await UpdateCommand.HandleAsync();
    case "review": {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: kapacitor review <pr-url-or-shorthand>");
            Console.Error.WriteLine("  Example: kapacitor review https://github.com/owner/repo/pull/123");
            Console.Error.WriteLine("  Example: kapacitor review owner/repo#123");

            return 1;
        }

        return await ReviewCommand.HandleReview(baseUrl!, args[1]);
    }
    case "mcp": {
        if (args.Length < 2) {
            Console.Error.WriteLine("Usage: kapacitor mcp review [--owner <owner> --repo <repo> --pr <number>]");

            return 1;
        }

        if (args[1] == "review") {
            var mcpOwner = GetArg(args, "--owner");
            var mcpRepo  = GetArg(args, "--repo");
            var mcpPr    = GetArg(args, "--pr");

            // Explicit PR args — use directly
            if (mcpOwner is not null && mcpRepo is not null && mcpPr is not null && int.TryParse(mcpPr, out var mcpPrNum)) {
                return await McpReviewServer.RunAsync(baseUrl!, mcpOwner, mcpRepo, mcpPrNum);
            }

            // No args — auto-detect from git
            return await McpReviewServer.RunAutoAsync(baseUrl!);
        }

        Console.Error.WriteLine($"Unknown mcp subcommand: {args[1]}");

        return 1;
    }
    case "cleanup":
        return await CleanupCommand.HandleCleanup();
    case "disable": {
        var sessionId = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID")?.Replace("-", "");

        if (sessionId is null) {
            Console.Error.WriteLine("KAPACITOR_SESSION_ID not set. Run this inside an active Claude Code session.");

            return 1;
        }

        // 1. Kill the watcher (and any subagent watchers)
        await WatcherManager.KillWatcher(sessionId);

        // Also kill subagent watchers — scan PID files matching "{sessionId}-*"
        var watcherDir = WatcherManager.GetWatcherDir();

        if (Directory.Exists(watcherDir)) {
            foreach (var pidFile in Directory.GetFiles(watcherDir, $"{sessionId}-*.pid")) {
                var subKey = Path.GetFileNameWithoutExtension(pidFile);
                await WatcherManager.KillWatcher(subKey);
            }
        }

        // 2. Mark session as disabled (prevents future hook calls from sending data)
        MarkSessionDisabled(sessionId);

        // 3. Tell server to delete session data
        using var disableClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();

        try {
            var resp = await disableClient.DeleteWithRetryAsync($"{baseUrl!}/api/sessions/{sessionId}");

            if (resp.IsSuccessStatusCode) {
                await Console.Out.WriteLineAsync($"Session {sessionId} disabled. Recording stopped and server data deleted.");
            } else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) {
                await Console.Out.WriteLineAsync($"Session {sessionId} disabled. No server data found (may have already been deleted).");
            } else if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) {
                return 1;
            } else {
                Console.Error.WriteLine($"Server returned HTTP {(int)resp.StatusCode}");

                return 1;
            }
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl!, ex);
            await Console.Out.WriteLineAsync("Session disabled locally (watcher stopped, hooks silenced). Server data not deleted.");
        }

        return 0;
    }
    case "hide": {
        var sessionId = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID")?.Replace("-", "");

        if (sessionId is null) {
            Console.Error.WriteLine("KAPACITOR_SESSION_ID not set. Run this inside an active Claude Code session.");

            return 1;
        }

        using var hideClient = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var       visPayload = new JsonObject { ["visibility"] = "none" };
        using var visContent = new StringContent(visPayload.ToJsonString(), Encoding.UTF8, "application/json");

        try {
            var resp = await hideClient.PutWithRetryAsync($"{baseUrl!}/api/sessions/{sessionId}/visibility", visContent);

            if (resp.IsSuccessStatusCode) {
                await Console.Out.WriteLineAsync($"Session {sessionId} hidden (owner-only).");
            } else if (await HttpClientExtensions.HandleUnauthorizedAsync(resp)) {
                return 1;
            } else {
                Console.Error.WriteLine($"Server returned HTTP {(int)resp.StatusCode}");

                return 1;
            }
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl!, ex);

            return 1;
        }

        return 0;
    }
    case "history": {
        string? filterCwd     = null;
        string? filterSession = null;
        var     minLines      = 10;
        var     cwdArgIdx     = Array.IndexOf(args, "--cwd");

        if (cwdArgIdx >= 0 && cwdArgIdx + 1 < args.Length) {
            filterCwd = args[cwdArgIdx + 1];
        }

        var sessionArgIdx = Array.IndexOf(args, "--session");

        if (sessionArgIdx >= 0 && sessionArgIdx + 1 < args.Length) {
            filterSession = args[sessionArgIdx + 1];
        }

        var minLinesIdx = Array.IndexOf(args, "--min-lines");

        if (minLinesIdx >= 0 && minLinesIdx + 1 < args.Length && int.TryParse(args[minLinesIdx + 1], out var parsed)) {
            minLines = parsed;
        }

        var generateSummaries = args.Contains("--generate-summaries");

        return await HistoryCommand.HandleHistory(baseUrl!, filterCwd, filterSession, minLines, generateSummaries);
    }
    case "watch" when args.Length < 3:
        Console.Error.WriteLine("Usage: kapacitor watch <sessionId> <transcriptPath> [--agent-id <agentId>] [--cwd <cwd>] [--skip-title]");

        return 1;
    case "watch": {
        var     watchSessionId = args[1].Replace("-", "");
        var     watchPath      = args[2];
        string? watchAgentId   = null;
        string? watchCwd       = null;
        var     agentIdIdx     = Array.IndexOf(args, "--agent-id");

        if (agentIdIdx >= 0 && agentIdIdx + 1 < args.Length) {
            watchAgentId = args[agentIdIdx + 1].Replace("-", "");
        }

        var cwdIdx = Array.IndexOf(args, "--cwd");

        if (cwdIdx >= 0 && cwdIdx + 1 < args.Length) {
            watchCwd = args[cwdIdx + 1];
        }

        var watchSkipTitle = Array.IndexOf(args, "--skip-title") >= 0;

        return await WatchCommand.RunWatch(baseUrl!, watchSessionId, watchPath, watchAgentId, watchCwd, watchSkipTitle);
    }
    case "permission-request":
        return await PermissionRequestCommand.Handle(baseUrl!);
    case "set-title" when args.Length < 2:
        Console.Error.WriteLine("Usage: kapacitor set-title <title>");
        Console.Error.WriteLine("  KAPACITOR_SESSION_ID must be set.");

        return 1;
    case "set-title": {
        var stSessionId = Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID")?.Replace("-", "");

        if (stSessionId is null) {
            Console.Error.WriteLine("KAPACITOR_SESSION_ID not set");

            return 1;
        }

        // Join all remaining args as the title (supports unquoted multi-word titles)
        var title = string.Join(' ', args.Skip(1)).Trim();

        if (string.IsNullOrWhiteSpace(title)) {
            Console.Error.WriteLine("Title cannot be empty");

            return 1;
        }

        // Limit to 120 chars
        if (title.Length > 120) {
            title = title[..120];
        }

        using var stClient  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        var       payload   = new JsonObject { ["session_id"] = stSessionId, ["title"] = title };
        using var stContent = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

        try {
            var resp = await stClient.PostWithRetryAsync($"{baseUrl!}/hooks/set-title", stContent);

            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine($"Server returned HTTP {(int)resp.StatusCode}");

                return 1;
            }
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl!, ex);

            return 1;
        }

        return 0;
    }
}

if (!hookCommands.Contains(command)) {
    Console.Error.WriteLine($"Unknown command: {command}");

    return 1;
}

var body = await Console.In.ReadToEndAsync();

// Inject home_dir and agent_host_id into all hook payloads, and normalize IDs
try {
    var node = JsonNode.Parse(body);

    if (node is not null) {
        // Normalize session_id and agent_id to dashless GUIDs
        NormalizeGuidField(node, "session_id");
        NormalizeGuidField(node, "agent_id");

        node["home_dir"] = PathHelpers.HomeDirectory;

        // If running inside a daemon-spawned agent, inject the agent ID
        var agentHostId = Environment.GetEnvironmentVariable("KAPACITOR_AGENT_ID");

        if (agentHostId is not null) {
            node["agent_host_id"] = agentHostId;
        }

        body = node.ToJsonString();
    }
} catch {
    // Best effort — don't fail the hook if JSON parsing fails
}

// Check if session is disabled — skip all server communication
try {
    var disabledSessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();

    if (disabledSessionId is not null && IsSessionDisabled(disabledSessionId)) {
        // For session-end: remove the marker so it doesn't accumulate
        if (command == "session-end") {
            RemoveDisabledMarker(disabledSessionId);
        }

        return 0;
    }
} catch {
    // Best effort — don't fail if JSON parsing fails
}

// On session-start, clear the last-emitted repo cache so this session always gets a
// RepositoryDetected event (the dedup cache is per-cwd, but each session needs its own link).
if (command == "session-start") {
    try {
        var cwdNode = JsonNode.Parse(body)?["cwd"]?.GetValue<string>();

        if (cwdNode is not null) {
            RepositoryDetection.ClearLastEmitted(cwdNode);
        }
    } catch {
        // Best effort
    }
}

// Enrich hook payloads with repository info.
// For session-end and subagent-stop, defer enrichment to run in parallel with watcher kill.
Task<string>? deferredRepoTask = null;

if (command is "session-end" or "subagent-stop") {
    deferredRepoTask = RepositoryDetection.EnrichWithRepositoryInfo(body);
} else {
    body = await RepositoryDetection.EnrichWithRepositoryInfo(body);
}

// Load config once for exclusion check and default_visibility injection.
// Runs after repo enrichment so the body already has repository.owner/repo_name,
// avoiding a redundant git detection in RepoExclusion.
var kapacitorConfig = await AppConfig.Load();

// Check repo exclusion — silently exit for excluded repos
if (kapacitorConfig?.ExcludedRepos is { Length: > 0 } repos && await RepoExclusion.IsExcludedAsync(body, repos)) {
    return 0;
}

// Inject default_visibility from config for session-start hooks
if (command == "session-start" && kapacitorConfig?.DefaultVisibility is { } vis) {
    try {
        var node = JsonNode.Parse(body);

        if (node is not null) {
            node["default_visibility"] = vis;
            body                       = node.ToJsonString();
        }
    } catch {
        // Best effort — don't block session start if config read fails
    }
}

// For session-start: read plan file if slug is known and inject plan_content into payload
var planContentInjected = false;

if (command == "session-start") {
    try {
        var node = JsonNode.Parse(body);
        var slug = node?["slug"]?.GetValue<string>();

        if (slug is not null) {
            var planContent = ReadPlanFile(slug);

            if (planContent is not null) {
                node!["plan_content"] = planContent;
                body                  = node.ToJsonString();
                planContentInjected   = true;
            }
        }
    } catch {
        // Best effort — don't fail the hook if plan reading fails
    }
}

// For session-end and subagent-stop: kill watcher BEFORE posting hook
// so transcript is fully drained before server computes stats.
// If watcher was already dead, do an inline drain to catch up.
// Repo enrichment runs concurrently (started above).
switch (command) {
    case "session-end": {
        try {
            var node           = JsonNode.Parse(body);
            var sessionId      = node?["session_id"]?.GetValue<string>();
            var transcriptPath = node?["transcript_path"]?.GetValue<string>();

            if (sessionId is not null) {
                await WatcherManager.KillWatcher(sessionId);

                // Always inline drain — the watcher may have been alive but never connected
                // (stuck in SignalR connect retry during server downtime). InlineDrainAsync
                // checks server position first, so it's a no-op if already fully drained.
                if (transcriptPath is not null) {
                    await WatcherManager.InlineDrainAsync(baseUrl!, sessionId, transcriptPath, agentId: null);
                }
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kapacitor] session-end pre-hook failed: {ex.Message}");
        }

        body = await deferredRepoTask!;

        break;
    }
    case "subagent-stop": {
        try {
            var node           = JsonNode.Parse(body);
            var sessionId      = node?["session_id"]?.GetValue<string>();
            var agentId        = node?["agent_id"]?.GetValue<string>();
            var transcriptPath = node?["transcript_path"]?.GetValue<string>();

            if (sessionId is not null && agentId is not null) {
                await WatcherManager.KillWatcher($"{sessionId}-{agentId}");

                if (transcriptPath is not null) {
                    var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                    var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                    await WatcherManager.InlineDrainAsync(baseUrl!, sessionId, agentTranscriptPath, agentId);
                }
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kapacitor] subagent-stop pre-hook failed: {ex.Message}");
        }

        body = await deferredRepoTask!;

        break;
    }
}

using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
using var content = new StringContent(body, Encoding.UTF8, "application/json");

HttpResponseMessage response;

try {
    response = await client.PostWithRetryAsync($"{baseUrl!}/hooks/{command}", content);
} catch (HttpRequestException ex) {
    HttpClientExtensions.WriteUnreachableError(baseUrl!, ex);

    return 1;
}

if (!response.IsSuccessStatusCode) {
    Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");

    return 1;
}

// Check session-end response for generate_whats_done flag
if (command == "session-end") {
    try {
        var responseBody = await response.Content.ReadAsStringAsync();
        var responseNode = JsonNode.Parse(responseBody);

        if (responseNode?["generate_whats_done"]?.GetValue<bool>() == true) {
            var node      = JsonNode.Parse(body);
            var sessionId = node?["session_id"]?.GetValue<string>();

            if (sessionId is not null) {
                WatcherManager.SpawnWhatsDoneGenerator(baseUrl!, sessionId);
            }
        }
    } catch {
        // Best effort — don't fail the hook if response parsing fails
    }
}

switch (command) {
    // For session-start and subagent-start: ensure watcher is running AFTER posting hook
    case "session-start": {
        var node           = JsonNode.Parse(body);
        var sessionId      = node?["session_id"]?.GetValue<string>();
        var transcriptPath = node?["transcript_path"]?.GetValue<string>();
        var sessionCwd     = node?["cwd"]?.GetValue<string>();

        // If CLI didn't inject plan_content, check if server resolved a slug (pending continuation)
        if (!planContentInjected && sessionId is not null) {
            try {
                var responseBody = await response.Content.ReadAsStringAsync();
                var responseNode = JsonNode.Parse(responseBody);
                var resolvedSlug = responseNode?["slug"]?.GetValue<string>();

                if (resolvedSlug is not null) {
                    var planContent = ReadPlanFile(resolvedSlug);

                    if (planContent is not null) {
                        await PostPlanContentAsync(client, baseUrl!, sessionId, planContent);
                    }
                }
            } catch {
                // Best effort — don't fail the hook if plan posting fails
            }
        }

        var source = node?["source"]?.GetValue<string>();

        var isResumeOrCompact = source is not null
         && (source.Equals("resume", StringComparison.OrdinalIgnoreCase)
             || source.Equals("compact", StringComparison.OrdinalIgnoreCase));

        if (sessionId is not null && transcriptPath is not null) {
            await WatcherManager.EnsureWatcherRunning(baseUrl!, sessionId, transcriptPath, agentId: null, cwd: sessionCwd, skipTitle: isResumeOrCompact);
        }

        break;
    }
    case "subagent-start": {
        var node           = JsonNode.Parse(body);
        var sessionId      = node?["session_id"]?.GetValue<string>();
        var agentId        = node?["agent_id"]?.GetValue<string>();
        var transcriptPath = node?["transcript_path"]?.GetValue<string>();

        if (sessionId is not null && agentId is not null && transcriptPath is not null) {
            var sessionDir          = Path.ChangeExtension(transcriptPath, null);
            var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
            await WatcherManager.EnsureWatcherRunning(baseUrl!, $"{sessionId}-{agentId}", agentTranscriptPath, agentId, sessionId);
        }

        break;
    }
    case "notification" or "stop": {
        // Check watcher liveness on every notification/stop hook
        var node           = JsonNode.Parse(body);
        var sessionId      = node?["session_id"]?.GetValue<string>();
        var transcriptPath = node?["transcript_path"]?.GetValue<string>();
        var sessionCwd     = node?["cwd"]?.GetValue<string>();

        if (sessionId is not null && transcriptPath is not null) {
            await WatcherManager.EnsureWatcherRunning(baseUrl!, sessionId, transcriptPath, agentId: null, cwd: sessionCwd);
        }

        break;
    }
}

// Wait for update check to print (if applicable)
if (updateCheckTask is not null) {
    await updateCheckTask;
}

return 0;

string? ReadPlanFile(string slug) {
    var planPath = Path.Combine(ClaudePaths.Plans, $"{slug}.md");

    try {
        return File.Exists(planPath) ? File.ReadAllText(planPath) : null;
    } catch (Exception ex) {
        Console.Error.WriteLine($"[kapacitor] Failed to read plan file at {planPath}: {ex.Message}");

        return null;
    }
}

async Task PostPlanContentAsync(HttpClient httpClient, string url, string sessionId, string planContent) {
    var       obj         = new JsonObject { ["plan_content"] = planContent };
    using var planPayload = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
    await httpClient.PostWithRetryAsync($"{url}/api/sessions/{sessionId}/plan", planPayload);
}

static string? GetArg(string[] arguments, string flag) {
    var idx = Array.IndexOf(arguments, flag);

    return idx >= 0 && idx + 1 < arguments.Length ? arguments[idx + 1] : null;
}

string? ResolveSessionId(string[] args, int skipCount = 1) {
    // Take the first positional argument (skip flags starting with --)
    var fromArg = args.Skip(skipCount).FirstOrDefault(a => !a.StartsWith("--"));

    return fromArg ?? Environment.GetEnvironmentVariable("KAPACITOR_SESSION_ID");
}

void NormalizeGuidField(JsonNode node, string fieldName) {
    var value = node[fieldName]?.GetValue<string>();

    if (value is not null && value.Contains('-')) {
        node[fieldName] = value.Replace("-", "");
    }
}

async Task PrintUsage() {
    var hookList = string.Join('\n', hookCommands.Select(h => $"  {h}"));
    var text     = EmbeddedResources.Load("help-usage.txt").Replace("{hookCommands}", hookList);
    await Console.Out.WriteAsync(text);
}

string GetDisabledDir() => PathHelpers.ConfigPath("disabled");

bool IsSessionDisabled(string sessionId) =>
    File.Exists(Path.Combine(GetDisabledDir(), sessionId));

void MarkSessionDisabled(string sessionId) {
    var dir = GetDisabledDir();
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, sessionId), "");
}

void RemoveDisabledMarker(string sessionId) {
    var path = Path.Combine(GetDisabledDir(), sessionId);

    try { File.Delete(path); } catch {
        /* ignore */
    }
}

async Task<int> PrintCommandHelp(string cmd) {
    var text = EmbeddedResources.TryLoad($"help-{cmd}.txt");

    if (text is not null) {
        await Console.Out.WriteAsync(text);
    } else if (hookCommands.Contains(cmd)) {
        var hookText = EmbeddedResources.Load("help-hook.txt").Replace("{cmd}", cmd);
        await Console.Out.WriteAsync(hookText);
    } else {
        Console.Error.WriteLine($"Unknown command: {cmd}");
        Console.Error.WriteLine("Run `kapacitor --help` for a list of commands.");

        return 1;
    }

    return 0;
}
