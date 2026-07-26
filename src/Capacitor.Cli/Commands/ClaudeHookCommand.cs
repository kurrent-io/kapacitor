using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.SessionStartMemory;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for Claude Code hooks. Claude invokes one
/// CLI entry per session event; this method consolidates the seven
/// historical per-event subcommands (<c>session-start</c>, <c>stop</c>, …)
/// behind one entry point and routes off the <c>hook_event_name</c>
/// in the JSON payload — mirroring <see cref="CodexHookCommand"/> and
/// <see cref="CursorHookCommand"/>.
/// </summary>
public static class ClaudeHookCommand {
    // Hard ceiling on the best-effort pre-POST drain (watcher kill + inline transcript
    // drain) for session-end / subagent-stop. Claude kills the SessionEnd hook at its
    // configured timeout (15s); a slow or retrying remote call in the drain could consume
    // all of it (the HTTP retry helper alone allows up to 30s) and the session-end POST
    // would never be sent, leaving the session stuck "Active". 8s leaves ample headroom to
    // send the POST; the server's StopAndDrain + the "kcap import" hint recover the rest.
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(8);

    public static Task<int> Handle(string baseUrl, TextReader stdin, Task? updateCheckTask = null, long processStart = 0,
            TextWriter? stdout = null) {
        var spool = new HookSpool(PathHelpers.ConfigPath("spool"));
        spool.ReapOlderThan(TimeSpan.FromDays(30));
        var ps = processStart == 0 ? Stopwatch.GetTimestamp() : processStart;
        return HandleWithDeps(spool, ps, baseUrl, stdin, updateCheckTask, stdout);
    }

    static Task<int> HandleWithDeps(HookSpool spool, long processStart, string baseUrl, TextReader stdin,
            Task? updateCheckTask, TextWriter? stdout = null)
        => HandleWithDeps(spool, processStart, baseUrl, stdin, updateCheckTask,
            () => HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl),
            async (forceRefresh, ct) => (await HttpClientExtensions.CreateClientWithAuthStatusAsync(
                baseUrl, ct, allowAutoRedirect: false, forceRefresh: forceRefresh)).Client,
            stdout);

    internal static async Task<int> HandleWithDeps(
            HookSpool spool, long processStart, string baseUrl, TextReader stdin, Task? updateCheckTask,
            Func<Task<(HttpClient Client, AuthStatus Status)>> clientFactory,
            Func<bool, CancellationToken, Task<HttpClient>>? memoryClientFactory = null,
            TextWriter? stdout = null) {
        string body;
        try { body = await stdin.ReadToEndAsync(); } catch { return 0; }

        // Minimal parse (no auth/git) so we can spool AND start the watcher even if client creation hangs.
        string? command = null, sessionId = null, transcriptPath = null, cwd = null, source = null, agentId = null;
        try {
            var node = JsonNode.Parse(body);
            var ev   = node?["hook_event_name"]?.GetValue<string>();
            command        = ev is null ? null : ToKebab(ev);
            sessionId      = node?["session_id"]?.GetValue<string>()?.Replace("-", "");
            transcriptPath = node?["transcript_path"]?.GetValue<string>();
            cwd            = node?["cwd"]?.GetValue<string>();
            source         = node?["source"]?.GetValue<string>();
            agentId        = node?["agent_id"]?.GetValue<string>();
        } catch { }

        var clientCap = HookBudget.Remaining(processStart, command ?? "stop");
        var created   = await CreateClientWithinBudgetAsync(clientFactory, clientCap);

        if (created is null) {
            // Auth/client creation exceeded the hook budget (hung /auth/config or refresh during an
            // outage). The watcher and the spool need no client — start capture and persist the
            // lifecycle event so neither the transcript nor the session record is lost.
            if (command == "session-start" && sessionId is not null && transcriptPath is not null) {
                var isResumeOrCompact = source is not null &&
                    (source.Equals("resume", StringComparison.OrdinalIgnoreCase) ||
                     source.Equals("compact", StringComparison.OrdinalIgnoreCase));
                try {
                    await WatcherManager.EnsureWatcherRunning(baseUrl, sessionId, transcriptPath,
                        agentId: null, cwd: cwd, skipTitle: isResumeOrCompact);
                } catch { }
            }
            if (command is "session-start" or "session-end" && sessionId is not null) {
                spool.Append(sessionId, command, NormalizeForSpool(body, command));
                await Console.Error.WriteLineAsync($"[kcap] {command} spooled (auth/client creation exceeded hook budget); will retry on the next kcap hook ({sessionId})");
            }
            else if (command == "subagent-stop" && sessionId is not null && agentId is not null) {
                spool.Append(sessionId, "subagent-stop", NormalizeForSpool(body, command));
                await Console.Error.WriteLineAsync($"[kcap] subagent-stop spooled (auth/client creation exceeded hook budget); will retry on the next kcap hook ({sessionId}/{agentId})");
            }
            return 0;
        }

        var (client, authStatus) = created.Value;
        try {
            return await HandleCore(client, authStatus, spool, processStart, baseUrl, new StringReader(body),
                updateCheckTask, memoryClientFactory, stdout: stdout);
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[kcap] claude hook failed (fail-open): {ex.Message}");
            return 0;
        } finally {
            client.Dispose();
        }
    }

    // Returns (client,status) if created within `cap`; null if the cap elapsed first
    // (abandoned creation task reaped on process exit).
    internal static async Task<(HttpClient Client, AuthStatus Status)?> CreateClientWithinBudgetAsync(
            Func<Task<(HttpClient Client, AuthStatus Status)>> factory, TimeSpan cap) {
        if (cap <= TimeSpan.Zero) return null;
        var task = factory();
        var winner = await Task.WhenAny(task, Task.Delay(cap));
        if (winner != task) {
            // Abandoned: observe ALL terminal states so a late fault (likely during the very
            // outage this guards) doesn't surface as an UnobservedTaskException; dispose the
            // client only if creation actually completed after the cap elapsed.
            _ = task.ContinueWith(static t => {
                if (t.IsFaulted) _ = t.Exception;
                else if (t.Status == TaskStatus.RanToCompletion) t.Result.Client.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return null;
        }
        try { return await task; } catch { return null; }
    }

    // Minimal normalization for an auth-timeout-spooled body: dashless ids (match the server's
    // expected form) and, for session-end, an ended_at stamp so a late replay keeps idempotency.
    static string NormalizeForSpool(string body, string command) {
        try {
            var node = JsonNode.Parse(body);
            if (node is null) return body;
            NormalizeGuidField(node, "session_id");
            NormalizeGuidField(node, "agent_id");
            if (command == "session-end" && node["ended_at"] is null)
                node["ended_at"] = DateTimeOffset.UtcNow.ToString("O");
            return node.ToJsonString();
        } catch { return body; }
    }

    internal static async Task<int> WithHardCap(Task<int> inner, TimeSpan budget) {
        var winner = await Task.WhenAny(inner, Task.Delay(budget));
        return winner == inner ? await inner : 0;
    }

    // Await repo enrichment but never past the remaining hook budget. If it can't finish in time,
    // proceed with the un-enriched body (repo info still reaches the session via the watcher's own
    // detection) so the bounded POST/spool path is always reached before Claude kills the hook.
    static async Task<string> AwaitEnrichmentWithinBudget(Task<string> enrichment, string fallbackBody, TimeSpan budget) {
        if (budget <= TimeSpan.Zero) {
            _ = enrichment.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
            return fallbackBody;
        }
        var winner = await Task.WhenAny(enrichment, Task.Delay(budget));
        if (winner != enrichment) {
            _ = enrichment.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default); // observe if it later faults
            return fallbackBody;
        }
        try { return await enrichment; } catch { return fallbackBody; }
    }

    // Repo/path exclusion gate shared by the main command path and the permission-request
    // watcher self-heal: true when the active profile excludes this session's repo or cwd
    // (caller should skip capture). The fallback repo detection is budgeted so a slow git/gh
    // probe can't blow the hook deadline; if it can't resolve in time we fail open to capturing
    // (the per-cwd cache makes subsequent sessions in an excluded repo resolve and exclude promptly).
    internal static async Task<bool> IsSessionExcludedAsync(Profile? profile, string body, long processStart, string command) {
        if (profile?.ExcludedRepos is { Length: > 0 } repos
         && await RepoExclusion.IsExcludedAsync(body, repos, HookBudget.Remaining(processStart, command))) {
            return true;
        }

        if (profile?.ExcludedPaths is { Length: > 0 } paths) {
            try {
                var cwd = JsonNode.Parse(body)?["cwd"]?.GetValue<string>();

                if (PathExclusion.IsExcluded(cwd, paths)) return true;
            } catch {
                // Best effort
            }
        }

        return false;
    }

    internal static async Task<int> HandleCore(HttpClient client, AuthStatus authStatus, HookSpool spool,
        long processStart, string baseUrl, TextReader stdin, Task? updateCheckTask = null,
        Func<bool, CancellationToken, Task<HttpClient>>? memoryClientFactory = null,
        Func<SessionStartMemoryLeaseStore>? memoryStoreFactory = null,
        TextWriter? stdout = null) {
        // Hook stdout (the SessionStart hookSpecificOutput envelope / systemMessage nudge) goes to the
        // injected writer when provided, else the process Console. Injecting a writer lets tests capture
        // it WITHOUT redirecting the process-global Console.Out — so a concurrently-running test writing
        // to Console can't leak into the capture (the flake this fixes).
        var writer = stdout ?? Console.Out;
        var body = await stdin.ReadToEndAsync();

        var eventName = ExtractEventName(body);
        string? nativeSessionId = null;
        try { nativeSessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>(); } catch { }

        if (eventName is null) {
            Console.Error.WriteLine("kcap hook --claude: missing hook_event_name in payload");
            return 1;
        }

        // Normalize Claude's PascalCase event names to the kebab-case server
        // route convention (`SessionStart` → `session-start`).
        var command = ToKebab(eventName);

        // Inject home_dir and agent_host_id into all hook payloads, and normalize IDs.
        try {
            var node = JsonNode.Parse(body);

            if (node is not null) {
                NormalizeGuidField(node, "session_id");
                NormalizeGuidField(node, "agent_id");

                node["home_dir"] = PathHelpers.HomeDirectory;

                var agentHostId = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");

                if (agentHostId is not null) {
                    node["agent_host_id"] = agentHostId;
                }

                body = node.ToJsonString();
            }
        } catch {
            // Best effort — don't fail the hook if JSON parsing fails.
        }

        // Check if session is disabled — skip all server communication.
        try {
            var disabledSessionId = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();

            if (disabledSessionId is not null && DisabledSessions.IsDisabled(disabledSessionId)) {
                if (command == "session-end") {
                    DisabledSessions.RemoveMarker(disabledSessionId);
                }

                return 0;
            }
        } catch {
            // Best effort — don't fail if JSON parsing fails.
        }

        // PermissionRequest has its own handler path (daemon bridge / fire-and-forget). The
        // repo/path exclusion gates run further below (AFTER this dispatch), so the watcher
        // self-heal — which would start uploading the transcript — must apply them HERE first;
        // otherwise a permission prompt in an excluded project spawns a watcher that
        // session-start intentionally skipped (data leak). Disabled sessions already returned
        // above. The permission record/long-poll itself is unaffected: hosted agents need the
        // decision regardless of exclusion.
        if (command == "permission-request") {
            var permProfile = await AppConfig.GetActiveProfileAsync();
            var selfHeal    = !await IsSessionExcludedAsync(permProfile, body, processStart, command);

            return await PermissionRequestCommand.Handle(baseUrl, body, selfHeal, stdout);
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
        // For session-start, session-end and subagent-stop, defer enrichment so a slow git/gh
        // probe never delays transcript-capture start (watcher-FIRST): session-end/subagent-stop
        // run it in parallel with the watcher kill, and session-start awaits it INSIDE its block
        // after EnsureWatcherRunning. Other commands enrich inline.
        Task<string>? deferredRepoTask = null;

        // detectPullRequest:false everywhere: a live `gh pr view` / `glab` round-trip (~600ms to
        // GitHub) is the single biggest client cost on the hook path and would push the facts
        // envelope past Claude's 5s SessionStart timeout. PR info is not needed here — the watcher
        // runs its own DetectRepositoryAsync (with PR detection) and backfills it independently.
        if (command == "session-start") {
            // Awaited INSIDE the session-start block after EnsureWatcherRunning so it never delays
            // transcript-capture start.
            deferredRepoTask = RepositoryDetection.EnrichWithRepositoryInfo(body, HookBudget.Remaining(processStart, command), detectPullRequest: false);
        } else if (command is "session-end" or "subagent-stop") {
            // Budgeted so a slow git probe can't push the bounded POST/spool path past the hook
            // deadline. The await below is also budget-bounded as a hard backstop.
            deferredRepoTask = RepositoryDetection.EnrichWithRepositoryInfo(body, HookBudget.Remaining(processStart, command), detectPullRequest: false);
        } else {
            body = await RepositoryDetection.EnrichWithRepositoryInfo(body, detectPullRequest: false);
        }

        // Resolve the V2 profile once for repo/path exclusion and
        // default_visibility injection. Reading these off the legacy top-level
        // LegacyV1Config silently misses v2 settings (the fields live under
        // the active profile), so per-profile `excluded_repos` / `private`
        // visibility were being ignored.
        var activeProfile = await AppConfig.GetActiveProfileAsync();

        // Silently exit for excluded repos/paths (see IsSessionExcludedAsync).
        if (await IsSessionExcludedAsync(activeProfile, body, processStart, command)) {
            return 0;
        }

        // Auth lapsed: do not POST (server would 401) and do not drain (a 401 would Drop the
        // spool backlog). Exit cleanly (0) so Claude shows no per-turn error banner; nudge once on
        // session-start via a systemMessage (shown to the user, not injected into the model context).
        if (authStatus is AuthStatus.Expired or AuthStatus.NotAuthenticated) {
            if (command == "session-start") {
                var notice = new JsonObject {
                    ["systemMessage"] = authStatus == AuthStatus.Expired
                        ? "[kcap] Authentication expired — session recording is paused. Run 'kcap login' to resume."
                        : "[kcap] Not authenticated — session recording is off. Run 'kcap login' to start recording."
                };
                writer.WriteLine(notice.ToJsonString());
            }
            return 0;
        }

        // Drain stranded lifecycle events before handling the fresh one. Current session
        // first so a stranded session-start replays before this session's session-end.
        try {
            var drainBudget = TimeSpan.FromMilliseconds(Math.Min(2000, HookBudget.Remaining(processStart, command).TotalMilliseconds));
            var curSid      = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
            if (drainBudget > TimeSpan.Zero)
                await spool.DrainAllAsync(curSid, ClaudePoster(client, baseUrl, drainBudget), drainBudget, CancellationToken.None);
        } catch { /* fail-open */ }

        // default_visibility and plan_content injection for session-start happen INSIDE the
        // session-start block below, after EnsureWatcherRunning and the deferred repo enrichment
        // await, so the watcher (transcript capture) is never delayed by them or by a slow probe.

        // For session-end and subagent-stop: kill watcher BEFORE posting hook
        // so transcript is fully drained before server computes stats.
        switch (command) {
            case "session-end": {
                try {
                    var node           = JsonNode.Parse(body);
                    var sessionId      = node?["session_id"]?.GetValue<string>();
                    var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                    if (sessionId is not null) {
                        // Clamp the pre-drain cap so it cannot consume the entire remaining budget
                        // that the bounded POST needs. Use whichever is smaller.
                        var remaining     = HookBudget.Remaining(processStart, "session-end");
                        var effectiveCap  = TimeSpan.FromMilliseconds(
                            Math.Min(PreHookDrainCap.TotalMilliseconds, remaining.TotalMilliseconds));

                        var drained = await TimeBudget.RunCappedAsync(
                            async () => {
                                await WatcherManager.KillWatcher(sessionId);

                                if (transcriptPath is not null) {
                                    await WatcherManager.InlineDrainAsync(baseUrl, sessionId, transcriptPath, agentId: null);
                                }
                            },
                            effectiveCap
                        );

                        if (!drained) {
                            await Console.Error.WriteLineAsync(
                                $"[kcap] session-end pre-drain cap ({effectiveCap.TotalSeconds:0.#}s) elapsed; proceeding to POST. "
                              + $"Transcript tail may be incomplete — recoverable via: kcap import --session {sessionId}"
                            );
                        }
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[kcap] session-end pre-hook failed: {ex.Message}");
                }

                body = await AwaitEnrichmentWithinBudget(deferredRepoTask!, body, HookBudget.Remaining(processStart, command));

                break;
            }
            case "subagent-stop": {
                try {
                    var node           = JsonNode.Parse(body);
                    var sessionId      = node?["session_id"]?.GetValue<string>();
                    var agentId        = node?["agent_id"]?.GetValue<string>();
                    var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                    if (sessionId is not null && agentId is not null) {
                        // Clamp the pre-drain cap so it cannot consume the entire remaining budget
                        // that the bounded POST needs (mirrors the session-end fix). Reserve at
                        // least Safety (1.5s) for the bounded POST so a slow drain doesn't starve
                        // it entirely. Use whichever is smallest.
                        var remaining    = HookBudget.Remaining(processStart, "subagent-stop");
                        var effectiveCap = TimeSpan.FromMilliseconds(
                            Math.Max(0, Math.Min(PreHookDrainCap.TotalMilliseconds,
                                remaining.TotalMilliseconds - HookBudget.Safety.TotalMilliseconds)));

                        var drained = await TimeBudget.RunCappedAsync(
                            async () => {
                                await WatcherManager.KillWatcher($"{sessionId}-{agentId}");

                                if (transcriptPath is not null) {
                                    var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                                    var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                                    await WatcherManager.InlineDrainAsync(baseUrl, sessionId, agentTranscriptPath, agentId);
                                }
                            },
                            effectiveCap
                        );

                        if (!drained) {
                            await Console.Error.WriteLineAsync(
                                $"[kcap] subagent-stop pre-drain cap ({effectiveCap.TotalSeconds:0.#}s) elapsed; proceeding to POST"
                            );
                        }
                    }
                } catch (Exception ex) {
                    Console.Error.WriteLine($"[kcap] subagent-stop pre-hook failed: {ex.Message}");
                }

                body = await AwaitEnrichmentWithinBudget(deferredRepoTask!, body, HookBudget.Remaining(processStart, command));

                break;
            }
        }

        // Dedicated bounded path for session-start: spawn the watcher FIRST (transcript capture
        // must never be lost even if the POST fails), then a single bounded POST, spool on
        // transient failure, and emit the context envelope + plan-content POST on success.
        if (command == "session-start") {
            var startNode      = JsonNode.Parse(body);
            var sessionId      = startNode?["session_id"]?.GetValue<string>();
            var transcriptPath = startNode?["transcript_path"]?.GetValue<string>();
            var sessionCwd     = startNode?["cwd"]?.GetValue<string>();
            var source         = startNode?["source"]?.GetValue<string>();
            var isResumeOrCompact = source is not null &&
                (source.Equals("resume", StringComparison.OrdinalIgnoreCase) ||
                 source.Equals("compact", StringComparison.OrdinalIgnoreCase));

            // 1. Capture never lost: spawn the watcher before any slow git/gh/POST.
            //    Idempotent — safe to call even if the POST subsequently fails.
            if (sessionId is not null && transcriptPath is not null) {
                await WatcherManager.EnsureWatcherRunning(
                    baseUrl, sessionId, transcriptPath,
                    agentId: null, cwd: sessionCwd, skipTitle: isResumeOrCompact);
            }

            // Now that the watcher is running, await the deferred repo enrichment (a slow git/gh
            // probe could not have delayed capture start) and then inject default_visibility +
            // plan_content onto the enriched body before the POST.
            body = await deferredRepoTask!;

            // best-effort git-root discovery for the session's cwd, fed to the server's
            // plan-artifact discovery so a repo-file plan/spec found at the workspace root can be
            // attributed even when cwd is a subdirectory. Fail-open: GitRepository.FindRoot swallows
            // I/O errors and returns null when no repo is found, in which case the field is simply
            // omitted (older servers ignore unknown fields regardless).
            if (sessionCwd is not null && GitRepository.FindRoot(sessionCwd) is { } workspaceRoot) {
                try {
                    var node = JsonNode.Parse(body);

                    if (node is not null) {
                        node["workspace_root"] = workspaceRoot;
                        body                    = node.ToJsonString();
                    }
                } catch {
                    // Best effort
                }
            }

            // Inject default_visibility from the active V2 profile. The legacy top-level
            // LegacyV1Config.DefaultVisibility shape is not populated by v2 configs (the field
            // lives under the profile), so reading it there silently fell back to "org_public"
            // and ignored per-profile `private` settings.
            if (activeProfile?.DefaultVisibility is { } vis) {
                try {
                    var node = JsonNode.Parse(body);

                    if (node is not null) {
                        node["default_visibility"] = vis;
                        body                       = node.ToJsonString();
                    }
                } catch {
                    // Best effort
                }
            }

            // Read plan file if slug is known and inject plan_content into payload.
            var planContentInjected = false;

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
                // Best effort
            }

            // Ordering guard: if this session's backlog couldn't fully drain, spool the fresh
            // session-start so a stranded session-start always reaches the server first.
            if (CurrentSessionHasBacklog(spool, sessionId)) {
                if (sessionId is not null) {
                    spool.Append(sessionId, "session-start", body);
                    await Console.Error.WriteLineAsync($"[kcap] session-start spooled (ordering guard); will retry on the next kcap hook ({sessionId})");
                }
                return 0;
            }

            // kick off the team-memory index fetch in PARALLEL with the hook POST so
            // it adds no latency to the critical path. Fully best-effort / fail-open: any failure,
            // a 401, or a budget overrun yields a null fragment and nothing is injected. Started
            // after the ordering-guard / backlog returns above so a spooled session-start doesn't
            // pay for a fetch it won't use.
            var memoryDisabled = AppConfig.ResolvedProfile?.Profile?.DisableMemoryIndex is true;
            var lifecycleReason = source?.ToLowerInvariant() switch {
                "resume" => SessionLifecycleReason.Resume,
                "reopen" => SessionLifecycleReason.Reopen,
                "fork" => SessionLifecycleReason.Fork,
                "compact" => SessionLifecycleReason.Compact,
                _ => SessionLifecycleReason.New
            };
            var memoryIndexTask = StartMemoryIndexTask(
                client, baseUrl, nativeSessionId, sessionCwd, memoryDisabled, lifecycleReason,
                HookBudget.Remaining(processStart, "session-start"), memoryClientFactory, memoryStoreFactory);

            // 2. Single bounded POST — keep resp alive to read the response body for the
            //    context-envelope emission and plan-content POST on success.
            var remaining = HookBudget.Remaining(processStart, "session-start");
            HttpResponseMessage? resp = null;
            try {
                if (remaining > TimeSpan.Zero) {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await client.PostOnceAsync($"{baseUrl}/hooks/session-start", content, remaining, CancellationToken.None);
                }
            } catch { resp = null; }

            if (resp is null || !resp.IsSuccessStatusCode) {
                var permanent = resp is not null && (int)resp.StatusCode is < 500 and not 408 and not 429;
                resp?.Dispose();
                if (!permanent && sessionId is not null) spool.Append(sessionId, "session-start", body);
                return 0;
            }

            // resp is 2xx — read the body ONCE for the envelope + plan-content emission.
            JsonNode? responseNode = null;
            try {
                var responseBody = await resp.Content.ReadAsStringAsync();
                responseNode = JsonNode.Parse(responseBody);
            } catch {
                // Best effort — envelope is optional; don't fail the hook.
            }

            // Plan-content POST from response-resolved slug (only if not already injected).
            if (responseNode is not null && !planContentInjected && sessionId is not null) {
                try {
                    var resolvedSlug = responseNode["slug"]?.GetValue<string>();

                    if (resolvedSlug is not null) {
                        var planContent = ReadPlanFile(resolvedSlug);

                        if (planContent is not null) {
                            await PostPlanContentAsync(client, baseUrl, sessionId, planContent);
                        }
                    }
                } catch {
                    // Best effort
                }
            }

            // Context-envelope emission (lessons/version-nudge).
            if (responseNode is not null) {
                try {
                    var disabled        = AppConfig.ResolvedProfile?.Profile?.DisableSessionGuidelines is true;
                    var lessonsFragment = SessionGuidelinesEmitter.BuildFragment(responseNode, disabled);
                    var nudgeFragment   = VersionNudgeEmitter.BuildFragment(responseNode, CapacitorVersion.CurrentDisplay());
                    // join the parallel memory-index fetch, bounded by the remaining
                    // hook budget so a slow fetch can't delay the hook (fail-open → null).
                    var memoryFragment = await AwaitMemoryFragmentAsync(memoryIndexTask, processStart);

                    var envelope = SessionStartAdditionalContext.BuildEnvelope(lessonsFragment, nudgeFragment, memoryFragment);

                    if (envelope is not null) {
                        writer.WriteLine(envelope);
                    }
                } catch {
                    // Best effort — never break session capture for hook output emission.
                }
            }

            resp.Dispose();

            if (updateCheckTask is not null) {
                await updateCheckTask;
            }

            return 0;
        }

        // Dedicated bounded POST for session-end: single attempt clamped to the remaining
        // hook budget, spools on transient failure, and checks generate_whats_done on success.
        // Other commands continue through the shared PostWithRetryAsync path below.
        if (command == "session-end") {
            // Parse once: stamp ended_at and extract sessionId in a single pass.
            string? sessionId = null;
            try {
                var node = JsonNode.Parse(body);
                sessionId = node?["session_id"]?.GetValue<string>();
                if (node is not null) {
                    node["ended_at"] = DateTimeOffset.UtcNow.ToString("O");
                    body             = node.ToJsonString();
                }
            } catch { }

            // Ordering guard: if this session's backlog couldn't fully drain, spool the fresh
            // session-end so a stranded session-start always reaches the server before it.
            if (CurrentSessionHasBacklog(spool, sessionId)) {
                if (sessionId is not null) {
                    spool.Append(sessionId, "session-end", body);
                    await Console.Error.WriteLineAsync($"[kcap] session-end spooled (ordering guard); will retry on the next kcap hook ({sessionId})");
                }
                return 0;
            }

            var remaining  = HookBudget.Remaining(processStart, "session-end");
            HttpResponseMessage? resp = null;
            try {
                if (remaining > TimeSpan.Zero) {
                    using var content = new StringContent(body, Encoding.UTF8, "application/json");
                    resp = await client.PostOnceAsync($"{baseUrl}/hooks/session-end", content, remaining, CancellationToken.None);
                }
            } catch { resp = null; }

            if (resp is null || !resp.IsSuccessStatusCode) {
                var permanent = resp is not null && (int)resp.StatusCode is < 500 and not 408 and not 429;
                resp?.Dispose();
                if (!permanent) {
                    if (sessionId is not null) {
                        spool.Append(sessionId, "session-end", body);
                        await Console.Error.WriteLineAsync($"[kcap] session-end spooled; will retry on the next kcap hook ({sessionId})");
                    } else {
                        await Console.Error.WriteLineAsync("[kcap] session-end transient failure but session_id missing — cannot spool; event dropped");
                    }
                }
                return 0;
            }

            try {
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                if (node?["generate_whats_done"]?.GetValue<bool>() == true && sessionId is not null)
                    WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sessionId);
            } catch { }
            resp.Dispose();
            return 0;
        }

        // Dedicated bounded POST for the per-agent subagent-stop: a single attempt clamped to the
        // remaining hook budget that spools on transient failure, so a dropped SubagentCompleted is
        // replayed on the next hook. Only the stop carrying agent_id maps to a completion;
        // without it, fall through to the shared best-effort path (behavior unchanged).
        if (command == "subagent-stop") {
            string? sessionId = null, agentId = null;
            try {
                var node  = JsonNode.Parse(body);
                sessionId = node?["session_id"]?.GetValue<string>();
                agentId   = node?["agent_id"]?.GetValue<string>();
            } catch { }

            if (sessionId is not null && agentId is not null) {
                // Ordering guard: if this session's backlog couldn't fully drain, spool the fresh
                // subagent-stop so a stranded session-start reaches the server before it.
                if (CurrentSessionHasBacklog(spool, sessionId)) {
                    spool.Append(sessionId, "subagent-stop", body);
                    await Console.Error.WriteLineAsync($"[kcap] subagent-stop spooled (ordering guard); will retry on the next kcap hook ({sessionId}/{agentId})");
                    return 0;
                }
                var remaining = HookBudget.Remaining(processStart, command);
                HttpResponseMessage? resp = null;
                try {
                    if (remaining > TimeSpan.Zero) {
                        using var content = new StringContent(body, Encoding.UTF8, "application/json");
                        resp = await client.PostOnceAsync($"{baseUrl}/hooks/subagent-stop", content, remaining, CancellationToken.None);
                    }
                } catch { resp = null; }

                if (resp is null || !resp.IsSuccessStatusCode) {
                    var permanent = resp is not null && (int)resp.StatusCode is < 500 and not 408 and not 429;
                    resp?.Dispose();
                    if (!permanent) {
                        spool.Append(sessionId, "subagent-stop", body);
                        await Console.Error.WriteLineAsync($"[kcap] subagent-stop spooled; will retry on the next kcap hook ({sessionId}/{agentId})");
                    }
                    return 0;
                }

                resp.Dispose();
                return 0;
            }
        }

        using var sharedContent = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;

        try {
            response = await client.PostWithRetryAsync($"{baseUrl}/hooks/{command}", sharedContent);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);

            return 1;
        }

        if (!response.IsSuccessStatusCode) {
            Console.Error.WriteLine($"HTTP {(int)response.StatusCode}");

            return 1;
        }

        switch (command) {
            case "subagent-start": {
                var node           = JsonNode.Parse(body);
                var sessionId      = node?["session_id"]?.GetValue<string>();
                var agentId        = node?["agent_id"]?.GetValue<string>();
                var transcriptPath = node?["transcript_path"]?.GetValue<string>();

                if (sessionId is not null && agentId is not null && transcriptPath is not null) {
                    var sessionDir          = Path.ChangeExtension(transcriptPath, null);
                    var agentTranscriptPath = Path.Combine(sessionDir, "subagents", $"agent-{agentId}.jsonl");
                    await WatcherManager.EnsureWatcherRunning(baseUrl, $"{sessionId}-{agentId}", agentTranscriptPath, agentId, sessionId);
                }

                break;
            }
            case "notification" or "stop": {
                var node           = JsonNode.Parse(body);
                var sessionId      = node?["session_id"]?.GetValue<string>();
                var transcriptPath = node?["transcript_path"]?.GetValue<string>();
                var sessionCwd     = node?["cwd"]?.GetValue<string>();

                if (sessionId is not null && transcriptPath is not null) {
                    await WatcherManager.EnsureWatcherRunning(baseUrl, sessionId, transcriptPath, agentId: null, cwd: sessionCwd);
                }

                break;
            }
        }

        if (updateCheckTask is not null) {
            await updateCheckTask;
        }

        return 0;
    }

    /// <summary>
    /// Extracts <c>hook_event_name</c> from a hook payload. Returns null if
    /// the payload is malformed or the field is missing. Best-effort —
    /// errors are swallowed so a malformed payload doesn't crash the CLI.
    /// </summary>
    static string? ExtractEventName(string body) {
        try {
            return JsonNode.Parse(body)?["hook_event_name"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Normalizes a hook event name to kebab-case: <c>SessionStart</c>,
    /// <c>session_start</c>, and <c>session-start</c> all return
    /// <c>session-start</c>.
    /// </summary>
    static string ToKebab(string s) {
        if (string.IsNullOrEmpty(s)) return s;

        var sb = new StringBuilder(s.Length + 4);

        for (var i = 0; i < s.Length; i++) {
            var c = s[i];

            if (c == '_') {
                sb.Append('-');
            } else if (char.IsUpper(c)) {
                if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            } else {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = node[fieldName]?.GetValue<string>();

        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    static Task<string?> StartMemoryIndexTask(
        HttpClient sharedClient,
        string baseUrl,
        string? nativeSessionId,
        string? cwd,
        bool disabled,
        SessionLifecycleReason reason,
        TimeSpan budget,
        Func<bool, CancellationToken, Task<HttpClient>>? memoryClientFactory,
        Func<SessionStartMemoryLeaseStore>? memoryStoreFactory) {
        if (disabled || string.IsNullOrEmpty(nativeSessionId) || budget <= TimeSpan.Zero)
            return Task.FromResult<string?>(null);

        // The memory subsystem is optional. Keep construction itself inside the fail-open
        // boundary: store-root validation and injected factories can throw synchronously.
        try {
            var store = memoryStoreFactory?.Invoke() ?? new SessionStartMemoryLeaseStore();
            var provider = new SessionStartMemoryContextProvider(
                new SessionStartMemoryScopeResolver(),
                memoryClientFactory ?? ((_, _) => Task.FromResult(sharedClient)),
                disposeClients: memoryClientFactory is not null);
            return new SessionStartMemoryOrchestrator(store, provider).GetFragmentAsync(
                new SessionMemoryLifecycle(SessionStartHarness.Claude, nativeSessionId, null,
                    IsTopLevel: true, ClassificationAuthoritative: true, reason,
                    CallbackMayRepeat: false),
                new SessionStartMemoryContextRequest(baseUrl, cwd, disabled, budget, CancellationToken.None));
        } catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) {
            return Task.FromResult<string?>(null);
        }
    }

    static async Task<string?> AwaitMemoryFragmentAsync(Task<string?> task, long processStart) {
        try {
            var budget = HookBudget.Remaining(processStart, "session-start");
            if (budget <= TimeSpan.Zero) return task.IsCompletedSuccessfully ? task.Result : null;
            return await task.WaitAsync(budget);
        } catch { return null; }
    }

    internal static string BuildMemoryIndexUrl(string baseUrl, string? repoHash, string? machineId) =>
        SessionStartMemoryContextProvider.BuildUrl(baseUrl, new SessionStartMemoryScope(repoHash, machineId));

    static string? ReadPlanFile(string slug) {
        var planPath = Path.Combine(ClaudePaths.Plans, $"{slug}.md");

        try {
            return File.Exists(planPath) ? File.ReadAllText(planPath) : null;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] Failed to read plan file at {planPath}: {ex.Message}");

            return null;
        }
    }

    static async Task PostPlanContentAsync(HttpClient httpClient, string url, string sessionId, string planContent) {
        var       obj         = new JsonObject { ["plan_content"] = planContent };
        using var planPayload = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
        await httpClient.PostWithRetryAsync($"{url}/api/sessions/{sessionId}/plan", planPayload);
    }

    /// <summary>
    /// Returns a poster closure that POSTs a spooled entry to the server and maps the response
    /// to a <see cref="DrainOutcome"/>. On a successful <c>session-end</c> replay, handles the
    /// <c>generate_whats_done</c> side effect so it is not lost.
    /// </summary>
    static Func<string, string, Task<DrainOutcome>> ClaudePoster(HttpClient client, string baseUrl, TimeSpan perAttempt) =>
        async (route, body) => {
            try {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp    = await client.PostOnceAsync($"{baseUrl}/hooks/{route}", content, perAttempt, CancellationToken.None);
                if (!resp.IsSuccessStatusCode) {
                    var code = (int)resp.StatusCode;
                    return code is >= 500 or 408 or 429 ? DrainOutcome.TransientStop : DrainOutcome.Drop;
                }
                if (route == "session-end") {
                    try {
                        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                        var sid  = JsonNode.Parse(body)?["session_id"]?.GetValue<string>();
                        if (node?["generate_whats_done"]?.GetValue<bool>() == true && sid is not null)
                            WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sid);
                    } catch { }
                }
                return DrainOutcome.Delivered;
            } catch { return DrainOutcome.TransientStop; }
        };

    /// <summary>
    /// Returns true if the given session still has undelivered spool entries. Used as an ordering
    /// guard so a stranded session-start always reaches the server before its session-end.
    ///
    /// <para>Delegates to the public <see cref="HookSpool.HasBacklog"/> rather than
    /// re-implementing the file checks: the ordered drain (now running on every non-Codex hook,
    /// including <c>--claude</c>) can WITHHOLD a spooled session-end in the <c>.ordered-*</c> temp
    /// namespace pending the transcript tail. A stale private check that only looked at
    /// <c>{sid}.jsonl</c> / <c>{sid}.*.draining</c> would miss that withheld terminal and let a later
    /// Claude hook (e.g. subagent-stop) post directly, AHEAD of the still-withheld session-end —
    /// the exact cross-spool ordering violation Blockers 1/3 exist to prevent. <c>HasBacklog</c>
    /// covers all three namespaces; <see cref="CursorHookCommand"/> already routes through it.</para>
    /// </summary>
    static bool CurrentSessionHasBacklog(HookSpool spool, string? sid) =>
        sid is not null && spool.HasBacklog(sid);
}
