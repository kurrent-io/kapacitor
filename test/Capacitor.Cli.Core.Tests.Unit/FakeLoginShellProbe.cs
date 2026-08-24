using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit;

/// Minimal scriptable fake of the login-shell probe, shared by the Core probe/installer tests
/// (the app's richer copy lives in the app test project — this assembly cannot see it).
internal sealed class FakeLoginShellProbe : ILoginShellProbe {
    public Func<CancellationToken, Task<string?>> TerminalPathBehavior = _ => Task.FromResult<string?>("/usr/bin:/bin");
    public Task<string?> TerminalPathAsync(CancellationToken ct) => TerminalPathBehavior(ct);

    public Func<CancellationToken, Task<bool?>> KcapOnPathBehavior = _ => Task.FromResult<bool?>(true);
    public Func<CancellationToken, Task<bool?>>? KcapOnPathFreshBehavior;
    public int KcapOnPathForceRefreshCallCount;
    public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) {
        if (forceRefresh) {
            KcapOnPathForceRefreshCallCount++;
            return (KcapOnPathFreshBehavior ?? KcapOnPathBehavior)(ct);
        }
        return KcapOnPathBehavior(ct);
    }

    public Func<CancellationToken, Task<string?>> KcapPathBehavior = _ => Task.FromResult<string?>(null);
    public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) => KcapPathBehavior(ct);
}
