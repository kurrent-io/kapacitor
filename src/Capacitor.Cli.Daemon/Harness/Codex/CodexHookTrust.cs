using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>One hook as reported by the <c>codex app-server</c> <c>hooks/list</c> response.</summary>
/// <param name="Key">The opaque per-hook key (<c>&lt;hooks.json path&gt;:&lt;event&gt;:i:j</c>), used verbatim
/// as the <c>hooks.state</c> table key when seeding trust.</param>
/// <param name="EventName">The hook event (protocol camelCase, e.g. <c>sessionStart</c>).</param>
/// <param name="Command">The hook's command line — how a kcap-owned hook is identified.</param>
/// <param name="CurrentHash">The hook's current content hash (<c>sha256:…</c>), or null if the
/// response omitted it (then the hook cannot be seeded).</param>
/// <param name="TrustStatus">The reported trust state (<c>trusted</c> / <c>untrusted</c>).</param>
internal readonly record struct CodexHookEntry(
    string Key, string EventName, string Command, string? CurrentHash, string TrustStatus);

/// <summary>The classifier's verdict for one launch's <c>hooks/list</c> snapshot.</summary>
internal abstract record CodexHookTrustDecision {
    /// <summary>Every required critical event has a trusted kcap-owned hook — launch as-is.</summary>
    public sealed record Proceed : CodexHookTrustDecision;

    /// <summary>One or more kcap-owned hooks are untrusted but seedable. Restart the app-server
    /// process with <c>-c &lt;StateOverride&gt;</c>, then re-run <c>hooks/list</c> to verify.</summary>
    public sealed record SeedAndRestart(string StateOverride) : CodexHookTrustDecision;

    /// <summary>A required critical event has no kcap-owned hook at all — seeding cannot conjure a
    /// hook that isn't configured. Fail closed (the <c>CodexHooksNotInstalledException</c> case).</summary>
    public sealed record MissingRequiredHooks(IReadOnlyList<string> MissingEvents) : CodexHookTrustDecision;

    /// <summary>An untrusted kcap-owned hook reported no <c>currentHash</c>, so its trust cannot be
    /// seeded. Fail closed rather than proceed with a hook that will be silently skipped.</summary>
    public sealed record Unseedable(IReadOnlyList<string> Keys) : CodexHookTrustDecision;
}

/// <summary>
/// Classifies an app-server <c>hooks/list</c> snapshot into a trust decision, and builds the
/// full-table <c>hooks.state</c> override used to seed trust for kcap-owned hooks.
///
/// <para>Why this exists: <c>codex app-server</c> silently SKIPS a hook whose
/// <c>[hooks.state]</c> <c>trusted_hash</c> is missing or stale — and it does not accept the
/// TUI/exec-only <c>--dangerously-bypass-hook-trust</c> flag — so a hosted launch whose ingestion
/// rides those hooks would lose the transcript with no error. The launch path therefore verifies
/// trust before its first turn: absence of a required hook fails closed (seeding can't create one),
/// distrust of a present kcap hook is repaired by one seeded restart, and the seed is verified after
/// the restart (that re-run is the runtime's; this class is the pure classify + build step).</para>
///
/// <para>The dotted-KEY <c>-c hooks.state.&lt;key&gt;.trusted_hash=…</c> form silently fails on these
/// path-shaped keys (the same clap/TOML limitation the <c>mcp_servers</c> override hits), so the
/// override is emitted as ONE full-table value, exactly like the MCP-isolation table.</para>
/// </summary>
internal static class CodexHookTrust {
    /// <summary>The critical events a hosted launch requires a kcap hook for — single-sourced to
    /// match <see cref="CodexLauncher"/>'s hooks.json preflight. Compared case-insensitively so the
    /// protocol's camelCase <c>eventName</c> matches these hooks.json-style names.</summary>
    static readonly string[] CriticalEvents = ["SessionStart", "Stop", "PermissionRequest"];

    const string TrustedStatus = "trusted";

    public static CodexHookTrustDecision Classify(IReadOnlyList<CodexHookEntry> hooks) {
        var kcapHooks = hooks.Where(h => CodexHooksParser.IsCapacitorCodexHookCommand(h.Command)).ToArray();

        // Inventory: every critical event must have at least one kcap-owned hook. Seeding never
        // conjures a hook that isn't configured, so an absent one is a fail-closed launch failure.
        var missing = CriticalEvents
            .Where(evt => !kcapHooks.Any(h => EventMatches(h.EventName, evt)))
            .ToArray();

        if (missing.Length > 0) return new CodexHookTrustDecision.MissingRequiredHooks(missing);

        // Trust: seed EVERY untrusted kcap-owned hook (not only the critical ones — the transcript
        // hooks must fire too), so a single restart makes the whole kcap hook set trusted.
        var untrusted = kcapHooks
            .Where(h => !string.Equals(h.TrustStatus, TrustedStatus, StringComparison.Ordinal))
            .ToArray();

        if (untrusted.Length == 0) return new CodexHookTrustDecision.Proceed();

        var unseedable = untrusted.Where(h => string.IsNullOrEmpty(h.CurrentHash)).Select(h => h.Key).ToArray();

        if (unseedable.Length > 0) return new CodexHookTrustDecision.Unseedable(unseedable);

        return new CodexHookTrustDecision.SeedAndRestart(BuildStateOverride(untrusted));
    }

    /// <summary>Builds the <c>hooks.state={ "&lt;key&gt;"={trusted_hash="&lt;hash&gt;"}, … }</c>
    /// full-table override value for the given (untrusted, seedable) kcap hooks.</summary>
    static string BuildStateOverride(IReadOnlyList<CodexHookEntry> untrusted) {
        var entries = untrusted.Select(h =>
            $"{CodexToml.String(h.Key)}={{trusted_hash={CodexToml.String(h.CurrentHash!)}}}");

        return $"hooks.state={{{string.Join(",", entries)}}}";
    }

    static bool EventMatches(string reported, string critical) =>
        string.Equals(reported, critical, StringComparison.OrdinalIgnoreCase);
}
