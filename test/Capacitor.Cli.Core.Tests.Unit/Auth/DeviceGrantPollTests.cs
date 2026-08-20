using System.Net;
using System.Text;
using Capacitor.Cli.Core.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// AI-2052 — the two defects in the RFC 8628 poll loop, both of which shipped in the GitHub flow.
/// </summary>
public class DeviceGrantPollTests {
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // interval 0 so the loop does not really wait; the clock only has to move the deadline.
    static HttpResponseMessage DeviceCode(int expiresIn) =>
        Json($$"""{"device_code":"dc","user_code":"UC","verification_uri":"","interval":0,"expires_in":{{expiresIn}}}""");

    /// <summary>
    /// The loop was <c>while (true)</c>: a code the server had already discarded was polled forever,
    /// so a user who abandoned the browser left the CLI running until they noticed.
    /// </summary>
    [Test]
    public async Task Stops_polling_once_the_device_code_has_expired() {
        var polls = 0;

        using var handler = new Handler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) return DeviceCode(expiresIn: 2);

            polls++;

            return Json("""{"error":"authorization_pending"}""");
        });
        using var http = new HttpClient(handler);

        // Every clock read moves a second, so the 2-second deadline is reached within a few polls.
        var time = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) };

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            http, "client_id", progress: new RecordingAuthProgress(), time: time);

        await Assert.That(token).IsNull();
        await Assert.That(polls).IsLessThan(5);   // bounded, not forever
    }

    /// <summary>
    /// The response was deserialized without checking the status and force-unwrapped, so a 429 or a
    /// 5xx HTML error page was a NullReferenceException mid-sign-in rather than a backoff.
    /// </summary>
    [Test]
    public async Task Backs_off_and_keeps_polling_when_the_token_endpoint_returns_an_unreadable_body() {
        var polls = 0;

        using var handler = new Handler(request => {
            if (request.RequestUri!.AbsolutePath.Contains("device/code")) return DeviceCode(expiresIn: 900);

            polls++;

            // A gateway's HTML, not JSON — exactly what used to NRE.
            return polls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                      Content = new StringContent("<html><body>Too Many Requests</body></html>", Encoding.UTF8, "text/html")
                  }
                : Json("""{"access_token":"tok"}""");
        });
        using var http = new HttpClient(handler);

        var token = await OAuthLoginFlow.RunDeviceFlowAsync(
            http, "client_id", progress: new RecordingAuthProgress(),
            time: new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromSeconds(1) });

        await Assert.That(token).IsEqualTo("tok");
        await Assert.That(polls).IsEqualTo(2);
    }
}
