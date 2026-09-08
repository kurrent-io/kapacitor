using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Commands;

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

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        var settingsPath = fakeHome.PathTo(".claude", "settings.json");
        await Assert.That(File.Exists(settingsPath)).IsFalse();
    }

    [Test]
    public async Task Install_claude_with_if_installed_refreshes_pre_marker_install() {
        using var fakeHome  = new TempDir();
        using var pluginDir = new TempDir();

        // Seed pre-marker install: enabledPlugins entry, no marker.
        var claudeDir = fakeHome.CreateDir(".claude");
        var settingsPath = claudeDir.PathTo("settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/old/path" } } },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """);

        var env = TestEnv(fakeHome.Path, pluginPath: pluginDir.Path);

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);

        // Marketplace path must now point at the new plugin dir.
        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();
        var path = root["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>();
        await Assert.That(path).IsEqualTo(pluginDir.Path);

        // Marker stamped.
        await Assert.That(File.Exists(claudeDir.PathTo(ClaudePluginInstaller.MarkerFileName))).IsTrue();
    }

    [Test]
    public async Task Install_claude_with_if_installed_is_noop_when_marker_matches_current_version() {
        using var fakeHome = new TempDir();

        var claudeDir = fakeHome.CreateDir(".claude");
        var settingsPath = claudeDir.PathTo("settings.json");

        // Sentinel content + matching marker.
        await File.WriteAllTextAsync(settingsPath, """{"sentinel": "must-survive"}""");
        claudeDir.CreateFile(ClaudePluginInstaller.MarkerFileName,
            CapacitorVersion.Current());

        var exit = await new PluginCommand(TestEnv(fakeHome.Path)).HandleAsync(["plugin", "install", "--if-installed"]);
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
        var claudeDir = fakeHome.CreateDir(".claude");
        claudeDir.CreateFile(ClaudePluginInstaller.MarkerFileName,
            "some-old-version");

        // …but plugin dir resolution fails (null = no plugin available).
        var env = TestEnv(fakeHome.Path, pluginPath: null, stderr: capturedErr);

        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--if-installed"]);
        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(capturedErr.ToString()).IsEmpty();
    }

    [Test]
    public async Task Install_claude_fresh_prints_restart_reminder() {
        using var fakeHome  = new TempDir();
        using var pluginDir = new TempDir();
        var       stdout    = new StringWriter();

        var env  = TestEnv(fakeHome.Path, pluginPath: pluginDir.Path, stdout: stdout);
        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install"]);

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
        var claudeDir = fakeHome.CreateDir(".claude");
        claudeDir.CreateFile("settings.json", """
            {
              "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/old/path" } } },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """);

        var env  = TestEnv(fakeHome.Path, pluginPath: pluginDir.Path, stdout: stdout);
        var exit = await new PluginCommand(env).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(exit).IsEqualTo(0);

        var output = stdout.ToString();
        await Assert.That(output).Contains("Plugin refreshed");
        await Assert.That(output).DoesNotContain("claude --continue");
    }

    [Test]
    public async Task Remove_claude_deletes_marker() {
        using var fakeHome = new TempDir();

        var claudeDir = fakeHome.CreateDir(".claude");
        claudeDir.CreateFile("settings.json", """
            {
              "extraKnownMarketplaces": { "kcap": { "source": { "source": "directory", "path": "/p" } } },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """);
        claudeDir.CreateFile(ClaudePluginInstaller.MarkerFileName, CapacitorVersion.Current());

        var exit = await new PluginCommand(TestEnv(fakeHome.Path)).HandleAsync(["plugin", "remove"]);
        await Assert.That(exit).IsEqualTo(0);

        await Assert.That(File.Exists(claudeDir.PathTo(ClaudePluginInstaller.MarkerFileName))).IsFalse();
    }

    static PluginEnvironment TestEnv(
        string      fakeHome,
        string?     pluginPath = null,
        TextWriter? stdout     = null,
        TextWriter? stderr     = null
    ) => new(
        Home:     new(fakeHome),
        Profiles:          new ProfileConfig(),
        ResolvePluginPath: () => pluginPath,
        Stdout:            stdout ?? TextWriter.Null,
        Stderr:            stderr ?? TextWriter.Null
    ) {
        Harnesses = TestHarnesses.Under(new(fakeHome)),
    };
}
