using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Tells the daemon hosting this agent that its turn ended (the user's move) or that a new one
/// began, so the daemon's own status surfaces show a PTY vendor's wait the way an ACP runtime's
/// turn end already does. Only a daemon-spawned agent has both <c>KCAP_AGENT_ID</c> and a
/// loopback <c>KCAP_DAEMON_URL</c>; for anything else this is a no-op. Best effort on a short
/// cap, and never past what the hook may still spend: the daemon is local, and a wedged one must
/// not push the hook's real work past the host's kill.
/// </summary>
internal static class DaemonInputWaitRelay {
    internal static readonly TimeSpan Cap = TimeSpan.FromSeconds(1);

    public static async Task NotifyAsync(string vendor, string? sessionId, string? cwd, bool waiting, TimeSpan budget) {
        var cap = budget < Cap ? budget : Cap;
        if (cap <= TimeSpan.Zero) return;
        if (HookAgentId.FromEnvironment() is not { } agentId) return;
        if (!DaemonBridgeUrl.TryParseLoopback(Environment.GetEnvironmentVariable("KCAP_DAEMON_URL"), out var bridge)) return;

        var payload = new JsonObject {
            ["session_id"] = sessionId,
            ["agent_id"]   = agentId,
            ["cwd"]        = cwd,
            ["waiting"]    = waiting,
        };

        try {
            using var client  = new HttpClient { Timeout = cap };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using var _       = await client.PostAsync($"{bridge}/{vendor}/input-wait", content);
        } catch {
            // A display hint, never a hook outcome.
        }
    }
}
