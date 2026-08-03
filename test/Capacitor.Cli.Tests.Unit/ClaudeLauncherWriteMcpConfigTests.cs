using System.Text.Json.Nodes;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Pins that <see cref="ClaudeLauncher.WriteMcpConfig"/> reads the source repo's
/// MCP servers from <c>~/.claude.json</c> under Claude Code's normalised
/// <c>projects[]</c> key (forward slashes on Windows — see
/// <see cref="ClaudeLauncher.NormalizeClaudeProjectKey"/>), with a raw-path
/// fallback for entries written by older builds or by hand. Before the fix the
/// lookup used the raw Windows backslash path, missed the normalised entry, and
/// silently skipped copying the user's MCP servers into the hosted worktree.
/// </summary>
[NotInParallel("HomeEnvVarMutation")]
public class ClaudeLauncherWriteMcpConfigTests {

    static async Task RunWithRelocatedConfigAsync(Func<string, string, string, Task> body) {
        var original = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var root     = Path.Combine(Path.GetTempPath(), "kcap-mcpcfg-" + Guid.NewGuid().ToString("N")[..8]);

        var configDir  = Path.Combine(root, "claude-cfg");
        var sourceRepo = Path.Combine(root, "source-repo");
        var worktree   = Path.Combine(root, "worktree");

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(sourceRepo);
        Directory.CreateDirectory(worktree);

        try {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", configDir);
            await body(configDir, sourceRepo, worktree);
        } finally {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", original);
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    static void WriteClaudeJson(string configDir, string projectKey) {
        var json = new JsonObject {
            ["projects"] = new JsonObject {
                [projectKey] = new JsonObject {
                    ["mcpServers"] = new JsonObject {
                        ["my-server"] = new JsonObject {
                            ["command"] = "some-mcp",
                            ["env"]     = new JsonObject { ["SECRET"] = "do-not-copy" }
                        }
                    }
                }
            }
        };

        File.WriteAllText(Path.Combine(configDir, ".claude.json"), json.ToJsonString());
    }

    static JsonObject? ReadWorktreeMcpServers(string worktree) {
        var path = Path.Combine(worktree, ".mcp.json");

        if (!File.Exists(path)) return null;

        return JsonNode.Parse(File.ReadAllText(path))?["mcpServers"]?.AsObject();
    }

    /// <summary>
    /// The entry Claude itself writes: keyed by the normalised path. On Windows
    /// that differs from the raw path (forward vs. back slashes) — the lookup
    /// must still find it. On POSIX both spellings coincide, so this documents
    /// the invariant and guards the Windows behaviour where CI runs there.
    /// </summary>
    [Test]
    public async Task Finds_servers_under_normalized_project_key() {
        await RunWithRelocatedConfigAsync(async (configDir, sourceRepo, worktree) => {
            WriteClaudeJson(configDir, ClaudeLauncher.NormalizeClaudeProjectKey(sourceRepo));

            ClaudeLauncher.WriteMcpConfig(sourceRepo, worktree);

            var servers = ReadWorktreeMcpServers(worktree);
            await Assert.That(servers).IsNotNull();
            await Assert.That(servers!.ContainsKey("my-server")).IsTrue();

            // env must be stripped from the copied server definition.
            await Assert.That(servers["my-server"]!.AsObject().ContainsKey("env")).IsFalse();
        });
    }

    /// <summary>
    /// Backward compatibility: an entry stored under the raw (unnormalised)
    /// path — e.g. written by an older kcap build on Windows — is still found
    /// via the fallback lookup.
    /// </summary>
    [Test]
    public async Task Falls_back_to_raw_project_key() {
        await RunWithRelocatedConfigAsync(async (configDir, sourceRepo, worktree) => {
            WriteClaudeJson(configDir, sourceRepo);

            ClaudeLauncher.WriteMcpConfig(sourceRepo, worktree);

            var servers = ReadWorktreeMcpServers(worktree);
            await Assert.That(servers).IsNotNull();
            await Assert.That(servers!.ContainsKey("my-server")).IsTrue();
        });
    }

    /// <summary>No project entry under either key → no .mcp.json written.</summary>
    [Test]
    public async Task No_matching_project_entry_writes_nothing() {
        await RunWithRelocatedConfigAsync(async (configDir, sourceRepo, worktree) => {
            WriteClaudeJson(configDir, Path.Combine(Path.GetTempPath(), "some-other-repo"));

            ClaudeLauncher.WriteMcpConfig(sourceRepo, worktree);

            await Assert.That(File.Exists(Path.Combine(worktree, ".mcp.json"))).IsFalse();
        });
    }

    // ── Phase-1 dedupe: don't propagate plugin-shadowed kcap entries ────────────

    static void WriteClaudeJsonWithServers(string configDir, string projectKey, JsonObject servers) {
        var json = new JsonObject {
            ["projects"] = new JsonObject {
                [projectKey] = new JsonObject { ["mcpServers"] = servers }
            }
        };

        File.WriteAllText(Path.Combine(configDir, ".claude.json"), json.ToJsonString());
    }

    static void MarkClaudePluginInstalled(string configDir) {
        // The merge-skip gates on ClaudePluginInstaller.IsEffectivelyInstalled: an enabled
        // registration in settings.json ($CLAUDE_CONFIG_DIR/settings.json here) AND the enabled
        // plugin's INSTALLED payload — resolved via plugins/installed_plugins.json the way
        // Claude loads it, never a marketplace source dir.
        var installPath = Path.Combine(configDir, "plugins", "cache", "kcap", "kcap", "1.0.0");
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, ".mcp.json"), "{}");
        File.WriteAllText(
            Path.Combine(configDir, "plugins", "installed_plugins.json"),
            $$"""
            { "version": 2, "plugins": { "kcap@kcap": [
                { "scope": "user", "installPath": {{JsonValue.Create(installPath).ToJsonString()}}, "version": "1.0.0" } ] } }
            """);
        File.WriteAllText(
            Path.Combine(configDir, "settings.json"),
            """{ "enabledPlugins": { "kcap@kcap": true } }""");
    }

    static JsonObject KcapEntry(string suffix, string command = "kcap") => new() {
        ["command"] = command,
        ["args"]    = new JsonArray("mcp", suffix)
    };

    /// <summary>
    /// The Claude plugin already ships the kcap servers session-wide, so a semantically
    /// canonical project-scope copy would only spawn a duplicate resident server process in
    /// the agent session. The skip is STRUCTURAL, never name-only: a divergent same-name
    /// entry (different command) is a user customization and is still merged, as is any
    /// non-kcap server.
    /// </summary>
    [Test]
    public async Task Skips_canonical_kcap_duplicate_but_keeps_divergent_and_custom_servers() {
        await RunWithRelocatedConfigAsync(async (configDir, sourceRepo, worktree) => {
            MarkClaudePluginInstalled(configDir);
            WriteClaudeJsonWithServers(configDir, ClaudeLauncher.NormalizeClaudeProjectKey(sourceRepo),
                new JsonObject {
                    ["kcap-flows"]    = KcapEntry("flows"),                          // canonical → skipped
                    ["kcap-sessions"] = KcapEntry("sessions", "/custom/build/kcap"), // divergent → kept
                    ["my-custom"]     = new JsonObject { ["command"] = "some-mcp" }  // foreign → kept
                });

            ClaudeLauncher.WriteMcpConfig(sourceRepo, worktree);

            var servers = ReadWorktreeMcpServers(worktree);
            await Assert.That(servers).IsNotNull();
            await Assert.That(servers!.ContainsKey("kcap-flows")).IsFalse();
            await Assert.That(servers.ContainsKey("kcap-sessions")).IsTrue();
            await Assert.That(servers.ContainsKey("my-custom")).IsTrue();
        });
    }

    /// <summary>
    /// Without the Claude plugin installed nothing shadows the project-scope entry — it is
    /// the user's only registration of the server and must keep being copied.
    /// </summary>
    [Test]
    public async Task Merges_canonical_kcap_entry_when_plugin_not_installed() {
        await RunWithRelocatedConfigAsync(async (configDir, sourceRepo, worktree) => {
            WriteClaudeJsonWithServers(configDir, ClaudeLauncher.NormalizeClaudeProjectKey(sourceRepo),
                new JsonObject { ["kcap-flows"] = KcapEntry("flows") });

            ClaudeLauncher.WriteMcpConfig(sourceRepo, worktree);

            var servers = ReadWorktreeMcpServers(worktree);
            await Assert.That(servers).IsNotNull();
            await Assert.That(servers!.ContainsKey("kcap-flows")).IsTrue();
        });
    }

    /// <summary>
    /// A stale <c>.kcap-plugin-version</c> marker (manual removal, failed refresh, npm
    /// re-layout) satisfies the refresh gate but must NOT authorize the merge-skip: without a
    /// loadable plugin payload the copy is the user's only registration of the server.
    /// </summary>
    [Test]
    public async Task Version_marker_alone_does_not_authorize_the_merge_skip() {
        await RunWithRelocatedConfigAsync(async (configDir, sourceRepo, worktree) => {
            File.WriteAllText(Path.Combine(configDir, ".kcap-plugin-version"), "9.9.9");
            WriteClaudeJsonWithServers(configDir, ClaudeLauncher.NormalizeClaudeProjectKey(sourceRepo),
                new JsonObject { ["kcap-flows"] = KcapEntry("flows") });

            ClaudeLauncher.WriteMcpConfig(sourceRepo, worktree);

            var servers = ReadWorktreeMcpServers(worktree);
            await Assert.That(servers).IsNotNull();
            await Assert.That(servers!.ContainsKey("kcap-flows")).IsTrue(); // still merged
        });
    }

    /// <summary>
    /// ALL kcap writers of ~/.claude.json share one cross-process lock (ConfigFileLock): the
    /// daemon's trust write must not be able to commit between doctor --clean's inside-lock
    /// re-read and its rename, where the rename would silently overwrite it. The doctor is
    /// parked inside its lock via a test hook; the trust write started there must block until
    /// the clean commits, and then land ON TOP of the cleaned file — both changes survive.
    /// Without the trust writer taking the lock, it completes inside the parked window and the
    /// doctor's rename deterministically erases it.
    /// </summary>
    [Test]
    public async Task Trust_write_during_a_parked_doctor_clean_cannot_be_lost() {
        await RunWithRelocatedConfigAsync(async (configDir, sourceRepo, worktree) => {
            var cfgPath  = Path.Combine(configDir, ".claude.json");
            var snapshot = """{ "mcpServers": { "kcap-flows": { "command": "kcap", "args": ["mcp","flows"] } } }""";
            File.WriteAllText(cfgPath, snapshot);

            Task? trust = null;
            var outcome = Capacitor.Cli.Commands.McpDoctorSection.TryCleanClaudeConfig(
                cfgPath, snapshot, null, out _,
                afterReReadForTesting: () => {
                    trust = Task.Run(() => ClaudeLauncher.TrustWorktreeInClaudeConfig(worktree));
                    // With the shared lock the trust writer blocks here (this thread holds the
                    // lock), so the bounded wait elapses. Without it, the trust write finishes
                    // NOW, and the doctor's rename below deterministically overwrites it.
                    trust.Wait(TimeSpan.FromSeconds(2));
                });
            await trust!;

            await Assert.That(outcome).IsEqualTo(Capacitor.Cli.Commands.McpDoctorSection.CleanOutcome.Cleaned);
            var root = (JsonObject)JsonNode.Parse(File.ReadAllText(cfgPath))!;
            await Assert.That(((JsonObject)root["mcpServers"]!).ContainsKey("kcap-flows")).IsFalse(); // clean applied
            var trustKey = ClaudeLauncher.NormalizeClaudeProjectKey(worktree);
            await Assert.That((bool)root["projects"]![trustKey]!["hasTrustDialogAccepted"]!).IsTrue(); // trust survived
        });
    }

    /// <summary>
    /// Inside the daemon Environment.ProcessPath is kcap-daemon, so recognizing a canonical
    /// absolute-path entry needs the CLI path from DaemonConfig — without it, exactly the
    /// duplicate this skip exists to remove would be copied into the worktree.
    /// </summary>
    [Test]
    public async Task Skips_canonical_absolute_path_entry_when_the_cli_path_is_supplied() {
        await RunWithRelocatedConfigAsync(async (configDir, sourceRepo, worktree) => {
            MarkClaudePluginInstalled(configDir);
            var cliPath = Path.Combine(configDir, "bin", "kcap"); // deterministic, never this test host
            WriteClaudeJsonWithServers(configDir, ClaudeLauncher.NormalizeClaudeProjectKey(sourceRepo),
                new JsonObject {
                    ["kcap-flows"]    = KcapEntry("flows", cliPath),          // canonical at the CLI path → skipped
                    ["kcap-sessions"] = KcapEntry("sessions", "/other/kcap")  // different binary → conflict, kept
                });

            ClaudeLauncher.WriteMcpConfig(sourceRepo, worktree, nativeKcapPath: cliPath);

            var servers = ReadWorktreeMcpServers(worktree);
            await Assert.That(servers).IsNotNull();
            await Assert.That(servers!.ContainsKey("kcap-flows")).IsFalse();
            await Assert.That(servers.ContainsKey("kcap-sessions")).IsTrue();
        });
    }
}
