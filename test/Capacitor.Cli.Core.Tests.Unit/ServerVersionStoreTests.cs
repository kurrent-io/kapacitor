namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// Round-trip + normalization coverage for <see cref="ServerVersionStore"/>, the durable per-server
/// cache of the <c>X-Kcap-Server-Version</c> header. Each test writes into its own root, so the
/// hashed per-URL files are private to it.
/// </summary>
public class ServerVersionStoreTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static string Host(string tag) => $"{tag}.example.com";
    static string Url(string tag)  => $"https://{Host(tag)}";

    [Test]
    public async Task SetThenGet_RoundTrips() {
        var url = Url("roundtrip");
        ServerVersionStore.Set(url, "0.11.15", Config.Root);

        await Assert.That(ServerVersionStore.Get(url, Config.Root)).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Get_UnknownServer_ReturnsNull() {
        await Assert.That(ServerVersionStore.Get(Url("unknown"), Config.Root)).IsNull();
    }

    [Test]
    public async Task Get_BlankUrl_ReturnsNull() {
        await Assert.That(ServerVersionStore.Get(null, Config.Root)).IsNull();
        await Assert.That(ServerVersionStore.Get("   ", Config.Root)).IsNull();
    }

    [Test]
    public async Task Set_NormalizesTrailingSlashAndCase() {
        var host = Host("normalize");
        ServerVersionStore.Set($"https://{host}/", "0.11.15", Config.Root);

        // A different-cased, slash-free spelling of the same server resolves the same entry.
        await Assert.That(ServerVersionStore.Get($"HTTPS://{host.ToUpperInvariant()}", Config.Root)).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Set_BlankVersionOrUrl_IsNoOp() {
        var url = Url("blank");
        ServerVersionStore.Set(url, "", Config.Root);
        ServerVersionStore.Set(url, null, Config.Root);
        ServerVersionStore.Set(null, "0.11.15", Config.Root);
        ServerVersionStore.Set("  ", "0.11.15", Config.Root);

        await Assert.That(ServerVersionStore.Get(url, Config.Root)).IsNull();
    }

    [Test]
    public async Task Set_DefaultPort_ConvergesWithImplicit() {
        // https://host and https://host:443 are the same server — one cache entry, not two.
        var host = Host("default-port");
        ServerVersionStore.Set($"https://{host}", "0.11.15", Config.Root);

        await Assert.That(ServerVersionStore.Get($"https://{host}:443", Config.Root)).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Set_PathCase_IsSignificant() {
        // Path-routed deployments are DISTINCT servers; path casing must not be flattened, or one tenant
        // would be capped against another's server version.
        var host = Host("path-case");
        ServerVersionStore.Set($"https://{host}/TenantA", "0.11.15", Config.Root);

        await Assert.That(ServerVersionStore.Get($"https://{host}/tenanta", Config.Root)).IsNull();
        await Assert.That(ServerVersionStore.Get($"https://{host}/TenantA", Config.Root)).IsEqualTo("0.11.15");
    }

    [Test]
    public async Task Set_DistinctServers_AreIndependent() {
        var a = Url("srv-a");
        var b = Url("srv-b");
        ServerVersionStore.Set(a, "0.11.15", Config.Root);
        ServerVersionStore.Set(b, "0.12.0", Config.Root);

        await Assert.That(ServerVersionStore.Get(a, Config.Root)).IsEqualTo("0.11.15");
        await Assert.That(ServerVersionStore.Get(b, Config.Root)).IsEqualTo("0.12.0");
    }

    [Test]
    public async Task Set_OverwritesWithNewerObservation() {
        var url = Url("overwrite");
        ServerVersionStore.Set(url, "0.11.15", Config.Root);
        ServerVersionStore.Set(url, "0.12.0", Config.Root); // a later deploy bumped the server

        await Assert.That(ServerVersionStore.Get(url, Config.Root)).IsEqualTo("0.12.0");
    }
}
