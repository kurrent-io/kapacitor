using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class PluginCommandCodexTests {
    [Test]
    public async Task InstallCodexHooks_writes_all_six_events() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        var ok = PluginCommand.InstallCodexHooks(path);
        await Assert.That(ok).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var hooks = root["hooks"]!.AsObject();

        foreach (var evt in new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop" }) {
            await Assert.That(hooks[evt]).IsNotNull();
            var entries = hooks[evt]!.AsArray();
            await Assert.That(entries.Count).IsEqualTo(1);

            var inner           = entries[0]!["hooks"]!.AsArray();
            var expectedTimeout = evt == "PermissionRequest" ? 86400 : 30;
            await Assert.That(inner[0]!["type"]!.GetValue<string>()).IsEqualTo("command");
            await Assert.That(inner[0]!["command"]!.GetValue<string>()).IsEqualTo("kcap hook --codex");
            await Assert.That(inner[0]!["timeout"]!.GetValue<int>()).IsEqualTo(expectedTimeout);
        }
    }

    [Test]
    public async Task PermissionRequest_hook_uses_long_timeout_so_dashboard_decision_isnt_killed() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        PluginCommand.InstallCodexHooks(path);

        var root             = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var permissionEntries = root["hooks"]!["PermissionRequest"]!.AsArray();
        var kcapEntry = permissionEntries.First(e =>
            (e!["hooks"] as JsonArray)!.Any(h =>
                h?["command"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("kcap hook --codex"))
        );
        var timeout = kcapEntry!["hooks"]!.AsArray()[0]!["timeout"]!.GetValue<int>();

        // 86400 = 24h; must be >= 86400 so Codex cannot kill the hook
        // before the dashboard sends back an approval or denial.
        await Assert.That(timeout).IsGreaterThanOrEqualTo(86400);
    }

    [Test]
    public async Task InstallCodexHooks_preserves_other_top_level_keys() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        await File.WriteAllTextAsync(path, """{"some_other_setting": true}""");

        PluginCommand.InstallCodexHooks(path);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        await Assert.That(root["some_other_setting"]!.GetValue<bool>()).IsTrue();
        await Assert.That(root["hooks"]).IsNotNull();
    }

    [Test]
    public async Task InstallCodexHooks_overwrites_existing_kcap_entries() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        // Seed with the pre-consolidation marker `kcap codex-hook` plus an
        // unrelated user-authored sibling. Install must rewrite the kcap entry
        // to the new `kcap hook --codex` shape while preserving the sibling.
        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "/usr/local/bin/other", "timeout": 5 }] }
                ]
              }
            }
            """);

        PluginCommand.InstallCodexHooks(path);

        var root         = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var sessionStart = root["hooks"]!["SessionStart"]!.AsArray();

        var commands = sessionStart
            .SelectMany(e => e!["hooks"]!.AsArray())
            .Select(h => h!["command"]!.GetValue<string>())
            .ToList();

        await Assert.That(commands).Contains("kcap hook --codex");
        await Assert.That(commands).Contains("/usr/local/bin/other");
        await Assert.That(commands.Count(c => c == "kcap hook --codex")).IsEqualTo(1);
        await Assert.That(commands).DoesNotContain("kcap codex-hook");
    }

    [Test]
    public async Task InstallCodexHooks_rewrites_pre_rename_kapacitor_marker() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        // Pre-rename installs left `kapacitor codex-hook` entries behind.
        // Install must rewrite them to the consolidated `kcap hook --codex`
        // form so postinstall refresh on upgrade fully migrates the user.
        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": "kapacitor codex-hook", "timeout": 5 }] }
                ]
              }
            }
            """);

        PluginCommand.InstallCodexHooks(path);

        var root         = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var sessionStart = root["hooks"]!["SessionStart"]!.AsArray();

        var commands = sessionStart
            .SelectMany(e => e!["hooks"]!.AsArray())
            .Select(h => h!["command"]!.GetValue<string>())
            .ToList();

        await Assert.That(commands).Contains("kcap hook --codex");
        await Assert.That(commands).DoesNotContain("kapacitor codex-hook");
    }

    [Test]
    public async Task RemoveCodexHooks_clears_all_kcap_entries() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        PluginCommand.InstallCodexHooks(path);
        var ok = PluginCommand.RemoveCodexHooks(path);
        await Assert.That(ok).IsTrue();

        var root  = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var hooks = root["hooks"]?.AsObject();

        if (hooks is not null) {
            foreach (var (_, value) in hooks) {
                var commands = value!.AsArray()
                    .SelectMany(e => e!["hooks"]!.AsArray())
                    .Select(h => h!["command"]!.GetValue<string>());

                foreach (var cmd in commands) {
                    await Assert.That(cmd).DoesNotContain("kcap hook --codex");
                    await Assert.That(cmd).DoesNotContain("kcap codex-hook");
                }
            }
        }
    }

    // Fix #2: non-string `command` field should not throw — treated as non-match.

    [Test]
    public async Task InstallCodexHooks_tolerates_numeric_command_field_in_existing_entry() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        // Entry whose inner hook has `command: 42` (a number, not a string).
        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": 42, "timeout": 5 }] }
                ]
              }
            }
            """);

        // Should not throw and should return true.
        var ok = PluginCommand.InstallCodexHooks(path);
        await Assert.That(ok).IsTrue();

        // The malformed entry (non-string command) must be preserved as a
        // non-kcap entry, and the kcap entry must also appear.
        var root         = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var sessionStart = root["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(2); // preserved + kcap
    }

    [Test]
    public async Task RemoveCodexHooks_tolerates_numeric_command_field_in_existing_entry() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        // Mix: a malformed entry (number command) and a real kcap entry.
        await File.WriteAllTextAsync(path, """
            {
              "hooks": {
                "SessionStart": [
                  { "hooks": [{ "type": "command", "command": 42, "timeout": 5 }] },
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 30 }] }
                ]
              }
            }
            """);

        var ok = PluginCommand.RemoveCodexHooks(path);
        await Assert.That(ok).IsTrue();

        // Malformed entry must be preserved; kcap entry removed.
        var root         = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var sessionStart = root["hooks"]!["SessionStart"]!.AsArray();
        await Assert.That(sessionStart.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EntryReferencesCapacitorCodexHook_returns_false_for_numeric_command() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":42}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesCapacitorCodexHook(entry)).IsFalse();
    }

    [Test]
    public async Task EntryReferencesCapacitorCodexHook_returns_true_for_matching_string_command() {
        var entry = JsonNode.Parse("""{"hooks":[{"type":"command","command":"kcap codex-hook"}]}""");
        await Assert.That(CodexHooksParser.EntryReferencesCapacitorCodexHook(entry)).IsTrue();
    }

    [Test]
    public async Task EntryReferencesCapacitorCodexHook_returns_false_for_null() {
        await Assert.That(CodexHooksParser.EntryReferencesCapacitorCodexHook(null)).IsFalse();
    }

    // ---- Agent skill install / remove (via AgentsSkillsInstaller) ----

    [Test]
    public async Task Install_copies_known_skills_with_kcap_prefix() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            WriteSkill(source, name, $"---\nname: {name}\n---\n{name} body");
        }

        var ok = AgentsSkillsInstaller.Install(source, target);
        await Assert.That(ok).IsTrue();

        var recap  = await File.ReadAllTextAsync(Path.Combine(target, "kcap-recap",  "SKILL.md"));
        var errors = await File.ReadAllTextAsync(Path.Combine(target, "kcap-errors", "SKILL.md"));
        await Assert.That(recap).Contains("recap body");
        await Assert.That(errors).Contains("errors body");
    }

    [Test]
    public async Task Install_copies_all_five_known_skills() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            WriteSkill(source, name, $"---\nname: {name}\n---\n{name} body");
        }

        var ok = AgentsSkillsInstaller.Install(source, target);
        await Assert.That(ok).IsTrue();

        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            var path = Path.Combine(target, $"kcap-{name}", "SKILL.md");
            await Assert.That(File.Exists(path)).IsTrue();
        }
    }

    [Test]
    public async Task SourceNames_contains_expected_skills() {
        var expected = new[] {
            "recap",
            "errors",
            "hide",
            "disable",
            "validate-plan",
            "review-flows",
            "guided-tour"
        };

        await Assert.That(AgentsSkillsInstaller.SourceNames).IsEquivalentTo(expected);
    }

    [Test]
    public async Task Install_returns_false_when_known_folder_missing() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");

        // Write four of the five expected names — leave validate-plan missing.
        WriteSkill(source, "recap",   "r");
        WriteSkill(source, "errors",  "e");
        WriteSkill(source, "hide",    "h");
        WriteSkill(source, "disable", "d");

        // Pre-existing target folder for one of the known skills. The preflight
        // must NOT delete it because the install is aborted before any destructive
        // step runs.
        WriteSkill(target, "kcap-recap", "stale recap that must survive");

        var ok = AgentsSkillsInstaller.Install(source, target);
        await Assert.That(ok).IsFalse();

        // Pre-existing target folder unchanged — preflight bailed before deletion.
        var preserved = await File.ReadAllTextAsync(Path.Combine(target, "kcap-recap", "SKILL.md"));
        await Assert.That(preserved).IsEqualTo("stale recap that must survive");

        // None of the other expected target folders were created.
        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-errors"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-hide"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-disable"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-validate-plan"))).IsFalse();
    }

    [Test]
    public async Task Install_overwrites_existing_kcap_skill() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            WriteSkill(source, name, $"---\nname: {name}\n---\n{(name == "recap" ? "new recap" : $"{name} body")}");
        }

        // Pre-existing stale copy in target.
        WriteSkill(target, "kcap-recap", "stale recap");

        AgentsSkillsInstaller.Install(source, target);

        var recap = await File.ReadAllTextAsync(Path.Combine(target, "kcap-recap", "SKILL.md"));
        await Assert.That(recap).Contains("new recap");
    }

    [Test]
    public async Task Install_preserves_foreign_skills() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "skills");
        var       target = Path.Combine(tmp.Path, "agents-skills");
        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            WriteSkill(source, name, $"---\nname: {name}\n---\n{name} body");
        }

        // Foreign skill the user installed themselves — must not be touched.
        WriteSkill(target, "user-skill", "user content");

        AgentsSkillsInstaller.Install(source, target);

        var foreign = await File.ReadAllTextAsync(Path.Combine(target, "user-skill", "SKILL.md"));
        await Assert.That(foreign).IsEqualTo("user content");
    }

    [Test]
    public async Task Install_returns_false_for_missing_source() {
        using var tmp    = new TempDir();
        var       source = Path.Combine(tmp.Path, "nonexistent");
        var       target = Path.Combine(tmp.Path, "agents-skills");

        var ok = AgentsSkillsInstaller.Install(source, target);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Remove_deletes_known_skills_only() {
        using var tmp    = new TempDir();
        var       target = Path.Combine(tmp.Path, "agents-skills");
        WriteSkill(target, "kcap-recap",  "recap");
        WriteSkill(target, "kcap-errors", "errors");
        WriteSkill(target, "user-skill",  "user content");

        var result = AgentsSkillsInstaller.Remove(target);
        await Assert.That(result.RemovedAny).IsTrue();

        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-recap"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(target, "kcap-errors"))).IsFalse();
        await Assert.That(File.Exists(Path.Combine(target, "user-skill", "SKILL.md"))).IsTrue();
    }

    [Test]
    public async Task Remove_returns_false_when_nothing_to_remove() {
        using var tmp    = new TempDir();
        var       target = Path.Combine(tmp.Path, "agents-skills");
        Directory.CreateDirectory(target);

        var result = AgentsSkillsInstaller.Remove(target);
        await Assert.That(result.RemovedAny).IsFalse();
    }

    [Test]
    public async Task Install_codex_with_if_installed_is_noop_when_no_marker_and_no_existing_entries() {
        using var fakeHome = new TempDir();

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--codex", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        // hooks.json must NOT exist — user never opted in.
        var hooksPath = Path.Combine(fakeHome.Path, ".codex", "hooks.json");
        await Assert.That(File.Exists(hooksPath)).IsFalse();
    }

    [Test]
    public async Task Install_codex_with_if_installed_refreshes_pre_marker_install() {
        using var fakeHome = new TempDir();

        // Seed hooks.json with a stale 5-second PermissionRequest timeout
        // and NO marker. This is the pre-marker scenario.
        var codexDir = Path.Combine(fakeHome.Path, ".codex");
        Directory.CreateDirectory(codexDir);
        var hooksPath = Path.Combine(codexDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """
            {
              "hooks": {
                "PermissionRequest": [
                  { "hooks": [{ "type": "command", "command": "kcap codex-hook", "timeout": 5 }] }
                ]
              }
            }
            """);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--codex", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        // PermissionRequest timeout must have been refreshed to 86400.
        var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        var entries = root["hooks"]!["PermissionRequest"]!.AsArray();
        var kcap = entries.First(e =>
            (e!["hooks"] as JsonArray)!.Any(h =>
                h?["command"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("kcap hook --codex")));
        await Assert.That(kcap!["hooks"]!.AsArray()[0]!["timeout"]!.GetValue<int>())
            .IsEqualTo(86400);

        // Marker now stamped → next upgrade takes the fast path.
        await Assert.That(File.Exists(Path.Combine(codexDir, CodexHooksInstaller.MarkerFileName))).IsTrue();
    }

    [Test]
    public async Task Install_codex_with_if_installed_is_noop_when_marker_matches_current_version() {
        using var fakeHome = new TempDir();

        var codexDir = Path.Combine(fakeHome.Path, ".codex");
        Directory.CreateDirectory(codexDir);

        // Pre-seed hooks.json with sentinel content + matching marker.
        var hooksPath = Path.Combine(codexDir, "hooks.json");
        await File.WriteAllTextAsync(hooksPath, """{"sentinel": "must-survive"}""");
        await File.WriteAllTextAsync(
            Path.Combine(codexDir, CodexHooksInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--codex", "--if-installed"],
            TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        // Sentinel intact → installer short-circuited.
        var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        await Assert.That(root["sentinel"]!.GetValue<string>()).IsEqualTo("must-survive");
        await Assert.That(root["hooks"]).IsNull();
    }

    [Test]
    public async Task InstallCodexHooks_stamps_marker_on_success() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        var ok = PluginCommand.InstallCodexHooks(path);
        await Assert.That(ok).IsTrue();

        var marker = Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName);
        await Assert.That(File.Exists(marker)).IsTrue();
        await Assert.That((await File.ReadAllTextAsync(marker)).Trim())
            .IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task RemoveCodexHooks_deletes_marker_when_kcap_entries_were_removed() {
        using var tmp  = new TempDir();
        var       path = Path.Combine(tmp.Path, "hooks.json");

        PluginCommand.InstallCodexHooks(path);
        await Assert.That(File.Exists(Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName))).IsTrue();

        var changed = PluginCommand.RemoveCodexHooks(path);
        await Assert.That(changed).IsTrue();
        await Assert.That(File.Exists(Path.Combine(tmp.Path, CodexHooksInstaller.MarkerFileName))).IsFalse();
    }

    static PluginEnvironment TestEnv(
        string      fakeHome,
        string?     pluginPath = null,
        TextWriter? stdout     = null,
        TextWriter? stderr     = null
    ) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => pluginPath,
        Stdout:            stdout ?? TextWriter.Null,
        Stderr:            stderr ?? TextWriter.Null
    );

    static void WriteSkill(string root, string name, string body) {
        var dir = Path.Combine(root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), body);
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}

public class PluginCommandCodexInstallIntegrationTests {
    [Test]
    public async Task InstallCodex_prints_hooks_trust_hint_after_success() {
        using var fakeHome   = new TempDir();
        using var pluginRoot = new TempDir();
        PlantFakePlugin(pluginRoot.Path);

        var capturedOut = new StringWriter();
        var env         = TestEnv(fakeHome.Path, pluginRoot.Path, stdout: capturedOut);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex"], env);
        await Assert.That(exit).IsEqualTo(0);

        var stdout = capturedOut.ToString();
        await Assert.That(stdout).Contains("/hooks");
        await Assert.That(stdout).Contains("trust");
    }

    // Regression: when the top-level skills/ folder is present but one
    // or more individual skill sub-folders are missing (packaging defect),
    // `plugin install --codex` must fail BEFORE writing hooks. The per-skill
    // preflight must run before InstallCodexHooks to maintain the atomicity
    // guarantee from — either everything installs or nothing does.
    [Test]
    public async Task InstallCodex_fails_before_writing_hooks_when_individual_skill_folder_missing() {
        using var fakeHome   = new TempDir();
        using var pluginRoot = new TempDir();

        // Plant a fake plugin tree with skills/ present but validate-plan/ missing.
        var skillsSrc = Path.Combine(pluginRoot.Path, "skills");
        Directory.CreateDirectory(skillsSrc);
        foreach (var name in AgentsSkillsInstaller.SourceNames.Where(n => n != "validate-plan")) {
            var dir = Path.Combine(skillsSrc, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\n---\n# {name}");
        }

        var capturedErr = new StringWriter();
        var env         = TestEnv(fakeHome.Path, pluginRoot.Path, stderr: capturedErr);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex"], env);
        await Assert.That(exit).IsEqualTo(1);

        // Atomicity contract: hooks.json must NOT exist after a failed install.
        var hooksPath = Path.Combine(fakeHome.Path, ".codex", "hooks.json");
        await Assert.That(File.Exists(hooksPath)).IsFalse();

        var stderr = capturedErr.ToString();
        await Assert.That(stderr).Contains("Cannot install Codex plugin");
    }

    // when the kcap plugin folder cannot be resolved (e.g.,
    // the binary was hand-copied and the sibling `kcap/` tree is gone),
    // `plugin install --codex` must fail BEFORE writing hooks. Otherwise the
    // user ends up with hook entries pointing at a kcap binary whose
    // skills never installed, breaking the documented `--codex` contract.
    [Test]
    public async Task InstallCodex_fails_before_writing_hooks_when_plugin_folder_missing() {
        using var fakeHome = new TempDir();

        var capturedErr = new StringWriter();
        // PluginPath = null signals ResolvePluginPath returned no plugin.
        var env = TestEnv(fakeHome.Path, pluginPath: null, stderr: capturedErr);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--codex"], env);
        await Assert.That(exit).IsEqualTo(1);

        // The atomic-install contract: NO hooks.json may exist in the
        // temp HOME after a failed install.
        var hooksPath = Path.Combine(fakeHome.Path, ".codex", "hooks.json");
        await Assert.That(File.Exists(hooksPath)).IsFalse();

        var stderr = capturedErr.ToString();
        await Assert.That(stderr).Contains("Cannot install Codex plugin");
    }

    [Test]
    public async Task InstallCodex_registers_mcp_servers_in_config_toml() {
        using var fakeHome   = new TempDir();
        using var pluginRoot = new TempDir();
        PlantFakePlugin(pluginRoot.Path);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "install", "--codex"], TestEnv(fakeHome.Path, pluginRoot.Path));
        await Assert.That(exit).IsEqualTo(0);

        var configPath = Path.Combine(fakeHome.Path, ".codex", "config.toml");
        var toml       = await File.ReadAllTextAsync(configPath);
        await Assert.That(toml).Contains("[mcp_servers.kcap-review]");
        await Assert.That(toml).Contains("[mcp_servers.kcap-sessions]");
        await Assert.That(toml).Contains("[mcp_servers.kcap-memory]");
    }

    [Test]
    public async Task RemoveCodex_user_scope_preserves_unowned_manual_mcp_servers() {
        using var fakeHome = new TempDir();
        var configPath = SeedCodexConfigWithKcapServers(fakeHome.Path);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "remove", "--codex"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var toml = await File.ReadAllTextAsync(configPath);
        await Assert.That(toml).Contains("kcap-review");
        await Assert.That(toml).Contains("kcap-sessions");
        await Assert.That(toml).Contains("kcap-memory");
        await Assert.That(toml).Contains("my-tool"); // user's server preserved
    }

    [Test]
    public async Task RemoveCodex_project_scope_leaves_user_global_mcp_servers() {
        // Regression: a project-scoped uninstall must NOT strip the user-global
        // kcap MCP servers, which every other repo relies on.
        using var fakeHome = new TempDir();
        var configPath = SeedCodexConfigWithKcapServers(fakeHome.Path);
        var before     = await File.ReadAllTextAsync(configPath);

        var exit = await PluginCommand.HandleAsync(
            ["plugin", "remove", "--codex", "--project"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        // config.toml is untouched — the user-global servers survive.
        await Assert.That(await File.ReadAllTextAsync(configPath)).IsEqualTo(before);
    }

    static string SeedCodexConfigWithKcapServers(string fakeHome) {
        var codexDir = Path.Combine(fakeHome, ".codex");
        Directory.CreateDirectory(codexDir);
        var configPath = Path.Combine(codexDir, "config.toml");
        File.WriteAllText(configPath,
            """
            [mcp_servers.kcap-review]
            command = "kcap"
            args = ["mcp", "review"]

            [mcp_servers.kcap-sessions]
            command = "kcap"
            args = ["mcp", "sessions"]

            [mcp_servers.kcap-memory]
            command = "kcap"
            args = ["mcp", "memory"]

            [mcp_servers.my-tool]
            command = "my-tool"
            args = ["serve"]
            """);
        return configPath;
    }

    static void PlantFakePlugin(string pluginRoot) {
        var skillsSrc = Path.Combine(pluginRoot, "skills");
        Directory.CreateDirectory(skillsSrc);

        foreach (var name in AgentsSkillsInstaller.SourceNames) {
            var dir = Path.Combine(skillsSrc, name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\n---\n# {name}");
        }
    }

    static PluginEnvironment TestEnv(
        string      fakeHome,
        string?     pluginPath = null,
        TextWriter? stdout     = null,
        TextWriter? stderr     = null
    ) => new(
        HomeDirectory:     fakeHome,
        ResolvePluginPath: () => pluginPath,
        Stdout:            stdout ?? TextWriter.Null,
        Stderr:            stderr ?? TextWriter.Null
    );

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-test-{Guid.NewGuid().ToString("N")[..8]}"
        );
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }
}
