using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class AgentStartArgsTests {
    [Test]
    public async Task Splits_kcap_flags_from_passthrough_at_double_dash() {
        var a = AgentStartArgs.Parse(["claude", "--worktree", "--daemon", "dev", "--", "--model", "opus", "fix"]);
        await Assert.That(a.Vendor).IsEqualTo("claude");
        await Assert.That(a.Worktree).IsTrue();
        await Assert.That(a.DaemonName).IsEqualTo("dev");
        await Assert.That(a.Passthrough).IsEquivalentTo(new[] { "--model", "opus", "fix" });
        await Assert.That(a.Error).IsNull();
    }

    [Test]
    public async Task Default_is_in_place_with_no_passthrough() {
        var a = AgentStartArgs.Parse(["codex"]);
        await Assert.That(a.Vendor).IsEqualTo("codex");
        await Assert.That(a.Worktree).IsFalse();
        await Assert.That(a.Passthrough).IsEmpty();
        await Assert.That(a.Error).IsNull();
    }

    [Test]
    public async Task Empty_args_is_an_error() {
        await Assert.That(AgentStartArgs.Parse([]).Error).IsNotNull();
    }

    [Test]
    public async Task Unknown_kcap_flag_before_dash_is_an_error() {
        var a = AgentStartArgs.Parse(["claude", "--model", "opus"]);
        await Assert.That(a.Error).IsNotNull();
    }

    [Test]
    public async Task Share_is_not_a_flag_sharing_is_a_ui_action() {
        // Sharing is server/UI-authoritative (tracks a future `kcap share` command),
        // so --share is just an unknown flag and is rejected.
        await Assert.That(AgentStartArgs.Parse(["claude", "--share"]).Error).IsNotNull();
    }

    [Test]
    public async Task Private_flag_is_parsed_and_defaults_false() {
        var on = AgentStartArgs.Parse(["claude", "--private", "--", "fix"]);
        await Assert.That(on.Private).IsTrue();
        await Assert.That(on.Passthrough).IsEquivalentTo(new[] { "fix" });
        await Assert.That(on.Error).IsNull();

        var off = AgentStartArgs.Parse(["claude"]);
        await Assert.That(off.Private).IsFalse();
    }

    [Test]
    public async Task Empty_passthrough_after_dash_is_allowed() {
        var a = AgentStartArgs.Parse(["claude", "--"]);
        await Assert.That(a.Error).IsNull();
        await Assert.That(a.Passthrough).IsEmpty();
    }

    [Test]
    public async Task Detach_parses_in_both_short_and_long_form() {
        await Assert.That(AgentStartArgs.Parse(["claude", "-d"]).Detached).IsTrue();
        await Assert.That(AgentStartArgs.Parse(["claude", "--detach"]).Detached).IsTrue();
        await Assert.That(AgentStartArgs.Parse(["claude"]).Detached).IsFalse();
    }

    [Test]
    public async Task Daemon_flag_requires_a_value() {
        await Assert.That(AgentStartArgs.Parse(["claude", "--daemon"]).Error).IsNotNull();
    }

    [Test]
    public async Task Removed_spellings_are_rejected_as_unknown_flags() {
        // The group deliberately keeps one spelling each: --daemon (was --name),
        // -d/--detach (was --detached). The old ones must not silently work.
        await Assert.That(AgentStartArgs.Parse(["claude", "--name", "dev"]).Error).IsNotNull();
        await Assert.That(AgentStartArgs.Parse(["claude", "--detached"]).Error).IsNotNull();
    }
}
