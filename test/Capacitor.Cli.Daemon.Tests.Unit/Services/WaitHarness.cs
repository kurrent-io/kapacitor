namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Bounded polling/waiting helpers for the AgentOrchestrator suite — every wait here has a
/// deadline, so a regression fails by name instead of hanging the run.
/// </summary>
internal static class WaitHarness {
    internal static readonly TimeSpan PollBound = TimeSpan.FromSeconds(5);

    internal static readonly TimeSpan Bounded = TimeSpan.FromSeconds(30);

    internal static readonly TimeSpan AcpHangGuard = TimeSpan.FromSeconds(5);

    internal static async Task PollUntilAsync(Func<bool> condition) {
        var deadline = DateTime.UtcNow + PollBound;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(condition()).IsTrue();
    }

    internal static async Task SpinUntilAsync(Func<bool> condition, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        if (!condition()) throw new TimeoutException("Condition was not met within the timeout.");
    }

    /// <summary>Bound EVERY await a §3.3 regression could turn infinite. The pins here work by parking the
    /// lane and releasing it later in the same test, so a re-added execution await inside a handler would
    /// block before the release ever runs — and with no <c>[Timeout]</c> on these tests that is a suite hang,
    /// which is a far worse signal than a named failure. Every <c>Submit*ForTest</c> call (the seams that
    /// must return WITHOUT execution) goes through here for exactly that reason.</summary>
    internal static async Task WaitBoundedAsync(Task task, string because) {
        var finished = await Task.WhenAny(task, Task.Delay(Bounded));
        await Assert.That(finished == task).IsTrue().Because(because);
        await task;
    }
}
