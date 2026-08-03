using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Mcp;

/// <summary>
/// The MCP-registrations doctor section, run entirely against fixture files in temp dirs
/// (never the real ~/.claude.json or harness configs). Pins: the plugin-installed gate, the
/// structural duplicate-vs-conflict split, --clean removing only canonical duplicates, and
/// the two-tier stale-path scan (missing binary vs. differs-from-current-resolution).
/// </summary>
public class McpDoctorSectionTests {
    sealed record Fixture(string Dir, string ClaudeConfig, string ClaudeSettings) {
        public static Fixture Create() {
            var dir = Directory.CreateTempSubdirectory("kcap-mcpdoctor-").FullName;
            return new(dir, Path.Combine(dir, ".claude.json"), Path.Combine(dir, "settings.json"));
        }

        /// <summary>An EFFECTIVE plugin install: enabled registration + the plugin's INSTALLED
        /// payload (installed_plugins.json → installPath cache dir shipping .mcp.json) — what
        /// the destructive duplicate audit requires.</summary>
        public void InstallPlugin() {
            var installPath = Path.Combine(Dir, "plugins", "cache", "kcap", "kcap", "1.0.0");
            Directory.CreateDirectory(installPath);
            File.WriteAllText(Path.Combine(installPath, ".mcp.json"), "{}");
            File.WriteAllText(Path.Combine(Dir, "plugins", "installed_plugins.json"), $$"""
                { "version": 2, "plugins": { "kcap@kcap": [
                    { "scope": "user", "installPath": {{JsonValue.Create(installPath).ToJsonString()}}, "version": "1.0.0" } ] } }
                """);
            File.WriteAllText(ClaudeSettings, """{ "enabledPlugins": { "kcap@kcap": true } }""");
        }

        /// <summary>The refresh gate's weakest signal: a version marker with no enabled
        /// registration and no payload — must never authorize the duplicate audit.</summary>
        public void WriteStaleMarkerOnly() =>
            File.WriteAllText(Path.Combine(Dir, ".kcap-plugin-version"), "9.9.9");
    }

    static async Task<(int Issues, string Output)> RunAsync(Fixture f, bool clean = false,
            IReadOnlyList<McpDoctorSection.RegistrationFile>? files = null,
            string? codexConfigPath = null, string? nativeBinaryPath = null) {
        await using var writer = new StringWriter();
        var issues = await McpDoctorSection.RunAsync(writer, clean, f.ClaudeConfig, f.ClaudeSettings,
                                                     files ?? [], codexConfigPath, nativeBinaryPath);
        return (issues, writer.ToString());
    }

    const string DuplicateAndConflictConfig = """
        { "mcpServers": {
            "kcap-flows":    { "command": "kcap", "args": ["mcp","flows"] },
            "kcap-sessions": { "command": "kcap", "args": ["mcp","sessions"], "env": { "X": "1" } },
            "other":         { "command": "npx" } },
          "projects": { "/w/repo": { "mcpServers": {
            "kcap-review": { "command": "kcap", "args": ["mcp","review"] } } } } }
        """;

    [Test]
    public async Task Reports_duplicates_in_both_scopes_and_conflicts_without_mutating() {
        var f = Fixture.Create();
        f.InstallPlugin();
        File.WriteAllText(f.ClaudeConfig, DuplicateAndConflictConfig);

        var (issues, output) = await RunAsync(f);

        await Assert.That(issues).IsEqualTo(3);
        await Assert.That(output).Contains("duplicate MCP registration 'kcap-flows' (user)");
        await Assert.That(output).Contains("duplicate MCP registration 'kcap-review' (projects[/w/repo])");
        await Assert.That(output).Contains("same-named MCP registration 'kcap-sessions'");
        // Read-only by default (passive doctor must not mutate).
        await Assert.That(File.ReadAllText(f.ClaudeConfig)).IsEqualTo(DuplicateAndConflictConfig);
    }

    [Test]
    public async Task Clean_removes_only_canonical_duplicates_and_preserves_conflicts() {
        var f = Fixture.Create();
        f.InstallPlugin();
        File.WriteAllText(f.ClaudeConfig, DuplicateAndConflictConfig);

        var (_, output) = await RunAsync(f, clean: true);

        await Assert.That(output).Contains("removed 2 duplicate MCP registration(s)");
        var root = (JsonObject)JsonNode.Parse(File.ReadAllText(f.ClaudeConfig))!;
        var servers = (JsonObject)root["mcpServers"]!;
        await Assert.That(servers.ContainsKey("kcap-flows")).IsFalse();     // canonical → removed
        await Assert.That(servers.ContainsKey("kcap-sessions")).IsTrue();   // conflict → preserved
        await Assert.That(servers.ContainsKey("other")).IsTrue();           // foreign → preserved
        var project = (JsonObject)root["projects"]!["/w/repo"]!["mcpServers"]!;
        await Assert.That(project.ContainsKey("kcap-review")).IsFalse();    // project scope cleaned too
    }

    [Test]
    public async Task Without_the_plugin_installed_nothing_is_flagged_or_removed() {
        var f = Fixture.Create(); // no settings.json → plugin not installed
        File.WriteAllText(f.ClaudeConfig, DuplicateAndConflictConfig);

        var (issues, output) = await RunAsync(f, clean: true);

        await Assert.That(issues).IsEqualTo(0);
        await Assert.That(output).Contains("no issues found");
        await Assert.That(File.ReadAllText(f.ClaudeConfig)).IsEqualTo(DuplicateAndConflictConfig);
    }

    [Test]
    public async Task Stale_version_marker_alone_never_authorizes_flagging_or_cleanup() {
        var f = Fixture.Create();
        f.WriteStaleMarkerOnly(); // satisfies the refresh gate (IsInstalled) but is not effective
        File.WriteAllText(f.ClaudeConfig, DuplicateAndConflictConfig);

        var (issues, output) = await RunAsync(f, clean: true);

        await Assert.That(issues).IsEqualTo(0);
        await Assert.That(output).Contains("no issues found");
        await Assert.That(File.ReadAllText(f.ClaudeConfig)).IsEqualTo(DuplicateAndConflictConfig);
    }

    [Test]
    public async Task Enabled_registration_without_a_resolvable_payload_never_authorizes_cleanup() {
        var f = Fixture.Create();
        f.InstallPlugin();
        Directory.Delete(Path.Combine(f.Dir, "plugins", "cache"), recursive: true); // re-layout: installed payload gone
        File.WriteAllText(f.ClaudeConfig, DuplicateAndConflictConfig);

        var (issues, _) = await RunAsync(f, clean: true);

        await Assert.That(issues).IsEqualTo(0);
        await Assert.That(File.ReadAllText(f.ClaudeConfig)).IsEqualTo(DuplicateAndConflictConfig);
    }

    [Test]
    public async Task Missing_claude_config_reports_healthy() {
        var f = Fixture.Create();
        var (issues, output) = await RunAsync(f);
        await Assert.That(issues).IsEqualTo(0);
        await Assert.That(output).Contains("no issues found");
    }

    [Test]
    public async Task Stale_scan_distinguishes_missing_binary_from_outdated_resolution() {
        var f = Fixture.Create();
        var current = Path.Combine(f.Dir, "kcap");     // exists = the "current" binary
        var old     = Path.Combine(f.Dir, "old", "kcap"); // exists but differs from current
        File.WriteAllText(current, "bin");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, "bin");
        var missing = Path.Combine(f.Dir, "gone", "kcap"); // never created

        var cursor = Path.Combine(f.Dir, "cursor-mcp.json");
        File.WriteAllText(cursor, $$"""
            { "mcpServers": {
                "kcap-review":   { "command": {{JsonValue.Create(missing).ToJsonString()}}, "args": ["mcp","review"] },
                "kcap-sessions": { "command": {{JsonValue.Create(old).ToJsonString()}}, "args": ["mcp","sessions"] },
                "kcap-flows":    { "command": {{JsonValue.Create(current).ToJsonString()}}, "args": ["mcp","flows"] },
                "my-tool":       { "command": "/does/not/exist/anywhere" } } }
            """);

        var (issues, output) = await RunAsync(f,
            files: [new McpDoctorSection.RegistrationFile("Cursor", cursor, "mcpServers")],
            nativeBinaryPath: current);

        await Assert.That(issues).IsEqualTo(2);
        await Assert.That(output).Contains("stale MCP registration 'kcap-review'");     // missing file
        await Assert.That(output).Contains("re-run `kcap setup`");
        await Assert.That(output).Contains("outdated MCP registration 'kcap-sessions'"); // differs from current
        await Assert.That(output).DoesNotContain("kcap-flows");                          // current → healthy
        await Assert.That(output).DoesNotContain("my-tool");                             // non-kcap → never inspected
    }

    /// <summary>
    /// The clean commits under a lock and re-reads first: a concurrent write to ~/.claude.json
    /// (Claude itself, the daemon's trust write) between the snapshot and the commit must abort
    /// the clean rather than be silently overwritten by a rewrite of the stale snapshot.
    /// </summary>
    [Test]
    public async Task Clean_aborts_without_writing_when_the_config_changed_after_the_snapshot() {
        var f = Fixture.Create();
        File.WriteAllText(f.ClaudeConfig, DuplicateAndConflictConfig);
        var snapshot = DuplicateAndConflictConfig;

        // Another writer lands between the findings snapshot and the commit.
        var concurrent = """{ "mcpServers": { "kcap-flows": { "command": "kcap", "args": ["mcp","flows"] } }, "newTrust": true }""";
        File.WriteAllText(f.ClaudeConfig, concurrent);

        var outcome = McpDoctorSection.TryCleanClaudeConfig(f.ClaudeConfig, snapshot, null, out _);

        await Assert.That(outcome).IsEqualTo(McpDoctorSection.CleanOutcome.Conflicted);
        await Assert.That(File.ReadAllText(f.ClaudeConfig)).IsEqualTo(concurrent); // the other writer's update survives
    }

    [Test]
    public async Task Clean_commits_when_the_snapshot_is_still_current() {
        var f = Fixture.Create();
        File.WriteAllText(f.ClaudeConfig, DuplicateAndConflictConfig);

        var outcome = McpDoctorSection.TryCleanClaudeConfig(f.ClaudeConfig, DuplicateAndConflictConfig, null, out _);

        await Assert.That(outcome).IsEqualTo(McpDoctorSection.CleanOutcome.Cleaned);
        var servers = (JsonObject)JsonNode.Parse(File.ReadAllText(f.ClaudeConfig))!["mcpServers"]!;
        await Assert.That(servers.ContainsKey("kcap-flows")).IsFalse();
        // No temp litter left beside the config.
        await Assert.That(Directory.GetFiles(f.Dir, ".claude.json.tmp-*")).IsEmpty();
    }

    /// <summary>A 0600 config must not come back 0644: the rewrite preserves the target's mode.</summary>
    [Test]
    public async Task Clean_preserves_the_configs_unix_mode() {
        if (OperatingSystem.IsWindows()) return;
        var f = Fixture.Create();
        File.WriteAllText(f.ClaudeConfig, DuplicateAndConflictConfig);
        File.SetUnixFileMode(f.ClaudeConfig, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var outcome = McpDoctorSection.TryCleanClaudeConfig(f.ClaudeConfig, DuplicateAndConflictConfig, null, out _);

        await Assert.That(outcome).IsEqualTo(McpDoctorSection.CleanOutcome.Cleaned);
        await Assert.That(File.GetUnixFileMode(f.ClaudeConfig))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Stale_scan_covers_codex_toml_kcap_entries_only() {
        var f = Fixture.Create();
        var missing = Path.Combine(f.Dir, "gone", "kcap");
        var codex = Path.Combine(f.Dir, "config.toml");
        // LITERAL (single-quoted) TOML strings: `\` is an escape introducer in a basic
        // (double-quoted) string, so interpolating a Windows temp path into one produced
        // invalid TOML — Tomlyn threw, the never-throws ReadMcpServerCommands returned [],
        // and this test reported 0 issues on the Windows CI leg. Production is unaffected
        // (the writer emits properly-escaped TOML via TomlSerializer); a literal string is
        // the canonical hand-written form for Windows paths and parses on every platform.
        File.WriteAllText(codex, $"""
            [mcp_servers.kcap-review]
            command = '{missing}'
            args = ["mcp", "review"]

            [mcp_servers.my-tool]
            command = '/also/gone/my-tool'
            """);

        var (issues, output) = await RunAsync(f, codexConfigPath: codex);

        await Assert.That(issues).IsEqualTo(1);
        await Assert.That(output).Contains("stale MCP registration 'kcap-review' (Codex)");
        await Assert.That(output).DoesNotContain("my-tool");
    }
}
