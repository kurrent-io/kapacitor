using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The watcher's parent-exit watchdog must NEVER be skipped silently. The original
/// bug was invisible precisely because, when the resolved parent PID was already
/// dead at watcher startup, the guard did nothing and logged nothing — the watcher
/// ran forever, orphaned, holding the session "active". These tests pin the explicit
/// three-way decision so the skip cases are observable.
/// </summary>
public class ParentWatchdogDecisionTests {
    [Test]
    public async Task Monitors_when_parent_pid_is_alive() {
        var decision = WatchCommand.DecideParentWatchdog(parentPid: 4242, isAlive: _ => true);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentWatchdog.Monitor);
    }

    [Test]
    public async Task Reports_no_parent_pid_when_null() {
        var decision = WatchCommand.DecideParentWatchdog(parentPid: null, isAlive: _ => true);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentWatchdog.NoParentPid);
    }

    [Test]
    public async Task Reports_parent_already_dead_at_startup() {
        // The actual stuck-session failure mode: a dead PID at startup must be a
        // distinct, surfaced outcome — not silently treated as "monitoring".
        var decision = WatchCommand.DecideParentWatchdog(parentPid: 4242, isAlive: _ => false);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentWatchdog.ParentAlreadyDead);
    }
}
