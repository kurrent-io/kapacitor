using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

internal class LaunchConsentEngineTests {
    static LaunchConsentInput Input(
        string? requester = "user_abc", bool owner = false, string kind = "agent",
        string repo = "/Users/me/dev/proj", string vendor = "claude", string? requesterDisplay = null)
        => new(requester, owner, kind, repo, vendor, requesterDisplay);

    static LaunchConsentPolicy Policy(
        LaunchConsentDefault def = LaunchConsentDefault.Allow, params LaunchConsentRule[] rules)
        => new(def, 45, rules);

    [Test]
    public async Task Owner_is_always_allowed_even_with_matching_deny_rule() {
        var policy = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("deny", null, null, null, null));
        var d = LaunchConsentEngine.Evaluate(policy, Input(owner: true));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Allow);
        await Assert.That(d.Source).IsEqualTo("owner");
    }

    [Test]
    public async Task First_matching_rule_wins_in_file_order() {
        var policy = Policy(LaunchConsentDefault.Prompt,
            new LaunchConsentRule("deny", null, "review-flow", null, null),
            new LaunchConsentRule("allow", null, null, null, null));
        var d = LaunchConsentEngine.Evaluate(policy, Input(kind: "review-flow"));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Deny);
        await Assert.That(d.Source).IsEqualTo("rule[0]");
    }

    [Test]
    public async Task Null_rule_fields_are_wildcards() {
        var policy = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, null, null));
        var d = LaunchConsentEngine.Evaluate(policy, Input());
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Allow);
    }

    [Test]
    public async Task Requester_specific_rule_does_not_match_null_requester() {
        var policy = Policy(LaunchConsentDefault.Prompt,
            new LaunchConsentRule("allow", "user_abc", null, null, null));
        var d = LaunchConsentEngine.Evaluate(policy, Input(requester: null));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Prompt);
        await Assert.That(d.Source).IsEqualTo("default");
    }

    [Test]
    public async Task Vendor_match_is_case_insensitive() {
        var policy = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, null, "Claude"));
        var d = LaunchConsentEngine.Evaluate(policy, Input(vendor: "claude"));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Allow);
    }

    [Test]
    public async Task Repo_prefix_glob_matches_subpaths_exact_matches_only_itself() {
        var glob = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, "/Users/me/dev/*", null));
        await Assert.That(LaunchConsentEngine.Evaluate(glob, Input(repo: "/Users/me/dev/proj")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Allow);
        await Assert.That(LaunchConsentEngine.Evaluate(glob, Input(repo: "/Users/me/other")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Deny);
        // Glob pattern also matches the bare directory itself
        await Assert.That(LaunchConsentEngine.Evaluate(glob, Input(repo: "/Users/me/dev")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Allow);

        var exact = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, "/Users/me/dev/proj", null));
        await Assert.That(LaunchConsentEngine.Evaluate(exact, Input(repo: "/Users/me/dev/proj/")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Allow);
        await Assert.That(LaunchConsentEngine.Evaluate(exact, Input(repo: "/Users/me/dev/proj2")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Deny);
    }

    [Test]
    public async Task Repo_pattern_matches_across_path_separators() {
        // A Windows-ish pattern/path pair: the pattern is authored with forward slashes (as an
        // operator would write it cross-platform), the incoming repo path uses backslashes (as
        // .NET reports a canonical Windows path) — both the glob and exact arms must normalize
        // before comparing, mirroring DaemonConfig.IsRepoAllowed.
        var glob = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, "C:/work/*", null));
        await Assert.That(LaunchConsentEngine.Evaluate(glob, Input(repo: "C:\\work\\proj")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Allow);

        var exact = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, "C:/work/proj", null));
        await Assert.That(LaunchConsentEngine.Evaluate(exact, Input(repo: "C:\\work\\proj")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Allow);
    }

    [Test]
    public async Task Repo_glob_does_not_match_a_sibling_directory_with_a_shared_prefix() {
        var policy = Policy(LaunchConsentDefault.Deny,
            new LaunchConsentRule("allow", null, null, "/src/*", null));
        await Assert.That(LaunchConsentEngine.Evaluate(policy, Input(repo: "/src2/x")).Verdict)
            .IsEqualTo(LaunchConsentVerdict.Deny);
    }

    [Test]
    [Arguments(LaunchConsentDefault.Allow, LaunchConsentVerdict.Allow)]
    [Arguments(LaunchConsentDefault.Deny, LaunchConsentVerdict.Deny)]
    [Arguments(LaunchConsentDefault.Prompt, LaunchConsentVerdict.Prompt)]
    public async Task Unmatched_falls_through_to_default(LaunchConsentDefault def, LaunchConsentVerdict expected) {
        var d = LaunchConsentEngine.Evaluate(Policy(def), Input());
        await Assert.That(d.Verdict).IsEqualTo(expected);
        await Assert.That(d.Source).IsEqualTo("default");
    }

    [Test] // tray pause rule (design spec §6): the wildcard deny it inserts at rules[0] must
           // still let an owner-originated launch through — the engine's owner exemption
           // precedes rules and default alike.
    public async Task Pause_rule_at_index_zero_still_allows_the_owner() {
        var policy = new LaunchConsentPolicy(LaunchConsentDefault.Allow, 45,
            [new LaunchConsentRule("deny", null, null, null, null)]);
        var d = LaunchConsentEngine.Evaluate(policy, Input(owner: true));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Allow);
        await Assert.That(d.Source).IsEqualTo("owner");
    }

    [Test] // first-match-wins pins the "Allow & remember shadowed by pause" contract (spec §4.1).
    public async Task Earlier_wildcard_deny_shadows_a_later_appended_allow() {
        var policy = new LaunchConsentPolicy(LaunchConsentDefault.Prompt, 45, [
            new LaunchConsentRule("deny", null, null, null, null),          // pause at rules[0]
            new LaunchConsentRule("allow", "github:1", null, null, null),   // appended by Allow & remember
        ]);
        var d = LaunchConsentEngine.Evaluate(policy, Input(requester: "github:1"));
        await Assert.That(d.Verdict).IsEqualTo(LaunchConsentVerdict.Deny);
        await Assert.That(d.Source).IsEqualTo("rule[0]");
    }

    [Test]
    public async Task KindToken_maps_all_launch_kinds() {
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.Default)).IsEqualTo("agent");
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.Review)).IsEqualTo("review");
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.ReviewFlow)).IsEqualTo("review-flow");
    }
}
