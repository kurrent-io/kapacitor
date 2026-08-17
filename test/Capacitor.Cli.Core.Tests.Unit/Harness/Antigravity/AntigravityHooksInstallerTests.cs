using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Antigravity;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Antigravity;

/// <summary>
/// Unit tests for <see cref="AntigravityHooksInstaller"/> / <see cref="AntigravityHooks"/>
/// the kcap block is installed with the two shape variants, user blocks are
/// preserved, remove strips only kcap's block, and malformed JSON is backed up (never
/// silently clobbered).
/// </summary>
public class AntigravityHooksInstallerTests {
    [Test]
    public async Task Install_writes_kcap_block_with_both_entry_shapes() {
        using var dir = TempDir.WithPathTo("hooks.json", out var path);
        AntigravityHooksInstaller.Install(path);

        var root  = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var block = (JsonObject)root[AntigravityHooks.BlockName]!;

        // Lifecycle event: DIRECT handler list, distinct per-event command.
        var stop = (JsonArray)block["Stop"]!;
        await Assert.That((string?)stop[0]!["command"]).IsEqualTo("kcap hook --antigravity Stop");
        await Assert.That(stop[0]!["matcher"]).IsNull();

        // Tool event: matcher + nested hooks[].
        var pre = (JsonArray)block["PreToolUse"]!;
        await Assert.That((string?)pre[0]!["matcher"]).IsEqualTo("*");
        await Assert.That((string?)pre[0]!["hooks"]![0]!["command"]).IsEqualTo("kcap hook --antigravity PreToolUse");

        // All five events present.
        foreach (var e in AntigravityHooks.LifecycleEvents.Concat(AntigravityHooks.ToolEvents))
            await Assert.That(block.ContainsKey(e)).IsTrue();

        await Assert.That(AntigravityHooksInstaller.IsInstalled(path)).IsTrue();
    }

    // GUI re-test: the GUI only loads a plugin dir that contains a plugin.json
    // manifest — without it, hooks.json is never read. Install must write it; Remove must
    // clean it up.
    [Test]
    public async Task Install_writes_plugin_manifest_marker_and_Remove_deletes_it() {
        using var dir = new TempDir();
        var path     = dir.PathTo("hooks.json");
        var manifest = dir.PathTo(AntigravityHooksInstaller.PluginManifestFileName);
        AntigravityHooksInstaller.Install(path);

        await Assert.That(File.Exists(manifest)).IsTrue();
        var m = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(manifest))!;
        await Assert.That((string?)m["name"]).IsEqualTo(AntigravityHooks.BlockName);
        await Assert.That(m.ContainsKey("version")).IsTrue();

        AntigravityHooksInstaller.Remove(path);
        await Assert.That(File.Exists(manifest)).IsFalse();
    }

    // plugin.json is load-bearing, so a failure to write it must fail the
    // install rather than silently leaving a hooks.json the GUI ignores. Simulate the failure by
    // making the manifest PATH an existing directory (WriteAllText can't overwrite a dir).
    [Test]
    public async Task Install_throws_when_the_plugin_manifest_cannot_be_written() {
        using var dir = new TempDir();
        var path = dir.PathTo("hooks.json");
        Directory.CreateDirectory(dir.PathTo(AntigravityHooksInstaller.PluginManifestFileName));

        await Assert.That(() => AntigravityHooksInstaller.Install(path)).Throws<Exception>();
        // No dead-weight hooks.json / marker left implying a good install.
        await Assert.That(AntigravityHooksInstaller.IsInstalled(path)).IsFalse();
    }

    // removing kcap must not neuter user-authored blocks by deleting the
    // manifest — while a hooks.json remains for the GUI to load, plugin.json is kept.
    [Test]
    public async Task Remove_keeps_plugin_manifest_when_user_blocks_remain() {
        using var dir = new TempDir();
        var path     = dir.PathTo("hooks.json");
        var manifest = dir.PathTo(AntigravityHooksInstaller.PluginManifestFileName);
        await File.WriteAllTextAsync(path, new JsonObject {
            ["my-guard"] = new JsonObject { ["PreToolUse"] = new JsonArray() }
        }.ToJsonString());
        AntigravityHooksInstaller.Install(path);

        AntigravityHooksInstaller.Remove(path);

        await Assert.That(File.Exists(path)).IsTrue();       // user block preserved
        await Assert.That(File.Exists(manifest)).IsTrue();   // → manifest kept so it still loads
        await Assert.That(AntigravityHooksInstaller.IsInstalled(path)).IsFalse();
    }

    [Test]
    public async Task Install_preserves_user_authored_blocks() {
        using var dir = TempDir.WithPathTo("hooks.json", out var path);
        var existing = new JsonObject {
            ["my-guard"] = new JsonObject { ["PreToolUse"] = new JsonArray() }
        };
        await File.WriteAllTextAsync(path, existing.ToJsonString());

        AntigravityHooksInstaller.Install(path);

        var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        await Assert.That(root.ContainsKey("my-guard")).IsTrue();
        await Assert.That(root.ContainsKey(AntigravityHooks.BlockName)).IsTrue();
    }

    [Test]
    public async Task Remove_strips_only_kcap_block_and_marker() {
        using var dir = TempDir.WithPathTo("hooks.json", out var path);
        await File.WriteAllTextAsync(path, new JsonObject {
            ["my-guard"] = new JsonObject()
        }.ToJsonString());
        AntigravityHooksInstaller.Install(path);

        AntigravityHooksInstaller.Remove(path);

        var root = (JsonObject)JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        await Assert.That(root.ContainsKey(AntigravityHooks.BlockName)).IsFalse();
        await Assert.That(root.ContainsKey("my-guard")).IsTrue();
        await Assert.That(AntigravityHooksInstaller.IsInstalled(path)).IsFalse();
    }

    [Test]
    public async Task Install_backs_up_malformed_json_then_writes_valid() {
        using var dir = TempDir.WithPathTo("hooks.json", out var path);
        await File.WriteAllTextAsync(path, "{ this is not json");

        AntigravityHooksInstaller.Install(path);

        await Assert.That(File.Exists(path + ".bak")).IsTrue();
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject;
        await Assert.That(root).IsNotNull();
        await Assert.That(AntigravityHooksInstaller.IsInstalled(path)).IsTrue();
    }
}
