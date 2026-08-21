using System.Diagnostics;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Guards the effective-provider-budget split added in #229: the probe (GitProviderRouter) and the
/// PR/MR detector share one ceiling, and the detector must run within the budget the probe LEFT
/// BEHIND — not the full cap. This is timing-dependent in production; an injected timestamp makes
/// it deterministic so a regression (handing the detector the full cap) is caught in CI.
/// </summary>
public class ProviderBudgetSplitTests {
    [Before(Test)]
    public void Reset() => GitProviderRouter.ResetMemoForTests();

    [Test]
    public async Task Detector_gets_the_budget_the_probe_left_behind() {
        var providerCap = TimeSpan.FromSeconds(2);

        // The probe "consumes" exactly half the cap: timestamp reads 0 at start, +1s afterwards.
        var calls = 0;
        long Timestamp() => calls++ == 0 ? 0L : Stopwatch.Frequency; // Frequency ticks == 1 second

        TimeSpan? detectorCap = null;
        CommandRunner run = (cmd, _, _, cap) => {
            if (cmd == "glab") { detectorCap = cap; return Task.FromResult<string?>("[]"); }
            return Task.FromResult<string?>("{}"); // gh auth status: custom host not a gh host → GitLab
        };

        // Custom host → the router probes (consuming the injected time); GitLab detector then runs.
        await RepositoryDetection.ResolveAndDetectPrAsync(
            "git.example.com", "owner", "repo", "main", "/cwd", providerCap, run, Timestamp);

        await Assert.That(detectorCap).IsNotNull();
        await Assert.That(detectorCap!.Value).IsLessThan(providerCap);                 // NOT the full cap
        await Assert.That(detectorCap!.Value.TotalMilliseconds).IsGreaterThan(500);    // ≈ 1s remainder
        await Assert.That(detectorCap!.Value.TotalMilliseconds).IsLessThan(1500);
    }

    [Test]
    public async Task No_detection_when_probe_exhausts_the_budget() {
        var providerCap = TimeSpan.FromSeconds(2);

        // Probe consumes the whole cap (0 → 2s) → no budget left → detector must not run.
        var calls = 0;
        long Timestamp() => calls++ == 0 ? 0L : Stopwatch.Frequency * 2;

        var detectorRan = false;
        CommandRunner run = (cmd, _, _, _) => {
            if (cmd == "glab") detectorRan = true;
            return Task.FromResult<string?>(cmd == "glab" ? "[]" : "{}");
        };

        var pr = await RepositoryDetection.ResolveAndDetectPrAsync(
            "git.example.com", "owner", "repo", "main", "/cwd", providerCap, run, Timestamp);

        await Assert.That(detectorRan).IsFalse();
        await Assert.That(pr).IsNull();
    }

    [Test]
    public async Task Non_monotonic_timestamp_never_inflates_the_detector_budget() {
        var providerCap = TimeSpan.FromSeconds(2);

        // A misbehaving timestamp seam that goes BACKWARDS (end < start) would make GetElapsedTime
        // negative and, unclamped, push detectCap above providerCap. The clamp must keep the
        // detector budget within the shared ceiling.
        var calls = 0;
        long Timestamp() => calls++ == 0 ? Stopwatch.Frequency : 0L; // start high, end low → negative elapsed

        TimeSpan? detectorCap = null;
        CommandRunner run = (cmd, _, _, cap) => {
            if (cmd == "glab") { detectorCap = cap; return Task.FromResult<string?>("[]"); }
            return Task.FromResult<string?>("{}");
        };

        await RepositoryDetection.ResolveAndDetectPrAsync(
            "git.example.com", "owner", "repo", "main", "/cwd", providerCap, run, Timestamp);

        await Assert.That(detectorCap).IsNotNull();
        await Assert.That(detectorCap!.Value).IsLessThanOrEqualTo(providerCap); // never exceeds the ceiling
    }
}
