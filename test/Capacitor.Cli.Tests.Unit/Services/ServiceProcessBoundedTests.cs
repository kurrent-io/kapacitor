using System.Diagnostics;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

public class ServiceProcessBoundedTests {
    [Test]
    public async Task Timeout_kills_the_tree_and_returns_promptly() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var sw = Stopwatch.StartNew();
        var (_, _, _, timedOut) = ServiceProcess.RunBounded("/bin/sleep", ["30"], TimeSpan.FromMilliseconds(200));
        sw.Stop();

        await Assert.That(timedOut).IsTrue();
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Completes_normally_and_captures_stdout() {
        Skip.When(OperatingSystem.IsWindows(), "execs a POSIX binary");

        var (exitCode, stdout, _, timedOut) = ServiceProcess.RunBounded("/bin/echo", ["hi"], TimeSpan.FromSeconds(5));

        await Assert.That(timedOut).IsFalse();
        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout).IsEqualTo("hi\n");
    }
}
