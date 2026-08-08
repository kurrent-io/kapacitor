using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Telemetry;

/// <summary>Builds the PostHog <c>/batch/</c> request body.</summary>
public static class PostHogPayload {
    const string SaasSuffix = ".kcap.ai";

    /// <summary>
    /// The `organization` group value, or null when it cannot be derived soundly.
    ///
    /// The server sets the group from `Tenant:Name`, which the Helm chart populates from the
    /// tenant slug — and a SaaS tenant is served at {slug}.kcap.ai, so the host label IS the
    /// group. That correspondence exists ONLY for SaaS: on a self-hosted deployment
    /// `Tenant:Name` defaults to "local" and is otherwise operator-chosen with no relationship
    /// to the hostname, so deriving a group there would produce one that looks joined to the
    /// server's but is not.
    /// </summary>
    public static string? OrgGroup(string? serverUrl) {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) return null;

        var host = uri.Host.ToLowerInvariant();
        if (!host.EndsWith(SaasSuffix, StringComparison.Ordinal)) return null;

        var slug = host[..^SaasSuffix.Length];

        return slug.Length == 0 || slug.Contains('.') ? null : slug;
    }

    public static string Build(
            IReadOnlyList<TelemetryEvent> events, string token, string distinctId, string? orgGroup) {
        var batch = new JsonArray();

        foreach (var e in events) {
            var props = (JsonObject)e.Properties.DeepClone();
            props["distinct_id"] = distinctId;

            // `$ip: null` alone does NOT suppress PostHog's GeoIP enrichment: $ip is populated
            // from the connecting IP regardless of this property, and the GeoIP transform falls
            // back to that request IP whenever $ip is falsy. `$geoip_disable: true` is PostHog's
            // documented switch for the enrichment itself. Both are set — $ip null is belt,
            // $geoip_disable is braces — because leaving only the former ships every event with
            // the developer's real-IP-derived $geoip_country_name/city/lat/long, on an EU-hosted
            // project whose privacy policy states an IP-discard posture.
            props["$ip"]            = null;
            props["$geoip_disable"] = true;

            // Group and property travel together, and only for SaaS. Deriving an `org` from a
            // self-hosted host label would put an internal hostname fragment in the data for no
            // analytical gain — it has no relationship to the server's own Tenant:Name.
            if (orgGroup is not null) {
                props["$groups"] = new JsonObject { ["organization"] = orgGroup };
                props["org"]     = orgGroup;
            }

            // Not batch.Add(new JsonObject {...}) directly: JsonArray.Add<T>(T) binds whenever the
            // argument's static type is narrower than JsonNode? — JsonObject qualifies even though
            // it IS a JsonNode, because exact-type overload betterness still prefers the generic
            // over the widening conversion to Add(JsonNode?). Only a JsonNode?-typed local selects
            // the AOT-safe non-generic overload.
            JsonNode? entry = new JsonObject {
                ["event"]      = e.Name,
                ["properties"] = props,
                ["timestamp"]  = e.Timestamp.ToString("o"),
            };
            batch.Add(entry);
        }

        return new JsonObject {
            ["api_key"] = token,
            ["batch"]   = batch,
        }.ToJsonString();
    }
}
