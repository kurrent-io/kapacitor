using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// Cross-process cache of the server's auth provider (the result of
/// <c>GET /auth/config</c>), keyed by base URL. A fresh hook process would
/// otherwise re-fetch <c>/auth/config</c> on every single event — a ~150–250 ms
/// round-trip on the SessionStart critical path (the in-process static in
/// <see cref="HttpClientExtensions"/> only helps within one process, and each
/// hook invocation is its own process).
///
/// <para>The provider for a given server is effectively static, so entries carry
/// a generous 24 h TTL and are refreshed opportunistically. Everything is
/// best-effort/fail-open: any read/write/parse error yields "no cache" and the
/// caller falls back to the live fetch — the cache never changes the auth
/// outcome, only whether the network call is skipped.</para>
/// </summary>
static class AuthProviderCache {
    static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    internal static string StorePath(ConfigRoot config) => config.Path("cache", "auth-providers.json");

    /// <summary>
    /// Pure: returns the still-fresh provider recorded for <paramref name="baseUrl"/> in
    /// <paramref name="storeJson"/>, or <c>null</c> when absent, expired, or malformed.
    /// </summary>
    internal static string? Read(string? storeJson, string baseUrl, long nowUnix, TimeSpan? ttl = null) {
        if (string.IsNullOrWhiteSpace(storeJson)) return null;

        try {
            if (JsonNode.Parse(storeJson) is not JsonObject obj) return null;
            if (obj[baseUrl] is not JsonObject entry) return null;

            var provider = entry["provider"]?.GetValue<string>();
            var fetched  = entry["fetched_at"]?.GetValue<long>();

            if (string.IsNullOrEmpty(provider) || fetched is null) return null;

            var age = nowUnix - fetched.Value;

            // Negative age (clock skew / tampered file) is treated as stale, not fresh.
            if (age < 0 || age > (long)(ttl ?? Ttl).TotalSeconds) return null;

            return provider;
        } catch {
            return null;
        }
    }

    /// <summary>
    /// Pure: returns <paramref name="storeJson"/> with <paramref name="baseUrl"/> upserted to
    /// <paramref name="provider"/> stamped at <paramref name="nowUnix"/>. A malformed store is
    /// replaced rather than propagated.
    /// </summary>
    internal static string Upsert(string? storeJson, string baseUrl, string provider, long nowUnix) {
        JsonObject obj;

        try {
            obj = (string.IsNullOrWhiteSpace(storeJson) ? null : JsonNode.Parse(storeJson)) as JsonObject ?? new JsonObject();
        } catch {
            obj = new JsonObject();
        }

        obj[baseUrl] = new JsonObject {
            ["provider"]   = provider,
            ["fetched_at"] = nowUnix
        };

        return obj.ToJsonString();
    }

    /// <summary>Best-effort disk read. Returns the cached provider or <c>null</c>.</summary>
    public static string? TryGet(string baseUrl, ConfigRoot config) {
        try {
            var path = StorePath(config);

            return File.Exists(path)
                ? Read(File.ReadAllText(path), baseUrl, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                : null;
        } catch {
            return null;
        }
    }

    /// <summary>Best-effort disk write. Silently no-ops on any failure.</summary>
    public static void Set(string baseUrl, string provider, ConfigRoot config) {
        try {
            var path = StorePath(config);
            var dir  = Path.GetDirectoryName(path);

            if (dir is not null) Directory.CreateDirectory(dir);

            var existing = File.Exists(path) ? File.ReadAllText(path) : null;

            File.WriteAllText(path, Upsert(existing, baseUrl, provider, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        } catch {
            // Best effort — the cache never breaks auth.
        }
    }
}
