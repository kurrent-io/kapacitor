using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// POST /api/first-run/flows. <c>machine</c> is display material — the browser shows it so a user
/// driving the flow from a different box can tell which machine is being configured — and the server
/// truncates it for exactly that reason: it is not identity.
/// </summary>
public sealed record CreateFirstRunFlowRequest {
    /// <summary>The id this CLI generated, which the browser is then sent to at <c>/setup?s=</c>.</summary>
    [JsonPropertyName("flow_id")] public required string FlowId { get; init; }

    /// <summary>This machine's name, for the browser to show. Not identity.</summary>
    [JsonPropertyName("machine")] public string? Machine { get; init; }
}

/// <summary>
/// What the server says about a flow this CLI owns, on both the create and the poll.
///
/// <para><b>Outcomes, never instructions.</b> Nothing here is acted on as configuration:
/// <c>kcap setup</c> writes Claude Code hooks and a hook entry is a command string Claude Code runs,
/// so a server-supplied command, path or file body must never reach one. The wire carries which steps
/// happened and how they ended, and the CLI composes everything else from values it already knows
/// locally. <see cref="FirstRunFlowOutcomes"/> is where that boundary is enforced: the strings below
/// are mapped onto closed local sets and an unrecognised member is dropped rather than forwarded.</para>
/// </summary>
public sealed record FirstRunFlowResponse {
    /// <summary>Echoed back. Compared against the id that was sent, since a flow other than the one
    /// asked for is not an answer to the question.</summary>
    [JsonPropertyName("flow_id")] public string FlowId { get; init; } = "";

    /// <summary>The machine tag as the server stored it, truncated.</summary>
    [JsonPropertyName("machine")] public string? Machine { get; init; }

    /// <summary>The step the browser is on, derived server-side from the outcomes below.</summary>
    [JsonPropertyName("step")] public string Step { get; init; } = "";

    /// <summary>Whether every gate has completed. Not "the flow is over" — see
    /// <see cref="FirstRunFlowOutcomes.IsFinished"/>, which needs both this and settled steps.</summary>
    [JsonPropertyName("can_finish")] public bool CanFinish { get; init; }

    /// <summary>Each step's outcome, keyed by the step's name.</summary>
    [JsonPropertyName("steps")] public Dictionary<string, string>? Steps { get; init; }
}
