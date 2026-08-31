using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Shared home/lock-dir isolation for the production-path suites.</summary>
sealed class ProdPathFixture : IDisposable {
    readonly TempDir _tmp = new();
    readonly TempDaemonStore _daemons = new("prod");
    readonly TempConfigRoot _config = new("prod");
    readonly string _id;

    public DaemonStore Store => _daemons.Store;
    public ConfigRoot Config => _config.Root;
    public UserHome Home { get; }
    public string PlistPath => LaunchdUnit.PlistPath(Home, _id);
    public string DaemonPath { get; }

    public LaunchdServiceManager Manager { get; }

    public ProdPathFixture(string id) {
        _id  = id;
        Home = new UserHome(_tmp.CreateDir("home"));

        DaemonPath = _daemons.CreateFile("kcap-daemon");

        Manager = new(
            Home,
            runProcess: (_, args) => PrintNotFound(args),
            runBounded: (_, args, _) => {
                var (code, stdout, stderr) = PrintNotFound(args);
                return (code, stdout, stderr, false);
            });
    }

    (int ExitCode, string StdOut, string StdErr) PrintNotFound(string[] args) =>
        args[0] == "print"
            ? (113, "", $"Could not find service \"{LaunchdUnit.Label(_id)}\" in domain for user gui: 501")
            : (0, "", "");

    public void Dispose() {
        _config.Dispose();
        _daemons.Dispose();
        _tmp.Dispose();
    }
}
