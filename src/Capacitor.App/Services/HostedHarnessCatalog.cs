using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services;

public sealed record HarnessOption(string Vendor, string Label, string TransportFamily, bool Available);

/// The picker's vendor list. Availability comes from what the daemon advertises
/// (DaemonInfoDto.SupportedVendors), never from a version check — a vendor auto-update must not
/// silently withdraw a harness. A vendor the daemon advertises but this build has never heard of
/// is still offered, listed under its raw token: the daemon is the authority on what it can host.
public static class HostedHarnessCatalog {
    // Transport family for each vendor: how the daemon hosts it (pty, acp, or rpc).
    // Vendors absent from this map default to "rpc".
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

    public static string DescriptionFor(HarnessOption option) => option.TransportFamily switch {
        "pty" => "PTY · terminal + chat",
        "acp" => "ACP · chat",
        _     => "chat",
    };
}
