namespace Capacitor.Cli.Core.WorkItems;

/// One call's result. <see cref="StatusCode"/> 0 is a transport failure or an id refused before any
/// request. <see cref="Body"/> is present on a 2xx whose body parsed; <see cref="Error"/> on a 4xx
/// whose body parsed as the shared error shape.
public sealed record WorkContextOutcome<T>(int StatusCode, T? Body, WorkItemErrorDto? Error) where T : class {
    public bool Succeeded => StatusCode is >= 200 and < 300 && Body is not null;
}

public interface IWorkContextChannel {
    Task<WorkContextOutcome<List<SessionWorkItemAssignmentDto>>> GetSessionAssignmentsAsync(string sessionId, CancellationToken ct);
    Task<WorkContextOutcome<WorkItemDto>>                         GetWorkItemAsync(string workItemId, CancellationToken ct);
    Task<WorkContextOutcome<WorkItemTopologyDto>>                 GetTopologyAsync(string workItemId, CancellationToken ct);
    Task<WorkContextOutcome<SessionSummaryDto>>                   GetSessionSummaryAsync(string sessionId, CancellationToken ct);
}
