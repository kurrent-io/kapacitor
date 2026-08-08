namespace Capacitor.Cli.Core.Telemetry;

/// <summary>Outcome of opt-out resolution. <paramref name="Reason"/> names the winning
/// source so `kcap config show` and KCAP_TELEMETRY_DEBUG can explain themselves.</summary>
public readonly record struct TelemetryDecision(bool Enabled, string Reason);

/// <summary>
/// Resolves whether telemetry is on. Pure over an injected environment so the precedence
/// table is testable without mutating the real process environment.
///
/// Precedence, highest first: KCAP_TELEMETRY (explicit, either direction) > DO_NOT_TRACK >
/// persisted config > enabled. KCAP_TELEMETRY deliberately outranks DO_NOT_TRACK in both
/// directions: it is the kcap-specific, deliberate statement, and the only way a user with a
/// blanket DO_NOT_TRACK in their shell profile can opt back in.
/// </summary>
public static class TelemetrySettings {
    public static TelemetryDecision Resolve(IReadOnlyDictionary<string, string?> env, bool? persisted) {
        if (TryReadBool(env, "KCAP_TELEMETRY", out var explicitChoice))
            return new TelemetryDecision(explicitChoice, "KCAP_TELEMETRY");

        if (IsDoNotTrackSet(env)) return new TelemetryDecision(false, "DO_NOT_TRACK");

        if (persisted is { } stored) return new TelemetryDecision(stored, "config");

        return new TelemetryDecision(true, "default");
    }

    /// <summary>Live resolution against the real environment and the persisted flag.</summary>
    public static TelemetryDecision Resolve(bool? persisted) =>
        Resolve(ReadEnv(), persisted);

    static IReadOnlyDictionary<string, string?> ReadEnv() =>
        new Dictionary<string, string?> {
            ["KCAP_TELEMETRY"] = Environment.GetEnvironmentVariable("KCAP_TELEMETRY"),
            ["DO_NOT_TRACK"]   = Environment.GetEnvironmentVariable("DO_NOT_TRACK"),
        };

    // DO_NOT_TRACK is "set to anything meaningful except 0". The consoledonottrack.com
    // convention is presence-based, but treating an explicit "0" as opt-out would make it
    // impossible to neutralise an inherited value.
    static bool IsDoNotTrackSet(IReadOnlyDictionary<string, string?> env) =>
        env.TryGetValue("DO_NOT_TRACK", out var raw)
        && !string.IsNullOrWhiteSpace(raw)
        && raw.Trim() != "0";

    static bool TryReadBool(IReadOnlyDictionary<string, string?> env, string key, out bool value) {
        value = false;
        if (!env.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw)) return false;

        switch (raw.Trim().ToLowerInvariant()) {
            case "1" or "on" or "true" or "yes":  value = true;  return true;
            case "0" or "off" or "false" or "no": value = false; return true;
            default:                              return false;   // unparseable → fall through
        }
    }
}
