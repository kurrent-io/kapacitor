using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Dispatcher for Google Antigravity's control hooks. Antigravity is a GUI
/// IDE with no shell hooks; the kcap plugin (a block in Antigravity's <c>hooks.json</c>)
/// registers one command per lifecycle/tool event. Because the JSON payload carries NO
/// event-name field, the event is passed as a positional arg:
///   <c>kcap hook --antigravity &lt;Event&gt;</c>   with the payload on stdin.
///
/// Wire contract (mirrors <see cref="OpenCodeHookCommand"/> — Antigravity likewise has
/// the watcher own session content + session-end): only <c>PreInvocation</c> is
/// actionable — it POSTs /hooks/session-start/antigravity and ensures a watcher is
/// running (vendor=antigravity) tailing the conversation's <c>transcript_full.jsonl</c>.
/// PreInvocation re-fires cheaply on every turn; the server's deterministic lifecycle id
/// collapses the repeats and <see cref="WatcherManager.EnsureWatcherRunning"/> is a no-op
/// once live. Session-end is watcher-owned: Antigravity's IDE process outlives any one
/// conversation (like the Codex desktop), so the watcher self-terminates on idle and
/// POSTs /hooks/session-end/antigravity. <c>Stop</c>/<c>PostInvocation</c>/tool events
/// are no-ops here (the watcher already tails the transcript continuously).
///
/// Fail-open throughout — a kcap/server problem must never disrupt the Antigravity IDE. The
/// session-start POST goes through <see cref="AgentHookPoster.PostOrSpoolAsync"/> (Task
/// 6): a lapsed/outage POST is durably spooled for a later drain, and the watcher still spawns
/// (<see cref="SpawnGateForTest"/>) — capture must not depend on lifecycle-POST delivery.
/// Antigravity conversation ids are dashed UUIDs; kcap canonicalizes them to the DASHLESS form
/// for BOTH the session-start payload and the watcher key so they resolve to one stream (the
/// dashed id lives on only in the transcript file path). Historical import canonicalizes the
/// same way, so a conversation captured live and later re-imported dedupes to one stream.
/// </summary>
static class AntigravityHookCommand {
    public static Task<int> Handle(string baseUrl, string[] args) =>
        Handle(baseUrl, args, Console.In);

    internal static async Task<int> Handle(string baseUrl, string[] args, TextReader stdin) {
        var eventName = EventArg(args);
        if (string.IsNullOrWhiteSpace(eventName)) {
            // Control hooks must always exit 0 (a non-zero exit makes Antigravity treat the
            // hook as failed) — surface the hint on stderr but don't fail the hook.
            Console.Error.WriteLine(
                "kcap hook --antigravity requires an event name, e.g. "
              + "`kcap hook --antigravity PreInvocation` (the kcap Antigravity plugin passes it; "
              + "re-run: kcap plugin install --antigravity)");
            return 0;
        }

        // PreInvocation is the only actionable event; the watcher owns everything else.
        if (eventName != "PreInvocation") return 0;

        JsonObject? payload;
        try {
            payload = JsonNode.Parse(await stdin.ReadToEndAsync()) as JsonObject;
        } catch {
            return 0; // malformed payload — fail open, next PreInvocation retries
        }
        if (payload is null) return 0;

        var conversationId = Str(payload, "conversationId");
        if (string.IsNullOrWhiteSpace(conversationId)) return 0;

        var transcriptPath = Str(payload, "transcriptPath");
        if (string.IsNullOrWhiteSpace(transcriptPath)) return 0; // nothing to tail

        // Canonical dashless id — matches how `kcap watch` and `kcap disable` normalize ids,
        // so session-start, the watcher's transcript batches, and disable all resolve to ONE
        // stream (the dashed conversationId is kept only for the transcript file path).
        var sessionId = conversationId!.Replace("-", "");

        var cwd = FirstWorkspacePath(payload);

        // Mirror the disabled-session fast path: `kcap disable` must stop every POST
        // and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId)) return 0;

        var activeProfile = await AppConfig.GetActiveProfileAsync();

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths)) {
            return 0;
        }

        return await HandleSessionStart(
            baseUrl, sessionId, transcriptPath!, cwd, payload, activeProfile);
    }

    static async Task<int> HandleSessionStart(
            string      baseUrl,
            string      sessionId,
            string      transcriptPath,
            string?     cwd,
            JsonObject  payload,
            Profile?    activeProfile
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
            ["home_dir"]        = PathHelpers.HomeDirectory,
            ["started_at"]      = DateTimeOffset.UtcNow.ToString("O")
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }
        if (Str(payload, "antigravityVersion") is { } version)
            forwarded["antigravity_version"] = version;

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId)
            forwarded["agent_host_id"] = agentHostId;

        // Stamp default visibility BEFORE enrichment so it survives the JsonString
        // round-trip (same rationale as the OpenCode/Kiro dispatchers); null lets the
        // server fall back to org-repo visibility.
        if (activeProfile?.DefaultVisibility is { } visibility)
            forwarded["default_visibility"] = visibility;

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId);
            return 0;
        }

        // Task 6: spawn-before-post. Route through the shared spool-aware poster (which
        // replaced this dispatcher's former bespoke poster) — a lapse/outage durably spools the
        // payload for a later drain AND still proceeds to spawn the watcher, so capture never
        // depends on lifecycle-POST delivery. Only a permanent Failed withholds the watcher.
        var spool   = new HookSpool(PathHelpers.ConfigPath("spool"));
        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            baseUrl, "session-start/antigravity", enriched, "antigravity-hook",
            spool, sessionId, route: "session-start/antigravity");

        // Fail-open: a non-zero exit would surface as a failed hook; skip the watcher
        // this firing and let the next PreInvocation retry.
        if (!SpawnGateForTest(outcome)) return 0;

        // Watcher key = the dashless session id (kcap watch strips dashes too, so the pid
        // file + the spawned watcher's stream all agree). The dashed conversation id lives on
        // in transcriptPath, from which the watcher derives the sibling gen_metadata db.
        await WatcherManager.EnsureWatcherRunning(
            baseUrl, sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "antigravity"
        );

        return 0;
    }

    /// <summary>Test seam mirroring <see cref="AgentHookPoster.ShouldSpawnAfter"/> — capture must
    /// start on <c>Posted</c> OR <c>Spooled</c>, never gated behind lifecycle-POST delivery.</summary>
    internal static bool SpawnGateForTest(HookPostOutcome o, string? baseUrl = "http://localhost:5108")
        => AgentHookPoster.ShouldSpawnAfter(o, baseUrl);

    /// <summary>The event name — the first positional token after <c>--antigravity</c>.</summary>
    internal static string? EventArg(string[] args) {
        var idx = Array.IndexOf(args, "--antigravity");
        if (idx < 0 || idx + 1 >= args.Length) return null;

        var next = args[idx + 1];
        return next.StartsWith('-') ? null : next;
    }

    static string? FirstWorkspacePath(JsonObject payload) {
        if (payload["workspacePaths"] is JsonArray { Count: > 0 } paths
         && AsString(paths[0]) is { Length: > 0 } first) {
            return first;
        }
        // Fall back to a singular form if present.
        return Str(payload, "cwd");
    }

    /// <summary>
    /// Safely read a string field: returns null when the key is absent OR the value is a
    /// non-string JSON shape (number/object/array). <c>JsonNode.GetValue&lt;string&gt;()</c>
    /// throws on a shape mismatch, which would break the hook's fail-open contract.
    /// </summary>
    static string? Str(JsonObject payload, string key) => AsString(payload[key]);

    static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
