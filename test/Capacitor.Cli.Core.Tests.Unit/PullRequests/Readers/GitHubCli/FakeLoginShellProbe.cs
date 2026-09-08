using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

internal sealed class FakeLoginShellProbe(string? terminalPath) : ILoginShellProbe {
    public int Probes;
    public Task<string?> TerminalPathAsync(CancellationToken ct) { Probes++; return Task.FromResult(terminalPath); }
    public Task<bool?> KcapOnPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult<bool?>(null);
    public Task<string?> KcapPathAsync(CancellationToken ct, bool forceRefresh = false) => Task.FromResult<string?>(null);
}
