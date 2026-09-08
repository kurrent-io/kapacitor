using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Fingerprint of an installed vendor CLI: the file that actually runs once every symlink
/// is followed, plus its size and last-write time. The path is part of it because a vendor update
/// commonly retargets a symlink at a new version directory.</summary>
public readonly record struct CliBinaryStat(string ResolvedPath, long Size, long MtimeTicks);

/// <summary>
/// Polls the CLI binaries of the vendors this daemon advertised at startup and, when one changes
/// on disk, asks the orchestrator to re-probe and re-advertise. Without it the advertised version
/// is a startup snapshot, and the first reviewer launch after a vendor auto-update is rejected
/// for the mismatch.
/// </summary>
internal sealed partial class VendorCliWatcher : BackgroundService {
    readonly DaemonConfig?                                             _config;
    readonly IReadOnlyDictionary<string, IHostedAgentRuntimeFactory>? _factories;
    readonly ILogger                                                   _logger;

    // Seams (assigned from DI in the production ctor; overridden directly in tests).
    internal Func<string, CliBinaryStat?>            StatBinary = StatCliBinary;
    internal Action<string>                          Refresh;
    internal IReadOnlyList<(string Vendor, string CliPath)> Watched;

    readonly Dictionary<string, CliBinaryStat?> _baselines = new(StringComparer.Ordinal);

    /// <summary>Fingerprints recorded when the advertised versions were probed. The advertisement
    /// describes the binary as it was then, so a vendor that updates before this service starts
    /// must read as a change on the first tick rather than become the baseline.</summary>
    IReadOnlyDictionary<string, CliBinaryStat?>? _recorded;

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    public VendorCliWatcher(
            DaemonConfig config, AgentOrchestrator orchestrator,
            IReadOnlyDictionary<string, IHostedAgentRuntimeFactory> factories,
            ILogger<VendorCliWatcher> logger) {
        _config    = config;
        _factories = factories;
        _logger    = logger;
        Refresh    = reason => orchestrator.RefreshAdvertisedCapabilities(reason);
        Watched    = [];
    }

    VendorCliWatcher(IReadOnlyList<(string Vendor, string CliPath)> watched, Action<string> refresh,
            Func<string, CliBinaryStat?> stat, IReadOnlyDictionary<string, CliBinaryStat?>? baselines) {
        _logger    = NullLogger.Instance;
        _recorded  = baselines;
        Watched    = watched;
        Refresh    = refresh;
        StatBinary = stat;
    }

    internal static VendorCliWatcher ForTest(
            IReadOnlyList<(string Vendor, string CliPath)> watched, Action<string> refresh,
            Func<string, CliBinaryStat?> stat, IReadOnlyDictionary<string, CliBinaryStat?>? baselines = null) =>
        new(watched, refresh, stat, baselines);

    internal void PrimeBaselines() {
        foreach (var (vendor, cliPath) in Watched)
            _baselines[vendor] = _recorded is { } recorded && recorded.TryGetValue(vendor, out var baseline)
                ? baseline
                : StatBinary(cliPath);
    }

    /// <summary>One poll iteration (timer-driven; also the unit-test entry point). All vendors
    /// that changed since the last tick share one refresh, since a refresh re-probes them all.</summary>
    internal void Tick() {
        List<string>? changed = null;
        foreach (var (vendor, cliPath) in Watched) {
            var current = StatBinary(cliPath);
            if (!Changed(_baselines.GetValueOrDefault(vendor), current)) continue;
            _baselines[vendor] = current;
            (changed ??= []).Add(vendor);
        }

        if (changed is null) return;
        var reason = $"{string.Join(", ", changed)} CLI binary changed on disk";
        LogChanged(_logger, reason);
        Refresh(reason);
    }

    /// <summary>A null current fingerprint is a transient (the binary is mid-replacement), never a
    /// change: acting on it would re-probe a file that is not there.</summary>
    internal static bool Changed(CliBinaryStat? baseline, CliBinaryStat? current) =>
        current is { } c && baseline != c;

    /// <summary>Resolves a bare command on PATH, follows the symlink chain to the file that runs and
    /// stats it. Null when the binary cannot be found right now.</summary>
    internal static CliBinaryStat? StatCliBinary(string cliPath) {
        try {
            if (CliResolver.ResolveExecutable(cliPath) is not { } resolved) return null;
            var info   = new FileInfo(resolved);
            var target = info.ResolveLinkTarget(returnFinalTarget: true) as FileInfo ?? info;
            return target.Exists ? new CliBinaryStat(target.FullName, target.Length, target.LastWriteTimeUtc.Ticks) : null;
        } catch {
            return null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        if (_config is not null && _factories is not null) {
            _recorded = _config.UnattendedVendorBaselines;
            Watched   = (_config.UnattendedVendors ?? [])
                .Where(_factories.ContainsKey)
                .Select(vendor => (vendor, _factories[vendor].CliPath))
                .Where(pair => !string.IsNullOrEmpty(pair.CliPath))
                .ToArray();
        }
        PrimeBaselines();

        using var timer = new PeriodicTimer(PollInterval);
        try {
            while (await timer.WaitForNextTickAsync(ct)) Tick();
        } catch (OperationCanceledException) { /* shutdown */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Re-probing advertised vendor CLI versions: {Reason}")]
    static partial void LogChanged(ILogger logger, string reason);
}
