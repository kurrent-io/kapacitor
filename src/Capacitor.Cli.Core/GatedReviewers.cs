namespace Capacitor.Cli.Core;

/// <summary>
/// One reviewer vendor that has BOTH an opt-out switch and an affirmable version floor.
///
/// <para><b>Lives in Core because three projects need the same list</b> and they cannot see each
/// other: the daemon reads <see cref="EnableEnvVar"/> to build its config, the CLI carries it into a
/// service unit, and the CLI's <c>daemon reviewer affirm</c> verb resolves a vendor by name.
/// <c>Capacitor.Cli</c> and <c>Capacitor.Cli.Daemon</c> both reference Core and neither references the
/// other, so Core is the only place a single list can sit.</para>
///
/// <para><b>Why a single list matters more than it used to.</b> These switches are opt-OUTs: unset
/// means the reviewer is enabled. So the variable is the operator's only lever for turning one off,
/// and a vendor that appears in the daemon's apply loop but not in the service-unit allowlist is a
/// reviewer that cannot be disabled on the supported install path — the exact hazard review raised.
/// While these were opt-INs the same drift meant a reviewer that could not be turned ON: annoying, and
/// safe. Two enumerations that must agree, with nothing making them, is the shape that produces that;
/// there is now one.</para>
/// </summary>
/// <param name="Vendor">The vendor token, as it appears on the wire and in <c>--vendor</c>.</param>
/// <param name="DefaultBinary">The binary looked up on PATH when <paramref name="PathEnvVar"/> is
/// unset.</param>
/// <param name="PathEnvVar">Overrides where the vendor binary is found.</param>
/// <param name="EnableEnvVar">The opt-OUT switch. Non-nullable deliberately: an entry here without one
/// would put a null into the service-unit allowlist, and there is no such thing as a gated reviewer
/// with no way to turn it off.</param>
public sealed record GatedReviewer(
        string Vendor, string DefaultBinary, string PathEnvVar, string EnableEnvVar);

/// <summary>The registry. Adding an entry here is what wires a new gated reviewer everywhere.</summary>
public static class GatedReviewers {
    /// <summary>
    /// Every reviewer with an opt-out switch and an affirmable floor.
    ///
    /// <para>Order is the order an operator sees in <c>--vendor</c> usage text; nothing depends on
    /// it.</para>
    /// </summary>
    public static readonly GatedReviewer[] All = [
        new("kiro",        "kiro-cli", "KCAP_KIRO_PATH",        "KCAP_KIRO_UNATTENDED_REVIEWER"),
        new("gemini",      "gemini",   "KCAP_GEMINI_PATH",      "KCAP_GEMINI_UNATTENDED_REVIEWER"),
        new("antigravity", "agy",      "KCAP_ANTIGRAVITY_PATH", "KCAP_ANTIGRAVITY_UNATTENDED_REVIEWER"),
        new("opencode",    "opencode", "KCAP_OPENCODE_PATH",    "KCAP_OPENCODE_UNATTENDED_REVIEWER")
    ];

    /// <summary>For usage text: <c>kiro | gemini | antigravity | opencode</c>.</summary>
    public static string VendorList => string.Join(" | ", All.Select(r => r.Vendor));

    /// <summary>Case-insensitive lookup, null when the vendor is not gated (or not a vendor).</summary>
    public static GatedReviewer? Resolve(string? vendor) =>
        All.FirstOrDefault(r => string.Equals(r.Vendor, vendor, StringComparison.OrdinalIgnoreCase));
}
