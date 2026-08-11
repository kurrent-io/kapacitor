using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capacitor.App.Services;

/// Mirrors Capacitor.Cli.Commands.ServiceStatusJson field-for-field; snake_case on the wire.
public sealed record ServiceSnapshot(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath, string? InstallBinaryPath,
    int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ServiceSnapshot))]
public partial class KcapCliJsonContext : JsonSerializerContext;

/// Typed facade over every CLI call the app shells out to (spec §3.1/§3.6, decision 1:
/// everything through the CLI). Consumed by the lifecycle controller (Task 19+) and faked in
/// tests behind IProcessRunner.
public interface IKcapCli {
    string? CliPath { get; }

    /// Runs `--version --no-update-check`; null on a non-zero exit, a timeout, or a malformed
    /// version string (CliResolver.ParseVersion).
    Task<string?> VersionAsync(CancellationToken ct);

    /// Runs `daemon service status --name <name> --json`; null = unknown (non-zero exit, a
    /// timeout, or a parse failure) — never a fabricated snapshot.
    Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct);

    Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct);

    Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct);

    /// `daemon start -d --name <name>`; AbandonWait, uncapped — a detached daemon must outlive an
    /// abandoned wait.
    Task<ProcessResult> DetachedStartAsync(CancellationToken ct);
}

public sealed class KcapCli : IKcapCli {
    // Strictly above the CLI's own 20s forward + 10s rollback reserve (spec §3.4) — the caller's
    // safety-net kill must never race the transaction's own rollback budget.
    static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(45);
    static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    readonly IProcessRunner _runner;
    readonly string _daemonName;
    readonly string _profileName;
    readonly string? _terminalPath;

    public KcapCli(IProcessRunner runner, string cliPath, string daemonName, string profileName, string? terminalPath) {
        _runner       = runner;
        CliPath       = cliPath;
        _daemonName   = daemonName;
        _profileName  = profileName;
        _terminalPath = terminalPath;
    }

    public string? CliPath { get; }

    public async Task<string?> VersionAsync(CancellationToken ct) {
        var result = await Run(["--version", "--no-update-check"], new RunOptions(EnvOverlay: Env(), Timeout: VersionTimeout), ct)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && !result.TimedOut ? CliResolver.ParseVersion(result.Stdout) : null;
    }

    public async Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct) {
        var result = await Run(
                ["daemon", "service", "status", "--name", _daemonName, "--json"],
                new RunOptions(EnvOverlay: Env()), ct)
            .ConfigureAwait(false);

        if (result.ExitCode != 0 || result.TimedOut) return null;

        try {
            return JsonSerializer.Deserialize(result.Stdout, KcapCliJsonContext.Default.ServiceSnapshot);
        } catch (JsonException) {
            return null;
        }
    }

    public Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct) =>
        Run(
            ["daemon", "service", "start", "--name", _daemonName, "--verify"],
            new RunOptions(EnvOverlay: Env(), Timeout: MutationTimeout), ct);

    // The interface's `replace` is the only per-call knob; the profile is pinned once at
    // construction (spec decision 7) and reused verbatim for both the `--profile` flag and the
    // KCAP_PROFILE overlay below, so the two can never disagree.
    public Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct) {
        List<string> args = ["daemon", "service", "install", "--name", _daemonName, "--profile", _profileName, "--verify"];
        if (replace) args.Add("--replace");

        return Run(args.ToArray(), new RunOptions(EnvOverlay: Env(), Timeout: MutationTimeout), ct);
    }

    public Task<ProcessResult> DetachedStartAsync(CancellationToken ct) =>
        Run(
            ["daemon", "start", "-d", "--name", _daemonName],
            new RunOptions(EnvOverlay: Env(), CancelMode: CancelMode.AbandonWait), ct);

    Task<ProcessResult> Run(string[] args, RunOptions options, CancellationToken ct) =>
        _runner.RunAsync(CliPath!, args, options, ct);

    // Every child carries the pinned profile; PATH only when the terminal probe knew it (spec
    // decision 7) — an unknown probe must never overlay a PATH that isn't actually the user's.
    Dictionary<string, string> Env() {
        var env = new Dictionary<string, string> { ["KCAP_PROFILE"] = _profileName };
        if (_terminalPath is not null) env["PATH"] = _terminalPath;

        return env;
    }
}
