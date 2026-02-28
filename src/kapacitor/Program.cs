using System.Text;
using System.Text.Json.Nodes;
using kapacitor;

// Skip all processing when spawned inside a headless claude invocation (e.g., title generation)
// to prevent infinite hook loops
if (Environment.GetEnvironmentVariable("KAPACITOR_SKIP") is "1")
    return 0;

var baseUrl = Environment.GetEnvironmentVariable("KAPACITOR_URL") ?? "http://localhost:5108";

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

switch (command) {
    case "errors" when args.Length < 2:
        Console.Error.WriteLine("Usage: kapacitor errors [--chain] <sessionId>");

        return 1;
    case "errors": {
        var useChain     = args.Contains("--chain");
        var errSessionId = args.Skip(1).First(a => a != "--chain");

        return await ErrorsCommand.HandleErrors(baseUrl, errSessionId, useChain);
    }
    case "recap" when args.Length < 2:
        Console.Error.WriteLine("Usage: kapacitor recap [--chain] <sessionId>");

        return 1;
    case "recap": {
        var useChain        = args.Contains("--chain");
        var recapSessionId  = args.Skip(1).First(a => a != "--chain");

        return await RecapCommand.HandleRecap(baseUrl, recapSessionId, useChain);
    }
    case "validate-plan" when args.Length < 2:
        Console.Error.WriteLine("Usage: kapacitor validate-plan <sessionId>");

        return 1;
    case "validate-plan": {
        var vpSessionId = args[1];

        return await ValidatePlanCommand.Handle(baseUrl, vpSessionId);
    }
    case "generate-whats-done" when args.Length < 2:
        Console.Error.WriteLine("Usage: kapacitor generate-whats-done <sessionId>");

        return 1;
    case "generate-whats-done": {
        var wdSessionId = args[1];

        return await WhatsDoneCommand.HandleGenerateWhatsDone(baseUrl, wdSessionId);
    }
    case "history": {
        string? filterCwd     = null;
        string? filterSession = null;
        int     minLines      = 10;
        var     cwdArgIdx     = Array.IndexOf(args, "--cwd");

        if (cwdArgIdx >= 0 && cwdArgIdx + 1 < args.Length)
            filterCwd = args[cwdArgIdx + 1];
        var sessionArgIdx = Array.IndexOf(args, "--session");

        if (sessionArgIdx >= 0 && sessionArgIdx + 1 < args.Length)
            filterSession = args[sessionArgIdx + 1];
        var minLinesIdx = Array.IndexOf(args, "--min-lines");

        if (minLinesIdx >= 0 && minLinesIdx + 1 < args.Length && int.TryParse(args[minLinesIdx + 1], out var parsed))
            minLines = parsed;

        return await HistoryCommand.HandleHistory(baseUrl, filterCwd, filterSession, minLines);
    }
    case "watch" when args.Length < 3:
        Console.Error.WriteLine("Usage: kapacitor watch <sessionId> <transcriptPath> [--agent-id <agentId>] [--cwd <cwd>]");

        return 1;
    case "watch": {
        var     watchSessionId = args[1];
        var     watchPath      = args[2];
        string? watchAgentId   = null;
        string? watchCwd       = null;
        var     agentIdIdx     = Array.IndexOf(args, "--agent-id");

        if (agentIdIdx >= 0 && agentIdIdx + 1 < args.Length)
            watchAgentId = args[agentIdIdx + 1];
        var cwdIdx = Array.IndexOf(args, "--cwd");

        if (cwdIdx >= 0 && cwdIdx + 1 < args.Length)
            watchCwd = args[cwdIdx + 1];

        return await WatchCommand.RunWatch(baseUrl, watchSessionId, watchPath, watchAgentId, watchCwd);
    }
}

if (!hookCommands.Contains(command)) {
    Console.Error.WriteLine($"Unknown command: {command}");

    return 1;
}

var body = await Console.In.ReadToEndAsync();

// Inject home_dir into all hook payloads
try {
    var node = JsonNode.Parse(body);
    if (node is not null) {
        node["home_dir"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        body = node.ToJsonString();
    }
} catch {
    // Best effort — don't fail the hook if JSON parsing fails
}

// Enrich all hook payloads with repository info
body = await RepositoryDetection.EnrichWithRepositoryInfo(body);

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

switch (command) {
// For session-end and subagent-stop: kill watcher BEFORE posting hook

// so transcript is fully drained before server computes stats.
// If watcher was already dead, do an inline drain to catch up.
    case "session-end": {
        var node           = JsonNode.Parse(body);
        var sessionId      = node?["session_id"]?.GetValue<string>();
        var transcriptPath = node?["transcript_path"]?.GetValue<string>();

        if (sessionId is not null) {
            var wasRunning = await WatcherManager.KillWatcher(sessionId);

            if (!wasRunning && transcriptPath is not null)
                await WatcherManager.InlineDrainAsync(baseUrl, sessionId, transcriptPath, agentId: null);
        }

        break;
    }
    case "subagent-stop": {
        var node           = JsonNode.Parse(body);
        var sessionId      = node?["session_id"]?.GetValue<string>();
        var agentId        = node?["agent_id"]?.GetValue<string>();
        var transcriptPath = node?["transcript_path"]?.GetValue<string>();

        if (sessionId is not null && agentId is not null) {
            var wasRunning = await WatcherManager.KillWatcher($"{sessionId}-{agentId}");

            if (!wasRunning && transcriptPath is not null) {
                var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                await WatcherManager.InlineDrainAsync(baseUrl, sessionId, agentTranscriptPath, agentId);
            }
        }

        break;
    }
}

using var client  = new HttpClient();
using var content = new StringContent(body, Encoding.UTF8, "application/json");

HttpResponseMessage response;
try {
    response = await client.PostWithRetryAsync($"{baseUrl}/hooks/{command}", content);
} catch (HttpRequestException ex) {
    HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
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

            if (sessionId is not null)
                WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sessionId);
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

                    if (planContent is not null)
                        await PostPlanContentAsync(client, baseUrl, sessionId, planContent);
                }
            } catch {
                // Best effort — don't fail the hook if plan posting fails
            }
        }

        if (sessionId is not null && transcriptPath is not null)
            WatcherManager.EnsureWatcherRunning(baseUrl, sessionId, transcriptPath, agentId: null, cwd: sessionCwd);

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
            WatcherManager.EnsureWatcherRunning(baseUrl, $"{sessionId}-{agentId}", agentTranscriptPath, agentId, sessionId);
        }

        break;
    }
    case "notification" or "stop": {
        // Check watcher liveness on every notification/stop hook
        var node           = JsonNode.Parse(body);
        var sessionId      = node?["session_id"]?.GetValue<string>();
        var transcriptPath = node?["transcript_path"]?.GetValue<string>();
        var sessionCwd     = node?["cwd"]?.GetValue<string>();

        if (sessionId is not null && transcriptPath is not null)
            WatcherManager.EnsureWatcherRunning(baseUrl, sessionId, transcriptPath, agentId: null, cwd: sessionCwd);

        break;
    }
}

return 0;

string? ReadPlanFile(string slug) {
    var home     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var planPath = Path.Combine(home, ".claude", "plans", $"{slug}.md");

    try {
        return File.Exists(planPath) ? File.ReadAllText(planPath) : null;
    } catch (Exception ex) {
        Console.Error.WriteLine($"[kapacitor] Failed to read plan file at {planPath}: {ex.Message}");
        return null;
    }
}

async Task PostPlanContentAsync(HttpClient httpClient, string url, string sessionId, string planContent) {
    var obj = new JsonObject { ["plan_content"] = planContent };
    using var planPayload = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
    await httpClient.PostWithRetryAsync($"{url}/api/sessions/{sessionId}/plan", planPayload);
}

void PrintUsage() {
    Console.WriteLine("kapacitor — Claude Code hook forwarder for Kurrent.Capacitor");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  kapacitor <hook-command>                                      Forward a hook payload (reads JSON from stdin)");
    Console.WriteLine("  kapacitor watch <sessionId> <path> [--agent-id <agentId>]     Watch a transcript file and POST lines to server");
    Console.WriteLine("  kapacitor history [--cwd <path>] [--session <id>] [--min-lines <n>]  Load historical transcript files into server");
    Console.WriteLine("  kapacitor errors [--chain] <id>                               List tool call errors for a session");
    Console.WriteLine("  kapacitor recap [--chain] <id>                                Session recap for context handoff");
    Console.WriteLine("  kapacitor validate-plan <id>                                  Validate plan completion for a session");
    Console.WriteLine("  kapacitor generate-whats-done <id>                            Generate what's-done summary for a session");
    Console.WriteLine("  kapacitor --help                                              Show this help");
    Console.WriteLine();
    Console.WriteLine("Hook commands:");

    foreach (var h in hookCommands)
        Console.WriteLine($"  {h}");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  KAPACITOR_URL    Server URL (default: http://localhost:5108)");
}
