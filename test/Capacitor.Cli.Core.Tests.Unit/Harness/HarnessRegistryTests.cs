using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Gemini;

namespace Capacitor.Cli.Core.Tests.Unit.Harness;

/// <summary>
/// The registry is the one place shared code names a vendor, so these pin what a reader of a
/// single entry cannot check for itself: that every harness is present exactly once, and that each
/// reports its own identity rather than a neighbour's.
/// </summary>
public class HarnessRegistryTests {
    [TempHome] public required TempHome Home { get; init; }

    // Bare: FromEnvironment reads every vendor override variable.
    [Test, NotInParallel]
    public async Task Every_harness_is_registered_exactly_once() {
        var ids = HarnessRegistry.FromEnvironment(Home).Select(h => h.Id).ToList();

        // Both directions: a missing vendor is a harness nothing can reach, and a duplicate is the
        // copy-paste this shape invites — a module declaring IHarness<SomeOtherVendor> compiles and
        // then reports that vendor's identity.
        await Assert.That(ids).IsEquivalentTo(Enum.GetValues<HarnessId>());
        await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Count);
    }

    [Test, NotInParallel]
    public async Task Every_harness_carries_a_label() {
        var labels = HarnessRegistry.FromEnvironment(Home).Select(h => h.Label).ToList();

        await Assert.That(labels.Any(string.IsNullOrWhiteSpace)).IsFalse();
        await Assert.That(labels.Distinct().Count()).IsEqualTo(labels.Count);
    }

    /// <summary>Antigravity's layout hangs off Gemini's root, and the registry composes it from the
    /// same instance — the reason its module has no <c>FromEnvironment</c> of its own.</summary>
    [Test]
    public async Task Antigravity_is_composed_over_the_gemini_instance() {
        var gemini = GeminiHarness.Over(new GeminiPaths(Home, geminiCliHome: null));

        var antigravity = AntigravityHarness.Over(gemini);

        await Assert.That(antigravity.Paths.McpConfigJson).StartsWith(gemini.Paths.Root);
    }
}
