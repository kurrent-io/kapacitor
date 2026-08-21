namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Tests for <see cref="DaemonNameResolver"/> — the shared name-resolution
/// helper used by both the CLI supervisor and the daemon binary. Keeping
/// these paths in lockstep is critical: a mismatch would mean the CLI
/// checks one PID file while the daemon writes to a different one, and
/// the PID-file guard would be bypassable again.
/// </summary>
[NotInParallel("KCAP_DAEMON_NAME")]
public class DaemonNameResolverTests {
    static readonly string? OriginalEnv = Environment.GetEnvironmentVariable("KCAP_DAEMON_NAME");

    static void Reset() => Environment.SetEnvironmentVariable("KCAP_DAEMON_NAME", OriginalEnv);

    [Test]
    public async Task Resolve_PrefersNameArg() {
        Environment.SetEnvironmentVariable("KCAP_DAEMON_NAME", null);

        try {
            var name = DaemonNameResolver.Resolve(["--name", "from-arg"], profileName: "from-profile");
            await Assert.That(name).IsEqualTo("from-arg");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_FallsBackToProfile_WhenNoArgOrEnv() {
        Environment.SetEnvironmentVariable("KCAP_DAEMON_NAME", null);

        try {
            var name = DaemonNameResolver.Resolve([], profileName: "from-profile");
            await Assert.That(name).IsEqualTo("from-profile");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_EnvVarOverridesProfile() {
        Environment.SetEnvironmentVariable("KCAP_DAEMON_NAME", "from-env");

        try {
            var name = DaemonNameResolver.Resolve([], profileName: "from-profile");
            await Assert.That(name).IsEqualTo("from-env");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_NameArgOverridesEnvVar() {
        // DaemonRunner previously had the env var trump --name,
        // which inverted the usual CLI convention (explicit flag is the
        // strongest signal). The new precedence puts --name first so the
        // user's explicit choice always wins.
        Environment.SetEnvironmentVariable("KCAP_DAEMON_NAME", "from-env");

        try {
            var name = DaemonNameResolver.Resolve(["--name", "from-arg"], profileName: "from-profile");
            await Assert.That(name).IsEqualTo("from-arg");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_FallsBackToUsername_WhenNothingProvided() {
        Environment.SetEnvironmentVariable("KCAP_DAEMON_NAME", null);

        try {
            var name = DaemonNameResolver.Resolve([]);
            // Should at minimum produce a non-empty fallback. The exact value
            // depends on the test runner environment, so just assert it's
            // sane.
            await Assert.That(name).IsNotEmpty();
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_Throws_WhenNameFlagHasNoValue() {
        Environment.SetEnvironmentVariable("KCAP_DAEMON_NAME", null);

        try {
            var ex = Assert.Throws<ArgumentException>(() => DaemonNameResolver.Resolve(["--name"]));
            await Assert.That(ex.Message).Contains("--name requires a value");
        } finally {
            Reset();
        }
    }

    [Test]
    public async Task Resolve_Throws_WhenNameFlagValueLooksLikeAnotherFlag() {
        // Defends against `agent stop --name --yes` (which would otherwise
        // try to stop a daemon literally named "--yes") and the more
        // dangerous `agent stop --yes --name` chain — see PR 67 review.
        Environment.SetEnvironmentVariable("KCAP_DAEMON_NAME", null);

        try {
            var ex = Assert.Throws<ArgumentException>(() => DaemonNameResolver.Resolve(["--name", "--yes"]));
            await Assert.That(ex.Message).Contains("--name requires a value");
        } finally {
            Reset();
        }
    }
}
