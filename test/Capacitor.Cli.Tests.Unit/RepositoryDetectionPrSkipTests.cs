using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// The Claude hook path passes <c>detectPullRequest: false</c> so a live
/// <c>gh pr view</c> / <c>glab</c> round-trip (~600 ms to GitHub) never blocks
/// the 5 s SessionStart deadline. The watcher owns PR detection and backfills
/// it independently, so the hook only needs base repo info (owner/repo/branch).
/// </summary>
public class RepositoryDetectionPrSkipTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Before(Test)]
    public void Reset() => GitProviderRouter.ResetMemoForTests();

    static CommandRunner RecordingRunner(List<string> commands) =>
        (cmd, args, _, _) => {
            commands.Add(cmd);
            string? reply = (cmd, args) switch {
                ("git", "branch --show-current") => "main",
                ("git", "config user.name")      => "Tester",
                ("git", "config user.email")     => "t@example.com",
                ("git", "remote get-url origin") => "git@github.com:acme/widget.git",
                _                                => null
            };
            return Task.FromResult(reply);
        };

    [Test]
    public async Task DetectPullRequest_false_never_spawns_gh_or_glab() {
        using var cwd = new TempDir();
        var commands = new List<string>();

        var repo = await RepositoryDetection.DetectRepositoryAsync(Config.Root, 
            cwd.Path, budget: TimeSpan.FromSeconds(5), detectPullRequest: false, run: RecordingRunner(commands));

        await Assert.That(repo).IsNotNull();
        await Assert.That(repo!.Owner).IsEqualTo("acme");
        await Assert.That(repo.RepoName).IsEqualTo("widget");
        await Assert.That(repo.Branch).IsEqualTo("main");
        await Assert.That(repo.PrNumber).IsNull();

        // The gate is the whole point: no provider probe (gh auth status) and no
        // detector (gh/glab) was ever spawned — only git commands ran.
        await Assert.That(commands.All(c => c == "git")).IsTrue();
    }

    [Test]
    public async Task DetectPullRequest_true_does_probe_the_provider() {
        using var cwd = new TempDir();
        var commands = new List<string>();

        await RepositoryDetection.DetectRepositoryAsync(Config.Root, 
            cwd.Path, budget: TimeSpan.FromSeconds(5), detectPullRequest: true, run: RecordingRunner(commands));

        // Proves the flag actually gates the round-trip: with detection ON, the
        // GitHub provider probe (gh) is attempted.
        await Assert.That(commands.Any(c => c == "gh")).IsTrue();
    }

    [Test]
    public async Task EnrichWithRepositoryInfo_false_never_spawns_gh_or_glab() {
        using var cwd = new TempDir();
        var commands = new List<string>();
        var payload  = $$"""{"cwd":"{{cwd.Path.Replace("\\", "/")}}","hook_event_name":"session-start"}""";

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(Config.Root, 
            payload, budget: TimeSpan.FromSeconds(5), detectPullRequest: false, run: RecordingRunner(commands));

        await Assert.That(enriched).Contains("acme");
        await Assert.That(commands.All(c => c == "git")).IsTrue();
    }
}
