using Capacitor.App.Services;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Tests.Unit;

/// <summary>
/// The source's own container, built by the factory it defaults to. Every other test injects a
/// factory instead, so nothing else constructs this lane — and a registration it needs but nobody
/// makes would surface as a resolve failure the first time a user opened the pane.
///
/// <para>The server is a closed port on purpose: what is under test is that the lane assembles and
/// sends, not what it gets back.</para>
/// </summary>
public class ServerWorkContextSourceLaneTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Session = "0123456789abcdef0123456789abcdef";
    const string Url     = "http://127.0.0.1:1";

    [Test]
    public async Task The_default_factory_builds_a_lane_that_resolves_and_sends() {
        var profiles = Resolutions.At(Url, Config.Root);

        // A credential the source can act on: without one the read answers SignedOut before the
        // lane ever sends, and the test would pass on a container that was never asked for a client.
        await AuthFixtures.NewTokenStore(Config.Root).SaveAsync(profiles.Name, new StoredTokens {
            AccessToken    = "tok",
            ExpiresAt      = DateTimeOffset.UtcNow.AddHours(1),
            GitHubUsername = "alice",
            Provider       = AuthProvider.GitHubApp,
            ServerUrl      = Url,
        });

        await using var source = new ServerWorkContextSource(Config.Root, profiles);

        var read = await source.ReadAsync(Session, CancellationToken.None);

        // Reaching a verdict at all means the container built and handed back a client: an
        // unregistered dependency throws out of the resolve rather than degrading to a read.
        await Assert.That(read.Kind).IsEqualTo(WorkContextReadKind.Unreachable)
            .Because("the lane sent to a closed port, which is as far as this needs to get");
    }
}
