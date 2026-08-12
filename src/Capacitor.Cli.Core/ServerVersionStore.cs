using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// Durable, per-server cache of the connected Kurrent Capacitor server's own version, learned
/// passively from the <c>X-Kcap-Server-Version</c> response header (see
/// <see cref="HttpClientExtensions"/>'s capture handler). The passive update notice and
/// <c>kcap status</c> read it to cap the recommended CLI version at <c>min(npm latest, server
/// version)</c> — a manually-rolled tenant can trail npm for days, and nudging a user to a CLI newer
/// than the server they talk to only risks protocol mismatch.
///
/// <para>One flat file per normalized server URL (<c>server-version-{hash}.json</c> in the CLI config
/// dir, honouring <c>KCAP_CONFIG_DIR</c> — the <see cref="MachineId"/> precedent), so a multi-profile
/// user gets a per-server cap for free and two servers never race one map file. Best-effort
/// throughout: a read or write failure never surfaces — an absent value just means "no cap", which is
/// exactly today's behaviour. <see cref="Set"/> is deduped in-process so the per-response hot path
/// touches disk at most once per distinct value per process.</para>
/// </summary>
public static class ServerVersionStore {
    // Per-process memo of the last value written for each server, so a long-lived process (the daemon)
    // making many requests doesn't rewrite the same file on every response.
    static readonly ConcurrentDictionary<string, string> WrittenThisProcess = new();

    /// <summary>
    /// Records the server version observed for <paramref name="serverUrl"/>. No-op for a blank URL or
    /// version, or when the same value was already written this process. Never throws.
    /// </summary>
    public static void Set(string? serverUrl, string? version) {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(version)) return;

        var key = Normalize(serverUrl);

        if (WrittenThisProcess.TryGetValue(key, out var prev) && prev == version) return;

        try {
            var obj = new JsonObject {
                ["url"]     = key,
                ["version"] = version,
                ["seen_at"] = DateTimeOffset.UtcNow,
            };

            var path     = PathFor(key);
            var tempPath = $"{path}.tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(tempPath, obj.ToJsonString());
            File.Move(tempPath, path, overwrite: true);

            WrittenThisProcess[key] = version;
        } catch {
            // Best-effort — a cache write must never break the request it rides on.
        }
    }

    /// <summary>
    /// The last-observed server version for <paramref name="serverUrl"/>, or null when none has been
    /// seen (an old CLI, a never-connected server, or a server that doesn't send the header). Never
    /// throws — a corrupt/unreadable file reads as null and re-populates on the next request.
    /// </summary>
    public static string? Get(string? serverUrl) {
        if (string.IsNullOrWhiteSpace(serverUrl)) return null;

        try {
            var path = PathFor(Normalize(serverUrl));
            if (!File.Exists(path)) return null;

            var node    = JsonNode.Parse(File.ReadAllText(path));
            var version = node?["version"]?.GetValue<string>();

            return string.IsNullOrWhiteSpace(version) ? null : version;
        } catch {
            return null;
        }
    }

    /// <summary>Normalizes a server URL to a stable cache key: trimmed, no trailing slash,
    /// lower-cased (scheme+host+port only — these carry no case-sensitive path).</summary>
    internal static string Normalize(string serverUrl) => serverUrl.Trim().TrimEnd('/').ToLowerInvariant();

    static string PathFor(string normalizedUrl) {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedUrl)))[..16].ToLowerInvariant();

        return PathHelpers.ConfigPath($"server-version-{hash}.json");
    }
}
