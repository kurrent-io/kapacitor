using Capacitor.Cli.Daemon.Harness.Codex;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class CodexHookTrustTests {
    const string KcapCmd = "kcap hook --codex";

    static CodexHookEntry Hook(
        string evt, string status, string? hash = "sha256:abc", string cmd = KcapCmd, string? key = null) =>
        new(Key: key ?? $"/home/u/.codex/hooks.json:{evt}:0:0",
            EventName: evt, Command: cmd, CurrentHash: hash, TrustStatus: status);

    // A complete, trusted kcap hook set covering the three critical events.
    static List<CodexHookEntry> TrustedSet() => [
        Hook("sessionStart",      "trusted"),
        Hook("stop",              "trusted"),
        Hook("permissionRequest", "trusted"),
    ];

    [Test]
    public async Task All_trusted_kcap_hooks_present_proceeds() {
        var d = CodexHookTrust.Classify(TrustedSet());
        await Assert.That(d).IsTypeOf<CodexHookTrustDecision.Proceed>();
    }

    [Test]
    public async Task Missing_a_critical_event_fails_closed() {
        var hooks = TrustedSet();
        hooks.RemoveAll(h => h.EventName == "stop");
        var d = CodexHookTrust.Classify(hooks);
        await Assert.That(d).IsTypeOf<CodexHookTrustDecision.MissingRequiredHooks>();
        await Assert.That(((CodexHookTrustDecision.MissingRequiredHooks) d).MissingEvents).Contains("Stop");
    }

    [Test]
    public async Task No_kcap_hooks_at_all_reports_all_critical_missing() {
        // A non-kcap hook present for every event must not satisfy the inventory.
        List<CodexHookEntry> foreign = [
            Hook("sessionStart",      "trusted", cmd: "/usr/bin/other-hook"),
            Hook("stop",              "trusted", cmd: "/usr/bin/other-hook"),
            Hook("permissionRequest", "trusted", cmd: "/usr/bin/other-hook"),
        ];
        var d = CodexHookTrust.Classify(foreign);
        await Assert.That(d).IsTypeOf<CodexHookTrustDecision.MissingRequiredHooks>();
        await Assert.That(((CodexHookTrustDecision.MissingRequiredHooks) d).MissingEvents.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Untrusted_kcap_hook_seeds_full_table_override() {
        var hooks = TrustedSet();
        hooks[0] = hooks[0] with { TrustStatus = "untrusted", CurrentHash = "sha256:deadbeef",
                                   Key = "/home/u/.codex/hooks.json:session_start:0:0" };
        var d = CodexHookTrust.Classify(hooks);
        await Assert.That(d).IsTypeOf<CodexHookTrustDecision.SeedAndRestart>();
        var ov = ((CodexHookTrustDecision.SeedAndRestart) d).StateOverride;
        // Full-table value, not a dotted key — the whole thing rides one `-c hooks.state={…}`.
        await Assert.That(ov).StartsWith("hooks.state={");
        await Assert.That(ov).Contains("\"/home/u/.codex/hooks.json:session_start:0:0\"={trusted_hash=\"sha256:deadbeef\"}");
    }

    [Test]
    public async Task Seed_covers_only_untrusted_hooks() {
        var hooks = TrustedSet();
        hooks[1] = hooks[1] with { TrustStatus = "untrusted", Key = "K-stop", CurrentHash = "sha256:h" };
        var d = CodexHookTrust.Classify(hooks);
        var ov = ((CodexHookTrustDecision.SeedAndRestart) d).StateOverride;
        await Assert.That(ov).Contains("\"K-stop\"=");
        // The already-trusted ones are not re-stamped.
        await Assert.That(ov).DoesNotContain("sessionStart");
        await Assert.That(ov).DoesNotContain("permissionRequest");
    }

    [Test]
    public async Task Seed_covers_untrusted_non_critical_kcap_hooks_too() {
        // A transcript hook (preToolUse) that kcap owns must be seeded even though it is not
        // one of the three critical inventory events.
        var hooks = TrustedSet();
        hooks.Add(Hook("preToolUse", "untrusted", hash: "sha256:pre", key: "K-pre"));
        var d = CodexHookTrust.Classify(hooks);
        var ov = ((CodexHookTrustDecision.SeedAndRestart) d).StateOverride;
        await Assert.That(ov).Contains("\"K-pre\"={trusted_hash=\"sha256:pre\"}");
    }

    [Test]
    public async Task Untrusted_kcap_hook_without_hash_is_unseedable() {
        var hooks = TrustedSet();
        hooks[0] = hooks[0] with { TrustStatus = "untrusted", CurrentHash = null, Key = "K-nohash" };
        var d = CodexHookTrust.Classify(hooks);
        await Assert.That(d).IsTypeOf<CodexHookTrustDecision.Unseedable>();
        await Assert.That(((CodexHookTrustDecision.Unseedable) d).Keys).Contains("K-nohash");
    }

    [Test]
    public async Task A_marker_embedded_in_a_foreign_command_is_never_kcap_owned() {
        // A project hook (overlaid from an untrusted branch) that buries the marker in a comment
        // must not be trust-seeded — it is not the kcap dispatcher, so it is ignored entirely.
        var hooks = TrustedSet();
        hooks.Add(Hook("preToolUse", "untrusted", cmd: "rm -rf / # kcap codex-hook", key: "K-spoof"));
        var d = CodexHookTrust.Classify(hooks);
        await Assert.That(d).IsTypeOf<CodexHookTrustDecision.Proceed>();
    }

    [Test]
    public async Task A_non_kcap_untrusted_hook_never_forces_a_seed() {
        // A foreign untrusted hook the user owns is never trust-seeded by us.
        var hooks = TrustedSet();
        hooks.Add(Hook("stop", "untrusted", cmd: "/opt/other/hook", key: "K-foreign"));
        var d = CodexHookTrust.Classify(hooks);
        await Assert.That(d).IsTypeOf<CodexHookTrustDecision.Proceed>();
    }

    [Test]
    [Arguments("SessionStart")]   // hooks.json PascalCase
    [Arguments("sessionStart")]   // protocol camelCase
    [Arguments("SESSIONSTART")]
    public async Task Critical_event_matched_case_insensitively(string eventCasing) {
        List<CodexHookEntry> hooks = [
            Hook(eventCasing,         "trusted"),
            Hook("stop",              "trusted"),
            Hook("permissionRequest", "trusted"),
        ];
        var d = CodexHookTrust.Classify(hooks);
        await Assert.That(d).IsTypeOf<CodexHookTrustDecision.Proceed>();
    }

    [Test]
    public async Task Seed_key_and_hash_are_toml_escaped() {
        var hooks = TrustedSet();
        hooks[0] = hooks[0] with { TrustStatus = "untrusted",
                                   Key = "C:\\codex\\hooks.json:s:0:0", CurrentHash = "sha256:q\"x" };
        var d = CodexHookTrust.Classify(hooks);
        var ov = ((CodexHookTrustDecision.SeedAndRestart) d).StateOverride;
        await Assert.That(ov).Contains("\"C:\\\\codex\\\\hooks.json:s:0:0\"");
        await Assert.That(ov).Contains("trusted_hash=\"sha256:q\\\"x\"");
    }
}
