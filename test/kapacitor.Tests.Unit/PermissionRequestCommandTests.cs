using kapacitor.Commands;

namespace kapacitor.Tests.Unit;

public class PermissionRequestCommandTests {
    const string EnvVar = "KAPACITOR_DAEMON_URL";

    [Test, NotInParallel(nameof(PermissionRequestCommandTests))]
    public async Task ReturnsFalseWhenEnvVarIsUnset() {
        using var _ = new EnvVarScope(EnvVar, null);

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test, NotInParallel(nameof(PermissionRequestCommandTests))]
    public async Task ReturnsFalseWhenEnvVarIsEmpty() {
        using var _ = new EnvVarScope(EnvVar, "");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test, NotInParallel(nameof(PermissionRequestCommandTests))]
    public async Task AcceptsLoopbackHttpAndTrimsTrailingSlash() {
        using var _ = new EnvVarScope(EnvVar, "http://127.0.0.1:51234/abc/");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsTrue();
        await Assert.That(url).IsEqualTo("http://127.0.0.1:51234/abc");
    }

    [Test, NotInParallel(nameof(PermissionRequestCommandTests))]
    public async Task AcceptsLocalhostHttp() {
        using var _ = new EnvVarScope(EnvVar, "http://localhost:51234/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsTrue();
        await Assert.That(url).IsEqualTo("http://localhost:51234/tok");
    }

    [Test, NotInParallel(nameof(PermissionRequestCommandTests))]
    public async Task RejectsNonLoopbackHost() {
        using var _ = new EnvVarScope(EnvVar, "http://example.com:8080/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test, NotInParallel(nameof(PermissionRequestCommandTests))]
    public async Task RejectsHttpsLoopback() {
        // The daemon bridge is plain http on loopback — https implies a different
        // endpoint and shouldn't be accepted via this env var.
        using var _ = new EnvVarScope(EnvVar, "https://127.0.0.1:51234/tok");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }

    [Test, NotInParallel(nameof(PermissionRequestCommandTests))]
    public async Task RejectsMalformedUrl() {
        using var _ = new EnvVarScope(EnvVar, "not-a-url");

        var ok = PermissionRequestCommand.TryGetLoopbackDaemonUrl(out var url);

        await Assert.That(ok).IsFalse();
        await Assert.That(url).IsEqualTo("");
    }
}

sealed class EnvVarScope : IDisposable {
    readonly string  _name;
    readonly string? _previous;

    public EnvVarScope(string name, string? value) {
        _name     = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
}
