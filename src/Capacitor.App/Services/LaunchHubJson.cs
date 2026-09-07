using System.Text.Json;

namespace Capacitor.App.Services;

/// <summary>
/// The EXACT SignalR JSON hub-protocol payload configuration ServerConnectionService applies —
/// extracted so wire-contract tests serialize with the genuine on-wire options instead of a
/// hand-built approximation that could silently diverge from production (mirrors kcap-server's
/// JsonDefaults.ConfigureSignalRPayload, extracted for the same reason). Matches
/// ServerConnection.cs / WatchCommand.cs: chain-insert the generated context rather than
/// replacing TypeInfoResolver, and set the same snake_case policy the server expects on every
/// hub payload — belt and braces alongside the payload's own explicit [JsonPropertyName]s.
/// </summary>
public static class LaunchHubJson {
    public static void Configure(JsonSerializerOptions options) {
        options.TypeInfoResolverChain.Insert(0, LaunchJsonContext.Default);
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    }
}
