using Capacitor.App.Services.Update;

namespace Capacitor.App.Tests.Unit;

/// Scripted IAppUpdater: every call is counted, the next check's answer is a settable field, and
/// downloads can be held on a TaskCompletionSource so a test can observe the in-flight state.
sealed class FakeAppUpdater : IAppUpdater {
    public bool IsAvailable { get; set; } = true;
    public string? InstalledVersion { get; set; } = "0.12.0-beta.2";
    public UpdateCandidate? PendingRestart { get; set; }

    public UpdateCandidate? NextCheck;
    public Exception? CheckFailure;
    public int CheckCalls;
    public int DownloadCalls;
    public TaskCompletionSource? HoldDownload;
    public readonly List<UpdateCandidate> ApplyOnExitCalls = [];
    public readonly List<UpdateCandidate> ApplyNowCalls = [];
    public Exception? ApplyNowFailure;

    public Task<UpdateCandidate?> CheckAsync(CancellationToken ct) {
        CheckCalls++;
        if (CheckFailure is { } failure) return Task.FromException<UpdateCandidate?>(failure);
        return Task.FromResult(NextCheck);
    }

    public async Task DownloadAsync(UpdateCandidate candidate, IProgress<int>? progress, CancellationToken ct) {
        DownloadCalls++;
        if (HoldDownload is { } hold) await hold.Task.WaitAsync(ct);
        PendingRestart = candidate;
    }

    public void ApplyOnExit(UpdateCandidate candidate) => ApplyOnExitCalls.Add(candidate);

    public void ApplyNow(UpdateCandidate candidate) {
        if (ApplyNowFailure is { } failure) throw failure;
        ApplyNowCalls.Add(candidate);
    }
}
