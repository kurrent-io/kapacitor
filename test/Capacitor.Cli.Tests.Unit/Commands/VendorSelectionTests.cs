using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class VendorSelectionTests {
    [Test]
    public async Task no_vendor_flags_means_all_detected() {
        var r = VendorSelection.Parse([]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task claude_flag_selects_only_claude() {
        var r = VendorSelection.Parse(["--claude"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("claude")).IsTrue();
    }

    [Test]
    public async Task codex_flag_selects_only_codex() {
        var r = VendorSelection.Parse(["--codex"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("codex")).IsTrue();
    }

    [Test]
    public async Task cursor_flag_selects_only_cursor() {
        var r = VendorSelection.Parse(["--cursor"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("cursor")).IsTrue();
    }

    [Test]
    public async Task kiro_flag_selects_only_kiro() {
        var r = VendorSelection.Parse(["--kiro"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("kiro")).IsTrue();
    }

    [Test]
    public async Task multiple_vendor_flags_are_additive() {
        var r = VendorSelection.Parse(["--claude", "--codex"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(2);
        await Assert.That(r.Vendors.Contains("claude")).IsTrue();
        await Assert.That(r.Vendors.Contains("codex")).IsTrue();
    }

    [Test]
    public async Task pi_flag_selects_only_pi() {
        var r = VendorSelection.Parse(["--pi"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(1);
        await Assert.That(r.Vendors.Contains("pi")).IsTrue();
    }

    [Test]
    public async Task parses_opencode_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--opencode" });
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors).Contains("opencode");
    }

    [Test]
    public async Task rejects_opencode_prefixed_unknown_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--opencode-foo" });
        await Assert.That(r.HasError).IsTrue();
    }

    [Test]
    public async Task parses_dsh_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--dsh" });
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors).Contains("dsh");
    }

    [Test]
    public async Task rejects_dsh_prefixed_unknown_flag() {
        var r = VendorSelection.Parse(new[] { "import", "--dsh-foo" });
        await Assert.That(r.HasError).IsTrue();
    }

    [Test]
    public async Task unknown_pi_prefix_flag_is_rejected() {
        // --pi is a known vendor flag, but --pi-<x> typos / future options must be
        // rejected as unknown source options, not silently ignored.
        var r = VendorSelection.Parse(["--pi-session", "abc"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--pi-session");
    }

    [Test]
    public async Task legacy_cursor_workspace_flag_is_rejected() {
        // cursor-workspace and --cursor-all-workspaces were dropped in
        // when the SQLite import path was retired. The JSONL walker doesn't need
        // workspace filtering, so these flags now surface as unknown source options.
        var r = VendorSelection.Parse(["--cursor-workspace", "/some/path"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--cursor-workspace");
    }

    [Test]
    public async Task legacy_cursor_all_workspaces_flag_is_rejected() {
        var r = VendorSelection.Parse(["--cursor-all-workspaces"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--cursor-all-workspaces");
    }

    [Test]
    public async Task unknown_cursor_prefix_flag_is_rejected() {
        var r = VendorSelection.Parse(["--cursor-worskpace", "/p"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--cursor-worskpace");
    }

    [Test]
    public async Task unknown_claude_prefix_flag_is_rejected() {
        var r = VendorSelection.Parse(["--claude-something"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--claude-something");
    }

    [Test]
    public async Task unknown_codex_prefix_flag_is_rejected() {
        var r = VendorSelection.Parse(["--codex-bogus"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--codex-bogus");
    }

    [Test]
    public async Task vendor_typo_one_edit_away_is_rejected() {
        var r = VendorSelection.Parse(["--curser"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--curser");
        await Assert.That(r.Error!).Contains("--cursor");
    }

    [Test]
    public async Task vendor_typo_two_edits_away_is_rejected() {
        var r = VendorSelection.Parse(["--codx"]);
        await Assert.That(r.HasError).IsTrue();
        await Assert.That(r.Error!).Contains("--codx");
        await Assert.That(r.Error!).Contains("--codex");
    }

    [Test]
    public async Task arbitrary_unrelated_flag_passes_through() {
        var r = VendorSelection.Parse(["--server-url", "http://example.com", "--no-update-check"]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Vendors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task global_flags_pass_through() {
        var r = VendorSelection.Parse([
            "--all",
            "--org",
            "--repo", "x/y",
            "--cwd", "/p",
            "--since", "2026-01-01",
            "--session", "abc",
            "--min-lines", "20",
            "--private",
            "--yes",
            "-y",
            "--generate-summaries",
            "--skip-title",
            "--reimport",
            "--server-url", "http://x",
            "--no-update-check",
            "--profile", "p"
        ]);
        await Assert.That(r.HasError).IsFalse();
        await Assert.That(r.Error).IsNull();
        await Assert.That(r.Vendors.Count).IsEqualTo(0);
    }
}
