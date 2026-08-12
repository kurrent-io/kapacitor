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
    // Same tier as VersionTimeout — also a read-only query — so a hung `launchctl print` can
    // never block the §3.2 per-mutation gate forever once the lifecycle controller polls this.
    static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);

    readonly IProcessRunner _runner;
    readonly string _daemonName;
    readonly string _profileName;
    // Lazy, not a static ctor value: the terminal PATH is only known once the async probe has run,
    // and the probe itself caches — so resolving it per install call is cheap and always current.
    readonly Func<CancellationToken, Task<string?>>? _terminalPathAsync;

    public KcapCli(
            IProcessRunner runner, string? cliPath, string daemonName, string profileName,
            Func<CancellationToken, Task<string?>>? terminalPathAsync) {
        _runner            = runner;
        CliPath            = cliPath;
        _daemonName        = daemonName;
        _profileName       = profileName;
        _terminalPathAsync = terminalPathAsync;
    }

    public string? CliPath { get; }

    /// A no-CLI (broken KCAP_APP_CLI_PATH — CliResolver.ResolvePath returned null) result the app
    /// treats identically to "unknown"/"nothing resolved": these two read-only queries return
    /// null instead of throwing, the same degradation an unparseable or timed-out response
    /// already models.
    public async Task<string?> VersionAsync(CancellationToken ct) {
        if (CliPath is not { } cliPath) return null;

        var result = await Run(cliPath, ["--version", "--no-update-check"], new RunOptions(EnvOverlay: Env(), Timeout: VersionTimeout), ct)
            .ConfigureAwait(false);

        return result.ExitCode == 0 && !result.TimedOut ? CliResolver.ParseVersion(result.Stdout) : null;
    }

    public async Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct) {
        if (CliPath is not { } cliPath) return null;

        var result = await Run(cliPath,
                ["daemon", "service", "status", "--name", _daemonName, "--json"],
                new RunOptions(EnvOverlay: Env(), Timeout: StatusTimeout), ct)
            .ConfigureAwait(false);

        if (result.ExitCode != 0 || result.TimedOut) return null;

        try {
            return JsonSerializer.Deserialize(result.Stdout, KcapCliJsonContext.Default.ServiceSnapshot);
        } catch (JsonException) {
            return null;
        }
    }

    public Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct) =>
        CliPath is not { } cliPath
            ? NoCliResult()
            : Run(cliPath, ["daemon", "service", "start", "--name", _daemonName, "--verify"],
                new RunOptions(EnvOverlay: Env(), Timeout: MutationTimeout), ct);

    // The interface's `replace` is the only per-call knob; the profile is pinned once at
    // construction (spec decision 7) and reused verbatim for both the `--profile` flag and the
    // KCAP_PROFILE overlay below, so the two can never disagree.
    public async Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct) {
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);

        List<string> args = ["daemon", "service", "install", "--name", _daemonName, "--profile", _profileName, "--verify"];
        if (replace) args.Add("--replace");

        var env = await EnvWithTerminalPathAsync(ct).ConfigureAwait(false);
        return await Run(cliPath, args.ToArray(), new RunOptions(EnvOverlay: env, Timeout: MutationTimeout), ct).ConfigureAwait(false);
    }

    public Task<ProcessResult> DetachedStartAsync(CancellationToken ct) =>
        CliPath is not { } cliPath
            ? NoCliResult()
            : Run(cliPath, ["daemon", "start", "-d", "--name", _daemonName],
                new RunOptions(EnvOverlay: Env(), CancelMode: CancelMode.AbandonWait), ct);

    // Deterministic exit 127 ("command not found") rather than throwing on a null CliPath — a
    // broken KCAP_APP_CLI_PATH must degrade the same way every other no-CLI case does, not crash
    // whichever lifecycle mutation happens to call it.
    static Task<ProcessResult> NoCliResult() => Task.FromResult(new ProcessResult(127, "", "kcap CLI not found", false));

    Task<ProcessResult> Run(string cliPath, string[] args, RunOptions options, CancellationToken ct) =>
        _runner.RunAsync(cliPath, args, options, ct);

    // Every child carries the pinned profile.
    Dictionary<string, string> Env() => new() { ["KCAP_PROFILE"] = _profileName };

    // PATH overlaid ONLY here (spec decision 7): install is the sole unit-writing mutation, so it's
    // the only call that needs the terminal's PATH baked into the launchd unit. Start-verify
    // (bootstrap/kickstart of an already-installed unit) and every read-only query are exempt.
    // Resolved lazily against the mutation's own token, never a detached one; an unknown probe
    // result (null) leaves PATH out rather than overlaying a value that isn't actually the user's —
    // ServiceEnvironment.Capture then honestly bakes whatever the app itself inherited.
    async Task<Dictionary<string, string>> EnvWithTerminalPathAsync(CancellationToken ct) {
        var env = Env();
        var terminalPath = _terminalPathAsync is null ? null : await _terminalPathAsync(ct).ConfigureAwait(false);
        if (terminalPath is not null) env["PATH"] = terminalPath;

        return env;
    }
}
