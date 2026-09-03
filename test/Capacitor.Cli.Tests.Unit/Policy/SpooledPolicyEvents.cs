namespace Capacitor.Cli.Tests.Unit.Policy;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

/// <summary>
/// Reads back what the policy emitter appended to a session's hook spool. Policy events are never
/// posted inline, so the spool file — not a stub server — is where a test observes them.
/// </summary>
/// <remarks>Addresses the spool file by the raw session id, which holds for the filename-safe ids
/// these tests use; <see cref="HookSpool"/> escapes anything else.</remarks>
static class SpooledPolicyEvents {
    /// <summary>The parsed bodies of a session's spooled entries for one route, in arrival order.</summary>
    public static List<JsonNode> For(ConfigRoot config, string sessionId, string route) {
        var bodies = new List<JsonNode>();
        var path = Path.Combine(config.Path("spool"), $"{sessionId}.jsonl");
        if (!File.Exists(path)) return bodies;

        foreach (var line in File.ReadAllLines(path)) {
            if (JsonNode.Parse(line) is not { } entry) continue;
            if (entry["route"]?.GetValue<string>() != route) continue;
            if (entry["body"]?.GetValue<string>() is { } raw && JsonNode.Parse(raw) is { } body) bodies.Add(body);
        }

        return bodies;
    }

    public static List<JsonNode> Decisions(ConfigRoot config, string sessionId) =>
        For(config, sessionId, "policy-decision");

    public static List<JsonNode> Snapshots(ConfigRoot config, string sessionId) =>
        For(config, sessionId, "policy-snapshot");
}
