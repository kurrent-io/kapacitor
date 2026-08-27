using System.Diagnostics;
using System.Net;

namespace Capacitor.Cli.Core.Tests.Unit;

public class HttpClientExtensionsRetryTests {
    [Test]
    public async Task SendWithRetry_converts_per_attempt_timeout_to_HttpRequestException_after_budget_exhausted() {
        // Simulates the original bug: a slow server holds the request open past the
        // per-attempt cap. The retry loop must surface the failure as
        // HttpRequestException so the import's `catch (HttpRequestException)` blocks
        // can degrade gracefully instead of crashing with TaskCanceledException.
        var attempts = 0;

        async Task<HttpResponseMessage> Send(CancellationToken token) {
            Interlocked.Increment(ref attempts);
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK); // unreachable — per-attempt CTS fires first.
        }

        // Small per-attempt cap under a modest total: retries accrue fast, so a scheduler stall
        // can't exhaust the budget before a second attempt — `attempts > 1` stays robust and the
        // test still finishes in ~1.5s.
        var ex = await Assert.That(async () => await HttpClientExtensions.SendWithRetryAsync(
                    Send,
                    totalTimeout: TimeSpan.FromMilliseconds(1_500),
                    perAttemptTimeout: TimeSpan.FromMilliseconds(50),
                    ct: CancellationToken.None
                )
            )
            .Throws<HttpRequestException>();

        await Assert.That(ex!.InnerException).IsTypeOf<TaskCanceledException>();
        await Assert.That(attempts).IsGreaterThan(1);
    }

    [Test]
    public async Task SendWithRetry_enforces_total_timeout_even_when_per_attempt_is_larger() {
        // Regression for Qodo finding: with total < per-attempt, a hung request
        // must not block past totalTimeout. The implementation caps each attempt
        // at min(perAttemptTimeout, remainingBudget) instead of always using the
        // full per-attempt cap.
        static async Task<HttpResponseMessage> Send(CancellationToken token) {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK); // unreachable
        }

        var sw = Stopwatch.StartNew();

        await Assert.That(async () => await HttpClientExtensions.SendWithRetryAsync(
                    Send,
                    totalTimeout: TimeSpan.FromMilliseconds(300),
                    perAttemptTimeout: TimeSpan.FromSeconds(30),
                    ct: CancellationToken.None
                )
            )
            .Throws<HttpRequestException>();

        sw.Stop();
        // Bound only needs to prove the 300ms total — not the 30s per-attempt cap — governs the
        // wall clock; 5s stays far below 30s with headroom for CI scheduling jitter.
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5_000);
    }

    [Test]
    public async Task SendWithRetry_retries_after_first_attempt_times_out_then_succeeds() {
        var attempts = 0;

        async Task<HttpResponseMessage> Send(CancellationToken token) {
            var attempt = Interlocked.Increment(ref attempts);

            if (attempt == 1) {
                // First attempt: hang until the per-attempt CTS cancels it.
                await Task.Delay(Timeout.Infinite, token);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send,
            totalTimeout: TimeSpan.FromSeconds(5),
            perAttemptTimeout: TimeSpan.FromMilliseconds(150),
            ct: CancellationToken.None
        );

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task SendWithRetry_propagates_caller_cancellation_without_converting_to_HttpRequestException() {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        static Task<HttpResponseMessage> Send(CancellationToken token) {
            token.ThrowIfCancellationRequested();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        await Assert.That(async () => await HttpClientExtensions.SendWithRetryAsync(
                    Send,
                    totalTimeout: TimeSpan.FromSeconds(1),
                    perAttemptTimeout: TimeSpan.FromSeconds(1),
                    ct: cts.Token
                )
            )
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task SendWithRetry_retries_transient_HttpRequestException_within_total_timeout() {
        var attempts = 0;

        Task<HttpResponseMessage> Send(CancellationToken token) {
            var attempt = Interlocked.Increment(ref attempts);

            return attempt < 3
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("connect refused"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send,
            totalTimeout: TimeSpan.FromSeconds(5),
            perAttemptTimeout: TimeSpan.FromSeconds(1),
            ct: CancellationToken.None
        );

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(3);
    }

    // ---- Retryable statuses. Opt-in: a lost transcript upload is counted and shown to someone, and a
    // count that mixes one unlucky 503 with a persistently stuck server is not worth acting on.

    [Test]
    [Arguments(HttpStatusCode.RequestTimeout)]
    [Arguments(HttpStatusCode.TooManyRequests)]
    [Arguments(HttpStatusCode.InternalServerError)]
    [Arguments(HttpStatusCode.BadGateway)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    [Arguments(HttpStatusCode.GatewayTimeout)]
    public async Task A_retryable_status_gets_a_second_attempt(HttpStatusCode first) {
        var attempts = 0;

        Task<HttpResponseMessage> Send(CancellationToken token) {
            var attempt = Interlocked.Increment(ref attempts);

            return Task.FromResult(new HttpResponseMessage(attempt == 1 ? first : HttpStatusCode.OK));
        }

        using var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromSeconds(5), CancellationToken.None, retryStatuses: true);

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest)]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.Forbidden)]
    [Arguments(HttpStatusCode.NotFound)]
    [Arguments(HttpStatusCode.Conflict)]
    [Arguments(HttpStatusCode.RequestEntityTooLarge)]
    public async Task A_refusal_the_server_meant_is_not_retried(HttpStatusCode status) {
        // Retrying a 4xx spends the budget re-asking a question already answered — and on a 413 or a
        // 401 it would re-send a body the server will refuse identically every time.
        var attempts = 0;

        Task<HttpResponseMessage> Send(CancellationToken token) {
            Interlocked.Increment(ref attempts);

            return Task.FromResult(new HttpResponseMessage(status));
        }

        using var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromSeconds(5), CancellationToken.None, retryStatuses: true);

        await Assert.That(resp.StatusCode).IsEqualTo(status);
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task Status_retry_is_off_unless_asked_for() {
        // The whole reason this is a flag: every hook, watch, daemon and MCP path shares this helper,
        // and their budgets are shaped around a single attempt.
        var attempts = 0;

        Task<HttpResponseMessage> Send(CancellationToken token) {
            Interlocked.Increment(ref attempts);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        using var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task An_exhausted_budget_returns_the_status_the_server_sent_rather_than_throwing() {
        // The caller counts a session failed off the status. Throwing here would surface a transport
        // error for a server that answered every time, and the call sites catch only
        // HttpRequestException — so a 503 would become an unhandled crash mid-import.
        var attempts = 0;

        Task<HttpResponseMessage> Send(CancellationToken token) {
            Interlocked.Increment(ref attempts);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        using var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromMilliseconds(600), CancellationToken.None, retryStatuses: true);

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(attempts).IsGreaterThan(1).Because("it did try again before giving up");
    }

    [Test]
    public async Task A_retry_after_longer_than_the_backoff_is_honoured() {
        // The server is the one party that knows when it will be ready; backing off less than it asked
        // is what earns the next 429.
        var attempts = 0;

        Task<HttpResponseMessage> Send(CancellationToken token) {
            var attempt = Interlocked.Increment(ref attempts);

            if (attempt > 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var refused = new HttpResponseMessage(HttpStatusCode.TooManyRequests);

            refused.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(900));

            return Task.FromResult(refused);
        }

        var sw = Stopwatch.StartNew();

        using var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromSeconds(10), CancellationToken.None, retryStatuses: true);

        sw.Stop();

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(sw.ElapsedMilliseconds).IsGreaterThanOrEqualTo(800)
                    .Because("the 250ms first backoff alone would have retried far sooner");
    }

    [Test]
    public async Task A_retry_after_never_outlives_the_budget() {
        // A server asking for an hour must not stretch an import by an hour. The wait is capped by what
        // is left, and the refusal is then returned.
        static Task<HttpResponseMessage> Send(CancellationToken token) {
            var refused = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            refused.Headers.RetryAfter = new(TimeSpan.FromHours(1));

            return Task.FromResult(refused);
        }

        var sw = Stopwatch.StartNew();

        using var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromMilliseconds(400), CancellationToken.None, retryStatuses: true);

        sw.Stop();

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(5_000);
    }

    [Test]
    public async Task A_caller_cancel_during_status_retry_is_cancellation_and_not_a_refusal() {
        using var cts      = new CancellationTokenSource();
        var       attempts = 0;

        Task<HttpResponseMessage> Send(CancellationToken token) {
            if (Interlocked.Increment(ref attempts) == 1) cts.Cancel();

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }

        await Assert.That(async () => await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromSeconds(5), cts.Token, retryStatuses: true))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task A_refusal_in_hand_outranks_a_transport_fault_that_lands_after_the_budget() {
        // The server DID answer. Surfacing a transport error instead would report "could not reach the
        // server" about one that replied to every attempt but the last.
        var attempts = 0;

        async Task<HttpResponseMessage> Send(CancellationToken token) {
            if (Interlocked.Increment(ref attempts) == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

            // Deliberately past the budget and deliberately ignoring the token, so the throw lands in
            // the UNGUARDED transport catch rather than the within-budget one that loops.
            await Task.Delay(600, CancellationToken.None);

            throw new HttpRequestException("connection reset");
        }

        using var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromMilliseconds(400), CancellationToken.None, retryStatuses: true);

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(attempts).IsEqualTo(2);
    }

    [Test]
    public async Task A_returned_refusal_is_not_disposed_on_the_way_out() {
        // It is handed to the caller, who owns it — reading its status must not throw.
        static Task<HttpResponseMessage> Send(CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));

        using var resp = await HttpClientExtensions.SendWithRetryAsync(
            Send, TimeSpan.FromMilliseconds(400), CancellationToken.None, retryStatuses: true);

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.BadGateway);
        await Assert.That(await resp.Content.ReadAsStringAsync()).IsEqualTo("");
    }
}
