using System.Text.Json;
using Capacitor.Cli.Harness.Cursor;

namespace Capacitor.Cli.Tests.Unit.Harness.Cursor;

public class CursorSubagentCorrelatorTests {
    // Cursor runs a subagent as its OWN session/transcript. The only in-data link is that
    // the child's first user_query (minus the <user_query> wrapper) is byte-identical to the
    // parent's `Task`/`Agent` tool_use `prompt`. The correlator recovers that link.

    static string Line(object o) => JsonSerializer.Serialize(o);

    static string ParentTranscript(string prompt, string subagentType = "generalPurpose") => string.Join("\n",
        Line(new { role = "user", message = new { content = new object[] { new { type = "text", text = "do the thing" } } } }),
        Line(new {
            role    = "assistant",
            message = new {
                content = new object[] {
                    new { type = "tool_use", name = "Task", input = new { description = "explore", prompt, subagent_type = subagentType } }
                }
            }
        })
    );

    static string ChildTranscript(string prompt) => string.Join("\n",
        Line(new { role = "user", message = new { content = new object[] { new { type = "text", text = "<user_query>\n" + prompt + "\n</user_query>" } } } }),
        Line(new { role = "assistant", message = new { content = new object[] { new { type = "text", text = "exploring" } } } })
    );

    static string TaskLine(string prompt, string subagentType) => Line(new {
        role    = "assistant",
        message = new { content = new object[] { new { type = "tool_use", name = "Task", input = new { prompt, subagent_type = subagentType } } } }
    });

    [Test]
    public async Task links_child_to_parent_when_first_user_query_matches_task_prompt() {
        using var fx = new CorrelatorFixture();
        const string prompt = "You are exploring the LoanApplicationDemo project. Return an overview.";
        var parent = fx.Add("11111111111111111111111111111111", ParentTranscript(prompt));
        var child  = fx.Add("22222222222222222222222222222222", ChildTranscript(prompt));

        var links = CursorSubagentCorrelator.Correlate([
            ("11111111111111111111111111111111", parent),
            ("22222222222222222222222222222222", child),
        ]);

        await Assert.That(links.ContainsKey("22222222222222222222222222222222")).IsTrue();
        await Assert.That(links["22222222222222222222222222222222"].ParentSessionId).IsEqualTo("11111111111111111111111111111111");
        await Assert.That(links["22222222222222222222222222222222"].SubagentType).IsEqualTo("generalPurpose");
        // The parent itself is not a child of anyone.
        await Assert.That(links.ContainsKey("11111111111111111111111111111111")).IsFalse();
    }

    [Test]
    public async Task no_link_when_no_prompt_matches() {
        using var fx = new CorrelatorFixture();
        var a = fx.Add("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ParentTranscript("prompt A"));
        var b = fx.Add("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", ChildTranscript("a totally different prompt"));

        var links = CursorSubagentCorrelator.Correlate([
            ("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", a),
            ("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", b),
        ]);

        await Assert.That(links.Count).IsEqualTo(0);
    }

    [Test]
    public async Task does_not_self_link_when_a_session_tasks_with_its_own_first_prompt() {
        // Degenerate guard: a session must never be classified as its own subagent.
        using var fx = new CorrelatorFixture();
        const string prompt = "explore repo";
        var self = fx.Add("cccccccccccccccccccccccccccccccc", ChildTranscript(prompt) + "\n" + TaskLine(prompt, "x"));

        var links = CursorSubagentCorrelator.Correlate([("cccccccccccccccccccccccccccccccc", self)]);

        await Assert.That(links.ContainsKey("cccccccccccccccccccccccccccccccc")).IsFalse();
    }

    [Test]
    public async Task ambiguous_prompt_across_two_parents_yields_no_link_regardless_of_input_order() {
        // Review finding: if two DISTINCT parents issue the same Task prompt, the linkage is
        // genuinely ambiguous — the correlator must NOT guess a parent (which could misattribute
        // the child). It drops the link entirely, deterministically, in any input order.
        using var fx = new CorrelatorFixture();
        const string prompt = "shared prompt";
        var p1    = fx.Add("11111111111111111111111111111111", ParentTranscript(prompt, "typeA"));
        var p2    = fx.Add("99999999999999999999999999999999", ParentTranscript(prompt, "typeB"));
        var child = fx.Add("22222222222222222222222222222222", ChildTranscript(prompt));

        var forward = CursorSubagentCorrelator.Correlate([
            ("11111111111111111111111111111111", p1),
            ("99999999999999999999999999999999", p2),
            ("22222222222222222222222222222222", child),
        ]);
        var reversed = CursorSubagentCorrelator.Correlate([
            ("22222222222222222222222222222222", child),
            ("99999999999999999999999999999999", p2),
            ("11111111111111111111111111111111", p1),
        ]);

        // No link either way — ambiguous prompt is not attributed to a guessed parent.
        await Assert.That(forward.ContainsKey("22222222222222222222222222222222")).IsFalse();
        await Assert.That(reversed.ContainsKey("22222222222222222222222222222222")).IsFalse();
    }

    [Test]
    public async Task same_parent_tasking_a_prompt_twice_is_not_ambiguous() {
        // A single parent issuing the same Task prompt more than once must still link its child —
        // only DISTINCT parents make a prompt ambiguous.
        using var fx = new CorrelatorFixture();
        const string prompt = "explore twice";
        var parent = fx.Add("11111111111111111111111111111111",
            ParentTranscript(prompt) + "\n" + TaskLine(prompt, "generalPurpose"));
        var child  = fx.Add("22222222222222222222222222222222", ChildTranscript(prompt));

        var links = CursorSubagentCorrelator.Correlate([
            ("11111111111111111111111111111111", parent),
            ("22222222222222222222222222222222", child),
        ]);

        await Assert.That(links["22222222222222222222222222222222"].ParentSessionId).IsEqualTo("11111111111111111111111111111111");
    }

    [Test]
    public async Task a_bad_transcript_entry_is_skipped_without_aborting_correlation() {
        // Review finding: a bad transcript entry must not break correlation (which would abort
        // ClassifyAsync for the whole import). A non-file path is skipped by the File.Exists
        // guard; genuine read errors are additionally swallowed by the per-session try/catch.
        using var fx = new CorrelatorFixture();
        const string prompt = "explore it";
        var parent = fx.Add("11111111111111111111111111111111", ParentTranscript(prompt));
        var child  = fx.Add("22222222222222222222222222222222", ChildTranscript(prompt));
        var badDir = Path.Combine(fx.Root, "not-a-file.jsonl");
        Directory.CreateDirectory(badDir); // a directory, not a readable transcript file

        var links = CursorSubagentCorrelator.Correlate([
            ("33333333333333333333333333333333", badDir),
            ("11111111111111111111111111111111", parent),
            ("22222222222222222222222222222222", child),
        ]);

        // Valid pair still correlated; no throw.
        await Assert.That(links["22222222222222222222222222222222"].ParentSessionId).IsEqualTo("11111111111111111111111111111111");
    }

    sealed class CorrelatorFixture : IDisposable {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"kcap-correlator-{Guid.NewGuid():N}");

        public CorrelatorFixture() => Directory.CreateDirectory(Root);

        public string Add(string sessionId, string jsonl) {
            var path = Path.Combine(Root, sessionId + ".jsonl");
            File.WriteAllText(path, jsonl.ReplaceLineEndings("\n") + "\n");
            return path;
        }

        public void Dispose() {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}
