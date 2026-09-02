using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// Pure: no dispatcher, so no session constraint.
public class ToolSummaryTests {
    /// The declared name map, held here so the test pins it in both directions.
    static readonly (ToolCategory Category, string[] Names)[] Rows = [
        (ToolCategory.Read,      ["Read", "NotebookRead", "read_file", "view_image"]),
        (ToolCategory.Edit,      ["Edit", "MultiEdit", "Write", "NotebookEdit", "apply_patch", "write_file"]),
        (ToolCategory.Command,   ["Bash", "BashOutput", "KillShell", "shell", "shell_command", "exec", "exec_command", "write_stdin", "local_shell", "container.exec"]),
        (ToolCategory.Search,    ["Grep", "Glob", "LS"]),
        (ToolCategory.WebSearch, ["WebSearch", "web_search"]),
        (ToolCategory.Fetch,     ["WebFetch"]),
        (ToolCategory.Skill,     ["Skill"]),
        (ToolCategory.Agent,     ["Task", "Agent", "TaskOutput", "TaskStop", "spawn_agent", "wait_agent", "send_input", "send_message", "resume_agent", "interrupt_agent", "close_agent", "list_agents"]),
        (ToolCategory.Plan,      ["TodoWrite", "update_plan"]),
        (ToolCategory.Question,  ["AskUserQuestion", "request_user_input"]),
    ];

    [Test]
    public async Task Every_declared_name_maps_to_its_row_case_insensitively_and_nothing_else_is_declared() {
        var expected = Rows.SelectMany(r => r.Names.Select(n => (n, r.Category))).ToDictionary(p => p.n, p => p.Category, StringComparer.Ordinal);
        foreach (var (name, category) in expected) {
            await Assert.That(ToolSummary.Categorize(name, null)).IsEqualTo(category);
            await Assert.That(ToolSummary.Categorize(name.ToUpperInvariant(), null)).IsEqualTo(category);
        }
        await Assert.That(ToolSummary.Names.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(expected.Keys)).IsTrue();
    }

    [Test]
    [Arguments("mcp__github__create_issue")]
    [Arguments("SomethingNew")]
    [Arguments("")]
    public async Task Unknown_and_mcp_names_are_other(string name) {
        await Assert.That(ToolSummary.Categorize(name, """{"x":1}""")).IsEqualTo(ToolCategory.Other);
    }

    [Test]
    public async Task Describe_uses_article_for_one_plural_for_many_first_appearance_order_and_lower_cases_after_the_first() {
        await Assert.That(ToolSummary.Describe([])).IsEqualTo("");
        await Assert.That(ToolSummary.Describe([ToolCategory.Command])).IsEqualTo("Ran a command");
        await Assert.That(ToolSummary.Describe([ToolCategory.Read, ToolCategory.Read])).IsEqualTo("Read files");
        await Assert.That(ToolSummary.Describe([ToolCategory.Read, ToolCategory.Command, ToolCategory.Read, ToolCategory.Edit]))
            .IsEqualTo("Read files, ran a command, edited a file");
        await Assert.That(ToolSummary.Describe([ToolCategory.Agent, ToolCategory.Skill, ToolCategory.Other, ToolCategory.Other]))
            .IsEqualTo("Ran an agent, loaded a skill, called tools");
        await Assert.That(ToolSummary.Describe([ToolCategory.Search, ToolCategory.Search, ToolCategory.WebSearch, ToolCategory.Fetch, ToolCategory.Fetch, ToolCategory.Plan, ToolCategory.Question]))
            .IsEqualTo("Searched files, searched the web, fetched pages, updated the plan, asked a question");
    }

    [Test]
    [Arguments("Read", """{"file_path":"/repo/.claude/skills/review/SKILL.md"}""", ToolCategory.Skill)]
    [Arguments("Read", """{"file_path":"/repo/SKILL.md.bak"}""", ToolCategory.Read)]
    [Arguments("exec_command", """{"cmd":"sed -n '1,40p' a.cs"}""", ToolCategory.Read)]
    [Arguments("exec_command", """{"cmd":"rg foo src"}""", ToolCategory.Search)]
    [Arguments("exec_command", """{"cmd":"ls src"}""", ToolCategory.Search)]
    [Arguments("exec_command", """{"cmd":"cat a && make"}""", ToolCategory.Command)]
    [Arguments("exec_command", """{"cmd":"cat skills/review/SKILL.md"}""", ToolCategory.Skill)]
    [Arguments("shell", """{"command":["rg","foo","src"]}""", ToolCategory.Search)]
    [Arguments("shell", """{"command":["bash","-lc","cat a.md"]}""", ToolCategory.Read)]
    [Arguments("Bash", """{"description":"List files"}""", ToolCategory.Command)]
    [Arguments("Bash", """{"command":"git status"}""", ToolCategory.Command)]
    [Arguments("Bash", "not json", ToolCategory.Command)]
    [Arguments("Bash", """["cat","a"]""", ToolCategory.Command)]
    [Arguments("exec", """{"input":"const r = 1;"}""", ToolCategory.Command)]
    [Arguments("spawn_agent", """{"task":"t"}""", ToolCategory.Agent)]
    public async Task Categorize_refines_reads_and_shell_commands_from_the_input(string name, string? input, ToolCategory expected) {
        await Assert.That(ToolSummary.Categorize(name, input)).IsEqualTo(expected);
    }
}
