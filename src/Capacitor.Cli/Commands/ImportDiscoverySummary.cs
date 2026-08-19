namespace Capacitor.Cli.Commands;

/// <summary>
/// What discovery found, before anything is uploaded: how much history each repository has, how much
/// could not be attributed to one, and how much falls inside each candidate <c>--since</c> window.
/// </summary>
/// <remarks>
/// Every figure here is already resolved during a normal import and then discarded — the counts are
/// what <c>BuildRepoChoices</c>'s <c>Distinct()</c> throws away. Surfacing them is what lets a caller
/// show the consequence of a scope choice before the user commits to it.
/// </remarks>
public sealed record ImportDiscoverySummary(
    IReadOnlyList<ImportDiscoverySummary.RepoTotals> Repos,
    int                                              UnmatchedCount,
    IReadOnlyList<ImportDiscoverySummary.WindowTotals> ByWindow) {

    /// <param name="LastSessionAt">
    /// The newest age among these sessions, by whatever rule <c>--since</c> uses for their vendor —
    /// a session start for most, a rollout's day directory for Codex, a transcript's last write where
    /// Claude's first timestamp cannot be read. Close enough to "last activity" to label, not precise
    /// enough to compute with.
    /// </param>
    public sealed record RepoTotals(
        string Owner, string Name, int SessionCount, DateTimeOffset? LastSessionAt);

    /// <param name="Since">The window's inclusive start, or null for "everything".</param>
    public sealed record WindowTotals(DateOnly? Since, int SessionCount);

    /// <summary>
    /// <paramref name="windows"/> comes from the caller rather than being fixed here, so the buckets
    /// and the <c>--since</c> values a UI offers cannot drift apart — they are the same list.
    /// </summary>
    public static ImportDiscoverySummary Build(
            IEnumerable<(string SessionId, DateTimeOffset? StartedAt)> sessions,
            IReadOnlyDictionary<string, (string Owner, string Name)?>  repoBySession,
            IReadOnlyList<DateOnly?>                                   windows) {
        var byRepo     = new Dictionary<(string Owner, string Name), (int Count, DateTimeOffset? Last)>(ImportScope.RepoComparer);
        var unmatched  = 0;
        var windowHits = new int[windows.Count];

        foreach (var (sessionId, startedAt) in sessions) {
            if (repoBySession.TryGetValue(sessionId, out var repo) && repo is { } r) {
                var prior = byRepo.GetValueOrDefault(r);

                byRepo[r] = (prior.Count + 1, Later(prior.Last, startedAt));
            } else {
                // `--all` includes these and any repo selection silently drops them, so the number is
                // both the honest one and how `kcap remap` gets discovered.
                unmatched++;
            }

            for (var i = 0; i < windows.Count; i++) {
                // An undated session counts in every window, because that is what `--since` does with
                // one: every source keeps a candidate whose timestamp it could not determine rather
                // than dropping it. Excluding it here would under-report a window against the import
                // it is predicting.
                if (windows[i] is not { } since
                 || startedAt is not { } at
                 || DateOnly.FromDateTime(at.UtcDateTime) >= since) {
                    windowHits[i]++;
                }
            }
        }

        var repos = byRepo
            .Select(kv => new RepoTotals(kv.Key.Owner, kv.Key.Name, kv.Value.Count, kv.Value.Last))
            .OrderByDescending(r => r.LastSessionAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => $"{r.Owner}/{r.Name}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new(repos, unmatched, [.. windows.Select((w, i) => new WindowTotals(w, windowHits[i]))]);
    }

    static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}
