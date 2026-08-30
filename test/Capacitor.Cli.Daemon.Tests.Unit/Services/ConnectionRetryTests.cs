using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class ConnectionRetryTests {
    static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds(1);

    [Test]
    public async Task Waits_for_readiness_before_the_first_invoke() {
        var invokeCalls = 0;
        var pollCount   = 0;
        // Not ready for the first two readiness polls, then ready.
        Func<bool> isReady = () => ++pollCount > 2;

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, isReady, FastPoll, _ => { }, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        // Invoked exactly once, and only after readiness was reached — never
        // fired against a not-yet-ready connection.
        await Assert.That(invokeCalls).IsEqualTo(1);
        await Assert.That(pollCount).IsGreaterThanOrEqualTo(3);
    }

    [Test]
    public async Task Recovers_after_transient_disconnect_once_ready() {
        var invokeCalls = 0;
        Func<bool> isReady = () => true;
        var retries = new List<int>();

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new TaskCanceledException();
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, isReady, FastPoll, retries.Add, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
        await Assert.That(retries).IsEquivalentTo(new[] { 1 });
    }

    [Test]
    public async Task Cancellation_during_readiness_wait_propagates_without_invoking() {
        using var cts = new CancellationTokenSource();
        var invoked = false;

        Func<Task<string>> invoke = () => { invoked = true; return Task.FromResult("nope"); };
        // Never ready, and cancel the token the first time readiness is polled.
        Func<bool> isReady = () => { cts.Cancel(); return false; };

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, isReady, FastPoll, _ => { }, cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(invoked).IsFalse();
    }

    [Test]
    public async Task Cancellation_after_transient_failure_propagates() {
        using var cts = new CancellationTokenSource();
        var retries = 0;

        Func<Task<string>> invoke = () => throw new TaskCanceledException();
        Func<bool>         isReady = () => true; // ready, so attempt 1 invokes immediately
        Action<int>        onRetry = _ => { retries++; cts.Cancel(); };

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, isReady, FastPoll, onRetry, cts.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(retries).IsEqualTo(1);
    }

    [Test]
    public async Task HubException_is_not_retried() {
        var retries = 0;

        Func<Task<string>> invoke = () => throw new HubException("server rejected");

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, () => true, FastPoll, _ => retries++, CancellationToken.None))
            .Throws<HubException>();

        await Assert.That(retries).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidOperationException_is_treated_as_transient_and_retried() {
        var invokeCalls = 0;

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new InvalidOperationException("connection is not active");
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, () => true, FastPoll, _ => { }, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
    }

    [Test]
    public async Task Transient_failure_while_already_ready_retries_without_hanging() {
        var invokeCalls = 0;

        Func<Task<string>> invoke = () => {
            invokeCalls++;
            if (invokeCalls == 1) throw new TaskCanceledException();
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, () => true, FastPoll, _ => { }, CancellationToken.None);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(invokeCalls).IsEqualTo(2);
    }

    // a HubException matching the retriable-server-error predicate is retried up to a
    // BOUND (unlike transient disconnects, which retry until the daemon shuts down). Used for the
    // "Caller is not the daemon owning session" error that can appear briefly after a reconnect
    // before per-agent re-registration restores ownership — retrying past that window avoids a
    // spurious deny, while the bound still lets a genuinely-permanent ownership error surface.
    static bool IsOwnershipError(Exception ex) =>
        ex is HubException he && he.Message.Contains("owning session", StringComparison.Ordinal);

    [Test]
    public async Task Retriable_server_error_is_retried_up_to_the_bound_then_succeeds() {
        var calls = 0;

        Func<Task<string>> invoke = () => {
            calls++;
            if (calls <= 2) throw new HubException("Caller is not the daemon owning session abc");
            return Task.FromResult("decision");
        };

        var result = await ConnectionRetry.InvokeWithConnectionRetryAsync(
            invoke, () => true, FastPoll, _ => { }, CancellationToken.None,
            isRetriableServerError: IsOwnershipError, maxServerErrorRetries: 5);

        await Assert.That(result).IsEqualTo("decision");
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    public async Task Retriable_server_error_propagates_after_exhausting_the_bound() {
        var calls = 0;

        Func<Task<string>> invoke = () => {
            calls++;
            throw new HubException("Caller is not the daemon owning session abc");
        };

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, () => true, FastPoll, _ => { }, CancellationToken.None,
                isRetriableServerError: IsOwnershipError, maxServerErrorRetries: 3))
            .Throws<HubException>();

        // initial attempt + 3 bounded retries
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    public async Task Server_error_not_matching_predicate_is_not_retried() {
        var calls = 0;

        Func<Task<string>> invoke = () => {
            calls++;
            throw new HubException("some unrelated server error");
        };

        await Assert.That(async () => await ConnectionRetry.InvokeWithConnectionRetryAsync(
                invoke, () => true, FastPoll, _ => { }, CancellationToken.None,
                isRetriableServerError: IsOwnershipError, maxServerErrorRetries: 3))
            .Throws<HubException>();

        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task An_abandonment_exception_propagates_on_the_first_attempt_without_cancellation() {
        var attempts = 0;
        await Assert.ThrowsAsync<PermissionRequestAbandonedException>(async () =>
            await ConnectionRetry.InvokeWithConnectionRetryAsync<string>(
                () => { attempts++; throw new PermissionRequestAbandonedException(); },
                isReady: () => true, TimeSpan.FromMilliseconds(1), _ => { }, CancellationToken.None));
        await Assert.That(attempts).IsEqualTo(1);
    }
}
