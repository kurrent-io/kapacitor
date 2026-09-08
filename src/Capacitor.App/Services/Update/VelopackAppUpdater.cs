using Velopack;
using Velopack.Sources;

namespace Capacitor.App.Services.Update;

/// IAppUpdater over Velopack's UpdateManager. The prerelease rule reads the installed version at
/// feed time, so a beta install follows betas and a stable install does not.
public sealed class VelopackAppUpdater : IAppUpdater {
    readonly UpdateManager _manager;
    UpdateInfo? _lastCheck;

    public VelopackAppUpdater(Func<string, string?> getEnv) {
        var source = new PrereleaseFilteringSource(
            new SimpleWebSource(UpdateFeed.Resolve(getEnv)),
            () => _manager?.CurrentVersion?.IsPrerelease == true);
        _manager = new UpdateManager(source);
    }

    public bool IsAvailable => _manager.IsInstalled;
    public string? InstalledVersion => _manager.CurrentVersion?.ToString();

    public UpdateCandidate? PendingRestart =>
        _manager.UpdatePendingRestart is { } asset ? new UpdateCandidate(asset.Version.ToString(), asset.Version.IsPrerelease) : null;

    public async Task<UpdateCandidate?> CheckAsync(CancellationToken ct) {
        var info = await _manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        _lastCheck = info;
        if (info is null) return null;
        var target = info.TargetFullRelease.Version;
        return new UpdateCandidate(target.ToString(), target.IsPrerelease);
    }

    public Task DownloadAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken ct) {
        var info = _lastCheck;
        if (info is null || info.TargetFullRelease.Version.ToString() != candidate.Version)
            throw new InvalidOperationException($"No check offered {candidate.Version}; check before downloading.");
        return _manager.DownloadUpdatesAsync(info, progress is null ? null : p => progress.Report(p), ct);
    }

    public void ApplyOnExit(UpdateCandidate candidate) =>
        _manager.WaitExitThenApplyUpdates(AssetFor(candidate), silent: true, restart: true);

    public void ApplyNow(UpdateCandidate candidate) =>
        _manager.ApplyUpdatesAndRestart(AssetFor(candidate));

    VelopackAsset AssetFor(UpdateCandidate candidate) {
        if (_manager.UpdatePendingRestart is { } pending && pending.Version.ToString() == candidate.Version) return pending;
        if (_lastCheck is { } info && info.TargetFullRelease.Version.ToString() == candidate.Version) return info.TargetFullRelease;
        throw new InvalidOperationException($"No downloaded package for {candidate.Version}.");
    }
}
