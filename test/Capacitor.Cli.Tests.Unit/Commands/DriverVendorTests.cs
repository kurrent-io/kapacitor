using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class DriverVendorTests {
    static Func<string, string?> Env(params (string Key, string? Val)[] pairs)
        => k => pairs.FirstOrDefault(p => p.Key == k).Val;

    [Test]
    public async Task Claude_marker_alone_infers_claude()
        => await Assert.That(DriverVendor.Infer(Env(("CLAUDE_CODE_SESSION_ID", "s1")))).IsEqualTo("claude");

    [Test]
    public async Task Codex_marker_alone_infers_codex()
        => await Assert.That(DriverVendor.Infer(Env(("CODEX_THREAD_ID", "t1")))).IsEqualTo("codex");

    [Test]
    public async Task Both_markers_is_ambiguous_null() // nested harness — neither is provably the driver
        => await Assert.That(DriverVendor.Infer(Env(("CLAUDE_CODE_SESSION_ID", "s1"), ("CODEX_THREAD_ID", "t1")))).IsNull();

    [Test]
    public async Task No_markers_is_null()
        => await Assert.That(DriverVendor.Infer(Env())).IsNull();

    // ── the --driver stamp (the six JSON harnesses) ────────────────────────────────────────────

    [Test]
    [Arguments("cursor")]
    [Arguments("copilot")]
    [Arguments("gemini")]
    [Arguments("kiro")]
    [Arguments("opencode")]
    [Arguments("antigravity")]
    public async Task A_known_driver_stamp_is_used_verbatim(string vendor)
        => await Assert.That(DriverVendor.Infer(vendor, Env())).IsEqualTo(vendor);

    [Test]
    public async Task The_stamp_wins_over_a_conflicting_env_marker() // deterministic beats inherited
        => await Assert.That(DriverVendor.Infer("cursor", Env(("CLAUDE_CODE_SESSION_ID", "s1")))).IsEqualTo("cursor");

    [Test]
    public async Task An_unknown_stamp_is_ignored_and_falls_back_to_env()
        => await Assert.That(DriverVendor.Infer("totally-not-a-vendor", Env(("CODEX_THREAD_ID", "t1")))).IsEqualTo("codex");

    [Test]
    public async Task An_unknown_stamp_with_no_env_is_null() // never echo arbitrary text as driver_vendor
        => await Assert.That(DriverVendor.Infer("bogus", Env())).IsNull();

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments(null)]
    public async Task A_blank_stamp_falls_back_to_env(string? stamp)
        => await Assert.That(DriverVendor.Infer(stamp, Env(("CLAUDE_CODE_SESSION_ID", "s1")))).IsEqualTo("claude");
}
