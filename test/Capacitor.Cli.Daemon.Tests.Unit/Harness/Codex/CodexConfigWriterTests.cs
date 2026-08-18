using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Daemon.Harness.Codex;
using Microsoft.Extensions.Logging.Abstractions;
using Tomlyn;
using Tomlyn.Model;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

// Tests modify the HOME env var; they must not run in parallel with each other
// (or with any other test that reads CodexPaths.Home) so that the scoped HOME is stable.
[NotInParallel("HomeEnvVarMutation")]
public class CodexConfigWriterTests {
    static TempDir ScopedHome(out string? originalHome) {
        var tmp = new TempDir("codex");
        originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", tmp.Path);

        return tmp;
    }

    static void RestoreHome(string? originalHome) {
        Environment.SetEnvironmentVariable("HOME", originalHome);
    }

    static TomlTable ReadToml(string path) =>
        TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))!;

    /// <summary>The <c>[projects]</c> key Codex itself would use — absolute, and lowercased on
    /// Windows. Asserting on the raw input path would only hold on Unix.</summary>
    static string Key(string worktreePath) => CodexPaths.NormalizeProjectKey(worktreePath);

    /// <summary>A seeded <c>[projects.'…']</c> header under the normalised key. TOML literal
    /// strings (single quotes) pass Windows backslashes through unescaped.</summary>
    static string SeededHeader(string worktreePath) => $"[projects.'{Key(worktreePath)}']";

    [Test]
    public async Task Writes_initial_projects_table_when_config_toml_missing() {
        using var tmp = ScopedHome(out var original);

        try {
            tmp.CreateDir(".codex");
            CodexConfigWriter.TrustWorktree("/tmp/some-worktree", NullLogger.Instance);

            var configPath = tmp.PathTo(".codex", "config.toml");
            await Assert.That(File.Exists(configPath)).IsTrue();

            var root     = ReadToml(configPath);
            var projects = (TomlTable)root["projects"];
            var entry    = (TomlTable)projects[Key("/tmp/some-worktree")];
            await Assert.That((string)entry["trust_level"]).IsEqualTo("trusted");
        } finally { RestoreHome(original); }
    }

    [Test]
    public async Task Writes_to_fresh_home_creates_codex_directory() {
        using var tmp = ScopedHome(out var original);

        // Explicitly NOT pre-creating .codex
        try {
            CodexConfigWriter.TrustWorktree("/tmp/wt", NullLogger.Instance);

            var codexDir = tmp.PathTo(".codex");
            await Assert.That(Directory.Exists(codexDir)).IsTrue();
            await Assert.That(File.Exists(tmp.PathTo(".codex", "config.toml"))).IsTrue();
        } finally { RestoreHome(original); }
    }

    [Test]
    public async Task Adds_entry_to_existing_config_preserving_other_tables() {
        using var tmp = ScopedHome(out var original);

        try {
            var codexDir = tmp.CreateDir(".codex");

            codexDir.CreateFile("config.toml",
                """
                model = "gpt-5.5"

                [mcp_servers.linear]
                url = "https://mcp.linear.app/mcp"

                [projects."/existing/path"]
                trust_level = "trusted"
                """
            );

            CodexConfigWriter.TrustWorktree("/tmp/new-wt", NullLogger.Instance);

            var root = ReadToml(tmp.PathTo(".codex", "config.toml"));
            await Assert.That((string)root["model"]).IsEqualTo("gpt-5.5");
            var mcp = (TomlTable)((TomlTable)root["mcp_servers"])["linear"];
            await Assert.That((string)mcp["url"]).IsEqualTo("https://mcp.linear.app/mcp");

            var projects = (TomlTable)root["projects"];
            await Assert.That((string)((TomlTable)projects["/existing/path"])["trust_level"]).IsEqualTo("trusted");
            await Assert.That((string)((TomlTable)projects[Key("/tmp/new-wt")])["trust_level"]).IsEqualTo("trusted");
        } finally { RestoreHome(original); }
    }

    [Test]
    public async Task Updates_trust_level_if_present_but_not_trusted() {
        using var tmp = ScopedHome(out var original);

        try {
            var codexDir = tmp.CreateDir(".codex");

            codexDir.CreateFile("config.toml",
                $"""
                 {SeededHeader("/tmp/wt")}
                 trust_level = "ask"
                 """
            );

            CodexConfigWriter.TrustWorktree("/tmp/wt", NullLogger.Instance);

            var root  = ReadToml(tmp.PathTo(".codex", "config.toml"));
            var entry = (TomlTable)((TomlTable)root["projects"])[Key("/tmp/wt")];
            await Assert.That((string)entry["trust_level"]).IsEqualTo("trusted");
        } finally { RestoreHome(original); }
    }

    [Test]
    public async Task No_op_when_trust_level_already_trusted() {
        using var tmp = ScopedHome(out var original);

        try {
            var codexDir   = tmp.CreateDir(".codex");
            var configPath = tmp.PathTo(".codex", "config.toml");

            codexDir.CreateFile("config.toml",
                $"""
                 {SeededHeader("/tmp/wt")}
                 trust_level = "trusted"
                 """
            );
            var originalMtime = File.GetLastWriteTimeUtc(configPath);

            await Task.Delay(20); // ensure mtime resolution gap
            CodexConfigWriter.TrustWorktree("/tmp/wt", NullLogger.Instance);

            await Assert.That(File.GetLastWriteTimeUtc(configPath)).IsEqualTo(originalMtime);
        } finally { RestoreHome(original); }
    }

    [Test]
    public async Task Atomic_rename_leaves_no_tmp_files() {
        using var tmp = ScopedHome(out var original);

        try {
            CodexConfigWriter.TrustWorktree("/tmp/wt-1", NullLogger.Instance);
            CodexConfigWriter.TrustWorktree("/tmp/wt-2", NullLogger.Instance);

            var codexDir = tmp.PathTo(".codex");
            var leftover = Directory.GetFiles(codexDir).Where(f => Path.GetFileName(f).Contains(".tmp-")).ToList();
            await Assert.That(leftover).IsEmpty();
        } finally { RestoreHome(original); }
    }

    [Test]
    public async Task Concurrent_writers_serialise_safely() {
        using var tmp = ScopedHome(out var original);

        try {
            var tasks = Enumerable.Range(0, 20)
                .Select(i => Task.Run(() => CodexConfigWriter.TrustWorktree($"/tmp/wt-{i}", NullLogger.Instance)))
                .ToArray();
            await Task.WhenAll(tasks);

            var configPath = tmp.PathTo(".codex", "config.toml");
            var root       = ReadToml(configPath);
            var projects   = (TomlTable)root["projects"];

            for (var i = 0; i < 20; i++) {
                var entry = (TomlTable)projects[Key($"/tmp/wt-{i}")];
                await Assert.That((string)entry["trust_level"]).IsEqualTo("trusted");
            }
        } finally { RestoreHome(original); }
    }

    [Test]
    public async Task Malformed_existing_config_is_skipped_not_overwritten() {
        using var tmp = ScopedHome(out var original);

        try {
            var          codexDir   = tmp.CreateDir(".codex");
            var          configPath = tmp.PathTo(".codex", "config.toml");
            const string garbage    = "{{{ not valid TOML";
            codexDir.CreateFile("config.toml", garbage);

            CodexConfigWriter.TrustWorktree("/tmp/wt", NullLogger.Instance);

            // File untouched, no throw
            await Assert.That(File.ReadAllText(configPath)).IsEqualTo(garbage);
        } finally { RestoreHome(original); }
    }
}
