using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportMissingCwdsReportTests {
    [Test, NotInParallel]
    public async Task Reports_missing_cwds_with_session_count_and_sample() {
        using var tmp = new TempDir();
        var existing = tmp.Path;
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = "/does/not/exist/repo-a",
            ["s2"] = "/does/not/exist/repo-a", // dup cwd → 1 distinct path, 2 sessions
            ["s3"] = "/does/not/exist/repo-b",
            ["s4"] = existing,
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d));

        await Assert.That(output).Contains("3 sessions reference 2 distinct paths that no longer exist on disk");
        await Assert.That(output).Contains("/does/not/exist/repo-a");
        await Assert.That(output).Contains("/does/not/exist/repo-b");
        await Assert.That(output).DoesNotContain(existing); // existing dir not reported
        await Assert.That(output).Contains("kcap remap");
    }

    [Test, NotInParallel]
    public async Task Hint_shifts_to_update_when_cwd_remap_already_configured() {
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = "/does/not/exist/repo-a",
        };

        var rules  = new[] { new CwdRemap { From = "/old", To = "/new" } };
        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, rules, d));

        await Assert.That(output).Contains("update or add mappings");
    }

    [Test, NotInParallel]
    public async Task Stays_silent_when_all_cwds_exist() {
        using var tmp = new TempDir();
        var existing = tmp.Path;
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = existing,
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d));

        await Assert.That(output).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task Stays_silent_when_no_sessions_have_cwds() {
        var output = Capture(d => ImportCommand.ReportMissingCwds(new Dictionary<string, string>(), cwdRemap: null, d));

        await Assert.That(output).IsEmpty();
    }

    [Test]
    public async Task ShortenHome_rewrites_home_prefix_to_tilde() {
        await Assert.That(ImportCommand.ShortenHome("/Users/alexey/dev/foo", "/Users/alexey"))
            .IsEqualTo("~/dev/foo");
    }

    [Test]
    public async Task ShortenHome_returns_tilde_for_exact_home() {
        await Assert.That(ImportCommand.ShortenHome("/Users/alexey", "/Users/alexey")).IsEqualTo("~");
    }

    [Test]
    public async Task ShortenHome_does_not_shrink_across_path_boundary() {
        // /Users/alexeyfoo must NOT become ~foo
        await Assert.That(ImportCommand.ShortenHome("/Users/alexeyfoo/dev", "/Users/alexey"))
            .IsEqualTo("/Users/alexeyfoo/dev");
    }

    [Test]
    public async Task ShortenHome_passes_through_non_home_paths() {
        await Assert.That(ImportCommand.ShortenHome("/var/tmp/x", "/Users/alexey")).IsEqualTo("/var/tmp/x");
    }

    [Test]
    public async Task ShortenHome_accepts_backslash_as_path_boundary() {
        // Even on a non-Windows host the helper must accept '\' as a separator
        // so transcripts that recorded Windows paths shrink correctly.
        await Assert.That(ImportCommand.ShortenHome(@"C:\Users\alexey\dev\foo", @"C:\Users\alexey"))
            .IsEqualTo(@"~\dev\foo");
    }

    [Test]
    public async Task CollapseDescendants_drops_worktree_when_parent_is_also_missing() {
        var input = new HashSet<string>(StringComparer.Ordinal) {
            "/dev/kapacitor",
            "/dev/kapacitor/.claude/worktrees/agent-1",
            "/dev/kapacitor/.claude/worktrees/agent-2",
        };

        var roots = ImportCommand.CollapseDescendants(input);

        await Assert.That(roots).IsEquivalentTo(new[] { "/dev/kapacitor" });
    }

    [Test]
    public async Task CollapseDescendants_keeps_siblings_when_no_common_parent_missing() {
        // /a/x and /a/y both missing, /a NOT missing → both remain.
        var input = new HashSet<string>(StringComparer.Ordinal) { "/a/x", "/a/y" };

        var roots = ImportCommand.CollapseDescendants(input);

        await Assert.That(roots).IsEquivalentTo(new[] { "/a/x", "/a/y" });
    }

    [Test, NotInParallel]
    public async Task Report_collapses_worktree_descendants_in_output() {
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = "/dev/kapacitor",
            ["s2"] = "/dev/kapacitor/.claude/worktrees/agent-1",
            ["s3"] = "/dev/kapacitor/.claude/worktrees/agent-2",
            ["s4"] = "/dev/other-repo",
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d));

        // 2 distinct roots (kapacitor + other-repo), 4 sessions still affected.
        await Assert.That(output).Contains("4 sessions reference 2 distinct paths");
        await Assert.That(output).Contains("/dev/kapacitor\n");
        await Assert.That(output).Contains("/dev/other-repo");
        await Assert.That(output).DoesNotContain("worktrees/agent-1");
        await Assert.That(output).DoesNotContain("worktrees/agent-2");
    }

    [Test, NotInParallel]
    public async Task ReportWorktreeAttributions_stays_silent_when_zero() {
        var output = Capture(d => ImportCommand.ReportWorktreeAttributions(0, d));
        await Assert.That(output).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task ReportWorktreeAttributions_reports_singular_phrasing_for_one() {
        var output = Capture(d => ImportCommand.ReportWorktreeAttributions(1, d));
        await Assert.That(output).Contains("Attributed 1 session to a parent project via worktree path.");
    }

    [Test, NotInParallel]
    public async Task ReportWorktreeAttributions_reports_plural_phrasing_for_many() {
        var output = Capture(d => ImportCommand.ReportWorktreeAttributions(477, d));
        await Assert.That(output).Contains("Attributed 477 sessions to a parent project via worktree path.");
    }

    [Test, NotInParallel]
    public async Task Truncates_sample_to_five_distinct_paths() {
        var sessionCwds = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["s1"] = "/missing/a",
            ["s2"] = "/missing/b",
            ["s3"] = "/missing/c",
            ["s4"] = "/missing/d",
            ["s5"] = "/missing/e",
            ["s6"] = "/missing/f",
            ["s7"] = "/missing/g",
        };

        var output = Capture(d => ImportCommand.ReportMissingCwds(sessionCwds, cwdRemap: null, d));

        await Assert.That(output).Contains("... and 2 more");
    }

    static string Capture(Action<ImportCommand.ImportDisplay> render) {
        using var capture = ConsoleOutput.StartCapture();
        render(new() { Tty = false });
        // Normalize CRLF→LF so line-anchored assertions (e.g. Contains("…\n"))
        // hold on Windows, where the writer emits Environment.NewLine.
        return capture.GetCapturedOutput().Replace("\r\n", "\n");
    }
}
