using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Services;

/// <summary>
/// Unit-identity resolution shared by <c>DaemonCommands</c>'s service verbs and
/// <see cref="ServiceVerify"/>'s gate: the daemon binary this install ships
/// (<see cref="ResolveDaemonBinary"/>) and the config.json path implied by a unit's baked
/// <c>KCAP_CONFIG_DIR</c> (<see cref="ConfigPathFromUnitEnv"/>). Neither method is itself a
/// decision — each caller wraps its own failure contract on top (UX-only fail-soft-to-null in
/// <c>DaemonCommands</c>, fail-closed to <c>StartGateReason.EvidenceUnreadable</c> in the gate).
/// </summary>
static class UnitIdentity {
    /// <summary>Resolve the kcap-daemon executable shipped alongside this binary.</summary>
    public static string? ResolveDaemonBinary() {
        var dir     = AppContext.BaseDirectory;
        var ext     = OperatingSystem.IsWindows() ? ".exe" : "";
        var sibling = Path.Combine(dir, $"kcap-daemon{ext}");

        return File.Exists(sibling) ? sibling : null;
    }

    /// <summary>config.json path for a baked <c>KCAP_CONFIG_DIR</c>, or the default config root
    /// when the unit baked none.</summary>
    public static string ConfigPathFromUnitEnv(IReadOnlyDictionary<string, string> unitEnv) =>
        unitEnv.TryGetValue("KCAP_CONFIG_DIR", out var dir) && !string.IsNullOrEmpty(dir)
            ? Path.Combine(dir, "config.json")
            : AppConfig.GetConfigPath();
}
