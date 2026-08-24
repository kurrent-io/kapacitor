using Capacitor.Cli.Core.Setup;

namespace Capacitor.Tests.Helpers;

/// <summary>Scripted <see cref="ILoginShellProbe"/> shared by the app and Core test suites (the
/// probe and its consumers moved to Core; the app's shim coordinator and lifecycle controller
/// still drive it). <see cref="KcapOnPathFreshBehavior"/>, when set, answers a forceRefresh=true
/// call distinctly from the cached <see cref="KcapOnPathBehavior"/> — otherwise a forced call just
/// falls back to it; the fresh-answer seam is what the post-install re-probe tests script.</summary>
public sealed class FakeLoginShellProbe : ILoginShellProbe {
    public Func<CancellationToken, Task<string?>> TerminalPathBehavior { get; set; } = _ => Task.FromResult<string?>("/usr/bin:/bin");
    public Task<string?> TerminalPathAsync(CancellationToken ct) => TerminalPathBehavior(ct);

    public Func<CancellationToken, Task<bool?>> KcapOnPathBehavior { get; set; } = _ => Task.FromResult<bool?>(true);
    public Func<CancellationToken, Task<bool?>>? KcapOnPathFreshBehavior { get; set; }
    public int KcapOnPathForceRefreshCallCount { get; set; }
    public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) {
        if (forceRefresh) {
            KcapOnPathForceRefreshCallCount++;
            return (KcapOnPathFreshBehavior ?? KcapOnPathBehavior)(ct);
        }
        return KcapOnPathBehavior(ct);
    }

    public Func<CancellationToken, Task<string?>> KcapPathBehavior { get; set; } = _ => Task.FromResult<string?>(null);
    public Func<CancellationToken, Task<string?>>? KcapPathFreshBehavior { get; set; }
    public List<bool> KcapPathForceRefreshCalls { get; } = [];
    public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) {
        KcapPathForceRefreshCalls.Add(forceRefresh);
        return forceRefresh ? (KcapPathFreshBehavior ?? KcapPathBehavior)(ct) : KcapPathBehavior(ct);
    }
}
