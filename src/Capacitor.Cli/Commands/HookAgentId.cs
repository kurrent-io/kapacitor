namespace Capacitor.Cli.Commands;

/// The hosted agent id every daemon-spawned agent exports; null outside one.
internal static class HookAgentId {
    public static string? FromEnvironment() {
        var id = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
