using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// Mirrors Capacitor.Cli.Commands.ServiceStatusJson field-for-field; snake_case on the wire.
public sealed record ServiceSnapshot(
    string ServiceId, bool UnitPresent, string State, string? BinaryPath, string? InstallBinaryPath,
    int? JobPid, int? DaemonPid, bool TxnMarker, bool TxnActive);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ServiceSnapshot))]
public partial class KcapCliJsonContext : JsonSerializerContext;

/// Domain classification of ServiceSnapshot.State's wire string, with an explicit Unknown arm —
/// positive-evidence-only (spec §6): an unrecognized/future/typo'd value must never silently read
/// as NotInstalled and enter a positive-mutation path.
public enum ServiceState { Running, Installed, NotInstalled, Unknown }

public static class ServiceStateClassifier {
    public static ServiceState Parse(string state) => state switch {
        "running"       => ServiceState.Running,
        "installed"     => ServiceState.Installed,
        "not_installed" => ServiceState.NotInstalled,
        _               => ServiceState.Unknown,
    };
}

public enum ImportScopeChoice { Everything, Org, Repo }

public sealed record ImportRequest(ImportScopeChoice Scope, string? OrgOrRepo, IReadOnlyList<string> VendorFlags);

/// Typed facade over every CLI call the app shells out to (spec §3.1/§3.6, decision 1:
/// everything through the CLI). Consumed by the lifecycle controller and faked in tests behind
/// IProcessRunner.
public interface IKcapCli {
    string? CliPath { get; }

    /// Runs `--version --no-update-check`; null on a non-zero exit, a timeout, or a malformed
    /// version string (CliResolver.ParseVersion).
    Task<string?> VersionAsync(CancellationToken ct);

    /// Runs <c>daemon service status --name &lt;name&gt; --json</c>; null = unknown (non-zero exit, a
    /// timeout, or a parse failure) — never a fabricated snapshot.
    Task<ServiceSnapshot?> ServiceStatusAsync(CancellationToken ct);

    Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct);

    Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct);

    /// <c>daemon start -d --name &lt;name&gt;</c>, bounded + ProcessOnly-kill, stamped with a boot-attempt id
    /// for the daemon's own boot-carrier correlation — the lane always mints a fresh one per action.
    Task<ProcessResult> DetachedStartAsync(string bootAttemptId, CancellationToken ct);

    /// `plugin install` (+ vendorFlag, when non-null); null = the flagless Claude default.
    Task<ProcessResult> PluginInstallAsync(string? vendorFlag, CancellationToken ct);

    /// `import` with scope/vendor flags, streamed live via onLine — unbounded internal timeout
    /// (imports are long; ct cancellation is the only bound).
    Task<StreamingResult> ImportAsync(ImportRequest request, Action<StreamedLine> onLine, CancellationToken ct);
}

public sealed class KcapCli : IKcapCli {
    // Strictly above the CLI transaction's true worst case: 20s forward + 10s rollback reserve
    // (spec §3.4) plus up to ~10s lock-wait and ~10s crash-recovery pre-phase, plus a 5s KillWait
    // outside the 30s envelope on the manual-owner branch — ~50s worst case. 60s keeps margin
    // above that without the caller's safety-net kill racing a legitimately still-working
    // transaction.
    static readonly TimeSpan MutationTimeout = TimeSpan.FromSeconds(60);
    static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);
    // Same tier as VersionTimeout — also a read-only query — so a hung `launchctl print` can
    // never block the §3.2 per-mutation gate forever once the lifecycle controller polls this.
    static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);
    // Above MutationTimeout's 60s — bounds the wrapper without killing a still-forking detach (ProcessOnly).
    static readonly TimeSpan DetachedStartTimeout = TimeSpan.FromSeconds(75);

    // Overlay variable names every app-initiated spawn carries — the lane and KcapCliTests reference these.
    public const string SpawnNoTelemetryVar   = "KCAP_APP_SPAWN_NO_TELEMETRY";
    public const string ConsentSeedDefaultVar = "KCAP_CONSENT_SEED_DEFAULT";
    public const string ExpectServerUrlVar    = "KCAP_EXPECT_SERVER_URL";
    public const string BootAttemptVar        = "KCAP_BOOT_ATTEMPT";

    readonly IProcessRunner _runner;
    readonly string _daemonName;
    readonly string _profileName;
    // Bound once at construction (one KcapCli per owned action); null is fine until a mutation runs.
    readonly string? _canonicalServer;
    // Lazy, not a static ctor value: the terminal PATH is only known once the async probe has run,
    // and the probe itself caches — so resolving it per install call is cheap and always current.
    readonly Func<CancellationToken, Task<string?>>? _terminalPathAsync;

    public KcapCli(
            IProcessRunner runner, string? cliPath, string daemonName, string profileName,
            Func<CancellationToken, Task<string?>>? terminalPathAsync, string? canonicalServer = null) {
        _runner            = runner;
        CliPath            = cliPath;
        _daemonName        = daemonName;
        _profileName       = profileName;
        _terminalPathAsync = terminalPathAsync;
        _canonicalServer   = canonicalServer;
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

    public Task<ProcessResult> ServiceStartVerifiedAsync(CancellationToken ct) {
        var env = MutationEnv(); // throws before any spawn if the instance carries no server
        return CliPath is not { } cliPath
            ? NoCliResult()
            : Run(cliPath, ["daemon", "service", "start", "--name", _daemonName, "--verify"],
                new RunOptions(EnvOverlay: env, Timeout: MutationTimeout), ct);
    }

    // The interface's `replace` is the only per-call knob; the profile is pinned once at
    // construction (spec decision 7) and reused verbatim for both the `--profile` flag and the
    // KCAP_PROFILE overlay below, so the two can never disagree.
    public async Task<ProcessResult> ServiceInstallVerifiedAsync(bool replace, CancellationToken ct) {
        var mutation = MutationEnv(); // throws before any spawn if the instance carries no server
        if (CliPath is not { } cliPath) return await NoCliResult().ConfigureAwait(false);

        List<string> args = ["daemon", "service", "install", "--name", _daemonName, "--profile", _profileName, "--verify"];
        if (replace) args.Add("--replace");

        var env = await EnvWithTerminalPathAsync(mutation, ct).ConfigureAwait(false);
        return await Run(cliPath, args.ToArray(), new RunOptions(EnvOverlay: env, Timeout: MutationTimeout), ct).ConfigureAwait(false);
    }

    public Task<ProcessResult> DetachedStartAsync(string bootAttemptId, CancellationToken ct) {
        var env = MutationEnv(); // throws before any spawn if the instance carries no server
        env[BootAttemptVar] = bootAttemptId;

        return CliPath is not { } cliPath
            ? NoCliResult()
            : Run(cliPath, ["daemon", "start", "-d", "--name", _daemonName],
                new RunOptions(
                    EnvOverlay: env, Timeout: DetachedStartTimeout,
                    CancelMode: CancelMode.AbandonWait, TimeoutKill: TimeoutKillScope.ProcessOnly),
                ct);
    }

    // Neither this nor ImportAsync overlays MutationEnv — non-daemon shelling keeps lenient
    // classification (spec §4); the vendor flag itself is the caller's exclusive-flag choice.
    public Task<ProcessResult> PluginInstallAsync(string? vendorFlag, CancellationToken ct) {
        if (CliPath is not { } cliPath) return NoCliResult();

        List<string> args = ["plugin", "install"];
        if (vendorFlag is not null) args.Add(vendorFlag);

        return Run(cliPath, args.ToArray(), new RunOptions(EnvOverlay: Env(), Timeout: MutationTimeout), ct);
    }

    public Task<StreamingResult> ImportAsync(ImportRequest request, Action<StreamedLine> onLine, CancellationToken ct) {
        if (CliPath is not { } cliPath)
            return Task.FromResult(new StreamingResult(-1, false, [new StreamedLine(ProcessStreamKind.Stderr, "kcap CLI not found")]));

        List<string> args = ["import"];
        args.Add(request.Scope switch {
            ImportScopeChoice.Everything => "--all",
            ImportScopeChoice.Org        => "--org",
            ImportScopeChoice.Repo       => "--repo",
            _                            => throw new ArgumentOutOfRangeException(nameof(request)),
        });
        if (request.Scope != ImportScopeChoice.Everything) args.Add(request.OrgOrRepo!);
        args.Add("--yes");
        args.AddRange(request.VendorFlags);

        return _runner.RunStreamingAsync(cliPath, args.ToArray(), new RunOptions(EnvOverlay: Env()), onLine, ct);
    }

    // Deterministic exit 127 ("command not found") rather than throwing on a null CliPath — a
    // broken KCAP_APP_CLI_PATH must degrade the same way every other no-CLI case does, not crash
    // whichever lifecycle mutation happens to call it.
    static Task<ProcessResult> NoCliResult() => Task.FromResult(new ProcessResult(127, "", "kcap CLI not found", false));

    Task<ProcessResult> Run(string cliPath, string[] args, RunOptions options, CancellationToken ct) =>
        _runner.RunAsync(cliPath, args, options, ct);

    // Every child carries the pinned profile and the app-spawn telemetry-suppression marker.
    Dictionary<string, string> Env() => new() {
        ["KCAP_PROFILE"]      = _profileName,
        [SpawnNoTelemetryVar] = "1",
    };

    // Null canonicalServer here is a construction bug (mutations are action-scoped) — fail loudly, don't spawn.
    Dictionary<string, string> MutationEnv() {
        if (_canonicalServer is not { } server)
            throw new InvalidOperationException("KcapCli: mutation spawn requires a non-null canonicalServer.");

        var env = Env();
        env[ConsentSeedDefaultVar] = "prompt";
        env[ExpectServerUrlVar]    = server;
        return env;
    }

    // PATH overlaid ONLY here (spec decision 7): install is the sole unit-writing mutation, so it's
    // the only call that needs the terminal's PATH baked into the launchd unit. Start-verify
    // (bootstrap/kickstart of an already-installed unit) and every read-only query are exempt.
    // Resolved lazily against the mutation's own token, never a detached one; an unknown probe
    // result (null) leaves PATH out rather than overlaying a value that isn't actually the user's —
    // ServiceEnvironment.Capture then honestly bakes whatever the app itself inherited.
    async Task<Dictionary<string, string>> EnvWithTerminalPathAsync(Dictionary<string, string> env, CancellationToken ct) {
        var terminalPath = _terminalPathAsync is null ? null : await _terminalPathAsync(ct).ConfigureAwait(false);
        if (terminalPath is not null) env["PATH"] = terminalPath;

        return env;
    }
}
