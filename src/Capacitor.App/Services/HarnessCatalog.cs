namespace Capacitor.App.Services;

public sealed record HarnessOption(string Vendor, string Label, string TransportFamily, bool Available);

/// The picker's vendor list. Availability comes from what the daemon advertises
/// (DaemonInfoDto.SupportedVendors), never from a version check — a vendor auto-update must not
/// silently withdraw a harness. A vendor the daemon advertises but this build has never heard of
/// is still offered, listed under its raw token: the daemon is the authority on what it can host.
public static class HarnessCatalog {
    static readonly (string Vendor, string Label, string Family)[] Known = [
        ("claude",      "Claude",      "pty"),
        ("codex",       "Codex",       "pty"),
        ("cursor",      "Cursor",      "acp"),
        ("copilot",     "Copilot",     "acp"),
        ("gemini",      "Gemini",      "acp"),
        ("kiro",        "Kiro",        "acp"),
        ("opencode",    "OpenCode",    "acp"),
        ("antigravity", "Antigravity", "rpc"),
        ("pi",          "Pi",          "rpc"),
    ];

    public static IReadOnlyList<HarnessOption> Build(string[]? supportedVendors) {
        // null = an older daemon that never sent the field: unknown, not empty.
        var advertised = supportedVendors is null
            ? null
            : new HashSet<string>(supportedVendors, StringComparer.OrdinalIgnoreCase);

        var options = Known
            .Select(k => new HarnessOption(k.Vendor, k.Label, k.Family, advertised?.Contains(k.Vendor) ?? true))
            .ToList();

        if (advertised is null) return options;

        var known = new HashSet<string>(Known.Select(k => k.Vendor), StringComparer.OrdinalIgnoreCase);
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
