using Capacitor.Cli.Daemon;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Tests.Unit.Daemon;

/// <summary>
/// Pins the non-obvious DI mechanism <see cref="DaemonRunner"/> depends on for the DaemonStatus
/// push to ever reach a real hub connection: <see cref="ServerConnection"/>'s trailing optional
/// <c>DaemonStatusNotifier?</c> parameter is resolved to the ONE registered singleton only because
/// its DI registration is a bare <c>AddSingleton&lt;ServerConnection&gt;()</c> — no factory
/// delegate. If a future change rewrites that registration with a factory that omits the
/// notifier (e.g. <c>AddSingleton(sp => new ServerConnection(...))</c> without the 4th arg),
/// <c>ServerConnection</c> falls back to a private notifier nobody subscribes to and every
/// hub-state pulse silently stops reaching StatusSubscribe clients — with no other test noticing,
/// since <see cref="ServerConnection.HubState"/> and every other observable behavior stay correct.
/// Uses the SAME bare registration shape <see cref="DaemonRunner"/> uses in production, not a
/// direct <c>new ServerConnection(...)</c> call — a direct construction wouldn't exercise the DI
/// resolution behavior this test exists to pin.
/// </summary>
public class DaemonStatusWiringTests {
    [Test]
    public async Task ServerConnection_resolved_via_DI_shares_the_one_registered_notifier() {
        var services = new ServiceCollection();
        services.AddSingleton(new DaemonConfig { Name = "wiring-test", ServerUrl = "http://127.0.0.1:1" });
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<DaemonStatusNotifier>();
        services.AddSingleton<ServerConnection>();

        await using var provider = services.BuildServiceProvider();

        var notifier   = provider.GetRequiredService<DaemonStatusNotifier>();
        var connection = provider.GetRequiredService<ServerConnection>();

        try {
            await Assert.That(ReferenceEquals(connection.StatusNotifierForTest, notifier)).IsTrue();
        } finally {
            await connection.DisposeAsync();
        }
    }
}
