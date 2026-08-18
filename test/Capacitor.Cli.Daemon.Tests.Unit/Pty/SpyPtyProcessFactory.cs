using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

sealed class SpyPtyProcessFactory(IPtyProcess? process = null) : IPtyProcessFactory {
    public int                         SpawnCalls  { get; private set; }
    public string?                     LastCommand { get; private set; }
    public string[]?                   LastArgs    { get; private set; }
    public Dictionary<string, string>? LastEnv     { get; private set; }

    public IPtyProcess Spawn(
            string                      command,
            string[]                    args,
            string                      cwd,
            Dictionary<string, string>? extraEnv = null,
            ushort                      cols     = 120,
            ushort                      rows     = 40
        ) {
        SpawnCalls++;
        LastCommand = command;
        LastArgs    = args;
        LastEnv     = extraEnv;

        return process ?? new StubPtyProcess();
    }
}
