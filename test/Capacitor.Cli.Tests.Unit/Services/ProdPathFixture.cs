using Capacitor.Cli.Core;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Shared HOME/lock-dir isolation for the production-path suites.</summary>
sealed class ProdPathFixture : IDisposable {
    readonly TempDir _tmp = new();
    readonly TempDaemonStore _daemons = new("prod");
    readonly TempConfigRoot _config = new("prod");
    readonly string _id;
    readonly string? _originalHome;
    readonly string _home;

    public DaemonStore Store => _daemons.Store;
    public ConfigRoot Config => _config.Root;
    public string PlistPath => LaunchdUnit.PlistPath(_id);
    public string DaemonPath { get; }

    public LaunchdServiceManager Manager { get; }

    public ProdPathFixture(string id) {
        _id = id;
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _home = _tmp.CreateDir("home");
        Environment.SetEnvironmentVariable("HOME", _home);

        DaemonPath = _daemons.CreateFile("kcap-daemon");

        Manager = new(
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
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        _config.Dispose();
        _daemons.Dispose();
        _tmp.Dispose();
    }
}
