namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class GlobPatternTests {
    [Test]
    [Arguments("git", "git", true)]
    [Arguments("git", "Git", false)]           // case-sensitive
    [Arguments("--force*", "--force", true)]
    [Arguments("--force*", "--force-with-lease", true)]
    [Arguments("--force*", "--f", false)]
    [Arguments("*.md", "README.md", true)]
    [Arguments("a?c", "abc", true)]
    [Arguments("a?c", "ac", false)]
    [Arguments("*", "", true)]
    [Arguments("**", "anything", true)]
    [Arguments("a*b*c", "aXbYc", true)]
    [Arguments("a*b*c", "acb", false)]
    public async Task Matches(string pattern, string text, bool expected) =>
        await Assert.That(GlobPattern.IsMatch(pattern, text)).IsEqualTo(expected);
}
