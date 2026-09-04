using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A leg waiting on the browser owes it a relinquish, and <c>Environment.Exit</c> pays nothing on the
/// way out: no <c>finally</c>, no <c>using</c> disposal, no <c>catch</c>. The signal handlers reach the
/// notice themselves; every other exit reaches it only through <c>AppDomain.ProcessExit</c>, which
/// <c>Environment.Exit</c> does raise.
///
/// <para>Pinned as a guard because the failure is silent and remote — the process ends cleanly, the
/// terminal looks finished, and what breaks is a page on another machine that never stops waiting.
/// Nothing in the CLI's own output would show it.</para>
/// </summary>
public class FirstRunExitNoticeGuardTests {
    static string RepoRoot([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException($"Could not locate repo root walking up from {here}");
    }

    [Test]
    public async Task The_process_exit_handler_sends_the_pending_relinquish() {
        var program = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "src", "Capacitor.Cli", "Program.cs"));

        var handler = Regex.Match(
            program, @"AppDomain\.CurrentDomain\.ProcessExit\s*\+=.*?\n\};", RegexOptions.Singleline);

        await Assert.That(handler.Success).IsTrue()
                    .Because("no ProcessExit handler means no exit path reaches the notice at all");

        await Assert.That(handler.Value).Contains("FirstRunInterruptRelinquish.RunBeforeExit")
                    .Because("an Environment.Exit anywhere in the CLI otherwise leaves the browser waiting");
    }
}
