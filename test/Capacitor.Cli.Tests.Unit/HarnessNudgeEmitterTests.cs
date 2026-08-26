using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Tests.Unit;

public class HarnessNudgeEmitterTests {
    static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);
    static readonly AgentDetectionInputs Inputs = new(PathEnv: null, PathExt: null, IsWindows: false, Home: "/nonexistent");

    static AgentDetectionResult DetectionWithOnly(params string[] ids) {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        DetectedAgent A(string id) => new(BinaryFound: set.Contains(id), InstallSignalFound: false);
        return new(A("claude"), A("codex"), A("cursor"), A("copilot"), A("gemini"),
                   A("kiro"), A("pi"), A("opencode"), A("antigravity"));
    }

    static string? Fragment(TempConfigRoot config, AgentDetectionResult detected, Func<string, AgentDetectionInputs, bool> isWired, bool optedOut = false) =>
        HarnessNudgeEmitter.ResolveFragment(Inputs, new(config.Root), optedOut, Now, isWired, _ => detected);

    [Test]
    public async Task Fragment_names_detected_unwired_vendor_and_install_command() {
        using var config = new TempConfigRoot();
        var f = Fragment(config, DetectionWithOnly("antigravity"), (_, _) => false)!;
        await Assert.That(f).Contains("Antigravity");
        await Assert.That(f).Contains("kcap plugin install --antigravity");
        await Assert.That(f).Contains("kcap harness dismiss antigravity");
    }

    [Test]
    public async Task Fragment_null_when_opted_out() {
        using var config = new TempConfigRoot();
        await Assert.That(Fragment(config, DetectionWithOnly("antigravity"), (_, _) => false, optedOut: true)).IsNull();
    }

    [Test]
    public async Task Fragment_null_when_nothing_nudgeable() {
        using var config = new TempConfigRoot();
        await Assert.That(Fragment(config, DetectionWithOnly("antigravity"), (_, _) => true)).IsNull();
    }

    [Test]
    public async Task Fragment_folds_multiple_vendors() {
        using var config = new TempConfigRoot();
        var f = Fragment(config, DetectionWithOnly("gemini", "antigravity"), (_, _) => false)!;
        await Assert.That(f).Contains("Gemini");
        await Assert.That(f).Contains("Antigravity");
    }

    [Test]
    public async Task Second_call_is_throttled_to_null() {
        using var config = new TempConfigRoot();
        var       store  = new HarnessOfferStore(config.Root);
        var       first  = HarnessNudgeEmitter.ResolveFragment(Inputs, store, false, Now, (_, _) => false, _ => DetectionWithOnly("antigravity"));
        var       second = HarnessNudgeEmitter.ResolveFragment(Inputs, store, false, Now, (_, _) => false, _ => DetectionWithOnly("antigravity"));
        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task Resolving_stamps_last_offered() {
        using var config = new TempConfigRoot();
        var store = new HarnessOfferStore(config.Root);
        HarnessNudgeEmitter.ResolveFragment(Inputs, store, false, Now, (_, _) => false, _ => DetectionWithOnly("antigravity"));
        await Assert.That(store.Load().Entry("antigravity")!.LastOffered).IsEqualTo(Now);
    }

    [Test]
    public async Task Exception_in_detect_yields_null() {
        using var config = new TempConfigRoot();
        var result = HarnessNudgeEmitter.ResolveFragment(Inputs, (HarnessOfferStore)new(config.Root), false, Now,
            (_, _) => false, _ => throw new InvalidOperationException("boom"));
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Notice_has_kcap_prefix_and_stop_asking_hint() {
        using var config = new TempConfigRoot();
        var       n      = HarnessNudgeEmitter.ResolveNotice(Inputs, (HarnessOfferStore)new(config.Root), false, Now, (_, _) => false, _ => DetectionWithOnly("antigravity"))!;
        await Assert.That(n).Contains("kcap: Antigravity detected");
        await Assert.That(n).Contains("kcap harness dismiss antigravity");
    }

    [Test]
    public async Task Combine_joins_both_when_present_and_passes_through_when_one_null() {
        await Assert.That(HarnessNudgeEmitter.Combine("a", "b")).IsEqualTo("a\n\nb");
        await Assert.That(HarnessNudgeEmitter.Combine("a", null)).IsEqualTo("a");
        await Assert.That(HarnessNudgeEmitter.Combine(null, "b")).IsEqualTo("b");
        await Assert.That(HarnessNudgeEmitter.Combine(null, null)).IsNull();
    }
}
