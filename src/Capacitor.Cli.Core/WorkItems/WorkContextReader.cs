namespace Capacitor.Cli.Core.WorkItems;

public enum WorkContextReadKind { Ready, SessionUnknown, SignedOut, NotInPlan, Unreachable }

/// One read of a session's work context, totalized. <see cref="WorkContextReadKind.Ready"/> with a
/// null <see cref="Primary"/> is the no-work-item state; <see cref="TopologyFailed"/> and
/// <see cref="SummaryFailed"/> degrade a section without failing the read.
public sealed record WorkContextRead(
        WorkContextReadKind                         Kind,
        IReadOnlyList<SessionWorkItemAssignmentDto> Assignments,
        SessionWorkItemAssignmentDto?               Primary,
        WorkItemTopologyDto?                        Topology,
        SessionSummaryDto?                          Summary,
        bool                                        TopologyFailed,
        bool                                        SummaryFailed,
        string?                                     Detail) {
    public static WorkContextRead Of(WorkContextReadKind kind, string? detail = null) =>
        new(kind, [], null, null, null, false, false, detail);
}

public static class WorkContextReader {
    public const string PlanGateError = "work_items_not_in_plan";

    /// Assignments and summary run concurrently and are both awaited before anything is
    /// classified. A final 401 anywhere signs the read out: the retry handler has already spent
    /// the refresh, and only that outcome makes the source drop its client.
    public static async Task<WorkContextRead> ReadAsync(IWorkContextChannel channel, string sessionId, CancellationToken ct) {
        var assignmentsTask = channel.GetSessionAssignmentsAsync(sessionId, ct);
        var summaryTask     = channel.GetSessionSummaryAsync(sessionId, ct);
        await Task.WhenAll(assignmentsTask, summaryTask).ConfigureAwait(false);
        var assignments = assignmentsTask.Result;
        var summary     = summaryTask.Result;

        if (assignments.StatusCode == 401 || summary.StatusCode == 401) return WorkContextRead.Of(WorkContextReadKind.SignedOut);

        switch (assignments) {
            case { Succeeded: true }: break;
            case { StatusCode: >= 200 and < 300 }: return WorkContextRead.Of(WorkContextReadKind.Unreachable, "malformed response");
            case { StatusCode: 404 }: return WorkContextRead.Of(WorkContextReadKind.SessionUnknown);
            case { StatusCode: 403, Error: { Error: PlanGateError } gate }: return WorkContextRead.Of(WorkContextReadKind.NotInPlan, gate.Message);
            default: return WorkContextRead.Of(WorkContextReadKind.Unreachable, StatusDetail(assignments.StatusCode));
        }

        var rows    = assignments.Body!;
        var primary = rows.FirstOrDefault(r => r.IsPrimary) ?? rows.FirstOrDefault();

        WorkItemTopologyDto? topology = null;
        var topologyFailed = false;
        if (primary is not null) {
            var outcome = await channel.GetTopologyAsync(primary.WorkItemId, ct).ConfigureAwait(false);
            if (outcome.StatusCode == 401) return WorkContextRead.Of(WorkContextReadKind.SignedOut);
            if (outcome is { StatusCode: 403, Error: { Error: PlanGateError } gate }) return WorkContextRead.Of(WorkContextReadKind.NotInPlan, gate.Message);
            if (outcome.Succeeded) topology = outcome.Body;
            else topologyFailed = true;
        }

        return new WorkContextRead(
            WorkContextReadKind.Ready, rows, primary, topology,
            summary.Succeeded ? summary.Body : null,
            topologyFailed, !summary.Succeeded, null);
    }

    static string StatusDetail(int status) => status == 0 ? "no response" : $"status {status}";
}
