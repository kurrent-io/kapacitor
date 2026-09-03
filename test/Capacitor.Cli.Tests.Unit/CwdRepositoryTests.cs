using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The repo-aware MCP servers auto-register, so every agent session spawns them whether or not a
/// tool is ever called. The working directory's repository must therefore cost nothing until a
/// tool needs it, resolve once, and never make the provider round-trip: only owner and name feed
/// the repo hash.
/// </summary>
public class CwdRepositoryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir]        public required TempDir        Cwd    { get; init; }

    [Before(Test)]
    public void Reset() => GitProviderRouter.ResetMemoForTests();

    static CommandRunner RecordingRunner(List<string> commands, string? origin = "git@github.com:acme/widget.git") =>
        (cmd, args, _, _) => {
            commands.Add($"{cmd} {args}");
            string? reply = (cmd, args) switch {
                ("git", "branch --show-current") => "main",
                ("git", "config user.name")      => "Tester",
                ("git", "config user.email")     => "t@example.com",
                ("git", "remote get-url origin") => origin,
                _                                => null
            };
            return Task.FromResult(reply);
        };

    [Test]
    public async Task Construction_spawns_nothing() {
        var commands = new List<string>();

        _ = new CwdRepository(Config.Root, Cwd.Path, RecordingRunner(commands));

        await Assert.That(commands).IsEmpty();
    }

    [Test]
    public async Task Hash_comes_from_origin_without_a_provider_probe() {
        var commands = new List<string>();
        var repo     = new CwdRepository(Config.Root, Cwd.Path, RecordingRunner(commands));

        var hash = await repo.GetHashAsync();

        await Assert.That(hash).IsEqualTo(RepoHashHelper.ComputeRepoHash("acme", "widget"));
        await Assert.That(commands).IsNotEmpty();
        await Assert.That(commands.All(c => c.StartsWith("git ", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Resolution_runs_once_per_instance() {
        var commands = new List<string>();
        var repo     = new CwdRepository(Config.Root, Cwd.Path, RecordingRunner(commands));

        await repo.GetHashAsync();
        var spawned = commands.Count;

        await repo.GetHashAsync();
        await repo.GetAsync();

        await Assert.That(spawned).IsGreaterThan(0);
        await Assert.That(commands.Count).IsEqualTo(spawned);
    }

    [Test]
    public async Task Outside_a_checkout_there_is_no_repository_and_no_hash() {
        var repo = new CwdRepository(Config.Root, Cwd.Path, (_, _, _, _) => Task.FromResult<string?>(null));

        await Assert.That(await repo.GetAsync()).IsNull();
        await Assert.That(await repo.GetHashAsync()).IsNull();
    }

    [Test]
    public async Task A_checkout_without_an_origin_remote_has_no_hash() {
        var commands = new List<string>();
        var repo     = new CwdRepository(Config.Root, Cwd.Path, RecordingRunner(commands, origin: null));

        await Assert.That(await repo.GetAsync()).IsNotNull();
        await Assert.That(await repo.GetHashAsync()).IsNull();
    }
}
