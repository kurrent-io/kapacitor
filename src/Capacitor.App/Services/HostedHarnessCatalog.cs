using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.App.Services;

public sealed record HarnessOption(string Vendor, string Label, string TransportFamily, bool Available);

/// One curated model suggestion for the combined harness+model picker. Slug is what the wire
/// carries verbatim; Label is the picker's friendly name.
public sealed record ModelChoice(string Slug, string Label);

public sealed record PermissionModeChoice(string Token, string Label);

/// The picker's vendor list. Availability comes from what the daemon advertises
/// (DaemonInfoDto.SupportedVendors), never from a version check — a vendor auto-update must not
/// silently withdraw a harness. A vendor the daemon advertises but this build has never heard of
/// is still offered, listed under its raw token: the daemon is the authority on what it can host.
public static class HostedHarnessCatalog {
    // Transport family for each vendor: how the daemon hosts it (pty, acp, or rpc).
    // Vendors absent from this map default to "rpc" — which reads as "chat" in the picker, true or
    // not, so HostedHarnessCatalogTests pins every harness Core knows to an entry here. The fallback
    // stays for a vendor only the DAEMON knows about; it is not a licence to skip a tenth entry when
    // one is added to Core.
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

        var options = HarnessRegistry.Identities
            .Select(h => (h.VendorId, h.Label))
            .Select(h => new HarnessOption(
                h.VendorId,
                h.Label,
                TransportFamilies.TryGetValue(h.VendorId, out var family) ? family : "rpc",
                advertised?.Contains(h.VendorId) ?? true))
            .ToList();

        if (advertised is null) return options;

        var known = new HashSet<string>(options.Select(o => o.Vendor), StringComparer.OrdinalIgnoreCase);
        foreach (var extra in supportedVendors!.Where(v => !known.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase))
            options.Add(new HarnessOption(extra, extra, "rpc", true));

        return options;
    }

    // Curated model suggestions per vendor — a hardcoded catalog, deliberately (T3 Code ships
    // Claude's exactly this way; it lists Codex's live from `codex app-server`, which our daemon
    // could do too once a model-list IPC exists). The picker always offers "vendor default" and a
    // typed custom id besides these, so catalog drift can never block a launch.
    static readonly Dictionary<string, ModelChoice[]> ModelChoices = new(StringComparer.OrdinalIgnoreCase) {
        ["claude"] = [
            new("claude-fable-5", "Claude Fable 5"),
            new("claude-opus-5", "Claude Opus 5"),
            new("claude-sonnet-5", "Claude Sonnet 5"),
            new("claude-haiku-4-5", "Claude Haiku 4.5"),
        ],
        ["codex"] = [
            new("gpt-5.6", "GPT-5.6"),
        ],
    };

    /// The curated suggestions for a vendor; empty for one without a list (picker then offers
    /// only the default + custom rows).
    public static IReadOnlyList<ModelChoice> ModelChoicesFor(string vendor) =>
        ModelChoices.TryGetValue(vendor, out var models) ? models : [];

    /// Chip wording for a model selection: the curated label when known, the raw slug otherwise,
    /// "Default" for the "" sentinel (same word the effort/agent pickers use for "harness chooses").
    public static string ModelLabelFor(string vendor, string model) =>
        string.IsNullOrWhiteSpace(model)
            ? EffortDefaultLabel
            : ModelChoicesFor(vendor).FirstOrDefault(m => string.Equals(m.Slug, model, StringComparison.OrdinalIgnoreCase))?.Label ?? model;

    /// The effort ladder the daemon passes through verbatim (codex maps max→xhigh itself); the
    /// picker's Default entry (null on the wire) hands the choice back to the harness. Lives here
    /// beside the model catalog so the launch vocabularies have one authority.
    public static readonly IReadOnlyList<string> EffortLadder = ["low", "medium", "high", "xhigh"];

    /// Shared chip/flyout word for "omit from the wire — let the harness decide". Not "Auto":
    /// that label is Claude's permission mode, a different control.
    public const string EffortDefaultLabel = "Default";

    /// Sentence-case display for an effort wire token; unknown tokens pass through unchanged.
    public static string EffortLabelFor(string? token) => token switch {
        null or "" => EffortDefaultLabel,
        "low"      => "Low",
        "medium"   => "Medium",
        "high"     => "High",
        "xhigh"    => "Max",
        _          => token,
    };

    /// Claude permission modes, most → least prompting. Manual is the product default and is
    /// omitted from the launch payload (same "harness default" idea as Effort's Default); the
    /// rows below it are escalations, so the flyout keeps a separator after Manual.
    public static readonly IReadOnlyList<PermissionModeChoice> PermissionModes = [
        new(ClaudePermissionModes.Manual, "Manual"),
        new(ClaudePermissionModes.AcceptEdits, "Accept edits"),
        new(ClaudePermissionModes.Auto, "Auto"),
        new(ClaudePermissionModes.BypassPermissions, "Bypass permissions"),
    ];

    public static string PermissionModeLabelFor(string token) =>
        PermissionModes.FirstOrDefault(m => string.Equals(m.Token, token, StringComparison.Ordinal))?.Label ?? token;

    /// The daemon rejects a mode for any other vendor, so the chip is withheld rather than sent
    /// and refused.
    public static bool SupportsPermissionMode(string vendor) =>
        string.Equals(vendor, "claude", StringComparison.OrdinalIgnoreCase);

    // Monogram + tint per vendor: the glyph is the fallback where the view layer has no brand
    // mark (VendorIcons); the tint colors both. Monochrome brands render in the near-white text
    // color; claude/gemini keep their brand hues. Beside the labels/families so adding a vendor
    // means one file, not three.
    static readonly Dictionary<string, (string Glyph, string Color)> VendorTiles = new(StringComparer.OrdinalIgnoreCase) {
        ["claude"]      = ("✳", "#D97757"),
        ["codex"]       = ("Cx", "#F1F3F7"),
        ["cursor"]      = ("Cu", "#F1F3F7"),
        ["copilot"]     = ("Cp", "#F1F3F7"),
        ["gemini"]      = ("Ge", "#7BA7F7"),
        ["kiro"]        = ("Ki", "#B78BF7"),
        ["opencode"]    = ("Oc", "#F1F3F7"),
        ["antigravity"] = ("An", "#F4B860"),
        ["pi"]          = ("π", "#A994FF"),
    };

    /// Glyph + tint for a vendor token; an unmapped token gets its first letter in neutral grey
    /// (the daemon is the authority on what it can host — see Build's same rule).
    public static (string Glyph, string Color) TileFor(string vendor) =>
        VendorTiles.TryGetValue(vendor, out var tile)
            ? tile
            : (vendor.Length > 0 ? vendor[..1].ToUpperInvariant() : "?", "#B0B7C6");

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

    /// The one source of the no-terminal wording (terminal tab and workspace tab strip alike).
    /// Suffixed only when the family is reliably known: ACP is; the rpc/"chat" bucket also covers
    /// claude/codex/any unmapped vendor whose has_terminal came back false for a reason this build
    /// can't classify further, so it gets no family token at all rather than leaking "RPC" (an
    /// internal transport name, not a user-facing concept).
    public static string NoTerminalNote(bool? hasTerminal, string vendor) =>
        EffectiveFamily(hasTerminal, vendor) == "acp"
            ? "This session runs over ACP — no terminal to attach to."
            : "This session has no terminal.";
}
