using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Commands;

[NotInParallel("HomeEnvVarMutation")]
public class PluginCommandClaudeTests {
    [Test]
    public async Task InstallPlugin_stamps_marker_on_success() {
        using var tmp = new TempDir();
        var settingsPath = tmp.PathTo("settings.json");

        var ok = SetupCommand.InstallPlugin(settingsPath, "/some/marketplace");
        await Assert.That(ok).IsTrue();

        var marker = tmp.PathTo(ClaudePluginInstaller.MarkerFileName);
        await Assert.That(File.Exists(marker)).IsTrue();
        await Assert.That((await File.ReadAllTextAsync(marker)).Trim())
            .IsEqualTo(CapacitorVersion.Current());
    }

    [Test]
    public async Task Install_claude_with_if_installed_is_noop_when_no_marker_and_no_entries() {
        using var fakeHome = new TempDir();
        var env            = TestEnv(fakeHome.Path);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        var settingsPath = Path.Combine(fakeHome.Path, ".claude", "settings.json");
        await Assert.That(File.Exists(settingsPath)).IsFalse();
    }

    [Test]
    public async Task Install_claude_with_if_installed_refreshes_pre_marker_install() {
        using var fakeHome  = new TempDir();
        using var pluginDir = new TempDir();

        // Seed pre-marker install: enabledPlugins entry, no marker.
        var claudeDir = Path.Combine(fakeHome.Path, ".claude");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/old/path" } } },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """);

        var env = TestEnv(fakeHome.Path, pluginPath: pluginDir.Path);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);

        // Marketplace path must now point at the new plugin dir.
        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        var path = root["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>();
        await Assert.That(path).IsEqualTo(pluginDir.Path);

        // Marker stamped.
        await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsTrue();
    }

    [Test]
    public async Task Install_claude_with_if_installed_is_noop_when_marker_matches_current_version() {
        using var fakeHome = new TempDir();

        var claudeDir = Path.Combine(fakeHome.Path, ".claude");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");

        // Sentinel content + matching marker.
        await File.WriteAllTextAsync(settingsPath, """{"sentinel": "must-survive"}""");
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        await Assert.That(root["sentinel"]!.GetValue<string>()).IsEqualTo("must-survive");
        await Assert.That(root["enabledPlugins"]).IsNull();
    }

    [Test]
    public async Task Install_claude_with_if_installed_swallows_plugin_resolution_failure() {
        using var fakeHome  = new TempDir();
        var capturedErr     = new StringWriter();

        // Seed: marker present so the gate proceeds…
        var claudeDir = Path.Combine(fakeHome.Path, ".claude");
        Directory.CreateDirectory(claudeDir);
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            "some-old-version");

        // …but plugin dir resolution fails (null = no plugin available).
        var env = TestEnv(fakeHome.Path, pluginPath: null, stderr: capturedErr);

        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"], env);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capturedErr.ToString()).IsEmpty();
    }

    [Test]
    public async Task Install_claude_fresh_prints_restart_reminder() {
        using var fakeHome  = new TempDir();
        using var pluginDir = new TempDir();
        var       stdout    = new StringWriter();

        var env  = TestEnv(fakeHome.Path, pluginPath: pluginDir.Path, stdout: stdout);
        var exit = await PluginCommand.HandleAsync(["plugin", "install"], env);

        await Assert.That(exit).IsEqualTo(0);

        var output = stdout.ToString();
        await Assert.That(output).Contains("Plugin installed");
        await Assert.That(output).Contains("new Claude Code session");
        await Assert.That(output).Contains("claude --continue");
    }

    [Test]
    public async Task Install_claude_refresh_omits_restart_reminder() {
        using var fakeHome  = new TempDir();
        using var pluginDir = new TempDir();
        var       stdout    = new StringWriter();

        // Seed a pre-marker install so --if-installed proceeds to refresh.
        var claudeDir = Path.Combine(fakeHome.Path, ".claude");
        Directory.CreateDirectory(claudeDir);
        await File.WriteAllTextAsync(Path.Combine(claudeDir, "settings.json"), """
            {
              "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/old/path" } } },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """);

        var env  = TestEnv(fakeHome.Path, pluginPath: pluginDir.Path, stdout: stdout);
        var exit = await PluginCommand.HandleAsync(["plugin", "install", "--if-installed"], env);

        await Assert.That(exit).IsEqualTo(0);

        var output = stdout.ToString();
        await Assert.That(output).Contains("Plugin refreshed");
        await Assert.That(output).DoesNotContain("claude --continue");
    }

    [Test]
    public async Task Remove_claude_deletes_marker() {
        using var fakeHome = new TempDir();

        var claudeDir = Path.Combine(fakeHome.Path, ".claude");
        Directory.CreateDirectory(claudeDir);
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/p" } } },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName),
            CapacitorVersion.Current());

        var exit = await PluginCommand.HandleAsync(["plugin", "remove"], TestEnv(fakeHome.Path));
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(Path.Combine(claudeDir, ClaudePluginInstaller.MarkerFileName))).IsFalse();
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
}
