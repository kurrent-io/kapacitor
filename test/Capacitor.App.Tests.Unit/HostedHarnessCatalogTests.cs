using Capacitor.App.Services;
using Capacitor.Cli.Core.Harness;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class HostedHarnessCatalogTests {
    [Test]
    public async Task Advertised_vendors_are_available_and_others_are_not() {
        var options = HostedHarnessCatalog.Build(["claude", "cursor"]);

        await Assert.That(options.Single(o => o.Vendor == "claude").Available).IsTrue();
        await Assert.That(options.Single(o => o.Vendor == "cursor").Available).IsTrue();
        await Assert.That(options.Single(o => o.Vendor == "pi").Available).IsFalse();
    }

    [Test]
    public async Task Unknown_vendor_set_leaves_everything_available() {
        var options = HostedHarnessCatalog.Build(null);
        await Assert.That(options.All(o => o.Available)).IsTrue();
    }

    [Test]
    public async Task Transport_family_matches_how_the_daemon_hosts_each_vendor() {
        var options = HostedHarnessCatalog.Build(null).ToDictionary(o => o.Vendor);

        await Assert.That(options["claude"].TransportFamily).IsEqualTo("pty");
        await Assert.That(options["codex"].TransportFamily).IsEqualTo("pty");
        await Assert.That(options["cursor"].TransportFamily).IsEqualTo("acp");
        await Assert.That(options["opencode"].TransportFamily).IsEqualTo("acp");
        await Assert.That(options["antigravity"].TransportFamily).IsEqualTo("rpc");
        await Assert.That(options["pi"].TransportFamily).IsEqualTo("rpc");
    }

    /// The vendor list comes from Core, the transport map does not: a tenth vendor added there
    /// would fall through to "rpc" and be labelled "chat" in the picker with nobody the wiser.
    /// This turns that into a red suite on the PR that adds it. The runtime fallback stays — a
    /// vendor only the DAEMON knows about must still be listed, not dropped.
    [Test]
    public async Task Every_harness_has_an_explicit_transport_family() {
        var mapped = new HashSet<string>(HostedHarnessCatalog.MappedVendors, StringComparer.OrdinalIgnoreCase);
        var unmapped = HarnessRegistry.Identities.Select(h => h.VendorId).Where(v => !mapped.Contains(v)).ToList();

        await Assert.That(unmapped).IsEmpty();
    }

    [Test]
    public async Task An_unknown_advertised_vendor_is_listed_rather_than_dropped() {
        var options = HostedHarnessCatalog.Build(["claude", "brandnew"]);

        var added = options.Single(o => o.Vendor == "brandnew");
        await Assert.That(added.Available).IsTrue();
        await Assert.That(added.Label).IsEqualTo("brandnew");
    }

    [Test]
    public async Task LabelFor_resolves_a_vendor_token_case_insensitively_and_falls_back_to_the_token() {
        var options = HostedHarnessCatalog.Build(null);

        await Assert.That(HostedHarnessCatalog.LabelFor(options, "CLAUDE"))
            .IsEqualTo(options.Single(o => o.Vendor == "claude").Label);
        await Assert.That(HostedHarnessCatalog.LabelFor(options, "neverheardof")).IsEqualTo("neverheardof");
    }

    [Test]
    public async Task Description_names_the_transport_and_the_surface() {
        var pty = HostedHarnessCatalog.Build(null).Single(o => o.Vendor == "claude");
        var acp = HostedHarnessCatalog.Build(null).Single(o => o.Vendor == "gemini");

        await Assert.That(HostedHarnessCatalog.DescriptionFor(pty)).IsEqualTo("PTY · terminal + chat");
        await Assert.That(HostedHarnessCatalog.DescriptionFor(acp)).IsEqualTo("ACP · chat");
    }

    [Test]
    public async Task FamilyFor_maps_vendors_and_defaults_unknown_to_rpc() {
        await Assert.That(HostedHarnessCatalog.FamilyFor("CLAUDE")).IsEqualTo("pty");
        await Assert.That(HostedHarnessCatalog.FamilyFor("gemini")).IsEqualTo("acp");
        await Assert.That(HostedHarnessCatalog.FamilyFor("neverheardof")).IsEqualTo("rpc");
    }

    [Test]
    public async Task ShowsTerminal_prefers_the_authoritative_flag_and_falls_back_to_family() {
        await Assert.That(HostedHarnessCatalog.ShowsTerminal(true, "gemini")).IsTrue();
        await Assert.That(HostedHarnessCatalog.ShowsTerminal(false, "claude")).IsFalse();
        await Assert.That(HostedHarnessCatalog.ShowsTerminal(null, "claude")).IsTrue();
        await Assert.That(HostedHarnessCatalog.ShowsTerminal(null, "gemini")).IsFalse();
    }

    [Test]
    public async Task EffectiveFamily_overrides_only_a_conflicting_pty_guess() {
        // codex app-server: vendor map says pty, daemon says no terminal → generic chat family.
        await Assert.That(HostedHarnessCatalog.EffectiveFamily(false, "codex")).IsEqualTo("rpc");
        // an already-non-PTY family is preserved, not flattened:
        await Assert.That(HostedHarnessCatalog.EffectiveFamily(false, "gemini")).IsEqualTo("acp");
        await Assert.That(HostedHarnessCatalog.EffectiveFamily(null, "claude")).IsEqualTo("pty");
        await Assert.That(HostedHarnessCatalog.EffectiveFamily(true, "claude")).IsEqualTo("pty");
    }

    [Test]
    public async Task Model_choices_are_curated_for_the_pty_vendors_and_empty_elsewhere() {
        await Assert.That(HostedHarnessCatalog.ModelChoicesFor("claude").Count).IsGreaterThan(0);
        await Assert.That(HostedHarnessCatalog.ModelChoicesFor("codex").Count).IsGreaterThan(0);
        await Assert.That(HostedHarnessCatalog.ModelChoicesFor("gemini")).IsEmpty();
        await Assert.That(HostedHarnessCatalog.ModelChoicesFor("no-such-vendor")).IsEmpty();
    }

    [Test]
    public async Task Every_mapped_vendor_has_a_tile_and_unknown_tokens_get_an_initial() {
        // Same guard shape as the TransportFamilies pin: adding a vendor means one catalog file.
        foreach (var vendor in HostedHarnessCatalog.MappedVendors) {
            var (glyph, color) = HostedHarnessCatalog.TileFor(vendor);
            await Assert.That(glyph).IsNotEmpty();
            await Assert.That(color).StartsWith("#");
        }
        await Assert.That(HostedHarnessCatalog.TileFor("zed").Glyph).IsEqualTo("Z");
    }

    [Test]
    public async Task Model_label_prefers_the_curated_name_and_falls_back_to_the_slug() {
        await Assert.That(HostedHarnessCatalog.ModelLabelFor("claude", "claude-fable-5")).IsEqualTo("Claude Fable 5");
        await Assert.That(HostedHarnessCatalog.ModelLabelFor("claude", "CLAUDE-FABLE-5")).IsEqualTo("Claude Fable 5");
        await Assert.That(HostedHarnessCatalog.ModelLabelFor("claude", "some-future-id")).IsEqualTo("some-future-id");
        await Assert.That(HostedHarnessCatalog.ModelLabelFor("claude", "")).IsEqualTo("Default");
        await Assert.That(HostedHarnessCatalog.ModelLabelFor("gemini", "  ")).IsEqualTo("Default");
    }

    [Test]
    public async Task Effort_labels_are_sentence_case_and_default_is_not_auto() {
        await Assert.That(HostedHarnessCatalog.EffortLabelFor(null)).IsEqualTo("Default");
        await Assert.That(HostedHarnessCatalog.EffortLabelFor("")).IsEqualTo("Default");
        await Assert.That(HostedHarnessCatalog.EffortLabelFor("low")).IsEqualTo("Low");
        await Assert.That(HostedHarnessCatalog.EffortLabelFor("medium")).IsEqualTo("Medium");
        await Assert.That(HostedHarnessCatalog.EffortLabelFor("high")).IsEqualTo("High");
        await Assert.That(HostedHarnessCatalog.EffortLabelFor("xhigh")).IsEqualTo("Max");
        await Assert.That(HostedHarnessCatalog.EffortLabelFor("weird")).IsEqualTo("weird");
        await Assert.That(HostedHarnessCatalog.EffortLadder)
            .IsEquivalentTo(["low", "medium", "high", "xhigh"], CollectionOrdering.Matching);
    }

    /// The tokens are the Claude CLI's own `--permission-mode` choices, listed from most to least
    /// prompting; the daemon forwards them verbatim, so a spelling here is a spelling on argv.
    [Test]
    public async Task Permission_modes_are_the_claude_cli_tokens_in_escalation_order() {
        string[] expected = ["manual", "acceptEdits", "auto", "bypassPermissions"];

        await Assert.That(HostedHarnessCatalog.PermissionModes.Select(m => m.Token))
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Permission_mode_labels_are_friendly_and_unknown_tokens_pass_through() {
        await Assert.That(HostedHarnessCatalog.PermissionModeLabelFor("manual")).IsEqualTo("Manual");
        await Assert.That(HostedHarnessCatalog.PermissionModeLabelFor("acceptEdits")).IsEqualTo("Accept edits");
        await Assert.That(HostedHarnessCatalog.PermissionModeLabelFor("auto")).IsEqualTo("Auto");
        await Assert.That(HostedHarnessCatalog.PermissionModeLabelFor("bypassPermissions")).IsEqualTo("Bypass permissions");
        await Assert.That(HostedHarnessCatalog.PermissionModeLabelFor("weird")).IsEqualTo("weird");
    }

    [Test]
    public async Task Only_claude_takes_a_launch_time_permission_mode() {
        await Assert.That(HostedHarnessCatalog.SupportsPermissionMode("claude")).IsTrue();
        await Assert.That(HostedHarnessCatalog.SupportsPermissionMode("Claude")).IsTrue();
        await Assert.That(HostedHarnessCatalog.SupportsPermissionMode("codex")).IsFalse();
        await Assert.That(HostedHarnessCatalog.SupportsPermissionMode("cursor")).IsFalse();
    }
}
