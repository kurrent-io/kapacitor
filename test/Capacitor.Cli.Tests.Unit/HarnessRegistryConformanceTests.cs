using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit;

/// Pins Core's harness registry against the CLI's hand-maintained vendor-flag list, so adding a
/// tenth installable harness fails HERE rather than silently missing every nudge/status surface.
public class HarnessRegistryConformanceTests {
    [Test]
    public async Task Registry_flags_match_known_vendor_flags_exactly() {
        var registryFlags = HarnessRegistry.Identities.Select(h => h.Id.Flag).OrderBy(x => x).ToArray();
        var knownFlags    = VendorSelection.KnownVendorFlags.OrderBy(x => x).ToArray();
        await Assert.That(registryFlags).IsEquivalentTo(knownFlags);
    }

    [Test]
    public async Task Every_known_vendor_flag_names_a_harness() {
        foreach (var flag in VendorSelection.KnownVendorFlags)
            await Assert.That(HarnessId.From(flag.TrimStart('-')) is null).IsFalse();
    }

    // The browser-answer fold (SetupDecisions.WithBrowserAnswer) is the second hand-maintained vendor
    // list, and the one with a user-visible promise behind it: a harness in the catalogue but missing
    // from the fold is offered on the Agents screen, ticked, reported back as "Chosen in the browser",
    // and then installed for nobody.
    //
    // The property per vendor is DERIVED from its id ("Skip" + id, case-insensitively — which is what
    // matches SkipOpenCode to "opencode"), never mirrored in a list here. A mirror is just a second
    // copy to forget, and it would drift in step with the thing it is meant to pin.
    static CodingAgentsStep.Options Bare => new(
        SkipClaude: false, SkipCodex: false, SkipCursor: false, SkipCopilot: false, NoPrompt: false);

    static FirstRunAgentsAnswer Answer(HarnessId harness, bool record = true, bool tools = true) =>
        new([new FirstRunAgentsChoice(harness, record, tools)],
            new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero), Unrecognised: 0);

    static FirstRunAgentsAnswer Decline =>
        new([], new DateTimeOffset(2026, 8, 25, 9, 30, 0, TimeSpan.Zero), Unrecognised: 0);

    static bool? Skip(CodingAgentsStep.Options options, string propertyName) =>
        typeof(CodingAgentsStep.Options)
            .GetProperties()
            .FirstOrDefault(p => p.PropertyType == typeof(bool)
                              && string.Equals(p.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            ?.GetValue(options) as bool?;

    [Test]
    public async Task Every_harness_has_a_hooks_arm_in_the_browser_answer_fold() {
        foreach (var harness in HarnessRegistry.Identities) {
            var vendorId = harness.VendorId;
            var property = "Skip" + vendorId;

            // Declined, then chosen. The property must exist AND must move — an arm that is missing
            // reads as "always skipped", which is exactly the silent no-op this pins against.
            var declined = Skip(SetupDecisions.WithBrowserAnswer(Bare, Decline), property);
            var chosen   = Skip(SetupDecisions.WithBrowserAnswer(Bare, Answer(harness.Id)), property);

            await Assert.That(declined).IsNotNull().Because($"{property} must exist for {vendorId}");
            await Assert.That(declined!.Value).IsTrue().Because($"{property} must be skipped when {vendorId} is declined");
            await Assert.That(chosen!.Value).IsFalse().Because($"{property} must be cleared when {vendorId} is chosen");
        }
    }

    // Vendors whose install bundles tools with capture have no separate Mcp property; the ones that
    // separate them must have their tools arm wired too, or "tools" is accepted and dropped.
    [Test]
    public async Task Every_vendor_that_separates_tools_has_a_tools_arm_in_the_browser_answer_fold() {
        foreach (var harness in HarnessRegistry.Identities) {
            var vendorId = harness.VendorId;
            var property = "Skip" + vendorId + "Mcp";

            if (Skip(Bare, property) is null) continue;

            var off = Skip(SetupDecisions.WithBrowserAnswer(Bare, Answer(harness.Id, tools: false)), property);
            var on  = Skip(SetupDecisions.WithBrowserAnswer(Bare, Answer(harness.Id, tools: true)), property);

            await Assert.That(off!.Value).IsTrue().Because($"{property} must be skipped when {vendorId} tools are declined");
            await Assert.That(on!.Value).IsFalse().Because($"{property} must be cleared when {vendorId} tools are chosen");
        }
    }
}
