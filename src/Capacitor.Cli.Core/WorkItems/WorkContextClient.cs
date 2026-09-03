using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Capacitor.Cli.Core.WorkItems;

/// The HTTP channel. <paramref name="http"/> must already carry the caller's bearer. Degrades rather
/// than throws, except for the caller's own cancellation, which propagates: turning a teardown into
/// a failed read would report an outage that never happened.
public sealed class WorkContextClient(HttpClient http, string serverUrl) : IWorkContextChannel {
    readonly string _base = serverUrl.TrimEnd('/');

    public Task<WorkContextOutcome<List<SessionWorkItemAssignmentDto>>> GetSessionAssignmentsAsync(string sessionId, CancellationToken ct) =>
        WorkContextIds.CanonicalSessionId(sessionId) is { } id
            ? GetAsync($"{_base}/api/work-items/session/{Uri.EscapeDataString(id)}", CapacitorJsonContext.Default.ListSessionWorkItemAssignmentDto, ct)
            : Task.FromResult(Refused<List<SessionWorkItemAssignmentDto>>());

    public Task<WorkContextOutcome<WorkItemTopologyDto>> GetTopologyAsync(string workItemId, CancellationToken ct) =>
        WorkContextIds.ValidWorkItemId(workItemId) is { } id
            ? GetAsync($"{_base}/api/work-items/{Uri.EscapeDataString(id)}/topology", CapacitorJsonContext.Default.WorkItemTopologyDto, ct)
            : Task.FromResult(Refused<WorkItemTopologyDto>());

    public Task<WorkContextOutcome<SessionSummaryDto>> GetSessionSummaryAsync(string sessionId, CancellationToken ct) =>
        WorkContextIds.CanonicalSessionId(sessionId) is { } id
            ? GetAsync($"{_base}/api/sessions/{Uri.EscapeDataString(id)}/summary", CapacitorJsonContext.Default.SessionSummaryDto, ct)
            : Task.FromResult(Refused<SessionSummaryDto>());

    static WorkContextOutcome<T> Refused<T>() where T : class => new(0, null, null);

    async Task<WorkContextOutcome<T>> GetAsync<T>(string url, JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class {
        try {
            using var req  = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, ct);
            var status = (int)resp.StatusCode;

            if (resp.IsSuccessStatusCode) return new(status, await ReadAsync(resp, typeInfo, ct), null);

            return new(status, null, await ReadAsync(resp, CapacitorJsonContext.Default.WorkItemErrorDto, ct));
        } catch (Exception e) when (IsTransient(e, ct)) {
            return new(0, null, null);
        }
    }

    static async Task<T?> ReadAsync<T>(HttpResponseMessage resp, JsonTypeInfo<T> typeInfo, CancellationToken ct) where T : class {
        try {
            return await resp.Content.ReadFromJsonAsync(typeInfo, ct);
        } catch (Exception e) when (IsTransient(e, ct)) {
            return null;
        }
    }

    static bool IsTransient(Exception e, CancellationToken ct) =>
        e is OperationCanceledException
            ? !ct.IsCancellationRequested
            : e is HttpRequestException or JsonException or NotSupportedException;
}
