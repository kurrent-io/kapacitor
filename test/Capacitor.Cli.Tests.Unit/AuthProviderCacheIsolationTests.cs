using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Guards the between-tests reset in <see cref="AuthProviderCacheGlobalSetup"/>. The provider cache
/// is a single shared on-disk store for the whole run, so without a clear between tests one test can
/// read an entry another test wrote under the same base URL — and an OS-reused WireMock port makes
/// that collision real. Each test asserts the store holds nothing for its key, then writes it;
/// whichever of the two runs second fails when the reset is missing.
///
/// <para>This is the isolated regression for the CI-only failure of
/// <c>ReportVersionCommandTests.NotAuthenticated_MakesNoRequest_AndReturnsZero</c>: a stale
/// <c>"None"</c> entry left by an earlier <c>StubDiscovery("None")</c> test was served to it, so
/// discovery reported "no auth required" and the command issued the very request the test asserts
/// never happens.</para>
/// </summary>
[NotInParallel(nameof(AuthProviderCacheIsolationTests))]
public class AuthProviderCacheIsolationTests {
    const string Url = "http://authprovider-isolation.invalid";

    static async Task AssertStoreCleanThenPolluteAsync() {
        await Assert.That(AuthProviderCache.TryGet(Url)).IsNull();
        AuthProviderCache.Set(Url, "None");
    }

    [Test]
    public Task StoreIsCleanAtTestStart_A() => AssertStoreCleanThenPolluteAsync();

    [Test]
    public Task StoreIsCleanAtTestStart_B() => AssertStoreCleanThenPolluteAsync();
}
