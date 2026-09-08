using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Capacitor.App.Services.Update;

/// One feed serves stable and beta installs: a stable install never sees a prerelease entry, a
/// prerelease install sees everything. Evaluated per feed read so the installed version decides.
public sealed class PrereleaseFilteringSource(IUpdateSource inner, Func<bool> allowPrerelease) : IUpdateSource {
    public async Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) {
        var feed = await inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
        return new VelopackAssetFeed { Assets = Filter(feed.Assets, allowPrerelease()) };
    }

    public Task DownloadReleaseEntry(
            IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default) =>
        inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);

    internal static VelopackAsset[] Filter(VelopackAsset[] assets, bool allowPrerelease) =>
        allowPrerelease ? assets : assets.Where(a => !a.Version.IsPrerelease).ToArray();
}
