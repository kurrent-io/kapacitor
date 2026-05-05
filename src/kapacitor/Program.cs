using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using kapacitor;
using kapacitor.Auth;
using kapacitor.Commands;
using kapacitor.Config;
using WatchCommand = kapacitor.Commands.WatchCommand;

// Declared before any PrintUsage()/PrintCommandHelp() call because both
// local functions capture this list to render the hooks section of the
// help text.
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

// Hooks only: short-circuit when spawned inside a headless claude invocation
// (e.g., title generation, the eval judge) so we don't forward the nested
// session's hook events back into kapacitor and blow up into a loop. Scoped
// to hook commands because non-hook commands — notably `kapacitor mcp judge`
// running as an MCP server child of the eval judge claude process — must
// actually execute despite inheriting KAPACITOR_SKIP=1 from the parent.
//
// Runs before ResolveServerUrl/update-check so a skipped hook does no work:
// ResolveServerUrl can shell out to `git remote -v` and emit warnings, and
// the update-check task hits the npm registry — both pure noise inside a
// nested headless invocation.
if (Environment.GetEnvironmentVariable("KAPACITOR_SKIP") is "1" && hookCommands.Contains(command)) {
    return 0;
}

var baseUrl = await AppConfig.ResolveServerUrl(args);

// Fire-and-forget update check (prints hint to stderr after command finishes)
var   noUpdateCheck   = args.Contains("--no-update-check");
Task? updateCheckTask = null;

if (!noUpdateCheck) {
    updateCheckTask = Task.Run(UpdateCommand.PrintUpdateHintIfAvailable);
}

if (command is "--help" or "-h" or "help") {
    await PrintUsage();

    return 0;
}

// Per-command help: kapacitor <command> --help / -h
if (args.Skip(1).Any(a => a is "--help" or "-h")) {
    return await PrintCommandHelp(command);
}

// Commands that don't need a server URL
string[] offlineCommands = ["--help", "-h", "help", "--version", "-v", "logout", "cleanup", "config", "agent", "setup", "status", "update", "plugin", "profile", "use", "repos", "login"];

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
    case "eval": {
        // --list-questions is a standalone sub-action; short-circuit.
        if (args.Contains("--list-questions")) {
            return await EvalCommand.HandleListQuestions(baseUrl!);
        }

        var evalSessionId = ResolveSessionId(args, valueFlags: ["--model", "--threshold", "--questions", "--skip"]);

        if (evalSessionId is null) {
            Console.Error.WriteLine("Usage: kapacitor eval [--model sonnet] [--chain] [--threshold N]");
            Console.Error.WriteLine("                     [--questions <csv> | --skip <csv>] [sessionId]");
            Console.Error.WriteLine("       kapacitor eval --list-questions");
            Console.Error.WriteLine("  No session ID provided and KAPACITOR_SESSION_ID not set.");

            return 1;
        }

        var evalChain     = args.Contains("--chain");
        var evalModel     = GetArg(args, "--model") ?? "sonnet";
        var evalThreshold = GetArg(args, "--threshold") is { } ts && int.TryParse(ts, out var parsed)
            ? parsed
            : (int?)null;
        var evalQuestions = GetArg(args, "--questions");
        var evalSkip      = GetArg(args, "--skip");

        // Guard against the user dropping the flag value — otherwise GetArg
        // silently returns the next token ("--skip", "--chain", …) and the
        // resolver later reports a confusing "unknown token" error.
        foreach (var (flag, value) in new[] { ("--questions", evalQuestions), ("--skip", evalSkip) }) {
            if (value is not null && value.StartsWith("--")) {
                Console.Error.WriteLine($"eval: {flag} requires a value (got '{value}')");
                return 2;
            }
        }

        return await EvalCommand.HandleEval(
            baseUrl!, evalSessionId, evalModel, evalChain, evalThreshold,
            evalQuestions, evalSkip
        );
    }
    case "generate-whats-done" when args.Length < 2:
        Console.Error.WriteLine("Usage: kapacitor generate-whats-done <sessionId>");

        return 1;
    case "generate-whats-done": {
        var wdSessionId = args[1].Replace("-", "");

        return await WhatsDoneCommand.HandleGenerateWhatsDone(baseUrl!, wdSessionId);
    }
    case "login": {
        if (args.Contains("--discover")) {
            return await HandleDiscoverLoginAsync();
        }

        if (baseUrl is null) {
            Console.Error.WriteLine("No server configured. Run `kapacitor setup`, set KAPACITOR_URL, or use `kapacitor login --discover`.");

            return 1;
        }

        return await OAuthLoginFlow.LoginWithDiscoveryAsync(baseUrl);
    }
    case "logout": {
        await TokenStore.DeleteAsync();
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
    case "repos":
        return await ReposCommand.HandleAsync(args);
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
            Console.Error.WriteLine("Usage: kapacitor mcp review|judge …");
            Console.Error.WriteLine("  kapacitor mcp review [--owner <owner> --repo <repo> --pr <number>]");
            Console.Error.WriteLine("  kapacitor mcp judge --session <sessionId>");

            return 1;
        }

        switch (args[1]) {
            case "review": {
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
            case "judge": {
                var session = GetArg(args, "--session");

                if (string.IsNullOrWhiteSpace(session)) {
                    Console.Error.WriteLine("Usage: kapacitor mcp judge --session <sessionId>");

                    return 1;
                }

                return await McpJudgeServer.RunAsync(baseUrl!, session);
            }
            default:
                Console.Error.WriteLine($"Unknown mcp subcommand: {args[1]}");

                return 1;
        }
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
        var     minLines      = 15;
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
        Console.Error.WriteLine("Usage: kapacitor watch <sessionId> <transcriptPath> [--agent-id <agentId>] [--cwd <cwd>] [--skip-title] [--parent-pid <pid>]");

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

        int? parentPid    = null;
        var  parentPidIdx = Array.IndexOf(args, "--parent-pid");

        if (parentPidIdx >= 0 && parentPidIdx + 1 < args.Length && int.TryParse(args[parentPidIdx + 1], out var ppid)) {
            parentPid = ppid;
        }

        return await WatchCommand.RunWatch(baseUrl!, watchSessionId, watchPath, watchAgentId, watchCwd, watchSkipTitle, parentPid);
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

        // Parse the server response body once. Two consumers:
        //   1. slug fallback for plan_content (resume/compact path)
        //   2. top_clusters → SessionStart additionalContext (DEV-1676)
        try {
            var responseBody = await response.Content.ReadAsStringAsync();
            var responseNode = JsonNode.Parse(responseBody);

            // (1) slug-resolved continuation — inject plan content if server resolved a slug
            if (!planContentInjected && sessionId is not null) {
                var resolvedSlug = responseNode?["slug"]?.GetValue<string>();

                if (resolvedSlug is not null) {
                    var planContent = ReadPlanFile(resolvedSlug);

                    if (planContent is not null) {
                        await PostPlanContentAsync(client, baseUrl!, sessionId, planContent);
                    }
                }
            }

            // (2) DEV-1676 — emit additionalContext from top_clusters unless the user opted out
            var disabled = AppConfig.ResolvedProfile?.Profile?.DisableSessionGuidelines is true;
            var emission = SessionGuidelinesEmitter.BuildAdditionalContext(responseNode, disabled);

            if (emission is not null) {
                Console.WriteLine(emission);
            }
        } catch {
            // Best effort — never fail session start over response parsing
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

string? ResolveSessionId(string[] args, int skipCount = 1, string[]? valueFlags = null) =>
    ArgParsing.ResolveSessionId(args, skipCount, valueFlags);

void NormalizeGuidField(JsonNode node, string fieldName) {
    var value = node[fieldName]?.GetValue<string>();

    if (value is not null && value.Contains('-')) {
        node[fieldName] = value.Replace("-", "");
    }
}

async Task<int> HandleDiscoverLoginAsync() {
    using var http  = new HttpClient();
    var proxyClient = new AuthProxyClient(http);

    var clientId = await proxyClient.GetGitHubClientIdAsync(AuthProxyEndpoint.Url);

    if (clientId is null) {
        await Console.Error.WriteLineAsync("Cannot reach the Kurrent auth service.");

        return 1;
    }

    var ghToken = await OAuthLoginFlow.RunDeviceFlowAsync(clientId);
    if (ghToken is null) return 1;

    var discovery = new TenantDiscovery(proxyClient, new SpectreTenantPicker());
    var outcome   = await discovery.RunAsync(AuthProxyEndpoint.Url, ghToken);

    if (outcome.ErrorMessage is not null) {
        await Console.Error.WriteLineAsync(outcome.ErrorMessage);

        return 1;
    }

    // Merge discovered tenants as profiles; the picked one becomes active
    var cfg = await AppConfig.LoadProfileConfig();
    cfg = TenantDiscovery.MergeProfiles(cfg, outcome.Tenants, outcome.Picked!);
    await AppConfig.SaveProfileConfig(cfg);

    // Discovery flows only via the shared GitHub App proxy, so every discovered tenant
    // uses the GitHubApp provider. If DiscoveredTenant ever gains a Provider field, read it here.

    // Exchange tokens for every discovered tenant so switching profiles works immediately.
    // One HttpClient shared across all per-tenant exchanges to avoid socket/port exhaustion.
    var exchanges = outcome.Tenants.Select(async tenant => {
        var origin = AppConfig.NormalizeUrl(tenant.Origin);
        var exit = await OAuthLoginFlow.ExchangeAndSaveAsync(
            http, origin, ghToken, AuthProvider.GitHubApp, tenant.OrgLogin);
        if (exit != 0) {
            await Console.Error.WriteLineAsync(
                $"Warning: token exchange failed for {tenant.OrgLogin}. Run 'kapacitor login' after switching to that profile.");
        }
    });
    await Task.WhenAll(exchanges);

    await Console.Out.WriteLineAsync($"Logged in. Active profile: {outcome.Picked!.OrgLogin}.");

    return 0;
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
