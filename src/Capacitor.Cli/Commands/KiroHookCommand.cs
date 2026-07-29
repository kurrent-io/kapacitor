using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for AWS Kiro CLI hooks. Kiro (the rebranded
/// Amazon Q Developer CLI) delivers each hook as JSON on STDIN; the kcap
/// installer writes one entry per event with the event name embedded in the
/// command: <c>kcap hook --kiro --event agentSpawn</c>.
/// </summary>
/// <remarks>
/// Wire contract (Kiro event → server route):
///   agentSpawn → POST /hooks/session-start/kiro, then ensure the watcher is
///                running (vendor=kiro). agentSpawn fires on EVERY prompt with
///                the SAME session id, so the server's deterministic lifecycle
///                event id collapses them to one SessionStarted and the
///                idempotent EnsureWatcherRunning is a no-op once live. The
///                watcher tails Kiro's append-only JSONL session log
///                (~/.kiro/sessions/cli/{id}.jsonl), streams it (vendor=kiro), and
///                — because Kiro has NO session-end hook — synthesizes
///                /hooks/session-end/kiro when it observes the kiro-cli process exit.
///   (any other) → no-op exit 0.
///
/// Kiro appends non-empty hook stdout straight into agent context. That is exactly the
/// team-memory injection channel for <c>agentSpawn</c> — but it is also why every OTHER event
/// here still emits NOTHING (a stdout-writing <c>stop</c> hook re-injects and loops the agent),
/// and why <c>agentSpawn</c>, which fires on EVERY prompt, must inject at most ONCE per session.
/// The raw fragment is written with no JSON envelope and no diagnostics: whatever lands on stdout
/// becomes conversation context verbatim.
/// </remarks>
static class KiroHookCommand {
    /// <summary>
    /// Writes the team-memory fragment as Kiro consumes it: raw text, no envelope.
    ///
    /// <para>The shared adapter renders <c>""</c> for a null fragment, so every no-memory path
    /// (opt-out, exclusion, provider failure, budget exhaustion, and — the common case here — a
    /// repeat <c>agentSpawn</c> whose lease is already spent) writes ZERO bytes and leaves the
    /// pre-memory behaviour of this hook byte-identical. Unlike the Codex and Copilot adapters
    /// there is no null-case asymmetry to encode: Kiro's empty output IS the shared rendering.</para>
    ///
    /// <para>Serialized before the first byte is written so a renderer fault degrades to silence
    /// rather than injecting a partial document into the model's context.</para>
    /// </summary>
    internal static void WriteAgentSpawnOutput(TextWriter writer, string? fragment) {
        string payload;

        try {
            payload = SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Kiro, fragment);
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return;
        }

        writer.Write(payload);
    }

    /// <summary>
    /// Starts the shared memory fetch so it overlaps the lifecycle POST. Returns a task that never
    /// faults — every failure resolves to null, which the writer renders as zero bytes.
    ///
    /// <para><b>The lease is load-bearing here, not incidental.</b> Kiro has no per-session hook:
    /// <c>agentSpawn</c> fires on every prompt with the SAME session id, so without the shared
    /// once-per-session lease the index would be re-injected — and re-charged — on every turn, and
    /// would steadily bias the conversation. The lifecycle reason is therefore
    /// <see cref="SessionLifecycleReason.RepeatedTurnCallback"/> with <c>CallbackMayRepeat: true</c>,
    /// which the shared policy resolves to a lease-guarded decision. A genuinely new session brings a
    /// new session id, hence a new lease key, hence a fresh injection — no Kiro-specific logic.</para>
    ///
    /// <para>Deliberately no commit gate (unlike Copilot): this hook always exits 0 and Kiro always
    /// consumes its stdout, so no POST OUTCOME can make a fetched fragment undeliverable, and the
    /// lease may commit as soon as the fetch succeeds.</para>
    ///
    /// <para>That argument is about the outcome only, and it is not sufficient on its own — the
    /// caller must ALSO guarantee nothing slow sits between the commit and the write. A hook killed
    /// at Kiro's timeout while awaiting something else would leave the lease spent with nothing on
    /// stdout, and every later <c>agentSpawn</c> would skip fetch and injection alike. Hence the
    /// call site writes and flushes the fragment before it awaits the lifecycle POST.</para>
    ///
    /// <para><b>Scope safety:</b> the git root discovered from the payload is preferred and the
    /// payload cwd is the fallback; with neither, injection is skipped rather than letting the shared
    /// resolver fall back to the hook PROCESS's cwd and inject an unrelated repository's memories.</para>
    /// </summary>
    static Task<string?> StartMemoryIndexTask(
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
                new SessionMemoryLifecycle(SessionStartHarness.Kiro, sessionId, LifecycleInstanceId: null,
                    IsTopLevel: true, ClassificationAuthoritative: true,
                    SessionLifecycleReason.RepeatedTurnCallback, CallbackMayRepeat: true),
                new SessionStartMemoryContextRequest(baseUrl, scopeRoot, disabled, budget, CancellationToken.None));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return Task.FromResult<string?>(null);
        }
    }

    /// <param name="processStart">Monotonic hook-start stamp anchoring every budget computation;
    /// defaults to now. Tests pass an older stamp to drive the budget-exhausted branch without sleeping.</param>
    public static async Task<int> Handle(string baseUrl, TextReader stdin, string[] args,
            long processStart = 0,
            Func<string?, CancellationToken, Task<HttpClient>>? memoryClientFactory = null,
            Func<SessionStartMemoryLeaseStore>?                 memoryStoreFactory  = null) {
        var ps = processStart == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : processStart;
        // The installer always passes --event; default to agentSpawn so a
        // hand-rolled hook entry without it still records.
        var eventName = GetArg(args, "--event") ?? "agentSpawn";

        var body = await stdin.ReadToEndAsync();

        JsonNode? node;
        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        // agentSpawn is the only actionable event; anything else is a no-op that
        // MUST exit 0 with empty stdout.
        if (eventName != "agentSpawn") return 0;

        // Kiro's session id is the conversation UUID (dashed). Unlike every other
        // vendor it is NOT in the hook's STDIN payload — Kiro's agentSpawn payload
        // is only {hook_event_name, cwd, prompt}. Kiro instead exposes the id to
        // hook processes via the KIRO_SESSION_ID env var, so read it from there
        // (with a payload fallback in case a future schema adds the field). Keep the
        // dashed form for the server payload (matches the transcript's
        // conversation_id) and the dashless form for local keys (watcher pid file /
        // disable markers), mirroring every other vendor dispatcher.
        var dashedSessionId = Environment.GetEnvironmentVariable("KIRO_SESSION_ID");
        if (string.IsNullOrEmpty(dashedSessionId)) {
            dashedSessionId = TryGetString(node, "session_id");
        }
        if (string.IsNullOrEmpty(dashedSessionId)) return 0;
        if (!Guid.TryParse(dashedSessionId, out _)) return 0;

        var sessionId = dashedSessionId.Replace("-", "");

        // Mirror the Claude/Codex/Copilot disabled-session fast path: `kcap
        // disable` must stop every POST and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId)) return 0;

        // Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(PathHelpers.ConfigPath("spool"));

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = await AppConfig.GetActiveProfileAsync();

        // Cheap string-prefix path exclusion runs on every firing; repo exclusion
        // runs once after enrichment, then marks the session disabled so later
        // agentSpawn firings take the fast path above.
        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths)) {
            return 0;
        }

        return await HandleAgentSpawn(baseUrl, node, dashedSessionId, sessionId, cwd, activeProfile, spool,
                   ps, memoryClientFactory, memoryStoreFactory);
    }

    static async Task<int> HandleAgentSpawn(
            string    baseUrl,
            JsonNode  node,
            string    dashedSessionId,
            string    sessionId,
            string?   cwd,
            Profile?  activeProfile,
            HookSpool spool,
            long      processStart,
            Func<string?, CancellationToken, Task<HttpClient>>? memoryClientFactory,
            Func<SessionStartMemoryLeaseStore>?                 memoryStoreFactory
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "agentSpawn",
            ["session_id"]      = dashedSessionId,
            ["home_dir"]        = PathHelpers.HomeDirectory
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Stamp default visibility BEFORE enrichment so it survives the
        // JsonString round-trip (same rationale as the Codex/Copilot dispatchers).
        // null lets the server fall back to org-repo visibility, which would
        // silently flip private-default users' Kiro sessions to org-visible.
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        // Model lives in the sibling {id}.json (the JSONL turn lines carry none),
        // so the server gets it only from this hook. Best-effort: at agentSpawn the
        // file may not exist yet — the next agentSpawn (fires every prompt) backfills.
        if (ReadKiroModel(dashedSessionId) is { } model) {
            forwarded["model"] = model;
        }

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId);
            return 0;
        }

        // Started BEFORE the lifecycle POST so the two overlap, and only after the disabled/
        // excluded-path/excluded-repo early-outs above so an excluded repo never reaches the memory
        // subsystem. The git root stamped onto the forwarded payload is the preferred scope; the
        // payload cwd is the fallback (never a process-cwd fallback — see StartMemoryIndexTask).
        // Ceiling: the generic 5s hook budget. kcap writes no `timeout_ms` into Kiro's agent hook
        // entry, so Kiro's own default governs and the conservative shared ceiling is the safe floor.
        var memoryTask = StartMemoryIndexTask(
            baseUrl, sessionId,
            TryGetString(JsonNode.Parse(enriched), "workspace_root") ?? cwd,
            // The EFFECTIVE profile: ProfileResolver returns a null Profile whenever --server-url or
            // KCAP_URL wins, so reading AppConfig.ResolvedProfile?.Profile here would silently ignore
            // the user's opt-out on those deployments (the defect found reviewing the Copilot adapter).
            activeProfile?.DisableMemoryIndex is true,
            // Remaining() already reserves Safety — do not subtract it again.
            HookBudget.Remaining(processStart, "session-start"),
            memoryClientFactory, memoryStoreFactory);

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. On a real
        // failure PostOrSpoolAsync already logged to stderr; a lapse or transient outage instead
        // durably spools the payload for a later drain pass. Only a permanent failure skips the
        // watcher this firing — agentSpawn fires again next prompt and retries.
        //
        // Started but NOT awaited yet, so the POST cannot stand between a fetched fragment and
        // stdout. PostWithRetryAsync carries a 30s retry budget — far beyond this hook's 5s — so
        // awaiting it first meant a hung POST could burn the whole hook budget AFTER the orchestrator
        // had already committed the once-per-session lease, leaving the lease spent, nothing written,
        // and every later agentSpawn skipping both fetch and injection. Safe to run concurrently with
        // the write below because the poster only ever writes to stderr, never stdout.
        var postTask = AgentHookPoster.PostOrSpoolAsync(
            baseUrl, "session-start/kiro", enriched, "kiro-hook",
            spool, sessionId, route: "session-start/kiro");

        // The fragment reaches stdout as soon as the bounded fetch resolves — before the POST is
        // awaited and before the watcher branch — so neither a slow POST nor a later
        // EnsureWatcherRunning stall can strand an already-committed injection. Flushed explicitly:
        // a fragment sitting in a buffer when Kiro's hook timeout kills the process is a fragment
        // whose lease was spent for nothing.
        WriteAgentSpawnOutput(Console.Out,
            await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, processStart, "session-start"));
        await Console.Out.FlushAsync();

        // The POST await is BOUNDED by what is left of the hook ceiling. Writing early is not enough on
        // its own: Kiro appends stdout only from a hook that COMPLETED, so an invocation killed at
        // Kiro's timeout while still awaiting a 30s-retrying POST discards the fragment anyway — and
        // its lease is already committed, so no later agentSpawn would re-fetch. Recording is the
        // retryable half of this hook (agentSpawn fires every prompt); the injection is once-per-
        // session. So when the budget lapses we stop waiting, spool the payload durably, and exit 0.
        //
        // Double delivery is harmless: an in-flight POST that lands after this spools the same
        // payload, and the server's deterministic lifecycle event id collapses the two onto one
        // SessionStarted (the same property that makes per-prompt agentSpawn re-POSTs free).
        HookPostOutcome outcome;

        try {
            outcome = await postTask.WaitAsync(HookBudget.Remaining(processStart, "session-start"));
        } catch (TimeoutException) {
            spool.Append(sessionId, "session-start/kiro", enriched);
            // Spooled, not Failed: a drain pass will replay it, so capture must still start.
            outcome = HookPostOutcome.Spooled;
        }

        if (!AgentHookPoster.ShouldSpawnAfter(outcome)) return 0;

        // The watcher tails Kiro's own append-only session log
        // ~/.kiro/sessions/cli/{id}.jsonl (the file is named with the dashed id).
        // The watcher also owns session-end: GetCodingAgentPid() inside
        // SpawnWatcher passes the kiro-cli pid as --parent-pid, so the watcher
        // POSTs session-end/kiro when kiro-cli exits.
        var transcriptPath = KiroPaths.SessionJsonl(dashedSessionId);

        await WatcherManager.EnsureWatcherRunning(
            baseUrl, sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "kiro"
        );

        return 0;
    }

    /// <summary>
    /// Reads the session model from the sibling <c>{id}.json</c>
    /// (<c>session_state.rts_model_state.model_info.model_id</c>, e.g. "auto").
    /// Returns null when the file is absent (agentSpawn can fire before Kiro
    /// writes it) or unparseable — model is best-effort enrichment.
    /// </summary>
    static string? ReadKiroModel(string dashedSessionId) {
        try {
            var path = KiroPaths.SessionJson(dashedSessionId);
            if (!File.Exists(path)) return null;

            var model = JsonNode.Parse(File.ReadAllText(path))
                ?["session_state"]?["rts_model_state"]?["model_info"]?["model_id"]?.GetValue<string>();

            return string.IsNullOrWhiteSpace(model) ? null : model;
        } catch {
            return null;
        }
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
