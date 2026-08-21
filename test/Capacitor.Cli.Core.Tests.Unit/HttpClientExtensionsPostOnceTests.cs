using System.Net;
using System.Text;

namespace Capacitor.Cli.Core.Tests.Unit;

public class HttpClientExtensionsPostOnceTests {
    [Test]
    public async Task PostOnceAsync_returns_response_on_success() {
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK));
        using var client  = new HttpClient(handler);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var resp = await client.PostOnceAsync("http://localhost/x", content, TimeSpan.FromSeconds(1));

        await Assert.That(resp.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task PostOnceAsync_does_not_retry_on_transient_failure() {
        var attempts = 0;

        using var handler = new StubHandler(
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => {
                attempts++;

                throw new HttpRequestException("connect refused");
            })
        );
        using var client  = new HttpClient(handler);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        await Assert.That(async () =>
                await client.PostOnceAsync("http://localhost/x", content, TimeSpan.FromSeconds(1))
            )
            .Throws<HttpRequestException>();
        await Assert.That(attempts).IsEqualTo(1);
    }

    [Test]
    public async Task PostOnceAsync_respects_caller_ct_cancellation() {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var handler = new StubHandler(_ => new(HttpStatusCode.OK));
        using var client  = new HttpClient(handler);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        await Assert.That(async () =>
                await client.PostOnceAsync("http://localhost/x", content, TimeSpan.FromSeconds(1), cts.Token)
            )
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task PostOnceAsync_times_out_after_specified_duration() {
        using var handler = new StubHandler(async _ => {
                await Task.Delay(2_000);

                return new(HttpStatusCode.OK);
            }
        );
        using var client  = new HttpClient(handler);
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.That(async () =>
                await client.PostOnceAsync("http://localhost/x", content, TimeSpan.FromMilliseconds(100))
            )
            .Throws<OperationCanceledException>();
        sw.Stop();
        await Assert.That(sw.ElapsedMilliseconds).IsLessThan(1_500);
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> sync)
            : this(req => Task.FromResult(sync(req))) { }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            ct.ThrowIfCancellationRequested();
            var task = impl(request);

            await using var reg = ct.Register(() => {
                    /* propagate */
                }
            );

            return await task.WaitAsync(ct);
        }
    }
}
