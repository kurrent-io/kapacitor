using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Surface 3 hook carrier: stamps this machine's harness inventory onto a SessionStart body, so a
/// daemonless machine still reports it. Serialized through the same <see cref="CapacitorJsonContext"/>
/// as the daemon's copy, so both carriers are byte-identical. Never throws — a probe failure just
/// omits the field (must never break a hook).
/// </summary>
static class SessionStartInventory {
    public static void Stamp(JsonObject body, ConfigRoot config) {
        try {
            var inv  = HarnessInventory.EvaluateCurrent(config);
            var json = JsonSerializer.Serialize(inv, CapacitorJsonContext.Default.HarnessInventory);
            body["harness_inventory"] = JsonNode.Parse(json);
        } catch {
            // best-effort metadata — never break a hook
        }
    }
}
