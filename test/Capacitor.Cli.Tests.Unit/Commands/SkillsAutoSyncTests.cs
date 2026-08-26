using System.Diagnostics;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Mutates the process-wide <see cref="SkillsAutoSync.ProcessStarterForTesting"/> seam,
/// so the class runs alone.</summary>
[NotInParallel]
public class SkillsAutoSyncTests {
    [Test]
    public async Task Throttle_skips_within_the_interval_and_runs_past_or_outside_it() {
        var now = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        static SkillsManifest M(DateTimeOffset t) => new() { SyncedAt = t };

        await Assert.That(SkillsCommand.AutoThrottled(M(now.AddHours(-1)), now)).IsTrue();
        await Assert.That(SkillsCommand.AutoThrottled(M(now.AddHours(-7)), now)).IsFalse();
        await Assert.That(SkillsCommand.AutoThrottled(null, now)).IsFalse();                    // no ledger ⇒ sync
        await Assert.That(SkillsCommand.AutoThrottled(new() { SyncedAt = null }, now)).IsFalse();
        // A future stamp (clock correction, tampered file) is stale, never an unbounded suppression.
        await Assert.That(SkillsCommand.AutoThrottled(M(now.AddHours(2)), now)).IsFalse();
    }

    [Test]
    public async Task Spawn_is_detached_quiet_and_never_throws() {
        ProcessStartInfo? seen = null;
        SkillsAutoSync.ProcessStarterForTesting = psi => { seen = psi; return null; };
        try {
            SkillsAutoSync.SpawnDetached("/some/repo");
            await Assert.That(seen).IsNotNull();
            await Assert.That(seen!.ArgumentList.ToArray()).IsEquivalentTo(new[] { "skills", "sync", "--auto" });
            await Assert.That(seen.WorkingDirectory).IsEqualTo("/some/repo");
            await Assert.That(seen.RedirectStandardOutput).IsTrue();   // the hook's stdout is a data channel

            SkillsAutoSync.ProcessStarterForTesting = _ => throw new InvalidOperationException("boom");
            SkillsAutoSync.SpawnDetached("/some/repo");                // swallowed — never breaks a hook
        } finally {
            SkillsAutoSync.ProcessStarterForTesting = null;
        }
    }
}

public class SkillsTargetCatalogTests {
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
        var targets = SkillsCommand.Targets().ToDictionary(t => t.Key);
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
