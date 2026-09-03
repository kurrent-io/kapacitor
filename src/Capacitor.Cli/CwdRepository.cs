using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli;

/// <summary>
/// The repository a long-lived server's working directory belongs to, resolved on first use and
/// held for the life of the process. Pull-request detection is skipped: callers need owner and
/// name only, and the provider round-trip would otherwise run in every agent session that spawns
/// the server, tool call or not. Not thread-safe; a concurrent first use resolves twice.
/// </summary>
sealed class CwdRepository(ConfigRoot config, string cwd, CommandRunner? run = null) {
    bool               _resolved;
    RepositoryPayload? _repository;

    /// <summary>Owner, name, host and branch of the checkout, or null outside one.</summary>
    public async ValueTask<RepositoryPayload?> GetAsync() {
        if (_resolved) return _repository;

        _repository = await RepositoryDetection.DetectRepositoryAsync(config, cwd, detectPullRequest: false, run: run);
        _resolved   = true;

        return _repository;
    }

    /// <summary>The server-side repo hash of the checkout, or null when its origin names no owner.</summary>
    public async ValueTask<string?> GetHashAsync() {
        var repo = await GetAsync();

        return repo?.Owner is null || repo.RepoName is null
            ? null
            : RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
    }
}
