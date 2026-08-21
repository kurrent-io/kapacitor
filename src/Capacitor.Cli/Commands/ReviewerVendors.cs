namespace Capacitor.Cli.Commands;

/// <summary>
/// The reviewer vendors this kcap build knows by name, in one place. Two surfaces offer them to a
/// human — the flows fallback guidance ("ask the user which vendor to use") and the `kcap config set
/// flows.reviewer_vendor` warning — and a list that drifted between them would have one surface
/// recommend a vendor the other warns about.
///
/// <para>This list is advisory, never authoritative: the SERVER decides which vendors are installed
/// and certified, so an unknown token here is a warning, not a rejection. That is what lets a new
/// vendor work with an older CLI.</para>
/// </summary>
static class ReviewerVendors {
    /// <summary>Canonical tokens, rendered exactly as they are shown to a user. The single source —
    /// <see cref="Known"/> is derived from it, so a token can only be added in one place.</summary>
    internal const string Tokens =
        "claude, codex, copilot, cursor, gemini, kiro, opencode, pi, antigravity";

    static readonly string[] Known = Tokens.Split(", ");

    /// <summary>Canonical form: what the server echoes back, so a preference saved as "Codex" still
    /// matches the applied-vendor echo instead of reading as a mismatch.</summary>
    internal static string Normalize(string value) => value.Trim().ToLowerInvariant();

    internal static bool IsKnown(string normalized) => Known.Contains(normalized, StringComparer.Ordinal);
}
