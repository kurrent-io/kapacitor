namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class ShellTokenPatternTests {
    static IReadOnlyList<string> Argv(string joined) => joined.Split(' ');

    [Test]
    [Arguments("git status", "git status", true)]
    [Arguments("git status", "git status --porcelain", false)]   // equal counts without a rest token
    [Arguments("git status *", "git status", true)]              // rest token matches zero tokens
    [Arguments("git status *", "git status --porcelain -z", true)]
    [Arguments("git *", "git push", true)]
    [Arguments("git status", "env git status", false)]           // allow is anchored at token 0
    [Arguments("git diff*", "git diff --output=x", false)]       // glob is within one token, not across argv
    public async Task Allow_matching(string pattern, string argv, bool expected) =>
        await Assert.That(ShellTokenPattern.Parse(pattern)!.MatchesAllow(Argv(argv))).IsEqualTo(expected);

    [Test]
    [Arguments("git push --force*", "git push --force origin main", true)]
    [Arguments("git push --force*", "git push --force-with-lease", true)]
    [Arguments("git push --force*", "env FOO=1 git push --force", true)]  // any position
    [Arguments("git push --force*", "git push origin --force", false)]    // run must be contiguous
    [Arguments("rm -rf", "echo rm -rf", true)]                            // over-trigger is accepted for tighten outcomes
    public async Task Restrictive_matching(string pattern, string argv, bool expected) =>
        await Assert.That(ShellTokenPattern.Parse(pattern)!.MatchesRestrictive(Argv(argv), exact: false))
            .IsEqualTo(expected);

    [Test]
    public async Task Exact_restrictive_anchors_at_token_zero_with_equal_counts() {
        var p = ShellTokenPattern.Parse("gh pr merge")!;
        await Assert.That(p.MatchesRestrictive(Argv("gh pr merge"), exact: true)).IsTrue();
        await Assert.That(p.MatchesRestrictive(Argv("gh pr merge --squash"), exact: true)).IsFalse();
        await Assert.That(p.MatchesRestrictive(Argv("echo gh pr merge"), exact: true)).IsFalse();
    }

    [Test]
    public async Task Bare_star_pattern_is_a_universal_rest_token() {
        var p = ShellTokenPattern.Parse("*")!;
        await Assert.That(p.HasRestToken).IsTrue();
        await Assert.That(p.MatchesAllow(Argv("anything at all"))).IsTrue();
        await Assert.That(p.MatchesRestrictive(Argv("anything"), exact: false)).IsTrue();
    }

    [Test]
    public async Task Empty_pattern_is_invalid() =>
        await Assert.That(ShellTokenPattern.Parse("   ")).IsNull();
}
