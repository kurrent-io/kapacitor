using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.SessionStartMemory;

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
    public static Task<int> Handle(string baseUrl, string[] args, long processStart = 0) =>
        Handle(baseUrl, args, Console.In, Console.Out, processStart);

    internal static Task<int> Handle(string baseUrl, string[] args, TextReader stdin, TextWriter stdout,
            long processStart = 0,
            Func<string?, CancellationToken, Task<HttpClient>>? memoryClientFactory = null,
            Func<SessionStartMemoryLeaseStore>?                 memoryStoreFactory  = null) =>
        HandleCore(baseUrl, args, stdin, stdout,
            processStart == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : processStart,
            memoryClientFactory, memoryStoreFactory);

    static async Task<int> HandleCore(string baseUrl, string[] args, TextReader stdin, TextWriter stdout,
            long processStart,
            Func<string?, CancellationToken, Task<HttpClient>>? memoryClientFactory,
            Func<SessionStartMemoryLeaseStore>?                 memoryStoreFactory) {
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
            baseUrl, sessionId, transcriptPath!, cwd, payload, activeProfile,
            stdout, processStart, memoryClientFactory, memoryStoreFactory);
    }

    /// <summary>
    /// Writes the team-memory fragment in Antigravity's PreInvocation shape:
    /// <c>{"injectSteps":[{"userMessage":"…"}]}</c>. <c>userMessage</c> rather than
    /// <c>ephemeralMessage</c> because the vendor's own embedded hook contract documents the latter as
    /// transient, and the index is meant to persist for the conversation.
    ///
    /// <para><b>A null fragment writes ZERO BYTES.</b> This hook emitted nothing at all before the
    /// memory index existed, so rendering the adapter's <c>{}</c> on the no-fragment path would change
    /// the wire behaviour of EVERY invocation for EVERY user — including the IDE-only majority, whose
    /// product was never probed — to buy nothing. Mirrors Copilot and Kiro. Do not "simplify" this by
    /// rendering the null case: the shared adapter's own null rendering is <c>{}</c>, which is exactly
    /// what must not reach stdout here.</para>
    ///
    /// <para>Serialized before the first byte so a renderer fault degrades to silence rather than a
    /// partial document.</para>
    /// </summary>
    internal static void WritePreInvocationOutput(TextWriter writer, string? fragment) {
        if (fragment is null) return;

        string payload;

        try {
            payload = SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Antigravity, fragment);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return;
        }

        writer.Write(payload);
    }

    static async Task<int> HandleSessionStart(
            string      baseUrl,
            string      sessionId,
            string      transcriptPath,
            string?     cwd,
            JsonObject  payload,
            Profile?    activeProfile,
            TextWriter  stdout,
            long        processStart,
            Func<string?, CancellationToken, Task<HttpClient>>? memoryClientFactory,
            Func<SessionStartMemoryLeaseStore>?                 memoryStoreFactory
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
            ["home_dir"]        = PathHelpers.HomeDirectory,
            ["started_at"]      = DateTimeOffset.UtcNow.ToString("O")
        };

        string? scopeRoot = null;
        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) {
                forwarded["workspace_root"] = workspaceRoot;
                scopeRoot = workspaceRoot;
            }
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

        // Start the memory fetch so it OVERLAPS the lifecycle POST. Never before it, and never
        // awaited before it — the POST is what capture depends on.
        var memoryTask = StartMemoryIndexTask(
            baseUrl, sessionId, scopeRoot,
            activeProfile?.DisableMemoryIndex is true,
            HookBudget.Remaining(processStart, "hook"),
            memoryClientFactory, memoryStoreFactory);

        // Task 6: spawn-before-post. Route through the shared spool-aware poster (which
        // replaced this dispatcher's former bespoke poster) — a lapse/outage durably spools the
        // payload for a later drain AND still proceeds to spawn the watcher, so capture never
        // depends on lifecycle-POST delivery. Only a permanent Failed withholds the watcher.
        var spool   = new HookSpool(PathHelpers.ConfigPath("spool"));
        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            baseUrl, "session-start/antigravity", enriched, "antigravity-hook",
            spool, sessionId, route: "session-start/antigravity");

        // AwaitBounded already subtracts HookBudget.Safety — do NOT subtract it again. Written
        // even when the watcher-spawn gate below returns early — a withheld watcher must not
        // suppress injection.
        WritePreInvocationOutput(
            stdout, await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, processStart, "hook"));

        // Fail-open: a non-zero exit would surface as a failed hook; skip the watcher
        // this firing and let the next PreInvocation retry.
        if (!SpawnGateForTest(outcome, baseUrl)) return 0;

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
    internal static bool SpawnGateForTest(HookPostOutcome o, string? baseUrl)
        => AgentHookPoster.ShouldSpawnAfter(o, baseUrl);

    /// <summary>
    /// The lifecycle this adapter reports. PreInvocation fires ONCE PER INVOCATION within a
    /// conversation (its payload carries `invocationNum`), so this is a REPEATING callback and the
    /// fenced lease is what makes injection once-per-conversation. Kiro's agentSpawn is the only
    /// other harness with this shape; every other adapter is CallbackMayRepeat: false and copying
    /// one would re-inject the index on every turn.
    ///
    /// <para>The lease key is derived from the harness token and the normalized session id only.
    /// `invocationNum` must never reach it, directly or transitively — it is the one field that
    /// varies between callbacks, so keying on it would mint a fresh lease per invocation.</para>
    /// </summary>
    internal static SessionMemoryLifecycle LifecycleFor(string sessionId) =>
        new(SessionStartHarness.Antigravity, sessionId, LifecycleInstanceId: null,
            IsTopLevel: true, ClassificationAuthoritative: true,
            SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true);

    /// <summary>
    /// Starts the shared memory fetch so it overlaps the lifecycle POST. Returns a task that never
    /// faults — every failure resolves to null, which the writer renders as zero bytes.
    ///
    /// <para><b>Scope safety:</b> with no scope root the fetch is skipped rather than letting the
    /// shared resolver fall back to the hook PROCESS's cwd and inject an unrelated repository's
    /// memories.</para>
    ///
    /// <para><c>CanAttempt</c> is checked BEFORE any client is constructed, because the client
    /// factory's EnsureAbsolute calls Environment.Exit(2) on an unusable base url — which would kill
    /// the hook before it can write its output.</para>
    /// </summary>
    internal static Task<string?> StartMemoryIndexTask(
            string     baseUrl,
            string     sessionId,
            string?    scopeRoot,
            bool       disabled,
            TimeSpan   budget,
            Func<string?, CancellationToken, Task<HttpClient>>? memoryClientFactory,
            Func<SessionStartMemoryLeaseStore>?                 memoryStoreFactory) {
        if (disabled || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(scopeRoot)
         || budget <= TimeSpan.Zero
         || !SessionStartMemoryHookSupport.CanAttempt(baseUrl))
            return Task.FromResult<string?>(null);

        try {
            var store = memoryStoreFactory?.Invoke() ?? new SessionStartMemoryLeaseStore();
            var provider = new SessionStartMemoryContextProvider(
                new SessionStartMemoryScopeResolver(),
                memoryClientFactory ?? SessionStartMemoryHookSupport.ClientFactory(baseUrl),
                // Only clients we created are ours to dispose; an injected factory's client belongs
                // to its caller and may be handed back again on the 401-refresh call.
                disposeClients: memoryClientFactory is null);

            return new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                LifecycleFor(sessionId),
                new SessionStartMemoryContextRequest(baseUrl, scopeRoot, disabled, budget, CancellationToken.None));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return Task.FromResult<string?>(null);
        }
    }

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
