using System.Text.RegularExpressions;
using Capacitor.Models.Transcripts.Harness.Claude;

namespace Capacitor.Cli.Core.Harness.Claude;

/// What the chat hides or rewrites in Claude records: meta and sidechain records, the blocks
/// Claude Code injects around a user turn, and the finished-background-task record it injects as
/// if the user had spoken.
public sealed partial class ClaudeChatRules : IChatDisplayRules {
    public static readonly ClaudeChatRules Instance = new();

    ClaudeChatRules() { }

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) {
        var slug = SchemaExtensions.Slug(evt.Payload, ClaudeCodeExtension.Slug);
        if (SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsSidechain) || SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta)) return null;

        switch (envelope.Kind) {
            case AcpEventKind.UserMessage: {
                if (SchemaExtensions.Text(slug, ClaudeCodeExtension.OriginKind) == "task-notification")
                    return TaskNotificationNote(envelope);
                var text = StripWrappers(envelope.Text ?? "");
                return text.Length == 0 ? null : envelope with { Text = text };
            }
            case AcpEventKind.ToolResult:
                return envelope with { ToolIsError = SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsError) };
            default:
                return envelope;
        }
    }

    // System-attributed: the summary in bold, then the result as markdown; a notification with
    // neither shows whatever is left once the wrapper tags are gone.
    static AcpEventEnvelope? TaskNotificationNote(AcpEventEnvelope envelope) {
        var raw     = envelope.Text ?? "";
        var summary = TaskSummary().Match(raw) is { Success: true } s ? s.Groups[1].Value.Trim() : "";
        var body    = TaskResult().Match(raw) is { Success: true } r ? r.Groups[1].Value.Trim() : "";
        var parts   = new List<string>(2);
        if (summary.Length > 0) parts.Add($"**{summary}**");
        if (body.Length > 0) parts.Add(body);
        var text = parts.Count > 0 ? string.Join("\n\n", parts) : TaskWrapper().Replace(raw, "").Trim();
        return text.Length == 0 ? null : envelope with { Kind = AcpEventKind.SystemNote, Text = text };
    }

    /// Removes the blocks Claude Code injects around a user turn: reminders and slash-command
    /// echoes.
    internal static string StripWrappers(string text) => Wrappers().Replace(text, "").Trim();

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex TaskSummary();

    [GeneratedRegex(@"<result>(.*?)</result>", RegexOptions.Singleline)]
    private static partial Regex TaskResult();

    [GeneratedRegex(@"</?task-notification>")]
    private static partial Regex TaskWrapper();

    [GeneratedRegex(@"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-caveat)>.*?</\1>", RegexOptions.Singleline)]
    private static partial Regex Wrappers();
}
