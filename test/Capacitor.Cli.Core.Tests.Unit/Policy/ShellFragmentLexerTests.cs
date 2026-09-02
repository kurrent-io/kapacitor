namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class ShellFragmentLexerTests {
    [Test]
    public async Task Whitespace_runs_collapse_and_quotes_resolve() {
        var frags = ShellFragmentLexer.Lex("git   push  '--force'   \"origin\"  main");
        await Assert.That(frags).IsEquivalentTo(new[] { "git", "push", "--force", "origin", "main" });
    }

    [Test]
    public async Task Redirection_stays_in_the_stream_as_fragments() {
        var frags = ShellFragmentLexer.Lex("git status > pwn.yml && rm -rf /");
        await Assert.That(frags).IsEquivalentTo(
            new[] { "git", "status", ">", "pwn.yml", "&&", "rm", "-rf", "/" });
    }

    [Test]
    public async Task Escaped_double_quote_is_resolved() {
        var frags = ShellFragmentLexer.Lex("echo \"a \\\" b\"");
        await Assert.That(frags).IsEquivalentTo(new[] { "echo", "a \" b" });
    }

    [Test]
    public async Task Unterminated_quote_abandons_lexing() =>
        await Assert.That(ShellFragmentLexer.Lex("echo 'oops")).IsEmpty();

    [Test]
    public async Task Newlines_split_like_whitespace() {
        var frags = ShellFragmentLexer.Lex("git add .\ngit commit");
        await Assert.That(frags).IsEquivalentTo(new[] { "git", "add", ".", "git", "commit" });
    }

    [Test]
    public async Task Empty_quoted_fragment_is_dropped() {
        var frags = ShellFragmentLexer.Lex("git push '' --force");
        await Assert.That(frags).IsEquivalentTo(new[] { "git", "push", "--force" });
    }
}
