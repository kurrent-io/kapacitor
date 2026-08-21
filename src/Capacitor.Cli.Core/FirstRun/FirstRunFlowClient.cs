using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>One create attempt. <paramref name="StatusCode"/> 0 is a transport failure;
/// <paramref name="RetryAfter"/> is populated only on a 429, and only when the server sent one.</summary>
public sealed record FirstRunCreateOutcome(int StatusCode, FirstRunFlowResponse? Body, TimeSpan? RetryAfter = null);

/// <summary>One poll. The body is absent on every non-200, and on a 200 whose body was unreadable —
/// which <see cref="FirstRunFlowPoll.Classify"/> treats as a blip rather than as an answer.</summary>
public sealed record FirstRunPollOutcome(int StatusCode, FirstRunFlowResponse? Body);

/// <summary>The two flow routes, as a seam: the loop, the backoff and the guards around them are the
/// part worth testing, and they should not need a socket to exercise.</summary>
public interface IFirstRunFlowChannel {
    /// <summary>Creates the flow, before the browser is opened.</summary>
    Task<FirstRunCreateOutcome> CreateAsync(string serverUrl, string flowId, string? machine, CancellationToken ct);

    /// <summary>Reads a flow this caller owns.</summary>
    Task<FirstRunPollOutcome> PollAsync(string serverUrl, string flowId, CancellationToken ct);
}

/// <summary>
/// The CLI's client for the tenant's first-run flow routes.
///
/// <para>Degrades rather than throws, on <c>TenantProvisioningClient</c>'s convention: a transient
/// blip mid-poll must not crash an interactive <c>kcap setup</c>, and the loop is the right place to
/// decide what a blip means.</para>
///
/// <para><paramref name="http"/> must already carry the caller's bearer. Both routes are
/// authenticated — there is no anonymous overload of either, deliberately, because Capacitor is
/// single-tenant <i>multi-user</i> and it is the server's ownership check, not the token, that
/// decides whose flow this is.</para>
/// </summary>
public sealed class FirstRunFlowClient(HttpClient http) : IFirstRunFlowChannel {
    /// <inheritdoc/>
    public async Task<FirstRunCreateOutcome> CreateAsync(
            string serverUrl, string flowId, string? machine, CancellationToken ct) {
        var payload = JsonSerializer.Serialize(
            new CreateFirstRunFlowRequest { FlowId = flowId, Machine = machine },
            CapacitorJsonContext.Default.CreateFirstRunFlowRequest);

        try {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base(serverUrl)}/api/first-run/flows") {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode)
                return new((int)resp.StatusCode, null, resp.Headers.RetryAfter?.Delta);

            return new((int)resp.StatusCode, await ReadAsync(resp, ct));
        } catch (Exception e) when (IsTransient(e)) {
            return new(0, null);
        }
    }

    /// <inheritdoc/>
    public async Task<FirstRunPollOutcome> PollAsync(string serverUrl, string flowId, CancellationToken ct) {
        try {
            using var req  = new HttpRequestMessage(
                HttpMethod.Get, $"{Base(serverUrl)}/api/first-run/flows/{Uri.EscapeDataString(flowId)}");
            using var resp = await http.SendAsync(req, ct);

            if (!resp.IsSuccessStatusCode) return new((int)resp.StatusCode, null);

            return new((int)resp.StatusCode, await ReadAsync(resp, ct));
        } catch (Exception e) when (IsTransient(e)) {
            return new(0, null);
        }
    }

    /// <summary>Guarded separately from the send: an unreadable body must not collapse to status 0,
    /// which the loop reports as "could not reach the server" about a server that just answered.</summary>
    static async Task<FirstRunFlowResponse?> ReadAsync(HttpResponseMessage resp, CancellationToken ct) {
        try {
            return await resp.Content.ReadFromJsonAsync(CapacitorJsonContext.Default.FirstRunFlowResponse, ct);
        } catch (Exception e) when (IsTransient(e)) {
            return null;
        }
    }

    static string Base(string serverUrl) => serverUrl.TrimEnd('/');

    static bool IsTransient(Exception e) =>
        e is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException;
}
