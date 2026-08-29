using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core;

/// The `applyPermissions` payload the web UI sends for "Always allow": Claude persists the rule
/// itself, so the client composes it rather than relaying the hook's permission_suggestions.
public static class ClaudePermissions {
    public static JsonElement AlwaysAllow(string toolName) {
        var json = JsonSerializer.Serialize(new[] { new AlwaysAllowEntry("toolAlwaysAllow", toolName) },
            ClaudePermissionsJsonContext.Default.AlwaysAllowEntryArray);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    internal sealed record AlwaysAllowEntry(string Type, string Tool);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClaudePermissions.AlwaysAllowEntry[]))]
internal partial class ClaudePermissionsJsonContext : JsonSerializerContext;
