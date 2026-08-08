using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

/// The consent decision log's single write/read shape (spec §4.4): the daemon appends one of
/// these per decision to consent-decisions.jsonl; the CLI `log` verb prints raw lines; the app
/// parses them for the Activity feed. Field names are the pre-existing on-disk names verbatim —
/// existing log files remain readable. Outcome: "allowed"|"denied". Source: "owner"|"rule[i]"|
/// "default"|"prompt_no_ui"|"prompt_user"|"prompt_timeout".
public sealed record ConsentDecisionRecord(
    string DecidedAt, string AgentId, string? Requester, bool RequesterIsOwner,
    string Kind, string RepoPath, string Vendor, string Outcome, string Source,
    string? RequesterDisplay);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ConsentDecisionRecord))]
public partial class ConsentDecisionJsonContext : JsonSerializerContext;
