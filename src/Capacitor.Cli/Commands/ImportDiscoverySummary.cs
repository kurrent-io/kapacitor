namespace Capacitor.Cli.Commands;

/// <summary>One candidate <c>--since</c> window. <paramref name="Key"/> is the only half that
/// crosses a wire; <paramref name="Since"/> is null for "everything" and is resolved against this
/// machine's today, never the server's.</summary>
public sealed record ImportDiscoveryWindow(string Key, DateOnly? Since);

/// <summary>
/// What discovery found, before anything is uploaded: how much history each repository has, how much
/// could not be attributed to one, and how much falls inside each candidate <c>--since</c> window.
/// </summary>
/// <remarks>
/// Every figure here is already resolved during a normal import and then discarded — the counts are
/// what <c>BuildRepoChoices</c>'s <c>Distinct()</c> throws away. Surfacing them is what lets a caller
/// show the consequence of a scope choice before the user commits to it.
///
/// <para>Per repository AND per window, not one or the other: "how many sessions will <i>this</i>
/// selection import" is a cell, and neither margin of a table gives you one.</para>
/// </remarks>
public sealed record ImportDiscoverySummary(
    IReadOnlyList<ImportDiscoverySummary.RepoTotals>   Repos,
    int                                                UnmatchedCount,
    IReadOnlyList<ImportDiscoverySummary.WindowTotals> ByWindow,
    IReadOnlyDictionary<string, int>                   UnmatchedByWindow) {

    /// <param name="SessionCount">Every session for this repository, whatever its age. Kept rather
    /// than read out of <paramref name="SessionsByWindow"/>, so it still means that when the caller
    /// asks for no "everything" window.</param>
    /// <param name="SessionsByWindow">Sessions inside each window the caller asked for, keyed by
    /// <see cref="ImportDiscoveryWindow.Key"/>.</param>
    /// <param name="LastSessionAt">
    /// The newest age among these sessions, by whatever rule <c>--since</c> uses for their vendor —
    /// a session start for most, a rollout's day directory for Codex, a transcript's last write where
    /// Claude's first timestamp cannot be read. Close enough to "last activity" to label, not precise
    /// enough to compute with.
    /// </param>
    public sealed record RepoTotals(
        string                           Owner,
        string                           Name,
        int                              SessionCount,
        DateTimeOffset?                  LastSessionAt,
        IReadOnlyDictionary<string, int> SessionsByWindow);

    /// <param name="Since">The window's inclusive start, or null for "everything".</param>
    public sealed record WindowTotals(string Key, DateOnly? Since, int SessionCount);

    /// <summary>
    /// <paramref name="windows"/> comes from the caller rather than being fixed here, so the buckets
    /// and the <c>--since</c> values a UI offers cannot drift apart — they are the same list.
    /// </summary>
    public static ImportDiscoverySummary Build(
            IEnumerable<(string SessionId, DateTimeOffset? StartedAt)> sessions,
            IReadOnlyDictionary<string, (string Owner, string Name)?>  repoBySession,
            IReadOnlyList<ImportDiscoveryWindow>                      windows) {
        var byRepo    = new Dictionary<(string Owner, string Name), Accumulator>(ImportScope.RepoComparer);
        var unmatched = new Accumulator(windows.Count);

        foreach (var (sessionId, startedAt) in sessions) {
            // `--all` includes an unattributed session and any repo selection silently drops it, so
            // its count is both the honest one and how `kcap remap` gets discovered.
            var into = unmatched;

            if (repoBySession.TryGetValue(sessionId, out var repo) && repo is { } r) {
                if (!byRepo.TryGetValue(r, out var bucket)) byRepo[r] = bucket = new Accumulator(windows.Count);

                into = bucket;
            }

            into.Add(startedAt, windows);
        }

        var repos = byRepo
            .Select(kv => new RepoTotals(
                kv.Key.Owner, kv.Key.Name, kv.Value.Count, kv.Value.Last, kv.Value.ByWindow(windows)))
            // Newest first, so a cap over this list keeps the repositories someone is working in.
            .OrderByDescending(r => r.LastSessionAt ?? DateTimeOffset.MinValue)
            .ThenBy(r => $"{r.Owner}/{r.Name}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new(
            repos,
            unmatched.Count,
            [.. windows.Select((w, i) => new WindowTotals(w.Key, w.Since, TotalIn(byRepo, unmatched, i)))],
            unmatched.ByWindow(windows));
    }

    static int TotalIn(
            Dictionary<(string Owner, string Name), Accumulator> byRepo, Accumulator unmatched, int index) =>
        byRepo.Values.Sum(a => a.Windows[index]) + unmatched.Windows[index];

    /// <summary>One bucket's running totals. A class so the loop above can hold whichever bucket a
    /// session belongs in and add to it once.</summary>
    sealed class Accumulator(int windowCount) {
        public int             Count   { get; private set; }
        public DateTimeOffset? Last    { get; private set; }
        public int[]           Windows { get; } = new int[windowCount];

        public void Add(DateTimeOffset? startedAt, IReadOnlyList<ImportDiscoveryWindow> windows) {
            Count++;
            Last = Later(Last, startedAt);

            for (var i = 0; i < windows.Count; i++) {
                // An undated session counts in every window, because that is what `--since` does with
                // one: every source keeps a candidate whose timestamp it could not determine rather
                // than dropping it. Excluding it here would under-report a window against the import
                // it is predicting.
                if (windows[i].Since is not { } since
                 || startedAt is not { } at
                 || DateOnly.FromDateTime(at.UtcDateTime) >= since) {
                    Windows[i]++;
                }
            }
        }

        public IReadOnlyDictionary<string, int> ByWindow(IReadOnlyList<ImportDiscoveryWindow> windows) {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < windows.Count; i++) counts[windows[i].Key] = Windows[i];

            return counts;
        }

        static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) =>
            a is null ? b : b is null ? a : a > b ? a : b;
    }
}
