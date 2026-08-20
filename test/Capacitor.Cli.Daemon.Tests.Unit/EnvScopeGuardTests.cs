namespace Capacitor.Cli.Daemon.Tests.Unit;

/// <summary>
/// The convention <see cref="EnvScope"/> enforces, asserted where the suite that depends on it can
/// see it break: a keyed constraint does not exclude the readers of a process-global variable, and
/// no constraint at all excludes nobody.
/// </summary>
/// <remarks>
/// A variable per test, because these carry three different constraints on purpose: a shared name
/// would let the unmarked test read what the keyed one is holding — the very race the type refuses.
/// Each assertion compares against a snapshot rather than null, since the host environment is free
/// to define anything.
/// </remarks>
public class EnvScopeGuardTests {
    [Test, NotInParallel]
    public async Task Exclusive_under_a_bare_constraint_sets_and_restores() {
        const string probe = "KCAP_ENVSCOPE_PROBE_BARE";
        var          before = Environment.GetEnvironmentVariable(probe);

        using (EnvScope.Exclusive(probe, "set")) {
            await Assert.That(Environment.GetEnvironmentVariable(probe)).IsEqualTo("set");
        }

        await Assert.That(Environment.GetEnvironmentVariable(probe)).IsEqualTo(before);
    }

    [Test, NotInParallel("EnvScopeGuardTests")]
    public async Task Exclusive_under_a_keyed_constraint_is_refused() {
        const string probe = "KCAP_ENVSCOPE_PROBE_KEYED_EXCLUSIVE";
        var          before = Environment.GetEnvironmentVariable(probe);

        var ex = Assert.Throws<InvalidOperationException>(() => EnvScope.Exclusive(probe, "set"));

        await Assert.That(ex!.Message).Contains("bare [NotInParallel]");
        // Refused before it wrote anything.
        await Assert.That(Environment.GetEnvironmentVariable(probe)).IsEqualTo(before);
    }

    [Test, NotInParallel("EnvScopeGuardTests")]
    public async Task The_constructor_accepts_a_keyed_constraint() {
        const string probe = "KCAP_ENVSCOPE_PROBE_KEYED_CTOR";
        var          before = Environment.GetEnvironmentVariable(probe);

        using (new EnvScope(probe, "set")) {
            await Assert.That(Environment.GetEnvironmentVariable(probe)).IsEqualTo("set");
        }

        await Assert.That(Environment.GetEnvironmentVariable(probe)).IsEqualTo(before);
    }

    [Test]
    public async Task An_unmarked_test_is_refused() {
        const string probe = "KCAP_ENVSCOPE_PROBE_UNMARKED";
        var          before = Environment.GetEnvironmentVariable(probe);

        var ex = Assert.Throws<InvalidOperationException>(() => new EnvScope(probe, "set").Dispose());

        await Assert.That(ex!.Message).Contains("[NotInParallel]");
        await Assert.That(Environment.GetEnvironmentVariable(probe)).IsEqualTo(before);
    }
}
