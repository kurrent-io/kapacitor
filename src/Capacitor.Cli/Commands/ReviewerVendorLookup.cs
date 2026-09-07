using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>The subset of the server's <c>DaemonInfo</c> the reviewer-vendor lookup consumes,
/// decoupled from the wire DTO so the aggregation stays a pure, unit-testable function.</summary>
public sealed record DaemonVendorRecord(
    string[] RepoPaths,
    string? MachineId,
    string[]? SupportedVendors,
    string[]? UnattendedVendors,
    IReadOnlyList<UnattendedVendorCapabilityLite>? Capabilities);

public sealed record UnattendedVendorCapabilityLite(string Vendor, bool SupportsReviewerModelResolution);

public sealed record ReviewerVendorsResult(
    [property: JsonPropertyName("repo")]          RepoIdent Repo,
    [property: JsonPropertyName("driver_vendor")] string? DriverVendor,
    [property: JsonPropertyName("reviewers")]     IReadOnlyList<ReviewerEntry> Reviewers,
    [property: JsonPropertyName("diagnostics")]   ReviewerVendorDiagnostics Diagnostics);

public sealed record RepoIdent(
    [property: JsonPropertyName("identity")] string? Identity,
    [property: JsonPropertyName("resolved")] bool Resolved);

public sealed record ReviewerEntry(
    [property: JsonPropertyName("vendor")]         string Vendor,
    [property: JsonPropertyName("daemons")]        int Daemons,
    [property: JsonPropertyName("model_override")] bool ModelOverride);

public sealed record ReviewerVendorDiagnostics(
    [property: JsonPropertyName("connected_daemons")]            int ConnectedDaemons,
    [property: JsonPropertyName("repo_hosting_daemons")]         int RepoHostingDaemons,
    [property: JsonPropertyName("skipped_daemon_records")]       int SkippedDaemonRecords,
    [property: JsonPropertyName("supported_but_not_unattended")] string[] SupportedButNotUnattended,
    [property: JsonPropertyName("reason")]                       string? Reason);

/// <summary>Repo-aware aggregation behind the <c>list_reviewer_vendors</c> MCP tool: given the
/// daemons the server reports and the current session's repo, returns the reviewer vendors that can
/// ACTUALLY run an unattended review flow for this repo right now. All the failure modes are
/// disambiguated by a single <c>reason</c> so an empty result never reads as one specific cause it is
/// not (see <see cref="Reason"/> and its precedence). <c>Identity</c> is a NON-sensitive repo id
/// (e.g. owner/repo) supplied by the caller — never the local repo path, which must not leak to the
/// model — while the local <c>repoRoot</c> argument is used only for the on-disk hosting match.</summary>
public static class ReviewerVendorLookup {
    public static class Reason {
        public const string RepoUnresolved      = "repo_unresolved";
        public const string SchemaSkew          = "schema_skew";
        public const string LookupFailed        = "lookup_failed";
        public const string NoDaemonsConnected  = "no_daemons_connected";
        public const string NoRepoHostingDaemon = "no_repo_hosting_daemon";
        public const string NoUnattendedReviewer= "no_unattended_reviewer";
    }

    /// <param name="daemons">Parsed daemon records, or null when the lookup itself failed
    /// (transport/auth/API) — distinct from an empty list, which is "connected zero daemons".</param>
    /// <param name="repoRoot">The session's local repo root. Matched both as given and collapsed to
    /// its main repository, because a daemon's <c>RepoPaths</c> carries either shape: what
    /// <see cref="Core.Config.RepoPathStore"/> persists is collapsed, while a configured
    /// <c>AllowedRepoPaths</c> entry is advertised as written. Collapsing one side alone loses a
    /// session in a linked worktree, or an operator who allowlisted that worktree by hand.</param>
    /// <param name="repoIdentity">A non-sensitive, stable id for the repo (e.g. owner/repo), surfaced
    /// as <c>repo.identity</c>. Never the local path.</param>
    /// <param name="schemaSkew">Set when the server response could not be parsed at all (client too
    /// old, or every record unparseable): reported as its own reason so it can never masquerade as an
    /// authoritative empty set.</param>
    public static ReviewerVendorsResult Aggregate(
            IReadOnlyList<DaemonVendorRecord>? daemons,
            string? repoRoot,
            string? requesterMachineId,
            string? driverVendor,
            bool schemaSkew = false,
            int skippedRecords = 0,
            string? repoIdentity = null) {
        var resolved = !string.IsNullOrEmpty(repoRoot);
        var repo     = new RepoIdent(repoIdentity, resolved);

        // Empty-result reasons, most-fundamental first — the caller maps exactly one to guidance.
        if (!resolved)
            return Empty(repo, driverVendor, daemons?.Count ?? 0, 0, skippedRecords, Reason.RepoUnresolved);
        if (schemaSkew)
            return Empty(repo, driverVendor, daemons?.Count ?? 0, 0, skippedRecords, Reason.SchemaSkew);
        if (daemons is null)
            return Empty(repo, driverVendor, 0, 0, skippedRecords, Reason.LookupFailed);

        var mainRoot = GitRepository.ResolveMainRepoRoot(repoRoot!);

        var hosting = daemons
            .Where(d => d.RepoPaths.Any(p => RepoPathMatches(p, repoRoot!) || RepoPathMatches(p, mainRoot)) &&
                        (requesterMachineId is null || d.MachineId == requesterMachineId))
            .ToList();

        var reviewers = hosting
            .SelectMany(d => d.UnattendedVendors ?? [])
            .GroupBy(v => v, StringComparer.Ordinal)
            .Select(g => new ReviewerEntry(
                g.Key,
                hosting.Count(d => (d.UnattendedVendors ?? []).Contains(g.Key, StringComparer.Ordinal)),
                ModelOverrideForAll(hosting, g.Key)))
            .OrderBy(e => e.Vendor, StringComparer.Ordinal)
            .ToList();

        var supportedNotUnattended = hosting
            .SelectMany(d => (d.SupportedVendors ?? []).Except(d.UnattendedVendors ?? [], StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();

        string? reason =
            reviewers.Count > 0 ? null
            : daemons.Count == 0 ? Reason.NoDaemonsConnected
            : hosting.Count == 0 ? Reason.NoRepoHostingDaemon
            : Reason.NoUnattendedReviewer;

        return new ReviewerVendorsResult(repo, driverVendor, reviewers,
            new ReviewerVendorDiagnostics(daemons.Count, hosting.Count, skippedRecords, supportedNotUnattended, reason));
    }

    // Conservative AND: a model override is only advertised when EVERY hosting daemon that offers the
    // vendor can resolve it — flow-start may route to any of them, so a partial capability must not be
    // reported as available.
    static bool ModelOverrideForAll(IReadOnlyList<DaemonVendorRecord> hosting, string vendor) {
        var advertising = hosting
            .Where(d => (d.UnattendedVendors ?? []).Contains(vendor, StringComparer.Ordinal))
            .ToList();
        return advertising.Count > 0 && advertising.All(d =>
            d.Capabilities?.Any(c => c.Vendor == vendor && c.SupportsReviewerModelResolution) == true);
    }

    static ReviewerVendorsResult Empty(
            RepoIdent repo, string? driver, int connected, int hosting, int skipped, string reason)
        => new(repo, driver, [], new ReviewerVendorDiagnostics(connected, hosting, skipped, [], reason));

    // Filesystem paths compare case-insensitively on Windows and macOS (case-preserving but
    // case-insensitive volumes) and case-sensitively on Linux; separators are unified and a trailing
    // one trimmed so a '\'-vs-'/' or trailing-slash difference between a daemon's RepoPaths and the
    // local repoRoot never produces a false no_repo_hosting_daemon. The conservative same-machine
    // filter means both sides come from the same OS, so one OS-appropriate rule is correct.
    static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    static bool RepoPathMatches(string daemonPath, string repoRoot)
        => string.Equals(Normalize(daemonPath), Normalize(repoRoot), PathComparison);

    static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>Tolerantly parse a <c>GET /api/daemons</c> body into the records
    /// <see cref="Aggregate"/> consumes. A body that is not a JSON array, or an array whose every
    /// element is unparseable, sets <c>SchemaSkew</c> — so schema skew can never be mistaken for an
    /// authoritative empty set; a single malformed element is skipped and counted. Only called on a
    /// 2xx; a non-2xx maps to <c>Aggregate(daemons: null, …)</c> = lookup_failed.</summary>
    public static (IReadOnlyList<DaemonVendorRecord> Records, int Skipped, bool SchemaSkew) ParseDaemons(string body) {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch { return ([], 0, true); }

        using (doc) {
            if (!doc.RootElement.IsArray)
                return ([], 0, true);

            var records = new List<DaemonVendorRecord>();
            var total = 0;
            var skipped = 0;

            // Field names are snake_case: the server serializes /api/daemons with
            // JsonNamingPolicy.SnakeCaseLower (HttpJsonConfiguration), so DaemonInfo.RepoPaths is
            // "repo_paths" on the wire, etc. — do not "correct" these to camelCase.
            foreach (var el in doc.RootElement.EnumerateArray()) {
                total++;
                // repo_paths is the one field required to place a daemon against a repo; without it the
                // record cannot participate in the intersection, so it is malformed → skip + count.
                if (!el.IsObject || el.Arr("repo_paths") is not { } rp) {
                    skipped++;
                    continue;
                }

                var supported  = el.Arr("supported_vendors") is { } sv ? StringArray(sv) : null;
                var unattended = el.Arr("unattended_vendors") is { } uv ? StringArray(uv) : null;

                List<UnattendedVendorCapabilityLite>? caps = null;
                if (el.Arr("unattended_vendor_capabilities") is { } cv) {
                    caps = [];
                    foreach (var c in cv.EnumerateArray()) {
                        if (c.Str("vendor") is not { } vendor) continue;
                        caps.Add(new UnattendedVendorCapabilityLite(
                            vendor, c.Bool("supports_reviewer_model_resolution") == true));
                    }
                }

                records.Add(new DaemonVendorRecord(StringArray(rp), el.Str("machine_id"), supported, unattended, caps));
            }

            return (records, skipped, total > 0 && records.Count == 0);
        }
    }

    static string[] StringArray(JsonElement arr) {
        var list = new List<string>();
        foreach (var e in arr.EnumerateArray())
            if (e.IsString && e.GetString() is { } s)
                list.Add(s);
        return list.ToArray();
    }
}
