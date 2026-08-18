using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[NotInParallel(nameof(DaemonLockPaths) + ".OverrideDirectoryForTesting")]
public class RestartCoordinatorTests : IDisposable {
    readonly TempDir _dir = new();

    [Before(HookType.Test)]
    public void Setup() {
        DaemonLockPaths.OverrideDirectoryForTesting(_dir.Path);
    }

    [After(HookType.Test)]
    public void Teardown() {
        DaemonLockPaths.OverrideDirectoryForTesting(null);
    }

    public void Dispose() => _dir.Dispose();

    sealed class SpyStrategy : IRestartStrategy {
        public int            Calls;
        public RestartOutcome Outcome = RestartOutcome.Initiated;
        public RestartOutcome Restart() { Calls++; return Outcome; }
    }

    static RestartCoordinator NewCoordinator(SpyStrategy spy, Func<bool> isBusy, Func<BinaryStat?> stat) {
        var c = RestartCoordinator.ForTest("laptop", "v0.4.11", spy);
        c.IsBusy     = isBusy;
        c.StatBinary = stat;
        c.PrimeBaseline();   // capture initial stat as baseline
        return c;
    }

    [Test]
    public async Task Binary_change_while_idle_triggers_restart() {
        var spy   = new SpyStrategy();
        var size  = 100L;
        var c     = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200; // simulate update landing
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Binary_change_while_busy_waits_then_fires_when_idle() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var busy = true;
        var c    = NewCoordinator(spy, isBusy: () => busy, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();                                   // busy → queued, no fire
        await Assert.That(spy.Calls).IsEqualTo(0);
        busy = false;
        c.Tick();                                   // idle → fire
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Transient_stat_failure_does_not_queue() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => false, stat: () => null);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Explicit_force_request_fires_even_when_busy() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => true, stat: () => new BinaryStat(100, 1));
        c.RequestRestart(force: true);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Restart_only_fires_once() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Failed_restart_is_retried_on_next_tick() {
        var spy  = new SpyStrategy { Outcome = RestartOutcome.Retry };
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();                                   // fires, strategy returns Retry → un-claimed
        await Assert.That(spy.Calls).IsEqualTo(1);
        c.Tick();                                   // still pending → retries
        await Assert.That(spy.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Noop_outcome_is_terminal() {
        var spy  = new SpyStrategy { Outcome = RestartOutcome.NoOp };
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);  // NoOp is terminal — no retry, no spam
    }

    [Test]
    public async Task Request_restart_fires_immediately_when_idle() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(100, 1));

        var result = c.RequestRestart(force: false);   // no Tick needed — evaluates now

        await Assert.That(spy.Calls).IsEqualTo(1);
        await Assert.That(result).IsEqualTo(RestartRequestResult.Restarting);
    }

    [Test]
    public async Task Request_restart_when_busy_queues_then_fires_when_idle() {
        var spy  = new SpyStrategy();
        var busy = true;
        var c    = NewCoordinator(spy, isBusy: () => busy, stat: () => new BinaryStat(100, 1));

        var result = c.RequestRestart(force: false);
        await Assert.That(result).IsEqualTo(RestartRequestResult.Queued);
        await Assert.That(spy.Calls).IsEqualTo(0);

        busy = false;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }
}
