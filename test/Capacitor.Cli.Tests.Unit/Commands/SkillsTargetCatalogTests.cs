using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SkillsTargetCatalogTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task Consumer_presence_maps_each_target_to_its_readers() {
        static Capacitor.Cli.Core.Setup.DetectedAgent Yes() => new(true, false);
        static Capacitor.Cli.Core.Setup.DetectedAgent No()  => new(false, false);
        static Capacitor.Cli.Core.Setup.AgentDetectionResult Only(string vendor) => new(
            Claude:      vendor == "claude"      ? Yes() : No(),
            Codex:       vendor == "codex"       ? Yes() : No(),
            Cursor:      vendor == "cursor"      ? Yes() : No(),
            Copilot:     vendor == "copilot"     ? Yes() : No(),
            Gemini:      vendor == "gemini"      ? Yes() : No(),
            Kiro:        vendor == "kiro"        ? Yes() : No(),
            Pi:          vendor == "pi"          ? Yes() : No(),
            OpenCode:    vendor == "opencode"    ? Yes() : No(),
            Antigravity: vendor == "antigravity" ? Yes() : No());

        // A codex-only machine adopts the shared agents tree and nothing vendored.
        await Assert.That(SkillsCommand.ConsumerPresent(Only("codex"), "agents")).IsTrue();
        await Assert.That(SkillsCommand.ConsumerPresent(Only("codex"), "claude")).IsFalse();
        await Assert.That(SkillsCommand.ConsumerPresent(Only("codex"), "kiro")).IsFalse();
        await Assert.That(SkillsCommand.ConsumerPresent(Only("codex"), "gemini")).IsFalse();
        // The gemini tree is shared by Gemini CLI AND Antigravity.
        await Assert.That(SkillsCommand.ConsumerPresent(Only("antigravity"), "gemini")).IsTrue();
        await Assert.That(SkillsCommand.ConsumerPresent(Only("gemini"), "gemini")).IsTrue();
        // Claude and Kiro read only their own trees.
        await Assert.That(SkillsCommand.ConsumerPresent(Only("claude"), "claude")).IsTrue();
        await Assert.That(SkillsCommand.ConsumerPresent(Only("kiro"), "kiro")).IsTrue();
        await Assert.That(SkillsCommand.ConsumerPresent(Only("kiro"), "agents")).IsFalse();
    }

    [Test]
    public async Task Shared_trees_carry_no_vendor_and_vendored_trees_match_their_harness() {
        var targets = SkillsCommand.Targets(TestHarnessPaths.NoOverrides(Home)).ToDictionary(t => t.Key);
        await Assert.That(targets.Keys.Order().ToArray())
            .IsEquivalentTo(new[] { "agents", "claude", "gemini", "kiro" });

        await Assert.That(targets["claude"].Vendor).IsEqualTo("claude");
        await Assert.That(targets["kiro"].Vendor).IsEqualTo("kiro");
        // Several harnesses read these trees, so a vendor-restricted doc must never land in
        // them: no vendor ⇒ unknown-excludes drops every vendor-restricted doc server-side.
        await Assert.That(targets["agents"].Vendor).IsNull();
        await Assert.That(targets["gemini"].Vendor).IsNull();

        foreach (var t in targets.Values)
            await Assert.That(Path.GetFileName(t.Root)).IsEqualTo("skills");
    }
}
