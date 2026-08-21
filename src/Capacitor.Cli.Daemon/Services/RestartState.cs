namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Shared flag the supervised strategy sets so <c>DaemonRunner.RunAsync</c> returns
/// <see cref="Capacitor.Cli.Core.ExitCodes.RestartRequested"/> instead of 0 after the
/// host shuts down. Registered as a singleton.
/// </summary>
public sealed class RestartState {
    public bool SupervisedRestart {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value);
    }
}
