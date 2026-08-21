namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Round-trip + normalization coverage for <see cref="ServerVersionStore"/>, the durable per-server
/// cache of the <c>X-Kcap-Server-Version</c> header. Writes into the assembly-wide shared config dir
/// (set by the module initializer); every test uses a UNIQUE server URL so the per-URL hashed files
/// never collide with a sibling test.
/// </summary>
public class ServerVersionStoreTests {
    // A distinct URL per test/case so the hashed filename (and the in-process dedup) can't collide.
    static string UniqueUrl(string tag) => $"https://{tag}-{Guid.NewGuid():N}.example.com";

    [Test]
    public async Task SetThenGet_RoundTrips() {
        var url = UniqueUrl("roundtrip");
        ServerVersionStore.Set(url, "0.11.15");

        await Assert.That(ServerVersionStore.Get(url)).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Get_UnknownServer_ReturnsNull() {
        await Assert.That(ServerVersionStore.Get(UniqueUrl("unknown"))).IsNull();
    }

    [Test]
    public async Task Get_BlankUrl_ReturnsNull() {
        await Assert.That(ServerVersionStore.Get(null)).IsNull();
        await Assert.That(ServerVersionStore.Get("   ")).IsNull();
    }

    [Test]
    public async Task Set_NormalizesTrailingSlashAndCase() {
        var host = $"cap-{Guid.NewGuid():N}.example.com";
        ServerVersionStore.Set($"https://{host}/", "0.11.15");

        // A different-cased, slash-free spelling of the same server resolves the same entry.
        await Assert.That(ServerVersionStore.Get($"HTTPS://{host.ToUpperInvariant()}")).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Set_BlankVersionOrUrl_IsNoOp() {
        var url = UniqueUrl("blank");
        ServerVersionStore.Set(url, "");
        ServerVersionStore.Set(url, null);
        ServerVersionStore.Set(null, "0.11.15");
        ServerVersionStore.Set("  ", "0.11.15");

        await Assert.That(ServerVersionStore.Get(url)).IsNull();
    }

    [Test]
    public async Task Set_DefaultPort_ConvergesWithImplicit() {
        // https://host and https://host:443 are the same server — one cache entry, not two.
        var host = $"cap-{Guid.NewGuid():N}.example.com";
        ServerVersionStore.Set($"https://{host}", "0.11.15");

        await Assert.That(ServerVersionStore.Get($"https://{host}:443")).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Set_PathCase_IsSignificant() {
        // Path-routed deployments are DISTINCT servers; path casing must not be flattened, or one tenant
        // would be capped against another's server version.
        var host = $"cap-{Guid.NewGuid():N}.example.com";
        ServerVersionStore.Set($"https://{host}/TenantA", "0.11.15");

        await Assert.That(ServerVersionStore.Get($"https://{host}/tenanta")).IsNull();
        await Assert.That(ServerVersionStore.Get($"https://{host}/TenantA")).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Set_DistinctServers_AreIndependent() {
        var a = UniqueUrl("srv-a");
        var b = UniqueUrl("srv-b");
        ServerVersionStore.Set(a, "0.11.15");
        ServerVersionStore.Set(b, "0.12.0");

        await Assert.That(ServerVersionStore.Get(a)).IsEqualTo("0.11.15");
        await Assert.That(ServerVersionStore.Get(b)).IsEqualTo("0.12.0");
    }

    [Test]
    public async Task Set_OverwritesWithNewerObservation() {
        var url = UniqueUrl("overwrite");
        ServerVersionStore.Set(url, "0.11.15");
        ServerVersionStore.Set(url, "0.12.0"); // a later deploy bumped the server

        await Assert.That(ServerVersionStore.Get(url)).IsEqualTo("0.12.0");
    }
}
