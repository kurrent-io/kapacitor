using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Daemon;

internal class LaunchConsentEngineTests {
    static LaunchConsentInput Input(
        string? requester = "user_abc", bool owner = false, string kind = "agent",
        string repo = "/Users/me/dev/proj", string vendor = "claude")
        => new(requester, owner, kind, repo, vendor);

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
    [Arguments(LaunchConsentDefault.Allow, LaunchConsentVerdict.Allow)]
    [Arguments(LaunchConsentDefault.Deny, LaunchConsentVerdict.Deny)]
    [Arguments(LaunchConsentDefault.Prompt, LaunchConsentVerdict.Prompt)]
    public async Task Unmatched_falls_through_to_default(LaunchConsentDefault def, LaunchConsentVerdict expected) {
        var d = LaunchConsentEngine.Evaluate(Policy(def), Input());
        await Assert.That(d.Verdict).IsEqualTo(expected);
        await Assert.That(d.Source).IsEqualTo("default");
    }

    [Test]
    public async Task KindToken_maps_all_launch_kinds() {
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.Default)).IsEqualTo("agent");
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.Review)).IsEqualTo("review");
        await Assert.That(LaunchConsentEngine.KindToken(LaunchKind.ReviewFlow)).IsEqualTo("review-flow");
    }
}
