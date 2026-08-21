using Capacitor.Cli.Core.Eval;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

public class EvalServicePromptVersionTests {
    static EvalCatalogDto Catalog(params EvalCatalogQuestionDto[] qs) => new() {
        RetrospectivePrompt = "retro {TRACE_JSON}", RetrospectivePromptVersion = "120",
        Questions = [.. qs]
    };

    static EvalCatalogQuestionDto CQ(string id, string cat, string raw, string rendered, string ver, bool tools = false) =>
        new() { Category = cat, Id = id, Title = "t", QuestionText = raw, Prompt = rendered, PromptVersion = ver, NeedsTools = tools };

    [Test]
    public async Task Reconcile_builds_run_questions_from_catalog_in_selected_order() {
        var catalog = Catalog(
            CQ("k1", "safety",  "raw1", "RENDERED1 {TRACE_JSON}", "100"),
            CQ("k2", "quality", "raw2", "RENDERED2 {TRACE_JSON}", "105", tools: true));
        // Selected ids (from dispatch), order k2 then k1.
        var selectedIds = new[] { "k2", "k1" };

        var reconciled = EvalService.ReconcileQuestions(selectedIds, catalog);

        await Assert.That(reconciled.Count).IsEqualTo(2);
        await Assert.That(reconciled[0].Id).IsEqualTo("k2");                 // order preserved
        await Assert.That(reconciled[0].Prompt).IsEqualTo("RENDERED2 {TRACE_JSON}"); // rendered (text path)
        await Assert.That(reconciled[0].RawText).IsEqualTo("raw2");          // raw (tools path)
        await Assert.That(reconciled[0].PromptVersion).IsEqualTo("105");
        await Assert.That(reconciled[0].NeedsTools).IsTrue();
        await Assert.That(reconciled[1].Id).IsEqualTo("k1");
        await Assert.That(reconciled[1].NeedsTools).IsFalse();
    }

    [Test]
    public async Task Reconcile_throws_on_selected_id_absent_from_catalog() {
        var catalog = Catalog(CQ("k1", "safety", "raw1", "R1", "100"));
        await Assert.That(() => EvalService.ReconcileQuestions(["k1", "ghost"], catalog))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task BuildTextQuestionPrompt_uses_rendered_prompt_and_strips_cache_boundary() {
        // Reconciled text-path question: Prompt is the catalog RENDERED prompt.
        var q = new EvalQuestionDto {
            Category = "safety", Id = "k1", Text = "t",
            Prompt = "RENDERED {CACHE_BOUNDARY} Trace:{TRACE_JSON} Sess:{SESSION_ID}",
            RawText = "raw", NeedsTools = false, PromptVersion = "100"
        };

        var prompt = EvalService.BuildTextQuestionPrompt(q, "sess-1", "run-1", "TRACE");

        await Assert.That(prompt).Contains("RENDERED");
        await Assert.That(prompt).Contains("Trace:TRACE");
        await Assert.That(prompt).Contains("Sess:sess-1");
        await Assert.That(prompt).DoesNotContain("{CACHE_BOUNDARY}");   // stripped
        await Assert.That(prompt).DoesNotContain("{TRACE_JSON}");
    }

    [Test]
    public async Task Aggregate_stamps_each_verdict_with_its_catalog_prompt_version() {
        var questions = new List<EvalQuestionDto> {
            new() { Category = "safety", Id = "k1", Text = "t", Prompt = "p", RawText = "r", PromptVersion = "100" },
            new() { Category = "quality", Id = "k2", Text = "t", Prompt = "p", RawText = "r", PromptVersion = "105" }
        };
        var verdicts = new List<EvalQuestionVerdict> {
            new() { Category = "safety",  QuestionId = "k1", Score = 5, Verdict = "pass", Finding = "ok" },
            new() { Category = "quality", QuestionId = "k2", Score = 4, Verdict = "pass", Finding = "ok" }
        };

        var agg = EvalService.Aggregate(verdicts, "run-1", "model", questions);

        var k1 = agg.Categories.SelectMany(c => c.Questions).Single(v => v.QuestionId == "k1");
        var k2 = agg.Categories.SelectMany(c => c.Questions).Single(v => v.QuestionId == "k2");
        await Assert.That(k1.PromptVersion).IsEqualTo("100");
        await Assert.That(k2.PromptVersion).IsEqualTo("105");
    }
}
