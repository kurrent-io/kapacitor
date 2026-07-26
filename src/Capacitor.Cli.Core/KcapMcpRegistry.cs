namespace Capacitor.Cli.Core;

/// <summary> D-c: the kcap-owned MCP server registry — the ONLY source a flow
/// definition's mcp: allowlist resolves against (never user config). StartsFlows marks
/// servers that can start flows; they are stripped from every allowlist regardless of
/// listing (the recursion guard — the server strips too, this is the authoritative layer).
/// Ids are canonical lower-case; Resolve is case-insensitive so casing can't dodge the strip.</summary>
public sealed record KcapMcpServerDescriptor(string Id, string[] Args, bool StartsFlows);

public static class KcapMcpRegistry {
    static readonly Dictionary<string, KcapMcpServerDescriptor> Entries = new(StringComparer.OrdinalIgnoreCase) {
        ["kcap-review"]    = new("kcap-review",    ["mcp", "review"],    false),
        ["kcap-sessions"]  = new("kcap-sessions",  ["mcp", "sessions"],  false),
        ["kcap-memory"]    = new("kcap-memory",    ["mcp", "memory"],    false),
        ["kcap-flows"]     = new("kcap-flows",     ["mcp", "flows"],     true),
        ["kcap-workitems"] = new("kcap-workitems", ["mcp", "workitems"], false),
        ["kcap-analytics"] = new("kcap-analytics", ["mcp", "analytics"], false),
    };

    /// <summary>Resolves an allowlist entry to its descriptor. Case-insensitive, trims
    /// surrounding whitespace. A null or blank name — e.g. a wire-deserialized allowlist
    /// element — returns null rather than throwing, so callers can route it through the
    /// same unknown-name skip path as any other unresolvable name.</summary>
    public static KcapMcpServerDescriptor? Resolve(string? name) {
        if (string.IsNullOrWhiteSpace(name)) return null;

        return Entries.TryGetValue(name.Trim(), out var d) ? d : null;
    }

    // ── Unattended review-flow reviewer auto-approval ──────────────────────────────────
    //
    // A hosted review-flow reviewer runs unattended, so any MCP tool it calls must be auto-approved
    // (no human to prompt). Authorization is a per-reviewer bridge token (see LocalPermissionBridge);
    // this registry defines which servers may be covered. The unit is the SERVER (bare Codex tool
    // names carry no server, and an exact tool-name gate would hang the reviewer on an un-curated
    // tool), restricted to READ-ONLY kcap servers — excluding the write server kcap-memory and the
    // flow-starting kcap-flows.

    /// <summary>The read-only kcap servers a review-flow reviewer may auto-approve. A flow
    /// allowlist containing anything else fails the launch fast (never a silent auto-approve or a
    /// hang). Case-insensitive.</summary>
    public static readonly IReadOnlySet<string> ReviewFlowAutoApprovableServers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kcap-review", "kcap-sessions" };

    /// <summary>The reserved result-submission server every review-flow reviewer is launched with,
    /// injected by the launcher independent of the allowlist. It is intentionally NOT a registry
    /// entry, so <see cref="TryResolveReviewFlowAllowlist"/> treats it as an already-satisfied no-op
    /// (never a rejection, never re-emitted) — the server's dynamic-flow policy legitimately lists
    /// it, and every reviewer runtime must agree on that.</summary>
    public const string ReservedResultChannelId = "kcap-flow-result";

    /// <summary>Explicit, reviewed classification: each auto-approvable server → the exact tool
    /// names it exposes that are unattended-safe (read / result-submit). The single source of
    /// truth the contract guard test cross-checks against each server's live <c>tools/list</c>;
    /// adding a mutating tool to one of these servers trips that guard until it's classified here
    /// in a reviewed change.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ReviewFlowUnattendedSafeTools =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase) {
            ["kcap-review"]   = new HashSet<string>(StringComparer.Ordinal) {
                "get_pr_summary", "list_pr_files", "get_file_context", "search_context",
                "list_sessions", "get_transcript",
            },
            ["kcap-sessions"] = new HashSet<string>(StringComparer.Ordinal) {
                "search_sessions", "get_session_summary", "get_session_transcript",
                "get_turn", "list_turns",
            },
        };

    /// <summary>Resolve a review-flow reviewer allowlist to canonical, auto-approvable server ids.
    /// Returns <c>true</c> + the deduped canonical ids only when EVERY entry resolves to a
    /// <see cref="ReviewFlowAutoApprovableServers"/> member; returns <c>false</c> + the offending
    /// <paramref name="rejected"/> name when any entry is unknown, flow-starting, or not
    /// auto-approvable (the caller fails the launch — never silently drops it). A null/empty input
    /// is valid (<c>true</c> + empty): such a reviewer only uses the separately-injected
    /// <c>kcap-flow-result</c> submit channel.</summary>
    public static bool TryResolveReviewFlowAllowlist(IReadOnlyList<string>? names, out string[] servers, out string? rejected) {
        rejected = null;

        if (names is null || names.Count == 0) {
            servers = [];

            return true;
        }

        var result = new List<string>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names) {
            // The reserved result channel is always injected by the launcher, so it is a no-op here
            // (satisfied, never re-emitted, never rejected) — consistent across every reviewer runtime.
            if (string.Equals(name?.Trim(), ReservedResultChannelId, StringComparison.OrdinalIgnoreCase))
                continue;

            var d = Resolve(name);

            if (d is null || d.StartsFlows || !ReviewFlowAutoApprovableServers.Contains(d.Id)) {
                rejected = name?.Trim();
                servers  = [];

                return false;
            }

            if (seen.Add(d.Id)) result.Add(d.Id);
        }

        servers = [.. result];

        return true;
    }
}
