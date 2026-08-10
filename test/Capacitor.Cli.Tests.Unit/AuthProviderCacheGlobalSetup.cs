using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Assembly-level isolation for <see cref="AuthProviderCache"/>. Unit tests run against the
/// developer's real <c>~/.config/kcap</c> (only the daemons dir is pinned elsewhere), so without
/// this any test whose SUT calls <c>DiscoverProviderAsync</c> could read a provider cached by a
/// prior run (an OS-reused WireMock port would collide) and skip its own <c>/auth/config</c> stub,
/// or pollute the real cache file. Pinning the store to a throwaway temp file per run keeps the
/// cache a clean no-op for every test that doesn't explicitly exercise it.
///
/// <para>Pinning alone is not enough: the pinned file is <b>shared for the whole run</b>, so an
/// entry one test writes for <c>http://localhost:{port}</c> survives into a later test whose
/// WireMock server the OS handed the same freed port — which then reads that stale provider instead
/// of its own stub. <see cref="ClearBetweenTests"/> deletes the file before every test so each
/// starts from an empty store. Without it, <c>ReportVersionCommandTests.NotAuthenticated</c> read a
/// <c>"None"</c> left by an earlier <c>StubDiscovery("None")</c> test and issued a request it
/// asserts never happens — a deterministic CI failure that never reproduced locally.</para>
/// </summary>
public class AuthProviderCacheGlobalSetup {
    static readonly string StoreFile = Path.Combine(
        Path.GetTempPath(),
        "kcap-authprovider-tests-" + Guid.NewGuid().ToString("N")[..8] + ".json"
    );

    [Before(Assembly)]
    public static void PinStore() => AuthProviderCache.OverridePathForTesting = StoreFile;

    [BeforeEvery(Test)]
    public static void ClearBetweenTests() {
        try { File.Delete(StoreFile); } catch { /* best effort */ }
    }

    [After(Assembly)]
    public static void CleanupStore() {
        AuthProviderCache.OverridePathForTesting = null;
        try { File.Delete(StoreFile); } catch { /* best effort */ }
    }
}
