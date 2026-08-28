namespace Capacitor.Cli.Core.Harness;

/// <summary>
/// Our own names for a harness: the id that appears in our CLI's arguments, our offer ledger and the
/// payloads we exchange with the server. A vendor never sees these — they are ours, and a persisted
/// ledger and a server's answers both carry them, so each spelling here is a compatibility
/// constraint rather than a display choice.
/// </summary>
public static class HarnessNames {
    extension(HarnessId id) {
        /// <summary>How this harness is spelled where something outside this build reads it.</summary>
        public string VendorId => id switch {
            HarnessId.Claude      => "claude",
            HarnessId.Codex       => "codex",
            HarnessId.Cursor      => "cursor",
            HarnessId.Copilot     => "copilot",
            HarnessId.Gemini      => "gemini",
            HarnessId.Kiro        => "kiro",
            HarnessId.Pi          => "pi",
            HarnessId.OpenCode    => "opencode",
            HarnessId.Antigravity => "antigravity",
        };

        /// <summary>The flag that selects this one harness on our commands.</summary>
        public string Flag => "--" + id.VendorId;

        /// <summary><c>kcap plugin install</c>'s selector, null for Claude — the bare command
        /// installs it, and that is the one flag we cannot add without changing what an existing
        /// invocation does.</summary>
        public string? PluginInstallFlag => id is HarnessId.Claude ? null : id.Flag;

        /// <summary>The harness a vendor id names, or null when the id came from outside this build —
        /// a newer server's payload, or a user's typo. Spelled as a switch rather than parsed:
        /// reflection would also accept numeric strings and comma-separated combinations, neither of
        /// which is a vendor id.</summary>
        public static HarnessId? From(string? vendorId) => vendorId switch {
            "claude"      => HarnessId.Claude,
            "codex"       => HarnessId.Codex,
            "cursor"      => HarnessId.Cursor,
            "copilot"     => HarnessId.Copilot,
            "gemini"      => HarnessId.Gemini,
            "kiro"        => HarnessId.Kiro,
            "pi"          => HarnessId.Pi,
            "opencode"    => HarnessId.OpenCode,
            "antigravity" => HarnessId.Antigravity,
            _             => null,
        };

        /// <summary>Every id a user may name, for a help or error line.</summary>
        public static string KnownIds => string.Join(", ", HarnessRegistry.Identities.Select(h => h.VendorId));
    }

    /// <summary>The command that silences these harnesses' nudge, spelled as a user would type it.
    /// Outside the extension block deliberately: a second block declares a second member named
    /// <c>extension</c>, which CA1708 reads as two names differing only by case.</summary>
    public static string DismissCommand(this IEnumerable<HarnessId> harnesses) =>
        "kcap harness dismiss " + string.Join(" ", harnesses.Select(h => h.VendorId));
}
