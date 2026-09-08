using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.App.ViewModels;

internal sealed class PullRequestSectionState(string key) {
    internal sealed record Page(string Cursor, string? Next, PullRequestRow[] Rows, long Touched) {
        internal long Bytes => 256 + 2L * (Cursor.Length + (Next?.Length ?? 0)) + Rows.Sum(row => row.Bytes);
    }
    internal string Key { get; } = key;
    internal List<Page> Pages { get; } = [];
    internal string? Snapshot;
    internal DateTime? Started;
    internal DateTime? Completed;
    internal DateTime? Fetched;
    internal string? Head;
    internal string Coverage = "limited";
    internal string? CoverageReason;
    internal PullRequestCountDto? Total;
    internal PullRequestCountDto? Excluded;
    internal string? Next;
    internal bool Stopped;
    internal string? Error;
    internal List<string> Earlier { get; } = [];
    internal string? Evicted => Earlier.LastOrDefault();
    internal long Bytes => 512 + 2L * (Key.Length + (Snapshot?.Length ?? 0) + Earlier.Sum(cursor => cursor.Length)) + Pages.Sum(page => page.Bytes);
}
