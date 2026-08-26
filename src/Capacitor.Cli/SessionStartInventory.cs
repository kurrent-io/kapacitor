using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli;

/// <summary>
/// Surface 3 hook carrier: stamps this machine's harness inventory and platform onto a SessionStart
/// body, so a daemonless machine still reports them. The inventory is serialized through the same
/// <see cref="CapacitorJsonContext"/> as the daemon's copy, so both carriers are byte-identical.
/// Never throws — a probe failure just omits the field (must never break a hook).
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
        // The CLI's own OS, feeding the server's live applicability gate. Independent of the
        // inventory probe (its own best-effort boundary — an inventory failure must not cost the
        // platform axis), and deliberately no path/heuristic inference: omitted when unrecognized,
        // and unknown EXCLUDES platform-restricted facts server-side, which beats a wrong guess.
        try {
            if (HostPlatform.Normalized is { } platform) body["platform"] = platform;
        } catch {
            // best-effort metadata — never break a hook
        }
    }
}

/// <summary>The applicability gate's platform vocabulary: macos / linux / windows.</summary>
static class HostPlatform {
    public static string? Normalized =>
        OperatingSystem.IsMacOS()   ? "macos"
      : OperatingSystem.IsLinux()   ? "linux"
      : OperatingSystem.IsWindows() ? "windows"
      : null;
}
