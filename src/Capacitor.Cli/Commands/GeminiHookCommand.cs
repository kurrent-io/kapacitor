using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for Google Gemini CLI hooks. Unlike
/// Copilot, Gemini's command-hook stdin payload carries a uniform
/// <c>hook_event_name</c> (PascalCase: <c>SessionStart</c> / <c>SessionEnd</c> /
/// <c>Notification</c>), so this dispatcher self-routes on it like Claude — the
/// installer registers a single <c>kcap hook --gemini</c> command per event.
/// </summary>
/// <remarks>
/// Wire contract (Gemini event → server route):
///   SessionStart → POST /hooks/session-start/gemini, then spawn the watcher
///                  tailing the payload's <c>transcript_path</c>
///                  (<c>~/.gemini/tmp/&lt;project&gt;/chats/session-*.jsonl</c>)
///                  with vendor=gemini. Gemini re-fires with source:"resume" on
///                  the same session id and appends to the same transcript file,
///                  so the server's deterministic lifecycle ids make the re-POST
///                  idempotent and the watcher resumes from the server watermark.
///   SessionEnd   → kill watcher + capped inline drain (mirror of the Copilot /
///                  Claude pre-drain cap), then POST /hooks/session-end/gemini.
///   Notification → best-effort forward to the Claude-shaped /hooks/notification.
/// Gemini treats hook stdout as a JSON decision channel, and selects the text to
/// parse as <c>stdout.trim() || stderr.trim()</c> — so an EMPTY stdout makes it
/// parse this process's STDERR (where kcap writes failed-POST and auth-lapse
/// diagnostics) as the hook's result.
///
/// Because of that, a recognised SessionStart writes exactly one JSON object on
/// every returning path: the memory envelope when there is one, else an explicit
/// <c>{"continue":true}</c>. SessionEnd/Notification still emit nothing, which
/// leaves them exposed to the same stderr fallback — tracked separately.
/// </remarks>
static class GeminiHookCommand {
    // Mirror of CopilotHookCommand.PreHookDrainCap: the drain must
    // never starve the session-end POST, or the session sticks "Active".
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(8);

    // Notification forwarding is telemetry — a stalled server must not block
    // Gemini's turn loop.
    static readonly TimeSpan NotificationPostBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Renders the SessionStart hook result. With a fragment this is the memory envelope; without one it
    /// is the explicit allow object — NOT zero bytes.
    ///
    /// <para>Emitting on the empty path is deliberate and diverges from the other memory adapters. Gemini
    /// parses <c>stdout.trim() || stderr.trim()</c>, so silent stdout makes it read kcap's STDERR
    /// diagnostics as the hook's output; a payload wins the <c>||</c> and shadows them. See
    /// <see cref="GeminiAllowEnvelope"/>. Do not "harmonise" this back to the other adapters' shape.</para>
    ///
    /// <para>Two separate try blocks on purpose: rendering completes before any byte is written, and a
    /// write that throws mid-payload must not alter the command's exit code. A truncated payload is safe
    /// on Gemini's side — a JSON parse failure degrades to plain text and never synthesises a blocking
    /// decision, which requires an explicit <c>decision: "block"|"deny"</c>.</para>
    /// </summary>
    /// <summary>Gemini's explicit allow-with-no-context result. A literal rather than a serializer call
    /// ON PURPOSE: this is the payload every failure path degrades to, so producing it must itself be
    /// incapable of failing. Serializing it would add a throw path to the one value that exists to
    /// guarantee we never emit zero bytes.
    ///
    /// <para>Carries no <c>hookSpecificOutput</c> key, so Gemini's <c>getAdditionalContext()</c>
    /// short-circuits on its own <c>"additionalContext" in …</c> guard and contributes nothing; and no
    /// <c>decision</c>/<c>stopReason</c>, so it cannot block.</para></summary>
    internal const string AllowPayload = """{"continue":true}""";

    internal static void WriteSessionStartOutput(TextWriter writer, string? fragment) {
        // Start from the payload that cannot fail, and only upgrade to the memory envelope when
        // rendering genuinely succeeds. Structured this way so that a render throw OR an empty render
        // degrades to the allow object rather than to silence — silence is what re-exposes stderr.
        var payload = AllowPayload;

        if (fragment is not null) {
            try {
                var rendered = SessionStartMemoryOutputAdapters.Render(SessionStartHarness.Gemini, fragment);
                if (!string.IsNullOrEmpty(rendered)) payload = rendered;
            } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
                // keep AllowPayload
            }
        }

        // Separate try: a write throwing mid-payload must not alter the command's exit code. A truncated
        // payload is safe on Gemini's side — a parse failure degrades to plain text, never a block.
        try { writer.Write(payload); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { }
    }

    static Task<string?> StartMemoryIndexTask(
            string     baseUrl,
            string     sessionId,
            string?    scopeRoot,
            bool       disabled,
            SessionLifecycleReason reason,
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
                disposeClients: memoryClientFactory is null);

            return new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                // CallbackMayRepeat: false — Gemini's SessionStart is a session-level event, not a
                // per-turn callback like Kiro's agentSpawn. A `resume` re-fire on the same session id is
                // made idempotent by the lease, not by this flag.
                new SessionMemoryLifecycle(SessionStartHarness.Gemini, sessionId, LifecycleInstanceId: null,
                    IsTopLevel: true, ClassificationAuthoritative: true, reason, CallbackMayRepeat: false),
                new SessionStartMemoryContextRequest(baseUrl, scopeRoot, disabled, budget, CancellationToken.None));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return Task.FromResult<string?>(null);
        }
    }

    /// <param name="processStart">Monotonic hook-start stamp anchoring every budget computation;
    /// defaults to now. Tests pass an older stamp to drive the budget-exhausted branch without sleeping.</param>
    public static async Task<int> Handle(string baseUrl, TextReader stdin,
            long processStart = 0,
            Func<string?, CancellationToken, Task<HttpClient>>? memoryClientFactory = null,
            Func<SessionStartMemoryLeaseStore>?                 memoryStoreFactory  = null) {
        var ps = processStart == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : processStart;

        var body = await stdin.ReadToEndAsync();

        JsonNode? node;
        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        var eventName = TryGetString(node, "hook_event_name");
        if (string.IsNullOrEmpty(eventName)) return 0;

        // Gemini session ids are dashed UUIDs; keep the dashless form for the
        // server (AgentSession-{dashless} convention shared by every vendor).
        // Recognised from HERE, not after session-id validation. Gemini fired a SessionStart and WILL
        // read our stdout whatever we make of the rest of the payload, so a bad session id must still
        // emit — otherwise this path re-exposes the stdout||stderr fallback.
        var isSessionStart = eventName == "SessionStart";

        var dashedSessionId = TryGetString(node, "session_id");
        if (string.IsNullOrEmpty(dashedSessionId) || !Guid.TryParse(dashedSessionId, out _)) {
            if (isSessionStart) WriteSessionStartOutput(Console.Out, fragment: null);
            return 0;
        }

        var sessionId = dashedSessionId.Replace("-", "");

        // Mirror the Claude/Codex/Copilot disabled-session fast path: `kcap
        // disable` must stop every POST and watcher restart for the session.
        // Invariant: a RECOGNISED SessionStart writes exactly one JSON object on every returning path,
        // including the suppression fast paths below — an exception-riddled invariant is not an
        // invariant, and these paths are not stderr-free (Program.cs drains the spool before dispatch).
        // Only genuinely unrecognisable input stays silent: unparseable stdin and a missing/blank
        // hook_event_name, where we cannot know a SessionStart occurred at all.
        if (DisabledSessions.IsDisabled(sessionId)) {
            if (eventName == "SessionEnd") DisabledSessions.RemoveMarker(sessionId);
            if (isSessionStart) WriteSessionStartOutput(Console.Out, fragment: null);
            return 0;
        }

        // Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(PathHelpers.ConfigPath("spool"));

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = await AppConfig.GetActiveProfileAsync();

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths)) {
            if (isSessionStart) WriteSessionStartOutput(Console.Out, fragment: null);
            return 0;
        }

        return eventName switch {
            "SessionStart" => await HandleSessionStart(baseUrl, node, sessionId, cwd, activeProfile, spool,
                                                       ps, memoryClientFactory, memoryStoreFactory),
            "SessionEnd"   => await HandleSessionEnd(baseUrl, node, sessionId, cwd),
            "Notification" => await HandleNotification(baseUrl, node, sessionId, cwd),
            _              => 0   // unknown / unsubscribed — fail-open like the other dispatchers
        };
    }

    static async Task<int> HandleSessionStart(
            string    baseUrl,
            JsonNode  node,
            string    sessionId,
            string?   cwd,
            Profile?  activeProfile,
            HookSpool spool,
            long      processStart,
            Func<string?, CancellationToken, Task<HttpClient>>? memoryClientFactory = null,
            Func<SessionStartMemoryLeaseStore>?                 memoryStoreFactory  = null
        ) {
        var source = TryGetString(node, "source") is { Length: > 0 } s ? s : "startup";

        var forwarded = new JsonObject {
            ["hook_event_name"] = "SessionStart",
            ["session_id"]      = sessionId,
            ["source"]          = source,
            ["home_dir"]        = PathHelpers.HomeDirectory
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }

        // Gemini stamps hook payloads with an ISO-8601 `timestamp`; forward it
        // as started_at so canonical SessionStarted carries the real start time
        // (the server falls back to UtcNow when absent).
        if (TryGetIsoTimestamp(node, "timestamp") is { } startedAt) {
            forwarded["started_at"] = startedAt.ToString("O");
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Stamp default visibility BEFORE enrichment so it survives the
        // JsonString round-trip (same rationale as the Codex/Copilot path).
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId);
            WriteSessionStartOutput(Console.Out, fragment: null);
            return 0;
        }

        // Started in PARALLEL with the POST below, not before it — the lifecycle POST is the
        // latency-critical path. Remaining() already subtracts its own safety margin; do not subtract
        // again here (double-subtraction was a real defect in the Copilot adapter).
        var memoryTask = StartMemoryIndexTask(
            baseUrl, sessionId,
            scopeRoot: GitRepository.FindRoot(cwd) ?? cwd,
            disabled: activeProfile?.DisableMemoryIndex is true,
            // Shared mapper, NOT a local one: it maps an unrecognised source to Unknown, which the
            // lifecycle policy suppresses. A local mapper defaulting to New would inject on an
            // unverified reason AND spend the once-per-session lease on it.
            reason: SessionStartMemoryHookSupport.ReasonFor(source),
            budget: HookBudget.Remaining(processStart, "session-start"),
            memoryClientFactory, memoryStoreFactory);

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. On a real
        // failure PostOrSpoolAsync already logged to stderr; a lapse or transient outage instead
        // durably spools the payload for a later drain pass. Only a permanent failure keeps the
        // prior non-zero exit and skips the watcher; the next resume/startup retries.
        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            baseUrl, "session-start/gemini", enriched, "gemini-hook",
            spool, sessionId, route: "session-start/gemini");

        // Write the hook result BEFORE any return, including the failed-POST return below. The Codex
        // adapter shipped a defect where an early `return 1` skipped the stdout handshake entirely; do
        // not reintroduce it. The memory index is independent of lifecycle capture — a server rejecting
        // the POST has not invalidated an index already fetched — and Gemini parses hook stdout
        // unconditionally, with the exit code only setting its own `success` flag.
        WriteSessionStartOutput(Console.Out,
            await SessionStartMemoryHookSupport.AwaitBounded(memoryTask, processStart, "session-start"));

        if (!AgentHookPoster.ShouldSpawnAfter(outcome)) return outcome == HookPostOutcome.Failed ? 1 : 0;

        // Task 6: await (was fire-and-forget) so a spawn failure is observed here rather
        // than silently swallowed, and the process isn't torn down before the spawn completes.
        await EnsureWatcher(baseUrl, sessionId, node, cwd, source);
        return 0;
    }

    /// <summary>Test seam mirroring <see cref="AgentHookPoster.ShouldSpawnAfter"/> — session-start
    /// capture must start on <c>Posted</c> OR <c>Spooled</c>, never gated behind lifecycle-POST
    /// delivery.</summary>
    internal static bool SpawnGateForTest(HookPostOutcome o) => AgentHookPoster.ShouldSpawnAfter(o);

    static async Task<int> HandleSessionEnd(string baseUrl, JsonNode node, string sessionId, string? cwd) {
        var transcriptPath = TryGetString(node, "transcript_path");

        // Kill watcher + inline-drain BEFORE the POST so the server computes
        // stats over the full transcript — capped so a slow drain can't starve
        // the session-end POST. Only drain when Gemini gave us a transcript path
        // (it always does today; defensive otherwise).
        if (!string.IsNullOrEmpty(transcriptPath)) {
            try {
                var drained = await TimeBudget.RunCappedAsync(
                    async () => {
                        await WatcherManager.KillWatcher(sessionId);
                        await WatcherManager.InlineDrainAsync(baseUrl, sessionId, transcriptPath, agentId: null, vendor: "gemini");
                        // Gemini fires no subagent-stop hook, so the parent owns subagent
                        // teardown: kill each live child watcher, drain its tail, and finalize
                        // it (subagent-stop). Restart-safe — driven off the on-disk files,
                        // not an in-memory set. Shared with the watcher's parent-exit fallback
                        // so a crash that bypasses this hook still finalizes subagents.
                        await GeminiSubagentTeardown.DrainAsync(baseUrl, sessionId, transcriptPath);
                    },
                    PreHookDrainCap
                );

                if (!drained) {
                    await Console.Error.WriteLineAsync(
                        $"[kcap] gemini session-end pre-drain cap ({PreHookDrainCap.TotalSeconds:0}s) elapsed; proceeding to POST. "
                      + $"Transcript tail may be incomplete — recoverable via: kcap import --gemini --session {sessionId}"
                    );
                }
            } catch (Exception ex) {
                Console.Error.WriteLine($"[kcap] gemini session-end pre-hook failed: {ex.Message}");
            }
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = TryGetString(node, "reason") ?? "exit",
            ["home_dir"]        = PathHelpers.HomeDirectory
        };

        if (cwd is not null) forwarded["cwd"] = cwd;

        if (TryGetIsoTimestamp(node, "timestamp") is { } endedAt) {
            forwarded["ended_at"] = endedAt.ToString("O");
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // AuthLapsed / Posted → clean exit (0); a real failure keeps the prior non-zero exit.
        return await PostHookAsync(baseUrl, "session-end/gemini", forwarded.ToJsonString()) == HookPostOutcome.Failed ? 1 : 0;
    }

    static async Task<int> HandleNotification(string baseUrl, JsonNode node, string sessionId, string? cwd) {
        // The server's NotificationHook requires message + notification_type.
        var message          = TryGetString(node, "message");
        var notificationType = TryGetString(node, "notification_type")
                            ?? TryGetString(node, "notificationType");

        if (message is null || notificationType is null) return 0;

        var forwarded = new JsonObject {
            ["hook_event_name"]   = "Notification",
            ["session_id"]        = sessionId,
            ["message"]           = message,
            ["notification_type"] = notificationType,
            ["home_dir"]          = PathHelpers.HomeDirectory
        };

        if (cwd is not null) forwarded["cwd"] = cwd;

        using var cts = new CancellationTokenSource(NotificationPostBudget);
        try {
            // Status-returning variant (not CreateAuthenticatedClientAsync, which writes a
            // per-turn "expired" line to stderr): on a lapse, stay quiet and skip the doomed POST.
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl, cts.Token);
            using (client) {
                if (AgentHookPoster.IsAuthLapsed(status)) return 0;
                using var content = new StringContent(forwarded.ToJsonString(), Encoding.UTF8, "application/json");
                using var _       = await client.PostAsync($"{baseUrl}/hooks/notification", content, cts.Token);
            }
        } catch {
            // Recording must never fail the hook.
        }

        return 0;
    }

    static async Task EnsureWatcher(string baseUrl, string sessionId, JsonNode node, string? cwd, string source) {
        // Gemini hands us the transcript path directly (no derivation needed,
        // unlike Copilot). Empty/absent → skip (can't tail nothing).
        var transcriptPath = TryGetString(node, "transcript_path");
        if (string.IsNullOrEmpty(transcriptPath)) return;

        // Skip title (re)generation on resume/clear — the session already has
        // one and resume appends to the same transcript.
        var skipTitle = source is "resume" or "clear";

        // Task 6: awaited (was fire-and-forget `_ =`) so a spawn failure surfaces to the
        // caller instead of being silently dropped, and the host process doesn't exit before the
        // spawn completes.
        await WatcherManager.EnsureWatcherRunning(
            baseUrl, sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: skipTitle, vendor: "gemini"
        );
    }

    // Shared auth-aware recording POST: skips the doomed POST (and the misleading per-turn
    // "HTTP 401" stderr line) when auth has lapsed, reporting AuthLapsed so the caller exits
    // cleanly instead of erroring. See AgentHookPoster.
    static Task<HookPostOutcome> PostHookAsync(string baseUrl, string endpoint, string body)
        => AgentHookPoster.PostAsync(baseUrl, endpoint, body, "gemini-hook");

    static DateTimeOffset? TryGetIsoTimestamp(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v
         && v.TryGetValue<string>(out var s)
         && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts)) {
            return ts;
        }

        return null;
    }

    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
