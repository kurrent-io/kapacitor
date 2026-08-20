using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Surface 3 (new-harness detection), hook carrier: stamps this machine's coding-agent inventory
/// onto a SessionStart hook body, so a machine with no daemon still reports it whenever a wired
/// harness is used. The value is the same <see cref="HarnessInventory"/> the daemon attaches to its
/// status report, serialized through the SAME <see cref="CapacitorJsonContext"/> so the two carriers
/// are byte-identical on the wire (<c>machine_id</c> travels inside the fragment). Computed fresh —
/// current-state metadata on an already-happening POST, so no throttle. Never throws: a probe
/// failure just omits the field (a nudge/inventory must never break a hook).
/// </summary>
static class SessionStartInventory {
    public static void Stamp(JsonObject body) {
        try {
            var inv  = HarnessInventory.EvaluateCurrent();
            var json = JsonSerializer.Serialize(inv, CapacitorJsonContext.Default.HarnessInventory);
            body["harness_inventory"] = JsonNode.Parse(json);
        } catch {
            // best-effort metadata — never break a hook
        }
    }
}
