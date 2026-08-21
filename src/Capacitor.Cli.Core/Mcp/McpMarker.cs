using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Mcp;

public interface IMcpMarker {
    /// <summary>True if `entry` under `name` is a kcap-owned registration, so overwrite/unregister
    /// may touch it. A v2 marker owns exactly the recorded per-entry fingerprint (any edit —
    /// command, args, env — relinquishes ownership); a v1 marker (names only, pre-fingerprint)
    /// falls back to the legacy command == "kcap" check. A user look-alike returns false.</summary>
    bool Owns(string configPath, string name, JsonNode entry);

    /// <summary>Records ownership of the given entries (name → the exact JSON just written).
    /// A null entry records the name with no fingerprint — legacy v1 semantics, kept for
    /// callers that cannot supply the written JSON.</summary>
    void Record(string configPath, IReadOnlyList<KeyValuePair<string, JsonNode?>> entries);

    IEnumerable<string> Owned(string configPath);
    void Clear(string configPath);
}

/// <summary>
/// Sidecar marker recording which `kcap-*` keys kcap wrote into a given config file.
/// Lives OUTSIDE the host MCP file (so it never alters the host's accepted schema):
///  - user-scope config → `.kcap-mcp-version` next to the config file;
///  - otherwise (project-scope / central) → `~/.kcap/mcp-markers/&lt;harness&gt;-&lt;hash(abs path)&gt;.json`.
/// The `markerPathFor` override lets tests point both cases at a temp dir.
///
/// <para><b>Format v2</b> records a per-entry fingerprint (<see cref="McpFingerprint"/>) so an
/// absolute-path <c>command</c> (the native binary, AI's Phase-2 registration shape) stays owned
/// across refresh-healing and uninstall — v1's literal <c>command == "kcap"</c> ownership test
/// would strand it. Migration is safe and lazy: a v1 marker (JSON array of names) reads as
/// fingerprint-less entries that keep the legacy command check, and the next
/// <see cref="Record(string, IReadOnlyList{KeyValuePair{string, JsonNode?}})"/> rewrites the file as v2.</para>
/// </summary>
public sealed class McpMarker(string harness, Func<string, string>? markerPathFor = null) : IMcpMarker {
    const int Version = 2;

    // Test seam: redirect the CENTRAL marker root (normally the user profile's `.kcap`) so unit tests
    // never read/write the real shared `~/.kcap/mcp-markers` — a single process-global dir that races
    // across parallel suites and pollutes the developer's home. Pinned once for the whole test
    // assembly by `McpMarkerGlobalSetup` (mirrors `DaemonPathsGlobalSetup`). Volatile so the
    // assembly-hook write publishes to every test thread.
    static volatile string? _centralRootOverride;
    internal static void OverrideCentralRootForTesting(string? kcapRoot) => _centralRootOverride = kcapRoot;

    public bool Owns(string configPath, string name, JsonNode entry) {
        if (!ReadEntries(MarkerPath(configPath), configPath).TryGetValue(name, out var fingerprint)) return false;
        if (entry is not JsonObject obj) return false; // malformed/non-object entry → not ours; never throw

        // v2: ownership is the exact recorded shape — any edit is the user's and is preserved.
        if (fingerprint is not null)
            return string.Equals(McpFingerprint.Compute(obj), fingerprint, StringComparison.Ordinal);

        // v1 marker (no fingerprint recorded): legacy command == "kcap" check.
        var cmd = obj["command"];
        return cmd is JsonValue v && v.TryGetValue(out string? s) && s == KcapMcpServers.Command
            || cmd is JsonArray a && a.Count > 0 && a[0] is JsonValue fv && fv.TryGetValue(out string? fs) && fs == KcapMcpServers.Command;
    }

    public void Record(string configPath, IReadOnlyList<KeyValuePair<string, JsonNode?>> entries) {
        var path = MarkerPath(configPath);
        var existing = ReadEntries(path, configPath); // merges + migrates a v1 marker in place
        foreach (var (name, entry) in entries)
            existing[name] = entry is null ? null : McpFingerprint.Compute(entry);

        var servers = new JsonObject();
        foreach (var (name, fingerprint) in existing.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            servers[name] = fingerprint is null ? null : JsonValue.Create(fingerprint);

        var doc = new JsonObject {
            ["version"] = Version,
            ["harness"] = harness,
            ["config"]  = Path.GetFullPath(configPath),
            ["servers"] = servers
        };
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Atomic sibling-rename, never an in-place truncate-rewrite: the marker is the ownership
        // record for ABSOLUTE-path entries (v2 fingerprints), so a write interrupted mid-truncate
        // would strand every such entry as unowned forever — no heal, no uninstall. Owner-only on
        // create (defense in depth; it holds no secrets but has no business being group-writable).
        WriteAtomicOwnerOnly(path, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    static void WriteAtomicOwnerOnly(string path, string content) {
        var options = new FileStreamOptions {
            Mode   = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share  = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var tmp = path + ".tmp-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
        try {
            using (var stream = new FileStream(tmp, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(content);
            File.Move(tmp, path, overwrite: true);
        } catch {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>Names-only convenience (legacy v1 semantics: no fingerprint, ownership by the
    /// command == "kcap" check). Prefer the entries overload wherever the written JSON is at hand.</summary>
    public void Record(string configPath, IReadOnlyList<string> names) =>
        Record(configPath, names.Select(n => KeyValuePair.Create(n, (JsonNode?)null)).ToArray());

    public IEnumerable<string> Owned(string configPath) => ReadEntries(MarkerPath(configPath), configPath).Keys;

    public void Clear(string configPath) {
        var p = MarkerPath(configPath);
        try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    /// <summary>name → fingerprint (null = recorded without one: a v1 marker or a names-only record).</summary>
    Dictionary<string, string?> ReadEntries(string markerPath, string configPath) {
        try {
            if (!File.Exists(markerPath)) return [];
            if (JsonNode.Parse(File.ReadAllText(markerPath)) is not JsonObject root) return [];
            // A user-scope sidecar is per-directory and could be shared; only trust a marker
            // that pertains to THIS harness + config path (else treat as not-ours → preserve).
            if ((string?)root["harness"] != harness) return [];
            if ((string?)root["config"] != Path.GetFullPath(configPath)) return [];

            return root["servers"] switch {
                // v1: a bare array of names — no fingerprints (legacy ownership semantics).
                JsonArray arr => arr.OfType<JsonValue>()
                    .Select(n => n.TryGetValue(out string? s) ? s : null)
                    .Where(s => s is not null)
                    .ToDictionary(s => s!, _ => (string?)null),
                // v2: name → fingerprint (null tolerated: recorded without one).
                JsonObject obj => obj.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value is JsonValue v && v.TryGetValue(out string? fp) ? fp : null),
                _ => []
            };
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
