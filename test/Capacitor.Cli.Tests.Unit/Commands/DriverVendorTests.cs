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
}
