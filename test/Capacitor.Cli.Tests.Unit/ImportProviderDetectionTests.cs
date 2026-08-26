using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Import discards PR/MR fields on every path, so running the live `gh pr view` / `glab api`
/// provider round-trip per cwd during a bulk import is wasted latency. These tests pin
/// the `detectPullRequest` switch on RepositoryDetection.DetectRepositoryAsync via an injected
/// command runner: import (false) resolves owner/repo with git only and never spawns a provider
/// CLI, while the default (live) path still does.
/// </summary>
public class ImportProviderDetectionTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // Records every command the detector spawns; answers git probes with a GitHub remote so
    // owner/repo/host resolve, and answers `gh pr view` with a PR so the live path can populate it.
    static CommandRunner Recording(List<string> log) => (cmd, args, _, _) => {
        log.Add($"{cmd} {args}");
        if (cmd == "git" && args.StartsWith("remote get-url", StringComparison.Ordinal)) return Task.FromResult<string?>("https://github.com/foo/bar");
        if (cmd == "git" && args.StartsWith("branch", StringComparison.Ordinal))         return Task.FromResult<string?>("main");
        if (cmd == "git")                                      return Task.FromResult<string?>("");
        if (cmd == "gh")  return Task.FromResult<string?>("""{"number":7,"title":"t","url":"https://github.com/foo/bar/pull/7","headRefName":"main"}""");
        return Task.FromResult<string?>(null);
    };

    [Test]
    public async Task Import_detection_resolves_repo_without_spawning_a_provider_cli() {
        var log = new List<string>();

        var repo = await RepositoryDetection.DetectRepositoryAsync(Config.Root, 
            "/fake/import/skip-pr", budget: null, detectPullRequest: false, run: Recording(log));

        // Base repo info still resolves from git alone…
        await Assert.That(repo!.Owner).IsEqualTo("foo");
        await Assert.That(repo.RepoName).IsEqualTo("bar");
        await Assert.That(repo.PrNumber).IsNull();
        // …but no `gh` / `glab` round-trip was made.
        await Assert.That(log.Any(c => c.StartsWith("gh", StringComparison.Ordinal) || c.StartsWith("glab", StringComparison.Ordinal))).IsFalse();
        await Assert.That(log.Any(c => c.StartsWith("git", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Live_detection_still_runs_provider_detection() {
        var log = new List<string>();

        var repo = await RepositoryDetection.DetectRepositoryAsync(Config.Root, 
            "/fake/live/do-pr", budget: null, detectPullRequest: true, run: Recording(log));

        await Assert.That(log.Any(c => c.StartsWith("gh pr view", StringComparison.Ordinal))).IsTrue(); // provider detection ran
        await Assert.That(repo!.PrNumber).IsEqualTo(7);
    }
}
