using System.Text;
using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts;

namespace Capacitor.App.ViewModels;

public enum ToolCategory { Read, Edit, Command, Search, WebSearch, Fetch, Skill, Agent, Plan, Question, Other }

/// What a group of settled tool calls says about itself. The name map keys on the name the
/// transcript carries (Codex's rollout says `shell`, its hook says `Bash`); a name in no row is
/// Other, so an unknown vendor tool still reads "Called a tool" rather than nothing.
public static class ToolSummary {
    internal static readonly IReadOnlyDictionary<string, ToolCategory> Names = new Dictionary<string, ToolCategory>(StringComparer.OrdinalIgnoreCase) {
        ["Read"] = ToolCategory.Read, ["NotebookRead"] = ToolCategory.Read, ["read_file"] = ToolCategory.Read, ["view_image"] = ToolCategory.Read,
        ["Edit"] = ToolCategory.Edit, ["MultiEdit"] = ToolCategory.Edit, ["Write"] = ToolCategory.Edit, ["NotebookEdit"] = ToolCategory.Edit,
        ["apply_patch"] = ToolCategory.Edit, ["write_file"] = ToolCategory.Edit,
        ["Bash"] = ToolCategory.Command, ["BashOutput"] = ToolCategory.Command, ["KillShell"] = ToolCategory.Command, ["shell"] = ToolCategory.Command,
        ["shell_command"] = ToolCategory.Command, ["exec"] = ToolCategory.Command, ["exec_command"] = ToolCategory.Command,
        ["write_stdin"] = ToolCategory.Command, ["local_shell"] = ToolCategory.Command, ["container.exec"] = ToolCategory.Command,
        ["Grep"] = ToolCategory.Search, ["Glob"] = ToolCategory.Search, ["LS"] = ToolCategory.Search,
        ["WebSearch"] = ToolCategory.WebSearch, ["web_search"] = ToolCategory.WebSearch,
        ["WebFetch"] = ToolCategory.Fetch,
        ["Skill"] = ToolCategory.Skill,
        ["Task"] = ToolCategory.Agent, ["Agent"] = ToolCategory.Agent, ["TaskOutput"] = ToolCategory.Agent, ["TaskStop"] = ToolCategory.Agent,
        ["spawn_agent"] = ToolCategory.Agent, ["wait_agent"] = ToolCategory.Agent, ["send_input"] = ToolCategory.Agent, ["send_message"] = ToolCategory.Agent,
        ["resume_agent"] = ToolCategory.Agent, ["interrupt_agent"] = ToolCategory.Agent, ["close_agent"] = ToolCategory.Agent, ["list_agents"] = ToolCategory.Agent,
        ["TodoWrite"] = ToolCategory.Plan, ["update_plan"] = ToolCategory.Plan,
        ["AskUserQuestion"] = ToolCategory.Question, ["request_user_input"] = ToolCategory.Question,
    };

    // Indexed by ToolCategory.
    static readonly (string One, string Many)[] Phrases = [
        ("Read a file", "Read files"),
        ("Edited a file", "Edited files"),
        ("Ran a command", "Ran commands"),
        ("Searched files", "Searched files"),
        ("Searched the web", "Searched the web"),
        ("Fetched a page", "Fetched pages"),
        ("Loaded a skill", "Loaded skills"),
        ("Ran an agent", "Ran agents"),
        ("Updated the plan", "Updated the plan"),
        ("Asked a question", "Asked questions"),
        ("Called a tool", "Called tools"),
    ];

    public static ToolCategory Categorize(string name, string? inputJson) {
        var category = Names.TryGetValue(name, out var known) ? known : ToolCategory.Other;
        if (category is not (ToolCategory.Read or ToolCategory.Command) || string.IsNullOrEmpty(inputJson)) return category;
        try {
            using var doc = JsonDocument.Parse(inputJson);
            var root = doc.RootElement;
            if (!root.IsObject) return category;
            if (category == ToolCategory.Read)
                return IsSkillFile(root.Str("file_path")) ? ToolCategory.Skill : category;
            var hint = CodexCommandClassifier.Classify(CommandText(root));
            return hint?.Type switch {
                "read"                   => IsSkillFile(hint.Name) ? ToolCategory.Skill : ToolCategory.Read,
                "search" or "list_files" => ToolCategory.Search,
                _                        => category,
            };
        } catch (JsonException) {
            return category;
        }
    }

    public static string Describe(IEnumerable<ToolCategory> categories) {
        var order = new List<ToolCategory>();
        var counts = new Dictionary<ToolCategory, int>();
        foreach (var c in categories) {
            if (!counts.TryAdd(c, 1)) counts[c]++;
            else order.Add(c);
        }
        var sb = new StringBuilder();
        foreach (var c in order) {
            var (one, many) = Phrases[(int)c];
            var phrase = counts[c] == 1 ? one : many;
            if (sb.Length == 0) sb.Append(phrase);
            else sb.Append(", ").Append(char.ToLowerInvariant(phrase[0])).Append(phrase, 1, phrase.Length - 1);
        }
        return sb.ToString();
    }

    static bool IsSkillFile(string? path) =>
        path is not null && (path == "SKILL.md" || path.EndsWith("/SKILL.md", StringComparison.Ordinal));

    /// `cmd` (unified exec), then `command` as a string, then `command` as an argv array — a
    /// `bash -lc &lt;script&gt;` array hands its script through, since the classifier only peels the
    /// wrapper when the script is one quoted token.
    static string? CommandText(JsonElement root) {
        if (root.Str("cmd") is { } cmd) return cmd;
        if (root.Str("command") is { } command) return command;
        if (root.Arr("command") is not { } argv) return null;
        var parts = argv.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!).ToList();
        if (parts.Count == 3 && parts[1] is "-lc" or "-c" && parts[0].EndsWith("sh", StringComparison.Ordinal)) return parts[2];
        return string.Join(' ', parts);
    }
}
