using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services;

public sealed record HarnessOption(string Vendor, string Label, string TransportFamily, bool Available);

/// The picker's vendor list. Availability comes from what the daemon advertises
/// (DaemonInfoDto.SupportedVendors), never from a version check — a vendor auto-update must not
/// silently withdraw a harness. A vendor the daemon advertises but this build has never heard of
/// is still offered, listed under its raw token: the daemon is the authority on what it can host.
public static class HostedHarnessCatalog {
    // Transport family for each vendor: how the daemon hosts it (pty, acp, or rpc).
    // Vendors absent from this map default to "rpc" — which reads as "chat" in the picker, true or
    // not, so HostedHarnessCatalogTests pins every vendor in Core's HarnessCatalog to an entry
    // here. The fallback stays for a vendor only the DAEMON knows about; it is not a licence to
    // skip a tenth entry when one is added to Core.
    static readonly Dictionary<string, string> TransportFamilies = new(StringComparer.OrdinalIgnoreCase) {
        { "claude",      "pty" },
        { "codex",       "pty" },
        { "cursor",      "acp" },
        { "copilot",     "acp" },
        { "gemini",      "acp" },
        { "kiro",        "acp" },
        { "opencode",    "acp" },
        { "antigravity", "rpc" },
        { "pi",          "rpc" },
    };

    /// The vendors with an EXPLICIT family above — what the guard test reads, since Build's
    /// fallback makes an unmapped vendor indistinguishable from a mapped "rpc" one.
    internal static IReadOnlyCollection<string> MappedVendors => TransportFamilies.Keys;

    public static IReadOnlyList<HarnessOption> Build(string[]? supportedVendors) {
        // null = an older daemon that never sent the field: unknown, not empty.
        var advertised = supportedVendors is null
            ? null
            : new HashSet<string>(supportedVendors, StringComparer.OrdinalIgnoreCase);

        var options = HarnessCatalog.All
            .Select(k => new HarnessOption(
                k.VendorId,
                k.Label,
                TransportFamilies.TryGetValue(k.VendorId, out var family) ? family : "rpc",
                advertised?.Contains(k.VendorId) ?? true))
            .ToList();

        if (advertised is null) return options;

        var known = new HashSet<string>(HarnessCatalog.All.Select(k => k.VendorId), StringComparer.OrdinalIgnoreCase);
        foreach (var extra in supportedVendors!.Where(v => !known.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase))
            options.Add(new HarnessOption(extra, extra, "rpc", true));

        return options;
    }

    /// Display label for a vendor token: the option's Label when the list carries one, the raw
    /// token otherwise (before the first daemon snapshot, or a token this build has never heard
    /// of). Shared by the harness chip and the repository menu's remembered-harness pill.
    public static string LabelFor(IReadOnlyList<HarnessOption> options, string vendor) =>
        options.FirstOrDefault(o => string.Equals(o.Vendor, vendor, StringComparison.OrdinalIgnoreCase))?.Label ?? vendor;

    public static string DescriptionFor(HarnessOption option) => option.TransportFamily switch {
        "pty" => "PTY · terminal + chat",
        "acp" => "ACP · chat",
        _     => "chat",
    };

    /// Family for a vendor token, unmapped defaulting to "rpc" — the shared seam so
    /// the workspace never duplicates the private map.
    public static string FamilyFor(string vendor) =>
        TransportFamilies.TryGetValue(vendor, out var family) ? family : "rpc";

    /// The Terminal-tab gate: the daemon's has_terminal when present, the vendor
    /// family guess when an older daemon sent null.
    public static bool ShowsTerminal(bool? hasTerminal, string vendor) =>
        hasTerminal ?? FamilyFor(vendor) == "pty";

    /// Header family, corrected: has_terminal=false cannot distinguish acp/rpc/
    /// app-server, so only a CONFLICTING pty guess is overridden (to generic chat);
    /// an already-non-PTY family is preserved.
    public static string EffectiveFamily(bool? hasTerminal, string vendor) {
        var family = FamilyFor(vendor);
        return hasTerminal == false && family == "pty" ? "rpc" : family;
    }
}
