using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// <see cref="PendingAcpInteractionRegistry"/> is a straight copy-shape of the already-
/// tested <see cref="PendingPermissionRegistry"/> parameterized on <see cref="AcpInteractionDecision"/>
/// — these tests mirror that class's own test coverage for the early-arrival race and cancellation.
/// </summary>
public class PendingAcpInteractionRegistryTests {
    static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(5);

    [Test]
    public async Task Resolve_BeforeAwait_IsBufferedAndReturnedImmediately() {
        var registry = new PendingAcpInteractionRegistry();
        var decision = new AcpInteractionDecision("allow", null, null, null, null, null);

        registry.Resolve("req-1", decision);

        var result = await registry.AwaitDecisionAsync("req-1", CancellationToken.None).WaitAsync(HangGuard);

        await Assert.That(result.Outcome).IsEqualTo("allow");
    }

    [Test]
    public async Task AwaitDecisionAsync_CompletesWhenResolveCalledAfter() {
        var registry = new PendingAcpInteractionRegistry();
        var decision = new AcpInteractionDecision("deny", null, null, null, null, null);

        var awaitTask = registry.AwaitDecisionAsync("req-2", CancellationToken.None);
        registry.Resolve("req-2", decision);

        var result = await awaitTask.WaitAsync(HangGuard);

        await Assert.That(result.Outcome).IsEqualTo("deny");
    }

    [Test]
    public async Task AwaitDecisionAsync_CancellationTokenFires_ThrowsOperationCanceled() {
        var registry = new PendingAcpInteractionRegistry();
        using var cts = new CancellationTokenSource();

        var awaitTask = registry.AwaitDecisionAsync("req-3", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await awaitTask.WaitAsync(HangGuard));
    }
}
