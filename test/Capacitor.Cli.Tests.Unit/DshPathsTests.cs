using Capacitor.Cli.Core.Dsh;

namespace Capacitor.Cli.Tests.Unit;

public class DshPathsTests {
    // Parallel-safe: asserts invariant layout relationships that hold regardless of how the
    // roots resolve (DSH_HOME / home), so no env mutation is needed. Segment-name assertions
    // avoid Path.Combine-vs-GetDirectoryName separator differences on Windows.

    [Test]
    public async Task SessionJsonl_is_flat_id_jsonl_under_the_kcap_cache() {
        var jsonl = DshPaths.SessionJsonl("abc123", home: "/fake/home");

        // ~/.cache/kcap/dsh/{id}.jsonl (flat — one file per session, like OpenCode's cache).
        await Assert.That(Path.GetFileName(jsonl)).IsEqualTo("abc123.jsonl");
        var dir = Path.GetDirectoryName(jsonl)!;
        await Assert.That(Path.GetFileName(dir)).IsEqualTo("dsh");
        await Assert.That(Path.GetFileName(Path.GetDirectoryName(dir)!)).IsEqualTo("kcap");
    }

    [Test]
    public async Task KcapPlugin_lives_in_the_dsh_home() {
        var plugin = DshPaths.KcapPlugin(home: "/fake/home");
        var dshHome = DshPaths.DshHome(home: "/fake/home");

        await Assert.That(Path.GetFileName(plugin)).IsEqualTo("kcap-dsh.plugin.mjs");
        // plugin sits directly in the dsh home dir (compare leaf names — separator-independent)
        await Assert.That(Path.GetFileName(Path.GetDirectoryName(plugin)!)).IsEqualTo(Path.GetFileName(dshHome));
    }
}
