using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

public class HarnessCatalogTests {
    [Test]
    public async Task Covers_all_nine_vendors_with_unique_ids() {
        await Assert.That(HarnessCatalog.All.Count).IsEqualTo(9);
        await Assert.That(HarnessCatalog.All.Select(h => h.VendorId).Distinct().Count()).IsEqualTo(9);
    }

    [Test]
    public async Task Install_flag_is_dash_dash_vendor_id_except_flagless_claude() {
        foreach (var h in HarnessCatalog.All) {
            if (h.VendorId == "claude") {
                await Assert.That(h.InstallFlag).IsNull();
            } else {
                await Assert.That(h.InstallFlag).IsEqualTo("--" + h.VendorId);
            }
        }
    }

    // The AgentDetectionResult has one DetectedAgent field per vendor; each catalog Select must map
    // to exactly one distinct field, so a result with a single field "detected" is selected by
    // exactly one catalog entry — and it is the entry we expect.
    [Test]
    [Arguments("claude")]
    [Arguments("codex")]
    [Arguments("cursor")]
    [Arguments("copilot")]
    [Arguments("gemini")]
    [Arguments("kiro")]
    [Arguments("pi")]
    [Arguments("opencode")]
    [Arguments("antigravity")]
    public async Task Each_selector_maps_to_exactly_one_distinct_detection_field(string vendorId) {
        var result = DetectionWithOnly(vendorId);
        var matches = HarnessCatalog.All.Where(h => h.Select(result).Detected).ToList();

        await Assert.That(matches.Count).IsEqualTo(1);
        await Assert.That(matches[0].VendorId).IsEqualTo(vendorId);
    }

    internal static AgentDetectionResult DetectionWithOnly(params string[] detectedVendorIds) {
        var set = new HashSet<string>(detectedVendorIds, StringComparer.Ordinal);
        DetectedAgent A(string id) => new(BinaryFound: set.Contains(id), InstallSignalFound: false);
        return new(
            Claude: A("claude"), Codex: A("codex"), Cursor: A("cursor"), Copilot: A("copilot"),
            Gemini: A("gemini"), Kiro: A("kiro"), Pi: A("pi"), OpenCode: A("opencode"),
            Antigravity: A("antigravity"));
    }
}
