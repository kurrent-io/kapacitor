using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.FirstRun;

/// <summary>
/// One harness as this machine found it. <b>Both detection signals travel, never one <c>detected</c>
/// flag</b> — the screen names the signal it saw, and the two do not mean the same thing per vendor:
/// Claude and Codex are probed on PATH with no marker checked, Cursor the reverse.
/// </summary>
public sealed record FirstRunHarnessReport {
    /// <summary>The harness's binary resolved on this process's PATH.</summary>
    [JsonPropertyName("binary_on_path")] public required bool BinaryOnPath { get; init; }

    /// <summary>Its own config directory or install marker exists.</summary>
    [JsonPropertyName("config_found")] public required bool ConfigFound { get; init; }

    /// <summary>kcap is already installed into it — the same wired-check <c>kcap status</c> uses.</summary>
    [JsonPropertyName("already_wired")] public required bool AlreadyWired { get; init; }
}

/// <summary>
/// POST /api/first-run/flows. <c>machine</c> is display material — the browser shows it so a user
/// driving the flow from a different box can tell which machine is being configured — and the server
/// truncates it for exactly that reason: it is not identity.
///
/// <para>The machine block rides the create because it is the only lane for it, and because the Agents
/// screen must find its rows already populated rather than waiting on a second round trip.</para>
/// </summary>
public sealed record CreateFirstRunFlowRequest {
    /// <summary>The id this CLI generated, which the browser is then sent to at <c>/setup?s=</c>.</summary>
    [JsonPropertyName("flow_id")] public required string FlowId { get; init; }

    /// <summary>This machine's name, for the browser to show. Not identity.</summary>
    [JsonPropertyName("machine")] public string? Machine { get; init; }

    /// <summary>This machine's id, so one machine's reports correlate. Distinct from
    /// <see cref="Machine"/>, which is the display tag.</summary>
    [JsonPropertyName("machine_id")] public string? MachineId { get; init; }

    /// <summary>Per vendor, keyed by canonical vendor id. <b>A vendor absent from the map is unknown,
    /// never not-installed</b> — key absence is the only way this shape says "we did not look", and the
    /// server renders a reported-absent vendor in its not-found list.</summary>
    [JsonPropertyName("harnesses")] public Dictionary<string, FirstRunHarnessReport>? Harnesses { get; init; }

    /// <summary>Vendors already declined locally, so a <c>kcap harness dismiss</c> is not silently
    /// reversed by a screen that ticks everything.</summary>
    [JsonPropertyName("declined")] public List<string>? Declined { get; init; }

    /// <summary>
    /// Whether this machine's <i>login</i> shell resolves the CLI, or null when nothing probed.
    ///
    /// <para><b>Null is not false.</b> Several harnesses shell out to the CLI, so a login shell that
    /// cannot find it installs hooks that run and record nothing — which is why the server draws its
    /// one error state from an explicit false. Rendering that alarm from an absent probe would invent
    /// it.</para>
    /// </summary>
    [JsonPropertyName("login_shell_finds_cli")] public bool? LoginShellFindsCli { get; init; }
}

/// <summary>One harness the user turned something on for, as the wire carries it.</summary>
public sealed record FirstRunAgentChoiceResponse {
    [JsonPropertyName("vendor")] public string Vendor { get; init; } = "";
    [JsonPropertyName("record")] public bool   Record { get; init; }
    [JsonPropertyName("tools")]  public bool   Tools  { get; init; }
}

/// <summary>
/// What the server says about a flow this CLI owns, on both the create and the poll.
///
/// <para><b>Values, never paths, file bodies or command strings.</b> <see cref="Agents"/> is a
/// configuration push and the CLI acts on it, so the rule is about what may cross rather than whether
/// anything does: <c>kcap setup</c> writes Claude Code hooks and a hook entry is a command string
/// Claude Code runs. What travels is vendor keys and booleans; the CLI composes every file body itself
/// from things it already knows locally, and rejects an enumeration member it does not recognise
/// rather than forwarding one a newer server invented. <see cref="FirstRunFlowOutcomes"/> is where that
/// boundary is enforced.</para>
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

    /// <summary>
    /// The Agents step's choice, once the user has made one. <b>Null is "not yet answered"; empty is
    /// "Not now".</b> A CLI that treats the two alike either installs nothing on a flow that has not
    /// been asked, or waits forever on a decline.
    /// </summary>
    [JsonPropertyName("agents")] public List<FirstRunAgentChoiceResponse>? Agents { get; init; }

    /// <summary>
    /// When that choice was made, on the server's clock. Null exactly when <see cref="Agents"/> is.
    ///
    /// <para>The server's identity for the decision: it advances when the answer changes and not when
    /// it is merely re-confirmed. <b>This CLI does not use it as a cursor</b> — it applies what it last
    /// polled, once, and never re-checks. Present because its absence is what makes a decision
    /// unreadable (see <see cref="FirstRunFlowOutcomes.Agents(FirstRunFlowResponse?)"/>), not because
    /// anything here compares it.</para>
    /// </summary>
    [JsonPropertyName("agents_decided_at")] public DateTimeOffset? AgentsDecidedAt { get; init; }
}
