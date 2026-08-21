using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RestartDecisionTests {
    static readonly BinaryStat A = new(100, 1000);

    [Test]
    public async Task Fires_when_pending_and_idle() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: false, force: false)).IsTrue();

    [Test]
    public async Task Does_not_fire_when_busy() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: true, force: false)).IsFalse();

    [Test]
    public async Task Force_overrides_busy() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: true, force: true)).IsTrue();

    [Test]
    public async Task Does_not_fire_when_not_pending() =>
        await Assert.That(RestartDecision.ShouldFire(pending: false, busy: false, force: true)).IsFalse();

    [Test]
    public async Task Size_change_is_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A with { Size = 200 })).IsTrue();

    [Test]
    public async Task Mtime_change_is_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A with { MtimeTicks = 2000 })).IsTrue();

    [Test]
    public async Task Identical_is_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A)).IsFalse();

    [Test]
    public async Task Null_current_is_transient_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, null)).IsFalse();

    [Test]
    public async Task Null_baseline_is_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(null, A)).IsFalse();
}
