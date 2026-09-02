using Capacitor.Cli.Core.Harness.Claude;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudePermissionModesTests {
    /// The tokens are the Claude CLI's own `--permission-mode` choices and reach argv verbatim, so
    /// their spelling and order are the contract the app chip and the daemon policy both read.
    [Test]
    public async Task Offered_tokens_are_the_claude_cli_choices_from_most_to_least_prompting() {
        string[] expected = ["manual", "acceptEdits", "auto", "bypassPermissions"];

        await Assert.That(ClaudePermissionModes.Offered).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(ClaudePermissionModes.Manual).IsEqualTo("manual");
    }

    [Test]
    [Arguments("manual", true)]
    [Arguments("acceptEdits", true)]
    [Arguments("auto", true)]
    [Arguments("bypassPermissions", true)]
    [Arguments("plan", false)]
    [Arguments("dontAsk", false)]
    [Arguments("AcceptEdits", false)]
    [Arguments("", false)]
    public async Task IsOffered_matches_exact_tokens_only(string token, bool offered) {
        await Assert.That(ClaudePermissionModes.IsOffered(token)).IsEqualTo(offered);
    }
}
