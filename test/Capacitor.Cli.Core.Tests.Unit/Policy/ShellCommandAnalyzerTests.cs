namespace Capacitor.Cli.Core.Tests.Unit.Policy;

using Capacitor.Cli.Core.Policy;

public class ShellCommandAnalyzerTests {
    [Test]
    public async Task Simple_command_is_analyzed_into_one_segment() {
        var r = ShellCommandAnalyzer.Analyze("git status --porcelain");
        await Assert.That(r.Analyzed).IsTrue();
        await Assert.That(r.Segments).IsEquivalentTo(
            new[] { new ShellSegment(["git", "status", "--porcelain"]) });
    }

    [Test]
    public async Task Top_level_operators_split_segments() {
        var r = ShellCommandAnalyzer.Analyze("git add -A && git commit -m done; git log | head");
        await Assert.That(r.Analyzed).IsTrue();
        await Assert.That(r.Segments.Count).IsEqualTo(4);
        await Assert.That(r.Segments[1].Argv).IsEquivalentTo(new[] { "git", "commit", "-m", "done" });
    }

    [Test]
    public async Task Quoted_literals_are_resolved_into_single_tokens() {
        var r = ShellCommandAnalyzer.Analyze("git commit -m 'two words' --author \"A B\"");
        await Assert.That(r.Analyzed).IsTrue();
        await Assert.That(r.Segments[0].Argv).IsEquivalentTo(
            new[] { "git", "commit", "-m", "two words", "--author", "A B" });
    }

    // The exhaustive unanalyzed table: each row is one banned construct.
    [Test]
    [Arguments("git status > out.txt")]          // redirection
    [Arguments("cat < in.txt")]                  // redirection
    [Arguments("cat <<EOF")]                     // here-doc
    [Arguments("echo $HOME")]                    // parameter expansion
    [Arguments("echo \"$HOME\"")]                // expansion inside double quotes
    [Arguments("echo `date`")]                   // command substitution
    [Arguments("diff <(sort a) <(sort b)")]      // process substitution
    [Arguments("ls *.md")]                       // glob
    [Arguments("ls ?.md")]                       // glob
    [Arguments("ls [ab].md")]                    // glob class
    [Arguments("ls ~/notes")]                    // tilde expansion at word start
    [Arguments("sleep 5 &")]                     // backgrounding
    [Arguments("a || b")]                        // || is not on the operator allowlist
    [Arguments("(cd /tmp)")]                     // subshell
    [Arguments("{ ls; }")]                       // group
    [Arguments("echo a{b,c}")]                   // brace expansion
    [Arguments("eval git status")]               // eval
    [Arguments("exec git status")]               // exec
    [Arguments("bash -c 'rm -rf x'")]            // nested shell
    [Arguments("sh script.sh")]                  // nested shell
    [Arguments("FOO=1 git push")]                // leading assignment hides the real program
    [Arguments("echo a\\ b")]                    // backslash escape
    [Arguments("git log # comment")]             // comment
    [Arguments("git status\ngit log")]           // newline separator
    [Arguments("echo 'unterminated")]            // unterminated quote
    [Arguments("git add . &&")]                  // trailing operator = empty segment
    [Arguments("&& git add .")]                  // leading operator = empty segment
    [Arguments("! git diff --quiet")]            // pipeline negation
    [Arguments("")]                              // empty command
    [Arguments("/bin/sh -c 'x'")]                // path-qualified nested shell
    [Arguments("BASH -c 'x'")]                   // case-variant nested shell
    [Arguments("a+=1 git push --force")]         // += leading assignment
    [Arguments("PATH+=/tmp git status")]         // += leading assignment
    [Arguments("git push '' --force")]           // empty argv token
    [Arguments("powershell -Command x")]         // non-POSIX nested shell
    [Arguments("pwsh -c x")]                     // non-POSIX nested shell
    [Arguments("cmd /c x")]                      // non-POSIX nested shell
    [Arguments("C:\\bash.exe -c x")]             // backslash path to a nested shell
    [Arguments("'C:\\bash.exe' -c x")]           // quoting hides neither the separator nor the .exe
    [Arguments("env bash -c 'x'")]               // wrapper in front of a nested shell
    [Arguments("sudo sh script.sh")]             // wrapper in front of a nested shell
    [Arguments("echo bash")]                     // deliberate over-match: any token naming a shell
    [Arguments("ash -c 'x'")]                    // nested shell
    [Arguments("busybox sh x")]                  // applet multiplexer in front of a nested shell
    [Arguments("busybox rm -rf x")]              // the multiplexer alone, with no shell to catch
    [Arguments("nu -c 'rm -rf /'")]              // modern shell on the maintained interpreter list
    [Arguments("xonsh -c x")]                    // modern shell on the maintained interpreter list
    [Arguments("if true; then rm -rf x; fi")]    // compound statement, not three simple commands
    [Arguments("for i in a; do echo x; done")]   // compound statement
    [Arguments("while true; do x; done")]        // compound statement
    public async Task Banned_constructs_are_unanalyzed(string command) {
        var r = ShellCommandAnalyzer.Analyze(command);
        await Assert.That(r.Analyzed).IsFalse();
        await Assert.That(r.Segments).IsEmpty();
    }

    [Test]
    [Arguments("git log HEAD~3")]                // ~ mid-token is literal
    [Arguments("grep -n issue#5 notes.txt")]     // # mid-token is literal
    [Arguments("git log --format=%H")]           // = and % in arguments are literal
    [Arguments("env FOO=1 git push --force")]    // assignment as env's argument, not leading
    [Arguments("grep 'a*b' file.txt")]           // glob chars inside quotes are literal
    [Arguments("echo if")]                       // a reserved word is reserved in command position only
    [Arguments("git log --grep=done")]           // reserved word inside an argument
    public async Task Literal_lookalikes_stay_analyzed(string command) =>
        await Assert.That(ShellCommandAnalyzer.Analyze(command).Analyzed).IsTrue();
}
