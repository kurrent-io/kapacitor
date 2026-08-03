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
        // ClaudePluginInstaller.IsInstalled recognises an enabledPlugins["kcap@kcap"] flag in
        // the settings file — with CLAUDE_CONFIG_DIR set that is $CLAUDE_CONFIG_DIR/settings.json.
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
}
