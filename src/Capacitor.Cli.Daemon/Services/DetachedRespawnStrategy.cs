using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Detached: spawn a fresh detached daemon from the (now-updated) on-disk binary with
/// the same argv plus --await-lock (so it waits out our flock), then shut ourselves down.
/// The successor's flock acquire succeeds once our cleanup releases it.
/// </summary>
internal sealed partial class DetachedRespawnStrategy(
        DaemonConfig config, IHostApplicationLifetime lifetime, ILogger<DetachedRespawnStrategy> logger
    ) : IRestartStrategy {

    /// <summary>Pure: original argv + "--await-lock" (idempotent).</summary>
    public static string[] BuildChildArgs(IReadOnlyList<string> originalArgs) =>
        originalArgs.Contains("--await-lock") ? [.. originalArgs] : [.. originalArgs, "--await-lock"];

    /// <summary>
    /// Pure: the boot-carrier env to re-apply on a self-respawned successor's <see cref="ProcessStartInfo"/>.
    /// Re-injects <see cref="DaemonRunner.BootCarriers.Seed"/>/<see cref="DaemonRunner.BootCarriers.Expect"/>
    /// (the successor needs the same consent-seed directive and expected server URL its predecessor was
    /// booted with) but deliberately NOT <see cref="DaemonRunner.BootCarriers.Attempt"/> — an attempt id
    /// is per launch-ACTION, and a self-respawn-after-update is not the app's own action.
    /// </summary>
    internal static Dictionary<string, string> SuccessorEnvOverlay(DaemonConfig config) {
        var env = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(config.ConsentSeedDirective)) env[DaemonRunner.BootCarriers.Seed] = config.ConsentSeedDirective;
        if (!string.IsNullOrEmpty(config.ExpectedServerUrl))    env[DaemonRunner.BootCarriers.Expect] = config.ExpectedServerUrl;

        return env;
    }

    public RestartOutcome Restart() {
        var exe = Environment.ProcessPath;
        if (exe is null) { LogNoProcessPath(logger); return RestartOutcome.Retry; }

        var psi = new ProcessStartInfo {
            FileName               = exe,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var a in BuildChildArgs(config.OriginalArgs)) psi.ArgumentList.Add(a);
        foreach (var (k, v) in SuccessorEnvOverlay(config)) psi.Environment[k] = v;

        try {
            var child = Process.Start(psi)!;
            child.StandardInput.Close();
            child.StandardOutput.Close();
            child.StandardError.Close();
            LogRespawned(logger, child.Id);
        } catch (Exception ex) {
            // Spawn failed — do NOT shut down (that would leave no daemon at all).
            // Report Retry so the coordinator keeps the request pending and tries again.
            LogRespawnFailed(logger, ex);
            return RestartOutcome.Retry;
        }

        lifetime.StopApplication();

        return RestartOutcome.Initiated;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restart-after-update: Environment.ProcessPath is null; cannot self-respawn")]
    static partial void LogNoProcessPath(ILogger logger);
    [LoggerMessage(Level = LogLevel.Information, Message = "Restart-after-update: respawned detached successor (PID {Pid}); shutting down")]
    static partial void LogRespawned(ILogger logger, int pid);
    [LoggerMessage(Level = LogLevel.Error, Message = "Restart-after-update: self-respawn failed; staying up")]
    static partial void LogRespawnFailed(ILogger logger, Exception ex);
}
