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
            props["$ip"]         = null;   // suppress geo-IP resolution at ingest

            // Group and property travel together, and only for SaaS. Deriving an `org` from a
            // self-hosted host label would put an internal hostname fragment in the data for no
            // analytical gain — it has no relationship to the server's own Tenant:Name.
            if (orgGroup is not null) {
                props["$groups"] = new JsonObject { ["organization"] = orgGroup };
                props["org"]     = orgGroup;
            }

            batch.Add(new JsonObject {
                ["event"]      = e.Name,
                ["properties"] = props,
                ["timestamp"]  = e.Timestamp.ToString("o"),
            });
        }

        return new JsonObject {
            ["api_key"] = token,
            ["batch"]   = batch,
        }.ToJsonString();
    }
}
