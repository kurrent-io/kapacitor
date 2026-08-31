using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Core.Tests.Unit.Mcp;

public class McpMarkerTests {
    static (McpMarker marker, string cfg, string markerFile) NewMarker(TempDir tmp) =>
        (new McpMarker("test", new(tmp.Path), _ => tmp.PathTo("marker.json")), tmp.PathTo("mcp.json"), tmp.PathTo("marker.json"));

    [Test]
    public async Task Record_then_Owned_roundtrips_names() {
        using var tmp = new TempDir();
        var (marker, cfg, _) = NewMarker(tmp);
        marker.Record(cfg, ["kcap-review", "kcap-sessions"]);
        await Assert.That(marker.Owned(cfg)).Contains("kcap-review");
        await Assert.That(marker.Owned(cfg)).Contains("kcap-sessions");
    }

    [Test]
    public async Task Owns_true_for_recorded_kcap_command_entry() {
        using var tmp = new TempDir();
        var (marker, cfg, _) = NewMarker(tmp);
        marker.Record(cfg, ["kcap-review"]);
        var entry = new JsonObject { ["command"] = "kcap", ["args"] = new JsonArray { "mcp", "review" } };
        await Assert.That(marker.Owns(cfg, "kcap-review", entry)).IsTrue();
    }

    [Test]
    public async Task Owns_false_for_unrecorded_lookalike() {
        using var tmp = new TempDir();
        var (marker, cfg, _) = NewMarker(tmp);
        // Never recorded → not owned, even though it's named kcap-review.
        var entry = new JsonObject { ["command"] = "mine" };
        await Assert.That(marker.Owns(cfg, "kcap-review", entry)).IsFalse();
    }

    [Test]
    public async Task Owns_true_for_opencode_command_array() {
        using var tmp = new TempDir();
        var (marker, cfg, _) = NewMarker(tmp);
        marker.Record(cfg, ["kcap-review"]);
        var entry = new JsonObject { ["command"] = new JsonArray { "kcap", "mcp", "review" } };
        await Assert.That(marker.Owns(cfg, "kcap-review", entry)).IsTrue();
    }

    [Test]
    public async Task Owns_false_for_malformed_nonstring_command_array() {
        using var tmp = new TempDir();
        var (marker, cfg, _) = NewMarker(tmp);
        marker.Record(cfg, ["kcap-review"]);
        var entry = new JsonObject { ["command"] = new JsonArray { 123, "mcp" } };
        await Assert.That(marker.Owns(cfg, "kcap-review", entry)).IsFalse(); // must not throw
    }

    [Test]
    public async Task Clear_removes_the_marker() {
        using var tmp = new TempDir();
        var (marker, cfg, markerFile) = NewMarker(tmp);
        marker.Record(cfg, ["kcap-review"]);
        marker.Clear(cfg);
        await Assert.That(File.Exists(markerFile)).IsFalse();
        await Assert.That(marker.Owned(cfg)).IsEmpty();
    }

    [Test]
    public async Task Owns_false_and_no_throw_for_nonobject_entry() {
        using var tmp = new TempDir();
        var (marker, cfg, _) = NewMarker(tmp);
        marker.Record(cfg, ["kcap-review"]);
        JsonNode arrEntry = new JsonArray { "x" };
        await Assert.That(marker.Owns(cfg, "kcap-review", arrEntry)).IsFalse();            // must not throw
        JsonNode valEntry = JsonValue.Create("disabled")!;
        await Assert.That(marker.Owns(cfg, "kcap-review", valEntry)).IsFalse();
    }

    [Test]
    public async Task Owned_ignores_marker_recorded_for_a_different_config() {
        using var tmp    = new TempDir();
        var       shared = tmp.PathTo(".kcap-mcp-version");
        var       m      = new McpMarker("test", new(tmp.Path), _ => shared); // both configs resolve to the SAME sidecar (simulates per-dir collision)
        var       cfgA   = tmp.PathTo("a.json");
        var       cfgB   = tmp.PathTo("b.json");
        m.Record(cfgA, ["kcap-review"]);
        await Assert.That(m.Owned(cfgA)).Contains("kcap-review");   // A owns it
        await Assert.That(m.Owned(cfgB)).IsEmpty();                 // B must NOT inherit A's marker
        await Assert.That(m.Owns(cfgB, "kcap-review", new JsonObject { ["command"] = "kcap" })).IsFalse(); // → preserved
    }

    [Test]
    public async Task Owned_matches_across_equivalent_path_forms() {
        using var tmp    = new TempDir();
        var       shared = tmp.PathTo(".kcap-mcp-version");
        var       m      = new McpMarker("test", new(tmp.Path), _ => shared);
        var       abs    = tmp.PathTo("mcp.json");
        var       equiv  = tmp.PathTo(".", "mcp.json"); // same file, non-canonical form
        m.Record(abs, ["kcap-review"]);
        await Assert.That(m.Owned(equiv)).Contains("kcap-review"); // equivalent path form still recognized
    }

    // ── v2 fingerprints + v1 migration ──────────────────────────────────────────

    static JsonObject CanonicalEntry(string cmd = "kcap") =>
        new() { ["command"] = cmd, ["args"] = new JsonArray { "mcp", "review" } };

    [Test]
    public async Task Owns_v2_matches_the_exact_recorded_entry_including_absolute_command() {
        using var tmp = new TempDir();
        var (marker, cfg, _) = NewMarker(tmp);
        var entry = CanonicalEntry("/opt/a/kcap");
        marker.Record(cfg, [KeyValuePair.Create("kcap-review", (JsonNode?)entry)]);

        // Same content, different instance + key order + formatting → still owned.
        var reparsed = JsonNode.Parse("""{ "args": ["mcp","review"], "command": "/opt/a/kcap" }""")!;
        await Assert.That(marker.Owns(cfg, "kcap-review", reparsed)).IsTrue();
    }

    [Test]
    public async Task Owns_v2_rejects_a_user_edited_entry_even_with_command_kcap() {
        using var tmp = new TempDir();
        var (marker, cfg, _) = NewMarker(tmp);
        marker.Record(cfg, [KeyValuePair.Create("kcap-review", (JsonNode?)CanonicalEntry())]);

        var edited = CanonicalEntry(); // command is still the literal "kcap"…
        edited["env"] = new JsonObject { ["X"] = "1" }; // …but the user customized it
        await Assert.That(marker.Owns(cfg, "kcap-review", edited)).IsFalse();
    }

    [Test]
    public async Task V1_marker_file_reads_with_legacy_command_semantics_and_migrates_on_record() {
        using var tmp = new TempDir();
        var cfg = tmp.PathTo("mcp.json");
        var markerFile = tmp.PathTo("marker.json");
        var marker = new McpMarker("test", new(tmp.Path), _ => markerFile);

        // The exact on-disk v1 format an older kcap wrote: servers as a bare name array.
        File.WriteAllText(markerFile, $$"""
            { "version": 1, "harness": "test", "config": {{JsonValue.Create(Path.GetFullPath(cfg)).ToJsonString()}},
              "servers": ["kcap-review"] }
            """);

        // v1 semantics: command "kcap" (string or argv head) is ours; anything else is not.
        await Assert.That(marker.Owns(cfg, "kcap-review", CanonicalEntry())).IsTrue();
        await Assert.That(marker.Owns(cfg, "kcap-review", CanonicalEntry("/opt/a/kcap"))).IsFalse();

        // Recording upgrades the file to v2 and keeps the v1 name (fingerprint-less).
        marker.Record(cfg, [KeyValuePair.Create("kcap-sessions", (JsonNode?)CanonicalEntry("/opt/a/kcap"))]);
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(markerFile))!;
        await Assert.That((int)root["version"]!).IsEqualTo(2);
        await Assert.That(marker.Owned(cfg)).Contains("kcap-review");   // migrated, still legacy-owned
        await Assert.That(marker.Owns(cfg, "kcap-review", CanonicalEntry())).IsTrue();
        await Assert.That(marker.Owns(cfg, "kcap-sessions", CanonicalEntry("/opt/a/kcap"))).IsTrue();
    }

    /// <summary>The marker is the ownership record for absolute-path entries: it is written via
    /// sibling temp + atomic rename (never an in-place truncate), owner-only on Unix.</summary>
    [Test]
    public async Task Record_writes_atomically_owner_only_with_no_temp_litter() {
        using var tmp = new TempDir();
        var (marker, cfg, markerFile) = NewMarker(tmp);
        marker.Record(cfg, [KeyValuePair.Create("kcap-review", (JsonNode?)CanonicalEntry("/opt/a/kcap"))]);
        marker.Record(cfg, [KeyValuePair.Create("kcap-sessions", (JsonNode?)CanonicalEntry("/opt/a/kcap"))]); // rewrite path too

        await Assert.That(marker.Owned(cfg)).Contains("kcap-review");
        await Assert.That(Directory.GetFiles(Path.GetDirectoryName(markerFile)!, "*.tmp-*")).IsEmpty();
        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(markerFile))
                .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // Exercises the REAL central-path resolution (no per-config markerPathFor override). The configs
    // sit OUTSIDE the home handed to the marker, which is what sends both to the central store rather
    // than to a sidecar — put them under it and this silently becomes a sidecar test. The central root
    // is `.kcap` under that throwaway home, so nothing touches the real ~/.kcap/mcp-markers.
    [Test]
    public async Task Two_configs_in_same_dir_have_independent_ownership() {
        using var tmp     = new TempDir();
        using var homeDir = new TempDir("home");
        var       m       = new McpMarker("test", new(homeDir.Path));
        var       a       = tmp.PathTo("a.json");
        var       b       = tmp.PathTo("b.json");
        m.Record(a, ["kcap-review"]);
        await Assert.That(m.Owned(a)).Contains("kcap-review"); // a owns it
        await Assert.That(m.Owned(b)).IsEmpty();               // b is independent of a
    }
}
