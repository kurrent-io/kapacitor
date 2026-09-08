using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests.Readers.GitHubCli;

internal sealed class GhHarness : IDisposable {
    public readonly FakeGhProcessRunner Process = new();
    public readonly FakeTimeProvider Time = new();
    public readonly GitHubCliReaderProvider Provider;
    public readonly string? GhPath;

    public GhHarness(TempDir tmp, bool installed = true) {
        string dir = tmp.CreateDir("bin");
        if (installed) {
            GhPath = tmp.CreateFile(["bin", OperatingSystem.IsWindows() ? "gh.exe" : "gh"]);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(GhPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var runner = new GitHubCliRunner(Process, null, name => name == "PATH" ? dir : null);
        Provider = new GitHubCliReaderProvider(runner, Time);
    }

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "gh", name));

    public void SignedIn(params string[] hosts) {
        var entries = hosts.Select(host => $"\"{host}\":[{{\"state\":\"success\",\"active\":true,\"host\":\"{host}\",\"login\":\"octocat\"}}]");
        Process.When(["auth", "status"], "{\"hosts\":{" + string.Join(',', entries) + "}}");
    }

    public string[] LastArgs => Process.Calls[^1].Args;

    public void Dispose() => Provider.Dispose();
}
