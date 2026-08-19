using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Naming a session that was already running when the integration first arrived, and saying nothing
/// otherwise.
/// </summary>
/// <remarks>
/// Almost every run has nothing to report, and the value is as much in the silence as in the rare
/// true case: telling someone their session is uncaptured when it is not sends them to kill a
/// conversation for nothing.
/// </remarks>
internal sealed class StaleAgentDetectorTests {
    static IReadOnlyList<StaleAgentProcess> Find(int[] pids, Func<int, string?>? cwdOf = null) =>
        StaleAgentDetector.Find(
            [new StaleAgentTarget("kiro", "kiro-cli")],
            _ => pids,
            cwdOf ?? (_ => "/home/dev/proj"));

    [Test]
    public async Task A_running_session_is_reported_with_where_it_is() {
        var stale = Find([4821]);

        await Assert.That(stale).Count().IsEqualTo(1);
        await Assert.That(stale[0].Pid).IsEqualTo(4821);
        await Assert.That(stale[0].Cwd).IsEqualTo("/home/dev/proj");
    }

    [Test]
    public async Task Nothing_running_reports_nothing() {
        await Assert.That(Find([])).IsEmpty();
    }

    [Test]
    public async Task An_unreadable_working_directory_still_reports_the_process() {
        // Windows has no cheap same-user cwd API, so the pid-only form is the normal shape there.
        var stale = Find([4821], cwdOf: _ => null);

        await Assert.That(stale).Count().IsEqualTo(1);
        await Assert.That(stale[0].Cwd).IsNull();
    }

    [Test]
    public async Task Each_vendor_is_matched_against_its_own_process_name() {
        var stale = StaleAgentDetector.Find(
            [new StaleAgentTarget("codex", "codex"), new StaleAgentTarget("kiro", "kiro-cli")],
            name => name == "kiro-cli" ? [7] : [],
            _ => null);

        await Assert.That(stale.Select(s => s.Vendor).ToArray()).IsEquivalentTo(["kiro"]);
    }

    [Test]
    public async Task The_line_locates_the_session_and_names_the_remedy_that_exists() {
        var line = StaleAgentDetector
            .Describe([new StaleAgentProcess("kiro", 4821, "/home/dev/gaffer")])
            .Single();

        await Assert.That(line).Contains("/home/dev/gaffer");
        await Assert.That(line).Contains("4821");
        // The transcript is on disk either way, so withholding the backfill would name a problem and
        // hide its fix.
        await Assert.That(line).Contains("kcap import --kiro");
        // But no instruction we cannot complete: only the destructive half of a restart is ours.
        await Assert.That(line.Contains("restart", StringComparison.OrdinalIgnoreCase)).IsFalse();
    }
}
