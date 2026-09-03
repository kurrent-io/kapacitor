using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionsArgsTests {
    [Test]
    public async Task Defaults_are_active_current_repo_everyone_limit_twenty_table() {
        var opts = SessionsArgs.Parse(["sessions"], out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.State).IsEqualTo("active");
        await Assert.That(opts.Repo).IsNull();
        await Assert.That(opts.Mine).IsFalse();
        await Assert.That(opts.Touching).IsNull();
        await Assert.That(opts.Limit).IsEqualTo(20);
        await Assert.That(opts.Json).IsFalse();
    }

    [Test]
    public async Task All_flags_parse() {
        var opts = SessionsArgs.Parse(["sessions", "--ended", "--repo", "acme/widgets", "--mine", "--touching", "src/Foo", "--limit", "5", "--json"], out var error);

        await Assert.That(error).IsNull();
        await Assert.That(opts!.State).IsEqualTo("ended");
        await Assert.That(opts.Repo).IsEqualTo("acme/widgets");
        await Assert.That(opts.RepoHash).IsEqualTo(RepoHashHelper.ComputeRepoHash("acme", "widgets"));
        await Assert.That(opts.Mine).IsTrue();
        await Assert.That(opts.Touching).IsEqualTo("src/Foo");
        await Assert.That(opts.Limit).IsEqualTo(5);
        await Assert.That(opts.Json).IsTrue();
    }

    [Test]
    public async Task Two_state_flags_is_a_usage_error() {
        var opts = SessionsArgs.Parse(["sessions", "--active", "--all"], out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--active");
    }

    [Test]
    [Arguments("all")]
    [Arguments("owner")]
    [Arguments("DA9C523C68AEE2F1")]
    public async Task Malformed_repo_is_a_usage_error(string repo) {
        var opts = SessionsArgs.Parse(["sessions", "--repo", repo], out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--repo");
    }

    [Test]
    [Arguments("--repo")]
    [Arguments("--touching")]
    [Arguments("--limit")]
    public async Task Value_flag_without_a_value_is_a_usage_error(string flag) {
        var opts = SessionsArgs.Parse(["sessions", flag], out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains(flag);
    }

    [Test]
    [Arguments("0")]
    [Arguments("101")]
    [Arguments("ten")]
    public async Task Limit_outside_one_to_hundred_is_a_usage_error(string limit) {
        var opts = SessionsArgs.Parse(["sessions", "--limit", limit], out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--limit");
    }

    [Test]
    public async Task Unknown_flag_is_a_usage_error() {
        var opts = SessionsArgs.Parse(["sessions", "--everyone"], out var error);

        await Assert.That(opts).IsNull();
        await Assert.That(error).Contains("--everyone");
    }
}
