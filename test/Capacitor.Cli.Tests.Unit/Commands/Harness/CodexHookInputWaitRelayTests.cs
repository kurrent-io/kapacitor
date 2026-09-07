using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The input-wait relay a daemon-hosted Codex session sends its daemon. Every test here mutates
/// process-wide environment and captures the console, hence the bare constraint on each and a
/// class of its own — the command's main suite carries a keyed one, and a method may not shadow it.
public class CodexHookInputWaitRelayTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// The payload carries no transcript, so neither the watcher nor the server post runs: the
    /// relay is the only thing this hook can reach the network with.
    [Test, NotInParallel]
    [Arguments("Stop", true)]
    [Arguments("UserPromptSubmit", false)]
    public async Task Hosted_turn_boundaries_relay_to_the_daemon_bridge(string eventName, bool waiting) {
        using var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/codex/input-wait").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        using var daemonUrl = EnvScope.Exclusive("KCAP_DAEMON_URL", $"http://127.0.0.1:{bridge.Ports[0]}/tok");
        using var agentId   = EnvScope.Exclusive("KCAP_AGENT_ID", "agent-1");
        using var capture   = ConsoleOutput.StartCapture();

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(TimeProvider.System), Home)
            .Handle(new StringReader($$"""{"hook_event_name":"{{eventName}}","session_id":"019e0322-05fc-7570-be65-75719c3ea861","cwd":"/tmp"}"""));

        await Assert.That(exit).IsEqualTo(0);
        var relayed = bridge.LogEntries.Single(e => e.RequestMessage.Path == "/tok/codex/input-wait");
        var body    = JsonDocument.Parse(relayed.RequestMessage.Body!).RootElement;
        await Assert.That(body.GetProperty("waiting").GetBoolean()).IsEqualTo(waiting);
        await Assert.That(body.GetProperty("agent_id").GetString()).IsEqualTo("agent-1");
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo("019e032205fc7570be6575719c3ea861");
    }
}
