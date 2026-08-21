using System.Text.Json.Serialization;

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

/// <summary>Pure repo-aware aggregation behind the <c>list_reviewer_vendors</c> MCP tool: given the
/// daemons the server reports and the current session's repo identity, returns the reviewer vendors
/// that can ACTUALLY run an unattended review flow for this repo right now. All the failure modes are
/// disambiguated by a single <c>reason</c> so an empty result never reads as one specific cause it is
/// not (see <see cref="Reason"/> and its precedence).</summary>
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
    /// <param name="schemaSkew">Set when the server response could not be parsed at all (client too
    /// old, or every record unparseable): reported as its own reason so it can never masquerade as an
    /// authoritative empty set.</param>
    public static ReviewerVendorsResult Aggregate(
            IReadOnlyList<DaemonVendorRecord>? daemons,
            string? repoRoot,
            string? requesterMachineId,
            string? driverVendor,
            bool schemaSkew = false,
            int skippedRecords = 0) {
        var resolved = !string.IsNullOrEmpty(repoRoot);
        var repo     = new RepoIdent(repoRoot, resolved);

        // Empty-result reasons, most-fundamental first — the caller maps exactly one to guidance.
        if (!resolved)
            return Empty(repo, driverVendor, daemons?.Count ?? 0, 0, skippedRecords, Reason.RepoUnresolved);
        if (schemaSkew)
            return Empty(repo, driverVendor, daemons?.Count ?? 0, 0, skippedRecords, Reason.SchemaSkew);
        if (daemons is null)
            return Empty(repo, driverVendor, 0, 0, skippedRecords, Reason.LookupFailed);

        var norm = Normalize(repoRoot!);
        var hosting = daemons
            .Where(d => d.RepoPaths.Any(p => Normalize(p) == norm) &&
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

    static string Normalize(string path) => path.TrimEnd('/', '\\');
}
