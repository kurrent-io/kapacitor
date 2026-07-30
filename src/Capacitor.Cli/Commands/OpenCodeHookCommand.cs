using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Dispatcher for the SST OpenCode live-ingest plugin. OpenCode has no
/// shell hooks; the shipped <c>kcap.ts</c> plugin invokes:
///   <c>kcap hook --opencode --event session-start --session &lt;id&gt; --file &lt;path&gt; [--cwd &lt;cwd&gt;] [--model &lt;m&gt;] [--provider &lt;p&gt;] [--version &lt;v&gt;]</c>
///
/// Wire contract (mirrors <see cref="KiroHookCommand"/> — OpenCode likewise has no
/// session-end signal): session-start → POST /hooks/session-start/opencode, then
/// ensure the watcher is running (vendor=opencode) tailing the JSONL file the
/// plugin writes. The watcher owns session-end: <c>GetCodingAgentPid("opencode")</c>
/// passes the opencode pid as <c>--parent-pid</c>, so the watcher POSTs
/// /hooks/session-end/opencode when opencode exits. The plugin re-fires
/// session-start cheaply on each session.idle — the server's deterministic
/// lifecycle id collapses the repeats and <see cref="WatcherManager.EnsureWatcherRunning"/>
/// is a no-op once live.
///
/// Fail-open throughout — a kcap/server problem must never disrupt the OpenCode session.
/// </summary>
static class OpenCodeHookCommand {
    public static async Task<int> Handle(string baseUrl, string[] args) {
        var eventName = GetArg(args, "--event");
        if (string.IsNullOrWhiteSpace(eventName)) {
            Console.Error.WriteLine(
                "kcap hook --opencode requires --event <session-start> "
              + "(the kcap OpenCode plugin passes it; re-run: kcap plugin install --opencode)");
            return 1;
        }

        var sessionIdRaw = GetArg(args, "--session");
        if (string.IsNullOrWhiteSpace(sessionIdRaw)) return 0;

        // OpenCode ids ("ses_…") carry no dashes; keep the raw form for the server
        // payload and a dashless form for local keys (mirrors every vendor dispatcher).
        var sessionId = sessionIdRaw.Replace("-", "");

        var file = GetArg(args, "--file");
        if (string.IsNullOrWhiteSpace(file)) return 0; // no transcript path — nothing to tail

        var cwd = GetArg(args, "--cwd");

        // Mirror the disabled-session fast path: `kcap disable` must stop every POST
        // and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId)) return 0;

        // Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(PathHelpers.ConfigPath("spool"));

        var activeProfile = await AppConfig.GetActiveProfileAsync();

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths)) {
            return 0;
        }

        // session-start is the only actionable event; the watcher owns session-end.
        return eventName == "session-start"
            ? await HandleSessionStart(baseUrl, sessionId, sessionIdRaw, file, cwd, args, activeProfile, spool)
            : 0;
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
        if (GetArg(args, "--model")    is { } model)    forwarded["model"]            = model;
        if (GetArg(args, "--provider") is { } provider) forwarded["provider_id"]      = provider;
        if (GetArg(args, "--version")  is { } version)  forwarded["opencode_version"] = version;

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Stamp default visibility BEFORE enrichment so it survives the JsonString
        // round-trip (same rationale as the Kiro/Copilot dispatchers); null lets the
        // server fall back to org-repo visibility.
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId);
            return 0;
        }

        // Spawn-before-post: capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. On a real
        // failure PostOrSpoolAsync already logged to stderr; a lapse or transient outage instead
        // durably spools the payload for a later drain pass. Only a permanent failure skips the
        // watcher this firing; the next session.idle retries.
        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            baseUrl, "session-start/opencode", enriched, "opencode-hook",
            spool, sessionId, route: "session-start/opencode");

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, baseUrl)) return 0;

        await WatcherManager.EnsureWatcherRunning(
            baseUrl, sessionId, file,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "opencode"
        );

        return 0;
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
