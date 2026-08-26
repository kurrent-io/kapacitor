namespace Capacitor.Cli.Core;

/// <summary>
/// The recovery a failed daemon mutation offers its caller (spec §3/§4 pinned tables). Shared by the
/// CLI's <c>daemon service ensure</c> ladder and the (retiring) desktop supervisor, so the pinned
/// token→surface mapping has exactly one home.
/// </summary>
public enum RecoverySurface { Takeover, Reinstall, Attention, Storage, None }

/// <summary>
/// Maps a machine-readable reason token to a recovery surface (spec §3/§4 pinned tables); unknown
/// tokens fail closed to Attention — a newer CLI's reason must not be destructively interpreted by
/// an older consumer.
/// </summary>
public static class ReasonRouting {
    public static RecoverySurface ForStartGate(string token) => token switch {
        "directive_missing" or "directive_invalid" or "identity_mismatch" or "foreign_binary" => RecoverySurface.Takeover,
        "package_inconsistent" => RecoverySurface.Reinstall,
        _ => RecoverySurface.Attention,
    };

    public static RecoverySurface ForDaemonStart(string token) => token switch {
        "package_inconsistent" => RecoverySurface.Reinstall,
        _ => RecoverySurface.Attention,
    };

    public static RecoverySurface ForBootRefusal(string token) => token switch {
        "server_expectation_mismatch" or "consent_seed_invalid" => RecoverySurface.Takeover,
        "consent_seed_unwritable" => RecoverySurface.Storage,
        _ => RecoverySurface.Attention,
    };
}
