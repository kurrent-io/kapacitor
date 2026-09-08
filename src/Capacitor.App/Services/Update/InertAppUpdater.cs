namespace Capacitor.App.Services.Update;

/// The updater outside a packed bundle, or when the Velopack adapter failed to construct.
public sealed class InertAppUpdater : IAppUpdater {
    public static readonly InertAppUpdater Instance = new();

    public bool IsAvailable => false;
    public string? InstalledVersion => null;
    public UpdateCandidate? PendingRestart => null;
    public Task<UpdateCandidate?> CheckAsync(CancellationToken ct) => Task.FromResult<UpdateCandidate?>(null);
    public Task DownloadAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken ct) => Task.CompletedTask;
    public void ApplyOnExit(UpdateCandidate candidate) { }
    public void ApplyNow(UpdateCandidate candidate) { }
}
