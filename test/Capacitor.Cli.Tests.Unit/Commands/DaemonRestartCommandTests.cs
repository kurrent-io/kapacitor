using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class DaemonRestartCommandTests {
    [Test]
    public async Task Bare_is_now() =>
        await Assert.That(DaemonCommands.ParseRestartMode([])).IsEqualTo("now");

    [Test]
    public async Task When_idle_flag() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--when-idle"])).IsEqualTo("when-idle");

    [Test]
    public async Task Force_flag() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--force"])).IsEqualTo("force");

    [Test]
    public async Task Force_wins_over_when_idle() =>
        await Assert.That(DaemonCommands.ParseRestartMode(["--when-idle", "--force"])).IsEqualTo("force");
}
