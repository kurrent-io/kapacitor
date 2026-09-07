using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// The container the app builds at construction, before any window exists. It assembles partway
/// through a constructor no test can enter, so without this nothing would build it until a user
/// started the app.
/// </summary>
public class AppForeignHttpTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    ServiceProvider Build() =>
        new ServiceCollection().AddAppForeignHttp(Config.Root).BuildValidated();

    [Test]
    public async Task The_foreign_container_resolves_what_the_app_takes_from_it() {
        using var sp = Build();

        // Everything App.axaml.cs asks this container for. A resolve it cannot answer is a crash
        // during startup or a re-auth, both past the point where a window could report it.
        await Assert.That(sp.GetRequiredService<TokenStore>()).IsNotNull();
        await Assert.That(sp.GetRequiredService<IHttpClientFactory>()).IsNotNull();
        await Assert.That(sp.GetRequiredService<IAuthProxyClient>()).IsNotNull();
        await Assert.That(sp.GetRequiredService<GitHubOAuthClient>()).IsNotNull();
        await Assert.That(sp.GetRequiredService<WorkOSClient>()).IsNotNull();
        await Assert.That(sp.GetRequiredService<TenantProvisioningClient>()).IsNotNull();
    }

    /// <summary>
    /// The assumption the app rests on rather than states: the token store's refresh against our own
    /// server asks for a lane only <c>AddCapacitorHttp</c> registers, and this container does not
    /// register it. A factory that threw on an unknown name — rather than handing back a plain
    /// pooled client — would take down the app at its first token refresh.
    /// </summary>
    [Test]
    public async Task An_unregistered_lane_name_yields_a_plain_client_rather_than_throwing() {
        using var sp = Build();

        var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(CapacitorClients.Default);

        await Assert.That(client).IsNotNull();
    }
}
