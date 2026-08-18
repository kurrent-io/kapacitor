using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Dispatcher for the DeepSeek Harness (dsh) live-ingest plugin (AI-2020). dsh has
/// no shell hooks; the shipped Cordis plugin invokes:
///   <c>kcap hook --dsh --event session-start --session &lt;id&gt; --file &lt;session.jsonl&gt; [--cwd] [--model] [--provider] [--version]</c>
///   <c>kcap hook --dsh --event session-end   --session &lt;id&gt; --file &lt;session.jsonl&gt; [--reason] [--cwd]</c>
///
/// dsh is event-sourced: its on-disk <c>session.jsonl</c> IS its <c>SessionEvent</c>
/// stream, so the watcher tails <c>--file</c> directly (vendor=dsh) — no SDK fetch or
/// JSONL synthesis (unlike OpenCode). session-start POSTs /hooks/session-start/dsh and
/// ensures the watcher; session-end kill+drains the watcher (capped) then POSTs
/// /hooks/session-end/dsh so the server computes stats over the full transcript. The
/// watcher's parent-exit watchdog remains a backstop if the plugin never fires
/// session-end. Fail-open throughout — a kcap/server problem must never disrupt dsh.
/// </summary>
static class DshHookCommand {
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(8);

    public static async Task<int> Handle(string baseUrl, string[] args) {
        var eventName = GetArg(args, "--event");
        if (string.IsNullOrWhiteSpace(eventName)) {
            Console.Error.WriteLine(
                "kcap hook --dsh requires --event <session-start|session-end> "
              + "(the kcap dsh plugin passes it; re-run: kcap plugin install --dsh)");
            return 1;
        }

        var sessionIdRaw = GetArg(args, "--session");
        if (string.IsNullOrWhiteSpace(sessionIdRaw)) return 0;

        // Keep the raw id for the server payload and a dashless form for local keys
        // (mirrors every vendor dispatcher).
        var sessionId = sessionIdRaw.Replace("-", "");

        var file = GetArg(args, "--file");
        if (string.IsNullOrWhiteSpace(file)) return 0; // no transcript path — nothing to tail/drain

        var cwd = GetArg(args, "--cwd");

        // Disabled-session fast path: `kcap disable` must stop every POST and watcher restart.
        if (DisabledSessions.IsDisabled(sessionId)) return 0;

        var spool         = new HookSpool(PathHelpers.ConfigPath("spool"));
        var activeProfile = await AppConfig.GetActiveProfileAsync();

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths)) {
            return 0;
        }

        return eventName switch {
            "session-start" => await HandleSessionStart(baseUrl, sessionId, sessionIdRaw, file, cwd, args, activeProfile, spool),
            "session-end"   => await HandleSessionEnd(baseUrl, sessionId, sessionIdRaw, file, cwd, args, spool),
            _               => 0
        };
    }

    static async Task<int> HandleSessionStart(
            string    baseUrl,
            string    sessionId,
            string    sessionIdRaw,
            string    file,
            string?   cwd,
            string[]  args,
            Profile?  activeProfile,
            HookSpool spool
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionIdRaw,
            ["home_dir"]        = PathHelpers.HomeDirectory,
            ["started_at"]      = DateTimeOffset.UtcNow.ToString("O")
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }
        if (GetArg(args, "--model")    is { } model)    forwarded["model"]       = model;
        if (GetArg(args, "--provider") is { } provider) forwarded["provider"]    = provider;
        if (GetArg(args, "--version")  is { } version)  forwarded["dsh_version"] = version;

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Stamp default visibility BEFORE enrichment so it survives the JsonString round-trip
        // (same rationale as the OpenCode/Copilot dispatchers); null lets the server fall back
        // to org-repo visibility.
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId);
            return 0;
        }

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse / outage).
        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            baseUrl, "session-start/dsh", enriched, "dsh-hook",
            spool, sessionId, route: "session-start/dsh");

        if (!AgentHookPoster.ShouldSpawnAfter(outcome)) return 0;

        await WatcherManager.EnsureWatcherRunning(
            baseUrl, sessionId, file,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "dsh"
        );

        return 0;
    }

    static async Task<int> HandleSessionEnd(
            string    baseUrl,
            string    sessionId,
            string    sessionIdRaw,
            string    file,
            string?   cwd,
            string[]  args,
            HookSpool spool
        ) {
        // Kill watcher + inline-drain BEFORE the POST so the server computes stats over the
        // full transcript — capped so a slow drain can't starve the session-end POST (mirror
        // of the Copilot/Claude pre-drain cap).
        try {
            var drained = await TimeBudget.RunCappedAsync(
                async () => {
                    await WatcherManager.KillWatcher(sessionId);
                    await WatcherManager.InlineDrainAsync(baseUrl, sessionId, file, agentId: null, vendor: "dsh");
                },
                PreHookDrainCap
            );

            if (!drained) {
                await Console.Error.WriteLineAsync(
                    $"[kcap] dsh session-end pre-drain cap ({PreHookDrainCap.TotalSeconds:0}s) elapsed; proceeding to POST. "
                  + $"Transcript tail may be incomplete — recoverable via: kcap import --dsh"
                );
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] dsh session-end pre-hook failed: {ex.Message}");
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionIdRaw,
            ["reason"]          = GetArg(args, "--reason") ?? "idle",
            ["home_dir"]        = PathHelpers.HomeDirectory,
            ["ended_at"]        = DateTimeOffset.UtcNow.ToString("O")
        };

        if (cwd is not null) forwarded["cwd"] = cwd;

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            baseUrl, "session-end/dsh", forwarded.ToJsonString(), "dsh-hook",
            spool, sessionId, route: "session-end/dsh");

        return outcome == HookPostOutcome.Failed ? 1 : 0;
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
