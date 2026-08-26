using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

// Bare because KCAP_DAEMON_URL is read by more than one production command (also
// CodexHookCommand) and inherited by spawned children, so no cohort of key-holders
// can exclude its readers.
[NotInParallel]
public class PermissionRequestCommandTests {
    const string EnvVar = "KCAP_DAEMON_URL";

    [Test]
    public async Task ReturnsFalseWhenEnvVarIsUnset() {
        using var _ = EnvScope.Exclusive(EnvVar, null);

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task ReturnsFalseWhenEnvVarIsEmpty() {
        using var _ = EnvScope.Exclusive(EnvVar, "");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task AcceptsLoopbackHttpAndTrimsTrailingSlash() {
        using var _ = EnvScope.Exclusive(EnvVar, "http://127.0.0.1:51234/abc/");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsTrue();
        await Assert.That(url).IsEqualTo("http://127.0.0.1:51234/abc");
    }

    [Test]
    public async Task RejectsLocalhostDnsName() {
        // We require literal 127.0.0.1 — "localhost" could resolve to non-loopback in a misconfigured env.
        using var _ = EnvScope.Exclusive(EnvVar, "http://localhost:51234/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsNonLoopbackHost() {
        using var _ = EnvScope.Exclusive(EnvVar, "http://example.com:8080/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsHttpsLoopback() {
        // The daemon bridge is plain http on loopback — https implies a different
        // endpoint and shouldn't be accepted via this env var.
        using var _ = EnvScope.Exclusive(EnvVar, "https://127.0.0.1:51234/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test]
    public async Task RejectsMalformedUrl() {
        using var _ = EnvScope.Exclusive(EnvVar, "not-a-url");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }
}
