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

    /// <summary>
    /// This machine's platform, as a <see cref="FirstRunPlatforms"/> token, or null when it is none the
    /// flow names.
    ///
    /// <para><b>What it buys is an affordance the screen can honestly offer.</b> The PATH fix is
    /// macOS-only, so the screen draws its button for an explicit <c>macos</c> and nothing else — the
    /// same shape as the warning itself, which appears only for an explicit
    /// <see cref="LoginShellFindsCli"/> of false. Null and a known non-macOS platform therefore render
    /// alike, and only one of them is a claim.</para>
    /// </summary>
    [JsonPropertyName("platform")] public string? Platform { get; init; }

    /// <summary>
    /// Whether the agent daemon is installed as a service and running, or null when nothing claimable
    /// was read.
    ///
    /// <para><b>False is an offer, not just a state.</b> It means the ensure ladder has a verb it could
    /// actually run here — so null covers both an ambiguous machine and one whose ladder could only
    /// refuse, since a button whose single possible answer is no is not an offer.</para>
    /// </summary>
    [JsonPropertyName("daemon_service_enabled")] public bool? DaemonServiceEnabled { get; init; }
}

/// <summary>One thing the browser is asking this machine to do, as the poll carries it.</summary>
public sealed record FirstRunMachineActionResponse {
    [JsonPropertyName("capability")] public string Capability { get; init; } = "";

    /// <summary>The request's identity. Nullable because a payload without one cannot be reported
    /// against, so it is dropped rather than acted on.</summary>
    [JsonPropertyName("requested_at")] public DateTimeOffset? RequestedAt { get; init; }
}

/// <summary>
/// POST /api/first-run/flows/{id}/actions — what performing a request produced.
///
/// <para><b>Two closed-set tokens and the request's timestamp. No detail, and that is deliberate.</b>
/// <c>ShimEnsureJson</c> carries a <c>Detail</c> that is raw shell stderr on the failed row, and a
/// composed <c>sudo</c> line; neither crosses. Both belong in the terminal, which is printing them
/// already, and the screen's copy keys off the outcome token — so the browser needs no free text and the
/// wire carries none.</para>
/// </summary>
public sealed record ReportFirstRunMachineActionRequest {
    [JsonPropertyName("capability")]   public required string         Capability  { get; init; }
    [JsonPropertyName("requested_at")] public required DateTimeOffset RequestedAt { get; init; }
    [JsonPropertyName("outcome")]      public required string         Outcome     { get; init; }
    [JsonPropertyName("reason")]       public          string?        Reason      { get; init; }
}

/// <summary>
/// POST /api/first-run/flows/{id}/import-outcome — how the import ended, once its passes are done.
///
/// <para><b>Also the signal that it finished.</b> Without it the screen cannot tell a run still working
/// from one that stopped, so an outcome that moved nothing is still worth sending.</para>
/// </summary>
/// <remarks>
/// Bound rather than loose, unlike the discovery report: three counts are the whole record, so the
/// server refuses a request missing one instead of defaulting it to a figure nobody measured.
/// </remarks>
public sealed record ReportFirstRunImportOutcomeRequest {
    /// <summary>The decision this answers, echoed from the poll's <c>import_decided_at</c>. <b>The
    /// report's identity</b>: the server records nothing for a decision the user has since replaced, so
    /// this is the stamp of the answer that actually ran and never the standing one.</summary>
    [JsonPropertyName("decided_at")] public required DateTimeOffset DecidedAt { get; init; }

    [JsonPropertyName("imported")] public required int Imported { get; init; }
    [JsonPropertyName("skipped")]  public required int Skipped  { get; init; }
    [JsonPropertyName("failed")]   public required int Failed   { get; init; }

    /// <summary>A <see cref="FirstRunImportOutcomeReasons"/> token, and only on a run that moved
    /// nothing — the server rejects the whole report otherwise.</summary>
    [JsonPropertyName("reason")] public string? Reason { get; init; }
}

/// <summary>
/// POST /api/first-run/flows/{id}/relinquish — this machine has stopped listening.
///
/// <para>One token and nothing else. What the browser does with it is take affordances away, so there is
/// nothing for a detail field to improve: a page that cannot be written to has no use for an error
/// string, and the terminal is where anything worth reading is already being printed.</para>
/// </summary>
public sealed record RelinquishFirstRunFlowRequest {
    /// <summary>A <see cref="FirstRunRelinquishReasons"/> token. Required by the route — the two members
    /// give opposite instructions, so there is no safe default for the server to pick.</summary>
    [JsonPropertyName("reason")] public required string Reason { get; init; }
}

/// <summary>One repository discovery found, as the report carries it.</summary>
/// <remarks><c>sessions</c> is keyed by window rather than positional: an array aligned by index goes
/// wrong silently. An absent key reads as "not counted", never as zero.</remarks>
public sealed record FirstRunImportRepoReport {
    [JsonPropertyName("owner")]           public required string                     Owner         { get; init; }
    [JsonPropertyName("name")]            public required string                     Name          { get; init; }
    [JsonPropertyName("sessions")]        public required Dictionary<string, int>    Sessions      { get; init; }
    [JsonPropertyName("last_session_at")] public          DateTimeOffset?            LastSessionAt { get; init; }
}

/// <summary>
/// POST /api/first-run/flows/{id}/import — what this machine has on disk, once discovery has finished.
/// The screen renders its waiting state until this lands.
/// </summary>
/// <remarks>
/// Every figure is already scoped to <see cref="Vendors"/>: this machine holds each session's vendor
/// while it scans, so it filters its own session set. Filtering server-side would need counts per
/// repository per window per vendor.
/// </remarks>
public sealed record ReportFirstRunImportRequest {
    /// <summary>Bounded to <see cref="MaxRepos"/>, ordered by last activity so the cap keeps the
    /// repositories someone is working in.</summary>
    [JsonPropertyName("repos")] public required List<FirstRunImportRepoReport> Repos { get; init; }

    /// <summary>Sessions no repository could be attributed to, per window. <c>--all</c> includes them
    /// and any repo selection drops them, so the number is both honest and how <c>kcap remap</c> gets
    /// found.</summary>
    [JsonPropertyName("unmatched")] public required Dictionary<string, int> Unmatched { get; init; }

    /// <summary>How many repositories this machine has before the cap, so the screen can disclose
    /// the remainder rather than hiding it.</summary>
    [JsonPropertyName("repo_total")] public required int RepoTotal { get; init; }

    /// <summary>The agents scanned, in catalogue order. <b>Absent and empty differ</b>: absent means
    /// this CLI does not report agents and no vendor filter applied, empty means none were scanned
    /// because none were kept.</summary>
    [JsonPropertyName("vendors")] public List<string>? Vendors { get; init; }

    /// <summary>The server's own cap. Reaching it is disclosed through
    /// <see cref="RepoTotal"/>; a repository past it is still reachable by hand.</summary>
    public const int MaxRepos = 200;

    /// <summary>GitHub's own limits. The server DROPS an over-long identity rather than truncating
    /// it, because owner and name are what resolve back to <c>--repo owner/name</c>, so sending one
    /// costs the repository its row.</summary>
    public const int MaxOwnerLength = 39;
    public const int MaxNameLength  = 100;
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
    /// The Import step's choice, once made. <b>Null is "not yet answered"; a decision whose
    /// <see cref="FirstRunImportDecisionResponse.Repos"/> is empty is "import nothing"</b>, which is
    /// an answer.
    /// </summary>
    [JsonPropertyName("import")] public FirstRunImportDecisionResponse? Import { get; init; }

    /// <summary>When the import choice was made, on the server's clock. Null exactly when
    /// <see cref="Import"/> is, and carried rather than compared — see
    /// <see cref="AgentsDecidedAt"/>.</summary>
    [JsonPropertyName("import_decided_at")] public DateTimeOffset? ImportDecidedAt { get; init; }

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

    /// <summary>
    /// The default session visibility the same decision chose, as a canonical
    /// <c>default_visibility</c> value.
    ///
    /// <para><b>Null is not a value</b>, and it is null in two situations that are not the same: the step
    /// was answered and no audience set, and the step was never answered at all. Only the first says
    /// anything about the profile — see <c>SetupCommand.DecideVisibility</c>, which separates them by
    /// whether the step settled, because the second has told the machine nothing.</para>
    ///
    /// <para><b>A stop this build cannot name is dropped, not written.</b> The value persists in profile
    /// config and is stamped on every session afterwards, so it is mapped through
    /// <c>AppConfig.ValidVisibilities</c> and degrades to null, which leaves the profile as it was.</para>
    /// </summary>
    [JsonPropertyName("default_visibility")] public string? DefaultVisibility { get; init; }

    /// <summary>
    /// What the browser is asking this machine to do, and the one field on this response the CLI acts on
    /// rather than records. Absent or empty means nothing is outstanding.
    ///
    /// <para><b>A named capability, never an instruction.</b> The CLI resolves its own binary and composes
    /// its own command; what crosses is a token from a closed set, and one this build does not know is
    /// dropped rather than forwarded — see <see cref="FirstRunFlowOutcomes.MachineActions"/>.</para>
    ///
    /// <para><b>A list, so adding a capability is not a wire break.</b> Entries this build cannot act on
    /// are left alone: the request stays outstanding and the browser goes on saying so, which is the honest
    /// state for a CLI too old to perform it.</para>
    /// </summary>
    [JsonPropertyName("machine_actions")] public List<FirstRunMachineActionResponse>? MachineActions { get; init; }
}

/// <summary>
/// What to import and how far it travels.
///
/// <para><b>Three closed sets and a repository identity, and no dates.</b> The window crosses as its
/// key: a date computed server-side would be the server's, against a machine whose clock and timezone
/// are its own.</para>
/// </summary>
public sealed record FirstRunImportDecisionResponse {
    [JsonPropertyName("window")] public string Window { get; init; } = "";
    [JsonPropertyName("titles")] public string Titles { get; init; } = "";

    [JsonPropertyName("repos")] public List<FirstRunImportRepoChoiceResponse>? Repos { get; init; }

    /// <summary>Which agents to import from. <b>Null means no filter</b>, so it must not be read as
    /// an empty selection; empty genuinely means import from nothing.</summary>
    [JsonPropertyName("vendors")] public List<string>? Vendors { get; init; }
}

/// <summary>One repository to import. <c>level</c> is a closed set; a member this build does not know
/// is a repository to leave alone, never one to guess at.</summary>
public sealed record FirstRunImportRepoChoiceResponse {
    [JsonPropertyName("owner")] public string Owner { get; init; } = "";
    [JsonPropertyName("name")]  public string Name  { get; init; } = "";
    [JsonPropertyName("level")] public string Level { get; init; } = "";
}
