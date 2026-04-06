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

var baseUrl = AppConfig.ResolveServerUrl(args);

// Fire-and-forget update check (prints hint to stderr after command finishes)
var noUpdateCheck = args.Contains("--no-update-check");
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
    PrintUsage();

    return 1;
}

var command = args[0];

if (command is "--help" or "-h" or "help") {
    PrintUsage();

    return 0;
}

// Per-command help: kapacitor <command> --help / -h
if (args.Skip(1).Any(a => a is "--help" or "-h")) {
    return PrintCommandHelp(command);
}

// Commands that don't need a server URL
string[] offlineCommands = ["--help", "-h", "help", "--version", "-v", "logout", "cleanup", "config", "agent", "setup", "status", "update"];

if (baseUrl is null && !offlineCommands.Contains(command)) {
    Console.Error.WriteLine("No server configured. Run `kapacitor setup` or set KAPACITOR_URL.");
    return 1;
}

switch (command) {
    case "--version" or "-v": {
        var version = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        Console.WriteLine($"kapacitor {version}");
        return 0;
    }
    case "errors": {
        var useChain     = args.Contains("--chain");
        var errSessionId = ResolveSessionId(args, skipCount: 1, skipFlag: "--chain");

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
        Console.WriteLine("Logged out.");

        return 0;
    }
    case "whoami": {
        var provider = await HttpClientExtensions.DiscoverProviderAsync(baseUrl!);

        if (provider == "None") {
            Console.WriteLine("Provider: None (no authentication)");
            Console.WriteLine($"Server:   {baseUrl!}");

            return 0;
        }

        var tokens = TokenStore.Load();

        if (tokens is null) {
            Console.Error.WriteLine("Not authenticated. Run `kapacitor login`.");

            return 1;
        }

        Console.WriteLine($"Username: {tokens.GitHubUsername}");
        Console.WriteLine($"Provider: {tokens.Provider}");
        Console.WriteLine($"Expires:  {tokens.ExpiresAt:u}");
        Console.WriteLine($"Server:   {baseUrl!}");
        Console.WriteLine($"Expired:  {(tokens.IsExpired ? "yes" : "no")}");

        return 0;
    }
    case "agent":
        return await AgentCommands.HandleAsync(args);
    case "setup":
        return await SetupCommand.HandleAsync(args);
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

        return await HistoryCommand.HandleHistory(baseUrl!, filterCwd, filterSession, minLines);
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

        node["home_dir"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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
var kapacitorConfig = AppConfig.Load();

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
            body = node.ToJsonString();
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
            WatcherManager.EnsureWatcherRunning(baseUrl!, sessionId, transcriptPath, agentId: null, cwd: sessionCwd, skipTitle: isResumeOrCompact);
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
            WatcherManager.EnsureWatcherRunning(baseUrl!, $"{sessionId}-{agentId}", agentTranscriptPath, agentId, sessionId);
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
            WatcherManager.EnsureWatcherRunning(baseUrl!, sessionId, transcriptPath, agentId: null, cwd: sessionCwd);
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

string? ResolveSessionId(string[] args, int skipCount = 1, string? skipFlag = null) {
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

void PrintUsage() {
    Console.WriteLine("kapacitor — CLI companion for Kurrent Capacitor");
    Console.WriteLine();
    Console.WriteLine("Usage: kapacitor <command> [options]");
    Console.WriteLine();
    Console.WriteLine("Getting Started:");
    Console.WriteLine("  setup                            Configure server, login, and install plugin");
    Console.WriteLine("  status                           Show server, auth, and agent status");
    Console.WriteLine("  login                            Authenticate via OAuth (browser)");
    Console.WriteLine("  logout                           Remove stored credentials");
    Console.WriteLine("  whoami                           Show current authenticated user");
    Console.WriteLine();
    Console.WriteLine("Configuration:");
    Console.WriteLine("  config show                      Print current configuration");
    Console.WriteLine("  config set <key> <value>         Update a config value");
    Console.WriteLine();
    Console.WriteLine("Agent Daemon:");
    Console.WriteLine("  agent start [-d]                 Start agent daemon (foreground, or -d for background)");
    Console.WriteLine("  agent stop                       Stop background agent daemon");
    Console.WriteLine("  agent status                     Show agent daemon status");
    Console.WriteLine();
    Console.WriteLine("Session:");
    Console.WriteLine("  errors [--chain] [id]            List tool call errors for a session");
    Console.WriteLine("  recap [--chain] [--full] [id]    Session summary (--full for raw transcript)");
    Console.WriteLine("  recap --repo                     Recent session summaries for current repo");
    Console.WriteLine("  validate-plan [id]               Validate plan completion for a session");
    Console.WriteLine("  generate-whats-done <id>         Generate what's-done summary");
    Console.WriteLine("  set-title <title>                Set session title");
    Console.WriteLine("  review <pr>                      Launch Claude with PR review context");
    Console.WriteLine();
    Console.WriteLine("Maintenance:");
    Console.WriteLine("  update                           Check for and install updates");
    Console.WriteLine("  cleanup                          Kill all orphaned watcher processes");
    Console.WriteLine("  --version                        Show version");
    Console.WriteLine("  --help                           Show this help");
    Console.WriteLine();
    Console.WriteLine("Hook Commands (internal — used by Claude Code plugin):");

    foreach (var h in hookCommands) {
        Console.WriteLine($"  {h}");
    }

    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  KAPACITOR_URL              Server URL (overrides config file)");
    Console.WriteLine("  KAPACITOR_SESSION_ID       Session ID (set automatically by SessionStart hook)");
    Console.WriteLine("  KAPACITOR_DAEMON_NAME      Daemon name (overrides config file)");
}

int PrintCommandHelp(string cmd) {
    switch (cmd) {
        case "setup":
            Console.WriteLine("kapacitor setup — Configure server, login, and install plugin");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor setup [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --server-url <url>          Server URL (skip prompt)");
            Console.WriteLine("  --daemon-name <name>        Daemon name (skip prompt)");
            Console.WriteLine("  --default-visibility <vis>  Default visibility: private, org_public, public");
            Console.WriteLine("  --no-prompt                 Non-interactive mode (requires --server-url)");
            break;
        case "status":
            Console.WriteLine("kapacitor status — Show server, auth, and agent status");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor status");
            break;
        case "login":
            Console.WriteLine("kapacitor login — Authenticate via OAuth");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor login");
            Console.WriteLine();
            Console.WriteLine("Opens a browser for OAuth authentication. The auth method (GitHub Device");
            Console.WriteLine("Flow or Auth0 PKCE) is auto-discovered from the server configuration.");
            break;
        case "logout":
            Console.WriteLine("kapacitor logout — Remove stored credentials");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor logout");
            break;
        case "whoami":
            Console.WriteLine("kapacitor whoami — Show current authenticated user");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor whoami");
            break;
        case "config":
            Console.WriteLine("kapacitor config — Manage configuration");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor config <subcommand>");
            Console.WriteLine();
            Console.WriteLine("Subcommands:");
            Console.WriteLine("  show                    Print current configuration");
            Console.WriteLine("  set <key> <value>       Update a config value");
            Console.WriteLine();
            Console.WriteLine("Config keys:");
            Console.WriteLine("  server_url              Server URL");
            Console.WriteLine("  daemon.name             Daemon name");
            Console.WriteLine("  daemon.max_agents       Max concurrent agents");
            Console.WriteLine("  update_check            Enable update check (true/false)");
            Console.WriteLine("  default_visibility      Default session visibility (private, org_public, public)");
            Console.WriteLine("  excluded_repos          Excluded repos, comma-separated (owner/repo,owner/repo)");
            break;
        case "agent":
            Console.WriteLine("kapacitor agent — Manage the agent daemon");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor agent <subcommand>");
            Console.WriteLine();
            Console.WriteLine("Subcommands:");
            Console.WriteLine("  start [-d]              Start the agent daemon (foreground, or -d for background)");
            Console.WriteLine("  stop                    Stop the background agent daemon");
            Console.WriteLine("  status                  Show agent daemon status");
            Console.WriteLine("  logs                    Show recent daemon log output");
            Console.WriteLine();
            Console.WriteLine("Options for start:");
            Console.WriteLine("  --name <name>           Daemon name");
            Console.WriteLine("  --server-url <url>      Server URL");
            Console.WriteLine("  --max-agents <n>        Max concurrent agents (default: 5)");
            Console.WriteLine("  --log-file <path>       Log to file instead of console");
            Console.WriteLine("  -d, --detach            Run in background (logs to file automatically)");
            break;
        case "errors":
            Console.WriteLine("kapacitor errors — List tool call errors for a session");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor errors [options] [sessionId]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --chain                 Include all sessions in the continuation chain");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  sessionId               Session ID (defaults to KAPACITOR_SESSION_ID)");
            break;
        case "recap":
            Console.WriteLine("kapacitor recap — Session summary");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor recap [options] [sessionId]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --chain                 Include all sessions in the continuation chain");
            Console.WriteLine("  --full                  Show raw transcript instead of summary");
            Console.WriteLine("  --repo                  Show recent session summaries for the current repo");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  sessionId               Session ID (defaults to KAPACITOR_SESSION_ID)");
            break;
        case "validate-plan":
            Console.WriteLine("kapacitor validate-plan — Validate plan completion for a session");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor validate-plan [sessionId]");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  sessionId               Session ID (defaults to KAPACITOR_SESSION_ID)");
            break;
        case "generate-whats-done":
            Console.WriteLine("kapacitor generate-whats-done — Generate what's-done summary");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor generate-whats-done <sessionId>");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  sessionId               Session ID (required)");
            break;
        case "set-title":
            Console.WriteLine("kapacitor set-title — Set session title");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor set-title <title>");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  title                   Session title (max 120 characters)");
            Console.WriteLine();
            Console.WriteLine("Environment:");
            Console.WriteLine("  KAPACITOR_SESSION_ID    Session ID (required)");
            break;
        case "review":
            Console.WriteLine("kapacitor review — Launch Claude with PR review context");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor review <pr-url-or-shorthand>");
            Console.WriteLine();
            Console.WriteLine("Arguments:");
            Console.WriteLine("  pr                      PR URL or shorthand (e.g., owner/repo#123)");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  kapacitor review https://github.com/owner/repo/pull/123");
            Console.WriteLine("  kapacitor review owner/repo#123");
            break;
        case "mcp":
            Console.WriteLine("kapacitor mcp — MCP server commands");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor mcp <subcommand>");
            Console.WriteLine();
            Console.WriteLine("Subcommands:");
            Console.WriteLine("  review                  Start the MCP review server (stdio)");
            Console.WriteLine();
            Console.WriteLine("Options for review:");
            Console.WriteLine("  --owner <owner>         Repository owner");
            Console.WriteLine("  --repo <repo>           Repository name");
            Console.WriteLine("  --pr <number>           PR number");
            Console.WriteLine();
            Console.WriteLine("If --owner/--repo/--pr are omitted, auto-detects from git.");
            break;
        case "update":
            Console.WriteLine("kapacitor update — Check for and install updates");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor update");
            break;
        case "cleanup":
            Console.WriteLine("kapacitor cleanup — Kill all orphaned watcher processes");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor cleanup");
            break;
        case "history":
            Console.WriteLine("kapacitor history — Import local transcript history to server");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor history [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --cwd <path>            Filter by working directory");
            Console.WriteLine("  --session <id>          Import a specific session only");
            Console.WriteLine("  --min-lines <n>         Skip sessions shorter than n lines (default: 10)");
            break;
        case "watch":
            Console.WriteLine("kapacitor watch — Watch a session transcript file (internal)");
            Console.WriteLine();
            Console.WriteLine("Usage: kapacitor watch <sessionId> <transcriptPath> [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --agent-id <id>         Agent ID (for subagent watchers)");
            Console.WriteLine("  --cwd <path>            Working directory");
            Console.WriteLine("  --skip-title            Skip title generation");
            break;
        case "permission-request":
            Console.WriteLine("kapacitor permission-request — Handle permission request (internal)");
            Console.WriteLine();
            Console.WriteLine("Usage: echo '<json>' | kapacitor permission-request");
            Console.WriteLine();
            Console.WriteLine("Used by the Claude Code PermissionRequest hook. Reads JSON from stdin.");
            break;
        default:
            if (hookCommands.Contains(cmd)) {
                Console.WriteLine($"kapacitor {cmd} — Hook command (internal)");
                Console.WriteLine();
                Console.WriteLine($"Usage: echo '<json>' | kapacitor {cmd}");
                Console.WriteLine();
                Console.WriteLine("Used internally by the Claude Code plugin. Reads JSON from stdin.");
            } else {
                Console.Error.WriteLine($"Unknown command: {cmd}");
                Console.Error.WriteLine("Run `kapacitor --help` for a list of commands.");
                return 1;
            }
            break;
    }
    return 0;
}
