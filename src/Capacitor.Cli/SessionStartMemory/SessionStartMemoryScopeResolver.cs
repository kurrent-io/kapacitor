using Capacitor.Cli.Core;

namespace Capacitor.Cli.SessionStartMemory;

internal sealed class SessionStartMemoryScopeResolver : ISessionStartMemoryScopeResolver {
    public async Task<SessionStartMemoryScope> ResolveAsync(string? cwd, TimeSpan budget, CancellationToken ct) {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        TimeSpan Remaining() {
            var value = budget - System.Diagnostics.Stopwatch.GetElapsedTime(started);
            return value > TimeSpan.Zero ? value : TimeSpan.Zero;
        }

        string? repoHash = null;
        string? machine = null;
        try {
            var path = string.IsNullOrWhiteSpace(cwd) ? Directory.GetCurrentDirectory() : cwd;
            var repo = await RepositoryDetection.DetectRepositoryAsync(path, Remaining(), detectPullRequest: false);
            if (repo?.Owner is not null && repo.RepoName is not null)
                repoHash = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
        } catch { }
        ct.ThrowIfCancellationRequested();
        if (Remaining() <= TimeSpan.Zero) throw new OperationCanceledException(ct);
        try { machine = await MachineIdProvider.GetOrCreateAsync(ct); } catch (OperationCanceledException) { throw; } catch { }
        return new SessionStartMemoryScope(repoHash, machine);
    }
}
