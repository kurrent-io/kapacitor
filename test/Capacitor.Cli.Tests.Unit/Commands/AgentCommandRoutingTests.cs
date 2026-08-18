using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class AgentCommandRoutingTests {
    [Test]
    public async Task Bare_agent_routes_to_ls() {
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent"]);
        await Assert.That(sub).IsEqualTo("ls");
        await Assert.That(rest).IsEmpty();
    }

    [Test]
    public async Task A_leading_flag_is_an_ls_option_not_a_subcommand() {
        // `kcap agent --daemon dev` lists that daemon's agents; it is not a subcommand
        // named `--daemon`, which is what a naive argv[1] split would report.
        var (sub, rest) = AgentCommand.SplitSubcommand(["agent", "--daemon", "dev"]);
        await Assert.That(sub).IsEqualTo("ls");
        await Assert.That(rest).IsEquivalentTo(new[] { "--daemon", "dev" });
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

    [Test]
    public async Task DaemonNameFrom_absent_flag_resolves_to_the_default() {
        var (name, error) = AgentCommand.DaemonNameFrom(["ab12"]);
        await Assert.That(name).IsNull();
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task DaemonNameFrom_returns_the_value() {
        var (name, error) = AgentCommand.DaemonNameFrom(["ab12", "--daemon", "dev"]);
        await Assert.That(name).IsEqualTo("dev");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task DaemonNameFrom_as_the_final_token_is_an_error_not_a_silent_default() {
        var (name, error) = AgentCommand.DaemonNameFrom(["--all", "-y", "--daemon"]);
        await Assert.That(name).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task DaemonNameFrom_followed_by_a_flag_is_an_error_not_passed_through() {
        var (name, error) = AgentCommand.DaemonNameFrom(["ab12", "--daemon", "-y"]);
        await Assert.That(name).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task DaemonNameFrom_resolves_the_value_amid_other_flags() {
        var (name, error) = AgentCommand.DaemonNameFrom(["--all", "-y", "--daemon", "dev"]);
        await Assert.That(name).IsEqualTo("dev");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task DaemonNameFrom_empty_string_value_is_an_error_not_a_silent_default() {
        // An explicitly empty "--daemon" value used to escape validation (it's neither missing
        // nor flag-shaped) and throw ArgumentException out of DaemonNameResolver.Resolve instead
        // of printing this clean error.
        var (name, error) = AgentCommand.DaemonNameFrom(["ab12", "--daemon", ""]);
        await Assert.That(name).IsNull();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Protected_agents_are_partitioned_out_for_the_confirmation_prompt() {
        AgentRow[] agents = [
            new("a1", "Running", "/r1", "agent", "", ""),
            new("f1", "Running", "/r2", "review-flow", "flow-7f3a", "reviewer"),
            new("v1", "Running", "/r3", "review", "", ""),
        ];

        var (stoppable, prot) = AgentCommand.PartitionByProtection(agents);

        await Assert.That(stoppable).IsEquivalentTo(new[] { "a1" });
        await Assert.That(prot).IsEquivalentTo(new[] { "f1", "v1" });
    }
}
