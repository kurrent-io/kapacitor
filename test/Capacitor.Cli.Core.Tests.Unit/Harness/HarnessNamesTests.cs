using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

/// <summary>
/// The vendor ids are a compatibility constraint, not a display choice: they key the offer ledger on
/// disk, the inventory and first-run payloads on the wire, the CLI's own flags, and the token hashed
/// into a session-memory identity. Spelled out here so renaming an enum member cannot quietly move
/// any of them.
/// </summary>
public class HarnessNamesTests {
    [Test]
    [Arguments(HarnessId.Claude,      "claude")]
    [Arguments(HarnessId.Codex,       "codex")]
    [Arguments(HarnessId.Cursor,      "cursor")]
    [Arguments(HarnessId.Copilot,     "copilot")]
    [Arguments(HarnessId.Gemini,      "gemini")]
    [Arguments(HarnessId.Kiro,        "kiro")]
    [Arguments(HarnessId.Pi,          "pi")]
    [Arguments(HarnessId.OpenCode,    "opencode")]
    [Arguments(HarnessId.Antigravity, "antigravity")]
    public async Task A_harness_keeps_its_vendor_id(HarnessId id, string vendorId) {
        await Assert.That(id.VendorId).IsEqualTo(vendorId);
        await Assert.That(HarnessId.From(vendorId)).IsEqualTo(id);
        await Assert.That(id.Flag).IsEqualTo("--" + vendorId);
    }

    /// An id from outside this build — a newer server's payload, a typo — reads as unknown rather
    /// than as some vendor.
    [Test]
    [Arguments("Claude")]
    [Arguments("claude-code")]
    [Arguments("")]
    [Arguments(null)]
    public async Task An_id_this_build_does_not_know_reads_as_null(string? vendorId) {
        await Assert.That(HarnessId.From(vendorId) is null).IsTrue();
    }

    /// The bare `kcap plugin install` installs Claude, so Claude is the one harness that cannot gain
    /// a selector without changing what an existing invocation does.
    [Test]
    public async Task Only_claude_has_no_plugin_install_flag() {
        foreach (var harness in HarnessRegistry.Identities) {
            var flag = harness.Id.PluginInstallFlag;

            if (harness.Id is HarnessId.Claude) await Assert.That(flag is null).IsTrue();
            else                                await Assert.That(flag).IsEqualTo(harness.Id.Flag);
        }
    }
}
