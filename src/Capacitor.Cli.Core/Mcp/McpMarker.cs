using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Mcp;

public interface IMcpMarker {
    /// <summary>True if `entry` under `name` is a kcap-owned registration (name recorded in the marker
    /// AND command == "kcap"), so overwrite/unregister may touch it. A user look-alike returns false.</summary>
    bool Owns(string configPath, string name, JsonNode entry);
    void Record(string configPath, IReadOnlyList<string> names);
    IEnumerable<string> Owned(string configPath);
    void Clear(string configPath);
}

/// <summary>
/// Sidecar marker recording which `kcap-*` keys kcap wrote into a given config file.
/// Lives OUTSIDE the host MCP file (so it never alters the host's accepted schema):
///  - user-scope config → `.kcap-mcp-version` next to the config file;
///  - otherwise (project-scope / central) → `~/.kcap/mcp-markers/&lt;harness&gt;-&lt;hash(abs path)&gt;.json`.
/// The `markerPathFor` override lets tests point both cases at a temp dir.
/// </summary>
public sealed class McpMarker(string harness, Func<string, string>? markerPathFor = null) : IMcpMarker {
    const int Version = 1;

    // Test seam: redirect the CENTRAL marker root (normally the user profile's `.kcap`) so unit tests
    // never read/write the real shared `~/.kcap/mcp-markers` — a single process-global dir that races
    // across parallel suites and pollutes the developer's home. Pinned once for the whole test
    // assembly by `McpMarkerGlobalSetup` (mirrors `DaemonLockPaths.OverrideDirectoryForTesting` /
    // `DaemonPathsGlobalSetup`). Volatile so the assembly-hook write publishes to every test thread.
    static volatile string? _centralRootOverride;
    internal static void OverrideCentralRootForTesting(string? kcapRoot) => _centralRootOverride = kcapRoot;

    public bool Owns(string configPath, string name, JsonNode entry) {
        if (!Owned(configPath).Contains(name)) return false;
        if (entry is not JsonObject obj) return false; // malformed/non-object entry → not ours; never throw
        var cmd = obj["command"];
        return cmd is JsonValue v && v.TryGetValue(out string? s) && s == KcapMcpServers.Command
            || cmd is JsonArray a && a.Count > 0 && a[0] is JsonValue fv && fv.TryGetValue(out string? fs) && fs == KcapMcpServers.Command;
    }

    public void Record(string configPath, IReadOnlyList<string> names) {
        var path = MarkerPath(configPath);
        var existing = ReadNames(path, configPath);
        foreach (var n in names) existing.Add(n);
        var doc = new JsonObject {
            ["version"] = Version,
            ["harness"] = harness,
            ["config"]  = Path.GetFullPath(configPath),
            ["servers"] = new JsonArray(existing.Select(n => (JsonNode)n!).ToArray())
        };
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public IEnumerable<string> Owned(string configPath) => ReadNames(MarkerPath(configPath), configPath);

    public void Clear(string configPath) {
        var p = MarkerPath(configPath);
        try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    HashSet<string> ReadNames(string markerPath, string configPath) {
        try {
            if (!File.Exists(markerPath)) return [];
            if (JsonNode.Parse(File.ReadAllText(markerPath)) is not JsonObject root) return [];
            // A user-scope sidecar is per-directory and could be shared; only trust a marker
            // that pertains to THIS harness + config path (else treat as not-ours → preserve).
            if ((string?)root["harness"] != harness) return [];
            if ((string?)root["config"] != Path.GetFullPath(configPath)) return [];
            return root["servers"] is JsonArray arr ? [.. arr.Select(n => (string)n!)] : [];
        } catch { return []; }
    }

    string MarkerPath(string configPath) {
        if (markerPathFor is not null) return markerPathFor(configPath);
        // Heuristic: a config under the user's home harness dir → sidecar; else central state.
        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var isUserScope = dir.StartsWith(home, StringComparison.Ordinal)
                          && !IsInsideRepo(dir);
        // Per-config sidecar (include the config file name) so multiple configs sharing a
        // directory never overwrite each other's ownership record.
        if (isUserScope) return Path.Combine(dir, $".kcap-mcp-version-{Path.GetFileName(configPath)}");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(configPath))))[..16].ToLowerInvariant();
        var centralRoot = _centralRootOverride ?? Path.Combine(home, ".kcap");
        return Path.Combine(centralRoot, "mcp-markers", $"{harness}-{hash}.json");
    }

    static bool IsInsideRepo(string dir) {
        for (var cur = new DirectoryInfo(dir); cur is not null; cur = cur.Parent) {
            var git = Path.Combine(cur.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return true; // .git is a file in worktrees/submodules
        }
        return false;
    }
}
