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

    /// Assignments and summary run concurrently; the topology follows the assignments alone, so it
    /// overlaps the summary rather than queueing behind it. Every call started is awaited before
    /// anything is classified. A final 401 anywhere signs the read out: the retry handler has
    /// already spent the refresh, and only that outcome makes the source drop its client.
    public static async Task<WorkContextRead> ReadAsync(IWorkContextChannel channel, string sessionId, CancellationToken ct) {
        var summaryTask = channel.GetSessionSummaryAsync(sessionId, ct);
        var assignments = await channel.GetSessionAssignmentsAsync(sessionId, ct).ConfigureAwait(false);

        var rows    = assignments.Succeeded ? assignments.Body! : [];
        var primary = rows.FirstOrDefault(r => r.IsPrimary) ?? rows.FirstOrDefault();
        var topologyTask = primary is null ? null : channel.GetTopologyAsync(primary.WorkItemId, ct);

        var summary = await summaryTask.ConfigureAwait(false);
        var topology = topologyTask is null ? null : await topologyTask.ConfigureAwait(false);

        if (assignments.StatusCode == 401 || summary.StatusCode == 401 || topology?.StatusCode == 401)
            return WorkContextRead.Of(WorkContextReadKind.SignedOut);

        switch (assignments) {
            case { Succeeded: true }: break;
            case { StatusCode: >= 200 and < 300 }: return WorkContextRead.Of(WorkContextReadKind.Unreachable, "malformed response");
            case { StatusCode: 404 }: return WorkContextRead.Of(WorkContextReadKind.SessionUnknown);
            default:
                return PlanGated(assignments) ?? WorkContextRead.Of(WorkContextReadKind.Unreachable, StatusDetail(assignments.StatusCode));
        }

        if (topology is not null && PlanGated(topology) is { } gated) return gated;

        return new WorkContextRead(
            WorkContextReadKind.Ready, rows, primary,
            topology is { Succeeded: true } ? topology.Body : null,
            summary.Succeeded ? summary.Body : null,
            TopologyFailed: topology is { Succeeded: false },
            SummaryFailed: !summary.Succeeded,
            Detail: null);
    }

    /// Both work-item routes share the plan gate, and a plan change between the two calls must not
    /// leave the pane ready on retained data.
    static WorkContextRead? PlanGated<T>(WorkContextOutcome<T> outcome) where T : class =>
        outcome is { StatusCode: 403, Error: { Error: PlanGateError } gate }
            ? WorkContextRead.Of(WorkContextReadKind.NotInPlan, gate.Message)
            : null;

    static string StatusDetail(int status) => status == 0 ? "no response" : $"status {status}";
}
