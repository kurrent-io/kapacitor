namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// <see cref="AuthProviderCache"/> lets a fresh hook process skip the ~150–250 ms
/// <c>/auth/config</c> round-trip by reading a recent result from disk. The pure
/// <c>Read</c>/<c>Upsert</c> helpers carry the TTL + scoping logic and are tested
/// here without touching disk; a single round-trip test covers the disk wrappers.
/// </summary>
public class AuthProviderCacheTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const long Now = 1_000_000;

    [Test]
    public async Task Read_returns_provider_within_ttl() {
        var store = AuthProviderCache.Upsert(null, "https://a.example", "WorkOS", Now);

        await Assert.That(AuthProviderCache.Read(store, "https://a.example", Now + 60)).IsEqualTo("WorkOS");
    }

    [Test]
    public async Task Read_returns_null_when_expired() {
        var store   = AuthProviderCache.Upsert(null, "https://a.example", "WorkOS", Now);
        var wayLater = Now + (long)TimeSpan.FromHours(48).TotalSeconds;

        await Assert.That(AuthProviderCache.Read(store, "https://a.example", wayLater)).IsNull();
    }

    [Test]
    public async Task Read_treats_negative_age_as_stale() {
        // fetched_at in the future (clock skew / tampered file) must not count as fresh.
        var store = AuthProviderCache.Upsert(null, "https://a.example", "WorkOS", Now);

        await Assert.That(AuthProviderCache.Read(store, "https://a.example", Now - 60)).IsNull();
    }

    [Test]
    public async Task Read_is_scoped_per_base_url() {
        var store = AuthProviderCache.Upsert(null, "https://a.example", "WorkOS", Now);

        await Assert.That(AuthProviderCache.Read(store, "https://b.example", Now)).IsNull();
    }

    [Test]
    public async Task Upsert_replaces_existing_entry_and_preserves_others() {
        var store = AuthProviderCache.Upsert(null, "https://a.example", "WorkOS", Now);
        store     = AuthProviderCache.Upsert(store, "https://b.example", "GitHubApp", Now);
        store     = AuthProviderCache.Upsert(store, "https://a.example", "None", Now + 5);

        await Assert.That(AuthProviderCache.Read(store, "https://a.example", Now + 5)).IsEqualTo("None");
        await Assert.That(AuthProviderCache.Read(store, "https://b.example", Now + 5)).IsEqualTo("GitHubApp");
    }

    [Test]
    public async Task Read_returns_null_on_malformed_store() {
        await Assert.That(AuthProviderCache.Read("not json", "https://a.example", Now)).IsNull();
        await Assert.That(AuthProviderCache.Read("[]", "https://a.example", Now)).IsNull();
        await Assert.That(AuthProviderCache.Read(null, "https://a.example", Now)).IsNull();
    }

    [Test]
    public async Task Upsert_replaces_malformed_store_rather_than_throwing() {
        var store = AuthProviderCache.Upsert("{ broken", "https://a.example", "WorkOS", Now);

        await Assert.That(AuthProviderCache.Read(store, "https://a.example", Now)).IsEqualTo("WorkOS");
    }

    [Test]
    public async Task Set_then_TryGet_round_trips_on_disk() {
        await Assert.That(AuthProviderCache.TryGet("https://rt.example", Config.Root)).IsNull(); // cold
        AuthProviderCache.Set("https://rt.example", "GitHubApp", Config.Root);
        await Assert.That(AuthProviderCache.TryGet("https://rt.example", Config.Root)).IsEqualTo("GitHubApp");
    }
}
