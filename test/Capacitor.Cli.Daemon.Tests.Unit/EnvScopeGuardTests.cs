namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// The convention <see cref="EnvScope"/> enforces, asserted where the suite that depends on it can
/// see it break: a keyed constraint does not exclude the readers of a process-global variable, and
/// no constraint at all excludes nobody.
/// </summary>
public class EnvScopeGuardTests {
    const string Probe = "KCAP_ENVSCOPE_GUARD_PROBE";

    [Test, NotInParallel]
    public async Task Exclusive_under_a_bare_constraint_sets_and_restores() {
        var before = Environment.GetEnvironmentVariable(Probe);

        using (EnvScope.Exclusive(Probe, "set")) {
            await Assert.That(Environment.GetEnvironmentVariable(Probe)).IsEqualTo("set");
        }

        await Assert.That(Environment.GetEnvironmentVariable(Probe)).IsEqualTo(before);
    }

    [Test, NotInParallel("EnvScopeGuardTests")]
    public async Task Exclusive_under_a_keyed_constraint_is_refused() {
        var ex = Assert.Throws<InvalidOperationException>(() => EnvScope.Exclusive(Probe, "set"));

        await Assert.That(ex!.Message).Contains("bare [NotInParallel]");
        await Assert.That(Environment.GetEnvironmentVariable(Probe)).IsNull();
    }

    [Test, NotInParallel("EnvScopeGuardTests")]
    public async Task The_constructor_accepts_a_keyed_constraint() {
        using (new EnvScope(Probe, "set")) {
            await Assert.That(Environment.GetEnvironmentVariable(Probe)).IsEqualTo("set");
        }
    }

    [Test]
    public async Task An_unmarked_test_is_refused() {
        var ex = Assert.Throws<InvalidOperationException>(() => new EnvScope(Probe, "set").Dispose());

        await Assert.That(ex!.Message).Contains("[NotInParallel]");
        await Assert.That(Environment.GetEnvironmentVariable(Probe)).IsNull();
    }
}
