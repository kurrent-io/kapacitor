using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit.Config;

public class ServerUrlNormalizerLoopbackTests {
    [Test]
    [Arguments("localhost", "http://localhost")]
    [Arguments("localhost:5108", "http://localhost:5108")]
    [Arguments("127.0.0.1", "http://127.0.0.1")]
    [Arguments("127.0.0.1:8080", "http://127.0.0.1:8080")]
    [Arguments("::1", "http://::1")]
    [Arguments("[::1]", "http://[::1]")]
    [Arguments("[::1]:5108", "http://[::1]:5108")]
    [Arguments("host.docker.internal", "http://host.docker.internal")]
    [Arguments("host.docker.internal:5108", "http://host.docker.internal:5108")]
    public async Task Loopback_HostsGetHttp(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("staging.kcap.ai", "https://staging.kcap.ai")]
    [Arguments("staging.kcap.ai:8443", "https://staging.kcap.ai:8443")]
    [Arguments("example.com/api", "https://example.com/api")]
    public async Task NonLoopback_HostsGetHttps(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("https://staging.kcap.ai/", "https://staging.kcap.ai")]
    [Arguments("http://localhost:5108", "http://localhost:5108")]
    [Arguments("https://example.com", "https://example.com")]
    public async Task ExistingScheme_IsPreservedAndTrimmed(string input, string expected) {
        var result = ServerUrlNormalizer.WithLoopbackDefault(input);
        await Assert.That(result).IsEqualTo(expected);
    }
}

public class ServerUrlNormalizerOrchestrationTests {
    static Func<string, TimeSpan, CancellationToken, Task<bool>> Probe(Func<string, bool> reachable) =>
        (url, _, _) => Task.FromResult(reachable(url));

    [Test]
    public async Task SkipProbe_ReturnsLoopbackDefault_NoWarning_NoProbeCalls() {
        var probeCalls = 0;
        var probe = Probe(u => { probeCalls++; return true; });

        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kcap.ai", skipProbe: true, CancellationToken.None, probe);

        await Assert.That(result.Url).IsEqualTo("https://staging.kcap.ai");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probeCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SchemePresent_ProbeSucceeds_NoWarning() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "https://staging.kcap.ai/", skipProbe: false, CancellationToken.None,
            Probe(_ => true));

        await Assert.That(result.Url).IsEqualTo("https://staging.kcap.ai");
        await Assert.That(result.Warning).IsNull();
    }

    [Test]
    public async Task SchemePresent_ProbeFails_Warns() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "https://staging.kcap.ai", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("https://staging.kcap.ai");
        await Assert.That(result.Warning).IsNotNull();
        await Assert.That(result.Warning!).Contains("could not reach");
        await Assert.That(result.Reachable).IsFalse();
    }

    [Test]
    public async Task SchemeMissing_HttpsFails_HttpSucceeds_NonLoopback_WarnsAboutDowngrade() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kcap.ai", skipProbe: false, CancellationToken.None,
            (u, _, _) => Task.FromResult(u.StartsWith("http://", StringComparison.Ordinal)));

        await Assert.That(result.Url).IsEqualTo("http://staging.kcap.ai");
        await Assert.That(result.Warning).IsNotNull();
        await Assert.That(result.Warning!).Contains("https probe failed");
        await Assert.That(result.Reachable).IsTrue();
    }

    [Test]
    public async Task SchemeMissing_HttpsSucceeds_UsesHttps() {
        var probedUrls = new List<string>();
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kcap.ai", skipProbe: false, CancellationToken.None,
            (u, _, _) => { probedUrls.Add(u); return Task.FromResult(u.StartsWith("https://", StringComparison.Ordinal)); });

        await Assert.That(result.Url).IsEqualTo("https://staging.kcap.ai");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probedUrls[0]).IsEqualTo("https://staging.kcap.ai");
    }

    [Test]
    public async Task SchemeMissing_HttpsFails_HttpSucceeds_UsesHttp() {
        var probedUrls = new List<string>();
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "localhost:5108", skipProbe: false, CancellationToken.None,
            (u, _, _) => { probedUrls.Add(u); return Task.FromResult(u.StartsWith("http://", StringComparison.Ordinal)); });

        await Assert.That(result.Url).IsEqualTo("http://localhost:5108");
        await Assert.That(result.Warning).IsNull();
        await Assert.That(probedUrls.Count).IsEqualTo(2);
        await Assert.That(probedUrls[0]).StartsWith("https://");
        await Assert.That(probedUrls[1]).StartsWith("http://");
    }

    [Test]
    public async Task SchemeMissing_BothFail_FallsBackToLoopbackDefault_Warns() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "staging.kcap.ai", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("https://staging.kcap.ai");
        await Assert.That(result.Warning).IsNotNull();
        await Assert.That(result.Warning!).Contains("could not reach");
        await Assert.That(result.Reachable).IsFalse();
    }

    [Test]
    public async Task SchemeMissing_BothFail_Loopback_UsesHttpFallback() {
        var result = await ServerUrlNormalizer.NormalizeAsync(
            "localhost:5108", skipProbe: false, CancellationToken.None,
            Probe(_ => false));

        await Assert.That(result.Url).IsEqualTo("http://localhost:5108");
        await Assert.That(result.Warning).IsNotNull();
    }
}
