using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class AgentCommandRoutingTests {
    [Test]
    public async Task Bare_agent_routes_to_ls() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent"]);
        await Assert.That(sub).IsEqualTo("ls");
        await Assert.That(rest).IsEmpty();
    }

    [Test]
    public async Task Subcommand_and_its_arguments_are_split() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent", "stop", "ab12", "--daemon", "dev"]);
        await Assert.That(sub).IsEqualTo("stop");
        await Assert.That(rest).IsEquivalentTo(new[] { "ab12", "--daemon", "dev" });
    }

    [Test]
    public async Task Start_passthrough_survives_the_split_intact() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent", "start", "claude", "--", "--model", "opus"]);
        await Assert.That(sub).IsEqualTo("start");
        await Assert.That(rest).IsEquivalentTo(new[] { "claude", "--", "--model", "opus" });
    }

    [Test]
    public async Task Known_subcommands_are_exactly_the_four_documented_verbs() {
        await Assert.That(AgentCommand.KnownSubcommands).IsEquivalentTo(new[] { "start", "ls", "stop", "attach" });
    }

    [Test]
    public async Task Unknown_subcommand_is_not_routable() {
        var (sub, _) = AgentCommand.SplitSubcommand(["agent", "frobnicate"]);
        await Assert.That(AgentCommand.KnownSubcommands).DoesNotContain(sub);
    }
}
