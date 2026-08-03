using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Mcp;

/// <summary>
/// Canonical fingerprint of a JSON MCP entry, used by <see cref="McpMarker"/>'s v2 per-entry
/// ownership claims (the JSON analogue of <c>CodexConfigToml</c>'s ownership-ledger fingerprint):
/// SHA-256 over a key-sorted, whitespace-free serialization, so cosmetic reformatting of the host
/// config never changes identity while any value edit does.
/// </summary>
public static class McpFingerprint {
    public static string Compute(JsonNode entry) {
        var canonical = Normalize(entry)?.ToJsonString() ?? "null";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    static JsonNode? Normalize(JsonNode? node) => node switch {
        JsonObject obj => new JsonObject(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => KeyValuePair.Create(kv.Key, Normalize(kv.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Normalize).ToArray()),
        _ => node?.DeepClone()
    };
}
