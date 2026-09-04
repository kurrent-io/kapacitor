using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core.Config;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// The session-start memory index is fetched on a lane that does not follow redirects, so a 3xx
/// reads as a non-answer rather than as content. Following one would let whatever the hop resolves
/// to be injected into the agent's context, and a cross-origin hop drops the credential on the way,
/// so the substituted body would not even be one the server authenticated.
/// </summary>
public class SessionStartMemoryRedirectTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test, NotInParallel]
    public async Task The_memory_index_fetch_does_not_follow_a_redirect() {
        var config = new ProfileConfig {
            ActiveProfile = "work",
            Profiles      = new() { ["work"] = new Profile { ServerUrl = _server.Url } }
        };
        await ConfigMutator.MutateAsync(Config.Root, _ => config);

        _server.Given(Request.Create().WithPath("/hooks/session-start/gemini").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        // Same-origin, so the hop is one the runtime would happily follow with the credential still
        // attached: what stops it has to be the lane, not the redirect being cross-origin.
        _server.Given(Request.Create().WithPath("/api/memories/index").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302)
                .WithHeader("Location", $"{_server.Url}/api/memories/index-hopped"));

        _server.Given(Request.Create().WithPath("/api/memories/index-hopped").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """
                [{"memory_id":"m1","slug":"hopped-slug","audience":"user",
                  "description":"served only behind the redirect","kind":"reference"}]
                """));

        var (_, stdout) = await RunAsync(Guid.NewGuid().ToString());

        // Without this the test passes on a run that never fetched at all.
        var fetches = _server.FindLogEntries(
            Request.Create().WithPath("/api/memories/index").UsingGet());
        await Assert.That(fetches.Count).IsEqualTo(1);

        // The hop is never requested — the 3xx ends the fetch.
        var hops = _server.FindLogEntries(
            Request.Create().WithPath("/api/memories/index-hopped").UsingGet());
        await Assert.That(hops.Count).IsEqualTo(0);

        // And nothing the hop would have served reaches the model.
        await Assert.That(stdout).DoesNotContain("hopped-slug");
    }

    async Task<(int ExitCode, string Stdout)> RunAsync(string sessionId) {
        var payload =
            $$"""
              {
                "hook_event_name": "SessionStart",
                "session_id":      "{{sessionId}}",
                "cwd":             "/tmp",
                "source":          "startup"
              }
              """;

        using var capture = ConsoleOutput.StartCapture();

        var exit = await new GeminiHookCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), new HookClock(TimeProvider.System), Home)
            .Handle(new StringReader(payload));

        return (exit, capture.GetCapturedOutput());
    }
}
