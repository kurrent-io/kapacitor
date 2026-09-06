namespace Capacitor.Cli.Daemon.Tests.Unit;

/// The version flag must answer before any config, environment or profile work: the release
/// pipeline runs it as the post-signing smoke on a runner with no daemon setup at all.
public class DaemonVersionFlagTests {
    [Test]
    public async Task Exactly_version_prints_and_is_handled() {
        var output = new StringWriter();

        var handled = DaemonRunner.TryHandleVersionFlag(["--version"], output);

        await Assert.That(handled).IsTrue();
        await Assert.That(output.ToString().TrimEnd()).IsEqualTo($"kcap-daemon {DaemonRunner.ResolveDaemonVersion()}");
    }

    [Test]
    [Arguments(new object[] { new string[0] })]
    [Arguments(new object[] { new[] { "--name", "x" } })]
    [Arguments(new object[] { new[] { "--version", "--name", "x" } })]
    public async Task Anything_else_is_not_handled(string[] args) {
        var output = new StringWriter();

        var handled = DaemonRunner.TryHandleVersionFlag(args, output);

        await Assert.That(handled).IsFalse();
        await Assert.That(output.ToString()).IsEmpty();
    }
}
